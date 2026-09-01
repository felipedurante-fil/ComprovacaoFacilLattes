using ComprovacaoFacilLattes.App.Services;
using ComprovacaoFacilLattes.Core.Models;
using ComprovacaoFacilLattes.Core.Persistence;
using ComprovacaoFacilLattes.Core.Qualis;
using ComprovacaoFacilLattes.Core.Reporting;
using Microsoft.EntityFrameworkCore;

namespace ComprovacaoFacilLattes.Tests.AppServices;

/// <summary>
/// Verifica a camada de serviços da App (ProfileImportService/ScanService/ReportService)
/// ponta-a-ponta, contra um banco SQLite TEMPORÁRIO — nunca o banco real do usuário
/// (<see cref="AppDb.DatabasePathOverride"/>). Substitui a verificação visual manual: a
/// UI em si (Avalonia) não pode ser dirigida por xUnit, mas toda a lógica por trás dos
/// botões — importar PDF, escanear pasta, gerar relatório — passa por aqui.
///
/// [Collection("AppDb")]: AppDb.DatabasePathOverride é um campo estático — sem agrupar
/// numa collection, o xUnit roda classes de teste diferentes em paralelo por padrão e
/// duas classes usando esse override ao mesmo tempo pisam uma na configuração da outra.
/// </summary>
[Collection("AppDb")]
public class AppServicesTests : IDisposable
{
    private readonly string _tempDir = Directory.CreateTempSubdirectory("cfl-app-services-tests-").FullName;
    private readonly string _dbPath;

    public AppServicesTests()
    {
        _dbPath = Path.Combine(_tempDir, "app.db");
        AppDb.DatabasePathOverride = _dbPath;
    }

    public void Dispose()
    {
        AppDb.DatabasePathOverride = null;
        Directory.Delete(_tempDir, recursive: true);
    }

    private static string FindCalibrationPdf()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Curriculo CAlibrar 2.pdf")))
            dir = dir.Parent;
        return Path.Combine(dir!.FullName, "Curriculo CAlibrar 2.pdf");
    }

    [Fact]
    public void ImportarNovoPerfilPersisteONomeESecoesDoParser()
    {
        var pdfPath = FindCalibrationPdf();

        var id = ProfileImportService.ImportNewProfile(pdfPath);

        using var db = AppDb.Create();
        var profile = db.Profiles
            .Include(p => p.Sections).ThenInclude(s => s.Entries)
            .Single(p => p.Id == id);

        Assert.Equal("Victor dos Santos Ronchim", profile.Name);
        Assert.True(profile.Sections.Count >= 20, "esperava dezenas de seções, igual ao parser puro já testado");
        Assert.Contains(profile.Sections, s => s.Title == "Artigos completos publicados em periódicos" && s.Entries.Count == 4);
        Assert.True(Directory.Exists(profile.SavePath));
    }

    [Fact]
    public void ImportarDuasVezesOMesmoPdfGeraNomesUnicos()
    {
        var pdfPath = FindCalibrationPdf();

        var id1 = ProfileImportService.ImportNewProfile(pdfPath);
        var id2 = ProfileImportService.ImportNewProfile(pdfPath);

        using var db = AppDb.Create();
        var name1 = db.Profiles.Single(p => p.Id == id1).Name;
        var name2 = db.Profiles.Single(p => p.Id == id2).Name;

        Assert.NotEqual(name1, name2);
        Assert.Equal("Victor dos Santos Ronchim (2)", name2);
    }

    [Fact]
    public void EscanearPastaSugereVincularComprovanteCujoNomeDeArquivoBateComOTituloDaEntrada()
    {
        var pdfPath = FindCalibrationPdf();
        var id = ProfileImportService.ImportNewProfile(pdfPath);

        using var db = AppDb.Create();
        var profile = db.Profiles
            .Include(p => p.Sections).ThenInclude(s => s.Entries).ThenInclude(e => e.Certificates)
            .Include(p => p.Certificates)
            .Single(p => p.Id == id);

        // Não usa uma entrada de "Artigo" aqui: RankedMatches tem um gate que exige um
        // identificador de publicação (ISSN/ISBN/DOI) no texto do certificado para
        // sequer considerar artigos — não é o que este teste está exercitando.
        var entry = profile.Sections.Single(s => s.Title == "Organização de eventos").Entries[0];

        // Cria um "comprovante" cujo nome de arquivo é o próprio texto bruto da
        // entrada (garante sobreposição de palavras alta o bastante pro matcher).
        var scanDir = Directory.CreateTempSubdirectory("cfl-scan-").FullName;
        var safeName = string.Join("_", entry.RawText.Split(Path.GetInvalidFileNameChars()))[..Math.Min(80, entry.RawText.Length)];
        File.WriteAllText(Path.Combine(scanDir, safeName + ".txt"), "conteúdo irrelevante");
        // Renomeia para .pdf só para passar pelo filtro de extensão do FileCollector —
        // não precisa ser um PDF válido pois o texto vem inteiramente do NOME do arquivo aqui.
        var fakePdfPath = Path.Combine(scanDir, safeName + ".pdf");
        File.Move(Path.Combine(scanDir, safeName + ".txt"), fakePdfPath);

        var results = ScanService.ScanFolder(profile, scanDir);

        // O objetivo aqui é a ENCANAÇÃO da app (FileCollector → extração → matcher →
        // persistência) — qual entrada exata o matcher escolhe entre as ~40 do perfil já
        // é coberto por CertificateMatcherTests; não repetimos essa precisão aqui.
        Assert.Single(results);
        Assert.NotNull(results[0].SuggestedEntry);
        var suggested = results[0].SuggestedEntry!;

        ScanService.ApplyLink(db, profile, results[0], suggested);

        var reloaded = db.Entries.Include(e => e.Certificates).Single(e => e.Id == suggested.Id);
        Assert.Equal(EntryStatus.Confirmed, reloaded.CertificateStatus);
        Assert.Single(reloaded.Certificates);
        Assert.True(reloaded.Certificates[0].IsConfirmed);

        Directory.Delete(scanDir, recursive: true);
    }

    [Fact]
    public void GerarRelatorioProduzUmPdfRealQuandoHaUmaEntradaConfirmada()
    {
        var pdfPath = FindCalibrationPdf();
        var id = ProfileImportService.ImportNewProfile(pdfPath);

        using var db = AppDb.Create();
        var profile = db.Profiles
            .Include(p => p.Sections).ThenInclude(s => s.Entries).ThenInclude(e => e.Certificates)
            .Single(p => p.Id == id);
        var entry = profile.Sections.First(s => s.Entries.Count > 0).Entries[0];

        // Usa o próprio PDF de calibração como "comprovante" confirmado (é um PDF real e válido).
        var cert = new Certificate(pdfPath) { IsConfirmed = true, Entry = entry, Profile = profile };
        entry.Certificates.Add(cert);
        db.Certificates.Add(cert);
        db.SaveChanges();

        var config = new ReportConfig { Profile = profile, IncludeLattes = false, IncludeToc = false, NumberPages = false };
        var qualis = new QualisService();
        var bytes = ReportService.Generate(profile, config, qualis);

        Assert.NotNull(bytes);
        Assert.Equal("%PDF", System.Text.Encoding.ASCII.GetString(bytes!, 0, 4));
    }
}
