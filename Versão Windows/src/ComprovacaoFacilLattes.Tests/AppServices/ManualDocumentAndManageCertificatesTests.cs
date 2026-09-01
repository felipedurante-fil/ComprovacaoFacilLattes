using ComprovacaoFacilLattes.App.Services;
using ComprovacaoFacilLattes.App.ViewModels;
using ComprovacaoFacilLattes.Core.Models;
using Microsoft.EntityFrameworkCore;

namespace ComprovacaoFacilLattes.Tests.AppServices;

/// <summary>Mesma collection "AppDb" de <see cref="AppServicesTests"/> — evita rodar em paralelo com quem também usa AppDb.DatabasePathOverride (campo estático).</summary>
[Collection("AppDb")]
public class ManualDocumentAndManageCertificatesTests : IDisposable
{
    private readonly string _tempDir = Directory.CreateTempSubdirectory("cfl-manual-doc-tests-").FullName;

    public ManualDocumentAndManageCertificatesTests()
    {
        AppDb.DatabasePathOverride = Path.Combine(_tempDir, "app.db");
    }

    public void Dispose()
    {
        AppDb.DatabasePathOverride = null;
        Directory.Delete(_tempDir, recursive: true);
    }

    private string TempFile(string name)
    {
        var path = Path.Combine(_tempDir, name);
        File.WriteAllText(path, "conteúdo");
        return path;
    }

    private Guid CreateEmptyProfile()
    {
        using var db = AppDb.Create();
        var profile = new LattesProfile("Perfil de Teste", TempFile("curriculo.pdf"), _tempDir);
        db.Profiles.Add(profile);
        db.SaveChanges();
        return profile.Id;
    }

    [Fact]
    public void AdicionarDocumentoCriaASecaoManualNaPrimeiraVezEReaproveitaNasSeguintes()
    {
        var id = CreateEmptyProfile();
        var cert1 = TempFile("doc1.pdf");
        var cert2 = TempFile("doc2.pdf");

        using (var db = AppDb.Create())
        {
            var profile = db.Profiles.Include(p => p.Sections).ThenInclude(s => s.Entries).Single(p => p.Id == id);
            ManualDocumentService.AddDocument(db, profile, "Certidão A", cert1);
        }
        using (var db = AppDb.Create())
        {
            var profile = db.Profiles.Include(p => p.Sections).ThenInclude(s => s.Entries).Single(p => p.Id == id);
            ManualDocumentService.AddDocument(db, profile, "Certidão B", cert2);
        }

        using var check = AppDb.Create();
        var reloaded = check.Profiles
            .Include(p => p.Sections).ThenInclude(s => s.Entries).ThenInclude(e => e.Certificates)
            .Single(p => p.Id == id);

        var manualSections = reloaded.Sections.Where(s => s.Title == ManualDocumentService.SectionTitle).ToList();
        Assert.Single(manualSections); // uma seção só, reaproveitada na 2ª chamada
        Assert.Equal(2, manualSections[0].Entries.Count);
        Assert.All(manualSections[0].Entries, e =>
        {
            Assert.Equal(EntryStatus.Confirmed, e.CertificateStatus);
            Assert.Single(e.Certificates);
            Assert.True(e.Certificates[0].IsConfirmed);
        });
    }

    [Fact]
    public void ExcluirDocumentoManualRemoveAEntradaMasDevolveOCertificadoAoLimbo()
    {
        // Mesma regra do resto do app (AppDbContextTests): deletar a entrada NÃO apaga
        // o certificado — ele volta a ficar sem entrada vinculada ("em limbo").
        var id = CreateEmptyProfile();
        using (var db = AppDb.Create())
        {
            var profile = db.Profiles.Include(p => p.Sections).ThenInclude(s => s.Entries).Single(p => p.Id == id);
            ManualDocumentService.AddDocument(db, profile, "Certidão A", TempFile("doc.pdf"));
        }

        using (var db = AppDb.Create())
        {
            var entry = db.Entries.Single(e => e.Title == "Certidão A");
            ManualDocumentService.DeleteDocument(db, entry);
        }

        using var check = AppDb.Create();
        Assert.DoesNotContain(check.Entries, e => e.Title == "Certidão A");
        var cert = Assert.Single(check.Certificates);
        Assert.Null(cert.EntryId);
    }

    [Fact]
    public void GerenciarComprovantesReordenaConfirmaEExcluiCorretamente()
    {
        var id = CreateEmptyProfile();
        Guid entryId;
        using (var db = AppDb.Create())
        {
            var profile = db.Profiles.Single(p => p.Id == id);
            var section = new Core.Models.LattesSection("Seção", 0) { Profile = profile };
            var entry = new LattesEntry("raw", "Entrada de teste", order: 0) { Section = section };
            section.Entries.Add(entry);
            db.Sections.Add(section);
            db.Entries.Add(entry);

            var certA = new Certificate(TempFile("a.pdf")) { Order = 0, Entry = entry, Profile = profile };
            var certB = new Certificate(TempFile("b.pdf")) { Order = 1, Entry = entry, Profile = profile };
            entry.Certificates.Add(certA);
            entry.Certificates.Add(certB);
            db.Certificates.AddRange(certA, certB);
            db.SaveChanges();
            entryId = entry.Id;
        }

        using var db2 = AppDb.Create();
        var reloadedEntry = db2.Entries.Include(e => e.Certificates).Single(e => e.Id == entryId);
        var vm = new ManageCertificatesViewModel(db2, reloadedEntry);

        Assert.Equal(2, vm.Certificates.Count);
        Assert.Equal("a.pdf", vm.Certificates[0].FileName);

        // Confirma o primeiro — status da entrada deve virar Confirmed.
        vm.ToggleConfirmCommand.Execute(vm.Certificates[0]);
        Assert.True(vm.Certificates[0].IsConfirmed);
        Assert.Equal(EntryStatus.Confirmed, reloadedEntry.CertificateStatus);

        // Move o 2º pra cima — b.pdf passa a ser o primeiro da lista.
        vm.MoveUpCommand.Execute(vm.Certificates[1]);
        Assert.Equal("b.pdf", vm.Certificates[0].FileName);
        Assert.Equal(0, vm.Certificates[0].Model.Order);
        Assert.Equal(1, vm.Certificates[1].Model.Order);

        // Exclui um — só sobra um certificado.
        vm.DeleteCommand.Execute(vm.Certificates[1]);
        Assert.Single(vm.Certificates);

        using var check = AppDb.Create();
        var finalEntry = check.Entries.Include(e => e.Certificates).Single(e => e.Id == entryId);
        Assert.Single(finalEntry.Certificates);
    }
}
