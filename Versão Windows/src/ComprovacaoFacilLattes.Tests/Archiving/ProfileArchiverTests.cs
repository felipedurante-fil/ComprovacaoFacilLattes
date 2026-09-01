using ComprovacaoFacilLattes.Core.Archiving;
using ComprovacaoFacilLattes.Core.Models;
using ComprovacaoFacilLattes.Core.Persistence;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace ComprovacaoFacilLattes.Tests.Archiving;

public class ProfileArchiverTests : IDisposable
{
    private readonly string _tempDir = Directory.CreateTempSubdirectory("cfl-archiver-tests-").FullName;

    public void Dispose() => Directory.Delete(_tempDir, recursive: true);

    private string TempFile(string name, string content = "conteúdo de teste")
    {
        var path = Path.Combine(_tempDir, name);
        File.WriteAllText(path, content);
        return path;
    }

    private static AppDbContext NewInMemoryDb(out SqliteConnection connection)
    {
        connection = new SqliteConnection("Data Source=:memory:");
        connection.Open();
        var options = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(connection).Options;
        var ctx = new AppDbContext(options);
        ctx.Database.EnsureCreated();
        return ctx;
    }

    private LattesProfile BuildSampleProfile()
    {
        var lattesPath = TempFile("curriculo.pdf", "%PDF-1.4 fake");
        var profile = new LattesProfile("Perfil de Teste", lattesPath, "/tmp/saves")
        {
            RejectedLinks = ["arquivoX||2024_titulo"],
        };

        var section = new LattesSection("Artigos completos publicados em periódicos", 0);
        var entry = new LattesEntry("raw", "Título do Artigo", kind: "Artigo", year: 2024)
        {
            CertificateStatus = EntryStatus.Confirmed,
        };
        var certPath = TempFile("cert1.pdf");
        // Objetos em memória (sem EF) não sincronizam navegação inversa sozinhos —
        // setar Entry/Profile manualmente para o grafo ficar consistente, como um
        // grafo carregado de verdade do banco estaria.
        var cert = new Certificate(certPath) { IsConfirmed = true, Confidence = 0.97, Order = 0, Entry = entry, Profile = profile };
        entry.Certificates.Add(cert);
        section.Entries.Add(entry);
        profile.Sections.Add(section);

        var limboPath = TempFile("orfao.pdf");
        var limboCert = new Certificate(limboPath) { Profile = profile };
        profile.Certificates.Add(cert);
        profile.Certificates.Add(limboCert);

        return profile;
    }

    [Fact]
    public void ExportaEReimportaOGrafoCompletoComArquivosEmbutidos()
    {
        var profile = BuildSampleProfile();

        var zipPath = ProfileArchiver.Export(profile, includeFiles: true, _tempDir);
        Assert.True(File.Exists(zipPath));

        using var db = NewInMemoryDb(out var connection);
        using (connection)
        {
            var imported = ProfileArchiver.Import(zipPath, db, _tempDir);

            Assert.Equal("Perfil de Teste", imported.Name);
            Assert.Equal(["arquivoX||2024_titulo"], imported.RejectedLinks);
            Assert.True(File.Exists(imported.PdfPath));
            Assert.Contains("fake", File.ReadAllText(imported.PdfPath));

            var reloaded = db.Profiles
                .Include(p => p.Sections).ThenInclude(s => s.Entries).ThenInclude(e => e.Certificates)
                .Include(p => p.Certificates)
                .Single(p => p.Id == imported.Id);

            Assert.Single(reloaded.Sections);
            Assert.Single(reloaded.Sections[0].Entries);
            var entry = reloaded.Sections[0].Entries[0];
            Assert.Equal("Título do Artigo", entry.Title);
            Assert.Equal(EntryStatus.Confirmed, entry.CertificateStatus);
            Assert.Single(entry.Certificates);
            Assert.True(entry.Certificates[0].IsConfirmed);
            Assert.Equal(0.97, entry.Certificates[0].Confidence, precision: 2);
            // Arquivo embutido foi materializado num caminho novo e existe de verdade.
            Assert.True(File.Exists(entry.Certificates[0].FilePath));

            // 2 certificados no perfil (1 vinculado + 1 em limbo).
            Assert.Equal(2, reloaded.Certificates.Count);
            Assert.Single(reloaded.LimboCertificates);
            Assert.True(File.Exists(reloaded.LimboCertificates.Single().FilePath));

            // IDs novos — nunca reaproveita os originais.
            Assert.NotEqual(profile.Id, imported.Id);
        }
    }

    [Fact]
    public void ImportarDuasVezesSufixaONomeComNumeroParaEvitarColisao()
    {
        var profile1 = BuildSampleProfile();
        var zip1 = ProfileArchiver.Export(profile1, includeFiles: false, _tempDir);

        using var db = NewInMemoryDb(out var connection);
        using (connection)
        {
            var first = ProfileArchiver.Import(zip1, db, _tempDir);
            Assert.Equal("Perfil de Teste", first.Name);

            var profile2 = BuildSampleProfile();
            var zip2 = ProfileArchiver.Export(profile2, includeFiles: false, _tempDir);
            var second = ProfileArchiver.Import(zip2, db, _tempDir);

            Assert.Equal("Perfil de Teste (2)", second.Name);
        }
    }

    [Fact]
    public void ExportarSemIncluirArquivosMantemOCaminhoOriginalAoReimportar()
    {
        var profile = BuildSampleProfile();
        var originalCertPath = profile.Sections[0].Entries[0].Certificates[0].FilePath;

        var zipPath = ProfileArchiver.Export(profile, includeFiles: false, _tempDir);

        using var db = NewInMemoryDb(out var connection);
        using (connection)
        {
            var imported = ProfileArchiver.Import(zipPath, db, _tempDir);
            var reloaded = db.Entries.Include(e => e.Certificates).Single(e => e.Title == "Título do Artigo");

            // Sem embutir, o certificado importado mantém o caminho ORIGINAL (só
            // funciona no destino se esse caminho existir lá).
            Assert.Equal(originalCertPath, reloaded.Certificates[0].FilePath);
        }
    }

    [Fact]
    public void ArquivoZipInvalidoLancaExcecaoComMensagemClara()
    {
        var badZip = TempFile("nao_e_um_zip.zip", "isto nao e um zip valido");

        using var db = NewInMemoryDb(out var connection);
        using (connection)
        {
            var ex = Assert.Throws<ProfileArchiveException>(() => ProfileArchiver.Import(badZip, db, _tempDir));
            Assert.Contains(".zip exportado por este app", ex.Message);
        }
    }
}
