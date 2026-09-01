using ComprovacaoFacilLattes.Core.Models;
using ComprovacaoFacilLattes.Core.Persistence;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace ComprovacaoFacilLattes.Tests.Persistence;

public class AppDbContextTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly DbContextOptions<AppDbContext> _options;

    public AppDbContextTests()
    {
        // ":memory:" só sobrevive enquanto a conexão ficar aberta — mantida pelo teste inteiro.
        _connection = new SqliteConnection("Data Source=:memory:");
        _connection.Open();
        _options = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite(_connection)
            .Options;

        using var ctx = new AppDbContext(_options);
        ctx.Database.EnsureCreated();
    }

    public void Dispose() => _connection.Dispose();

    [Fact]
    public void SalvaERecarregaOGrafoCompletoProfileSectionEntryCertificate()
    {
        var profileId = Guid.Empty;

        using (var ctx = new AppDbContext(_options))
        {
            var profile = new LattesProfile("Fulano de Tal", "/tmp/curriculo.pdf", "/tmp/saves")
            {
                RejectedLinks = ["arquivo1||2024_titulo"]
            };

            var section = new LattesSection("Artigos completos publicados", 0);
            var entry = new LattesEntry("texto bruto", "Título do artigo", kind: "Artigo", year: 2024);
            var certConfirmado = new Certificate("/tmp/comprovantes/cert1.pdf") { IsConfirmed = true };
            var certLimbo = new Certificate("/tmp/comprovantes/orfao.pdf");

            entry.Certificates.Add(certConfirmado);
            section.Entries.Add(entry);
            profile.Sections.Add(section);
            profile.Certificates.Add(certConfirmado);
            profile.Certificates.Add(certLimbo);

            ctx.Profiles.Add(profile);
            ctx.SaveChanges();

            profileId = profile.Id;
        }

        using (var ctx = new AppDbContext(_options))
        {
            var reloaded = ctx.Profiles
                .Include(p => p.Sections).ThenInclude(s => s.Entries).ThenInclude(e => e.Certificates)
                .Include(p => p.Certificates)
                .Single(p => p.Id == profileId);

            Assert.Equal("Fulano de Tal", reloaded.Name);
            Assert.Equal(["arquivo1||2024_titulo"], reloaded.RejectedLinks);
            Assert.Single(reloaded.Sections);
            Assert.Single(reloaded.Sections[0].Entries);

            var entry = reloaded.Sections[0].Entries[0];
            Assert.Equal(EntryStatus.None, entry.CertificateStatus);
            Assert.Equal("2024_título do artigo", entry.HashKey);
            Assert.Single(entry.Certificates);
            Assert.True(entry.Certificates[0].IsConfirmed);

            // 2 certificados no total do perfil (1 vinculado + 1 em limbo).
            Assert.Equal(2, reloaded.Certificates.Count);
            Assert.Single(reloaded.LimboCertificates);
            Assert.Equal("orfao.pdf", reloaded.LimboCertificates.Single().FileName);
        }
    }

    [Fact]
    public void DeletarUmaEntradaDevolveOsCertificadosParaOLimboEmVezDeApagaLos()
    {
        Guid profileId, certId;

        using (var ctx = new AppDbContext(_options))
        {
            var profile = new LattesProfile("Perfil", "/tmp/c.pdf", "/tmp/s");
            var section = new LattesSection("Seção", 0);
            var entry = new LattesEntry("raw", "Título", order: 0);
            var cert = new Certificate("/tmp/x.pdf");

            entry.Certificates.Add(cert);
            section.Entries.Add(entry);
            profile.Sections.Add(section);
            profile.Certificates.Add(cert);

            ctx.Profiles.Add(profile);
            ctx.SaveChanges();

            profileId = profile.Id;
            certId = cert.Id;
        }

        using (var ctx = new AppDbContext(_options))
        {
            var profile = ctx.Profiles
                .Include(p => p.Sections).ThenInclude(s => s.Entries)
                .Single(p => p.Id == profileId);

            ctx.Entries.RemoveRange(profile.Sections[0].Entries);
            ctx.SaveChanges();
        }

        using (var ctx = new AppDbContext(_options))
        {
            var cert = ctx.Certificates.Single(c => c.Id == certId);
            Assert.Null(cert.EntryId);
        }
    }

    [Fact]
    public void DeletarOPerfilApagaEmCascataSecoesEntradasECertificados()
    {
        Guid profileId;

        using (var ctx = new AppDbContext(_options))
        {
            var profile = new LattesProfile("Perfil", "/tmp/c.pdf", "/tmp/s");
            var section = new LattesSection("Seção", 0);
            var entry = new LattesEntry("raw", "Título", order: 0);
            var cert = new Certificate("/tmp/x.pdf");

            entry.Certificates.Add(cert);
            section.Entries.Add(entry);
            profile.Sections.Add(section);
            profile.Certificates.Add(cert);

            ctx.Profiles.Add(profile);
            ctx.SaveChanges();
            profileId = profile.Id;
        }

        using (var ctx = new AppDbContext(_options))
        {
            ctx.Profiles.Remove(ctx.Profiles.Single(p => p.Id == profileId));
            ctx.SaveChanges();
        }

        using (var ctx = new AppDbContext(_options))
        {
            Assert.Empty(ctx.Sections);
            Assert.Empty(ctx.Entries);
            Assert.Empty(ctx.Certificates);
        }
    }
}
