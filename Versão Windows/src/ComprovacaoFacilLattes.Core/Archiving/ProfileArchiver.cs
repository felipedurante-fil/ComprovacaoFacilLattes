using System.IO.Compression;
using System.Text.Json;
using System.Text.Json.Serialization;
using ComprovacaoFacilLattes.Core.Models;
using ComprovacaoFacilLattes.Core.Persistence;

namespace ComprovacaoFacilLattes.Core.Archiving;

public sealed class ProfileArchiveException : Exception
{
    public ProfileArchiveException(string message) : base(message) { }
}

/// <summary>
/// Exporta/importa um perfil completo (currículo + seções + entradas + comprovantes)
/// como um único arquivo .zip — para backup ou para abrir em outro computador.
///
/// Adaptação do port: o app original usava o utilitário de linha de comando <c>ditto</c>
/// (sempre presente no macOS) para não adicionar uma dependência externa de zip; aqui
/// <see cref="ZipFile"/> (biblioteca padrão do .NET) cobre a mesma necessidade sem
/// precisar de nenhum processo externo — o FORMATO do pacote (manifest.json + PDF +
/// pasta files/) é o que importa manter, não a ferramenta usada para compactar.
/// </summary>
public static class ProfileArchiver
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    // MARK: - Modelo serializável

    private sealed class ProfileArchiveData
    {
        public int FormatVersion { get; set; } = 1;
        public string Name { get; set; } = "";
        public string PdfPath { get; set; } = "";
        public DateTime ImportDate { get; set; }
        public DateTime LastUpdated { get; set; }
        public string SavePath { get; set; } = "";
        public string RawText { get; set; } = "";
        public List<string> RejectedLinks { get; set; } = new();
        public List<SectionData> Sections { get; set; } = new();

        /// <summary>Certificados sem entrada vinculada.</summary>
        public List<CertificateData> LimboCertificates { get; set; } = new();

        public bool IncludesFiles { get; set; }
    }

    private sealed class SectionData
    {
        public string Title { get; set; } = "";
        public int Order { get; set; }
        public List<EntryData> Entries { get; set; } = new();
    }

    private sealed class EntryData
    {
        public string RawText { get; set; } = "";
        public string Title { get; set; } = "";
        public string Kind { get; set; } = "";
        public int Year { get; set; }
        public string Authors { get; set; } = "";
        public string Venue { get; set; } = "";
        public string Doi { get; set; } = "";
        public string Isbn { get; set; } = "";
        public string Portaria { get; set; } = "";
        public string Issn { get; set; } = "";
        public string Edital { get; set; } = "";
        public int EndYear { get; set; }
        public int Order { get; set; }
        public EntryStatus CertificateStatus { get; set; }
        public List<CertificateData> Certificates { get; set; } = new();
    }

    private sealed class CertificateData
    {
        public string OriginalFilePath { get; set; } = "";

        /// <summary>Presente só quando <see cref="ProfileArchiveData.IncludesFiles"/> == true.</summary>
        public string? BundledRelativePath { get; set; }

        public string ExtractedText { get; set; } = "";
        public double Confidence { get; set; }
        public bool IsConfirmed { get; set; }
        public bool IsRejected { get; set; }
        public DateTime ImportDate { get; set; }
        public int Order { get; set; }
    }

    // MARK: - Exportação

    /// <summary>
    /// Monta o pacote num arquivo .zip temporário (dentro de <paramref name="tempDirectory"/>)
    /// e devolve seu caminho — o chamador decide para onde movê-lo/copiá-lo (ex.: um
    /// diálogo "Salvar como") e deve removê-lo depois. <paramref name="profile"/> precisa
    /// vir com Sections/Entries/Certificates já carregados (eager-loaded).
    /// </summary>
    public static string Export(LattesProfile profile, bool includeFiles, string tempDirectory)
    {
        var workDir = Path.Combine(tempDirectory, $"LattesExport_{Guid.NewGuid()}");
        var filesDir = Path.Combine(workDir, "files");
        Directory.CreateDirectory(filesDir);

        CertificateData ArchiveCert(Certificate cert)
        {
            string? bundledRel = null;
            if (includeFiles && cert.Exists)
            {
                var destName = $"{cert.Id}_{cert.FileName}";
                var dest = Path.Combine(filesDir, destName);
                try
                {
                    File.Copy(cert.FilePath, dest, overwrite: true);
                    bundledRel = $"files/{destName}";
                }
                catch
                {
                    // melhor esforço — se a cópia falhar, exporta sem o arquivo embutido
                }
            }
            return new CertificateData
            {
                OriginalFilePath = cert.FilePath, BundledRelativePath = bundledRel,
                ExtractedText = cert.ExtractedText, Confidence = cert.Confidence,
                IsConfirmed = cert.IsConfirmed, IsRejected = cert.IsRejected,
                ImportDate = cert.ImportDate, Order = cert.Order,
            };
        }

        var sections = profile.SortedSections.Select(section => new SectionData
        {
            Title = section.Title,
            Order = section.Order,
            Entries = section.SortedEntries.Select(entry => new EntryData
            {
                RawText = entry.RawText, Title = entry.Title, Kind = entry.Kind, Year = entry.Year,
                Authors = entry.Authors, Venue = entry.Venue, Doi = entry.Doi, Isbn = entry.Isbn,
                Portaria = entry.Portaria, Issn = entry.Issn, Edital = entry.Edital, EndYear = entry.EndYear,
                Order = entry.Order, CertificateStatus = entry.CertificateStatus,
                Certificates = entry.SortedCertificates.Select(ArchiveCert).ToList(),
            }).ToList(),
        }).ToList();

        var limbo = profile.LimboCertificates.Select(ArchiveCert).ToList();

        var archive = new ProfileArchiveData
        {
            Name = profile.Name, PdfPath = profile.PdfPath, ImportDate = profile.ImportDate,
            LastUpdated = profile.LastUpdated, SavePath = profile.SavePath, RawText = profile.RawText,
            RejectedLinks = profile.RejectedLinks, Sections = sections, LimboCertificates = limbo,
            IncludesFiles = includeFiles,
        };

        File.WriteAllText(Path.Combine(workDir, "manifest.json"), JsonSerializer.Serialize(archive, JsonOptions));

        // O PDF original do Lattes é sempre incluído (pequeno e essencial — sem ele o
        // relatório final não consegue embutir o "Currículo Lattes completo").
        if (File.Exists(profile.PdfPath))
        {
            try { File.Copy(profile.PdfPath, Path.Combine(workDir, "curriculo.pdf"), overwrite: true); }
            catch { /* melhor esforço */ }
        }

        var safeName = string.IsNullOrEmpty(profile.Name) ? "Curriculo" : profile.Name.Replace('/', '-');
        var zipDest = Path.Combine(tempDirectory, $"{safeName}_export_{Guid.NewGuid()}.zip");
        try
        {
            ZipFile.CreateFromDirectory(workDir, zipDest, CompressionLevel.Optimal, includeBaseDirectory: false);
        }
        catch (Exception ex) when (ex is not ProfileArchiveException)
        {
            throw new ProfileArchiveException("Não foi possível compactar o arquivo exportado.");
        }
        finally
        {
            try { Directory.Delete(workDir, recursive: true); } catch { /* melhor esforço */ }
        }
        return zipDest;
    }

    // MARK: - Importação

    /// <summary>Importa um backup .zip, recriando todo o grafo de dados com IDs NOVOS (não reaproveita os UUIDs originais). Persiste via <paramref name="db"/> e devolve o perfil criado.</summary>
    public static LattesProfile Import(string zipPath, AppDbContext db, string tempDirectory)
    {
        var workDir = Path.Combine(tempDirectory, $"LattesImport_{Guid.NewGuid()}");
        Directory.CreateDirectory(workDir);
        try
        {
            try
            {
                ZipFile.ExtractToDirectory(zipPath, workDir);
            }
            catch (Exception ex) when (ex is not ProfileArchiveException)
            {
                throw new ProfileArchiveException(
                    "Não foi possível abrir o arquivo — verifique se é um .zip exportado por este app.");
            }

            var manifestPath = FindManifest(workDir)
                ?? throw new ProfileArchiveException("Arquivo inválido: não contém um currículo exportado por este app.");
            var root = Path.GetDirectoryName(manifestPath)!;

            var archive = JsonSerializer.Deserialize<ProfileArchiveData>(File.ReadAllText(manifestPath), JsonOptions)
                ?? throw new ProfileArchiveException("Arquivo inválido: não contém um currículo exportado por este app.");

            var name = UniqueName(archive.Name, db);
            var destBase = DefaultSavePath(name);
            Directory.CreateDirectory(destBase);

            var pdfPath = archive.PdfPath;
            var bundledPdf = Path.Combine(root, "curriculo.pdf");
            if (File.Exists(bundledPdf))
            {
                var dest = Path.Combine(destBase, "Currículo Lattes.pdf");
                try
                {
                    if (File.Exists(dest)) File.Delete(dest);
                    File.Copy(bundledPdf, dest);
                    pdfPath = dest;
                }
                catch { /* melhor esforço */ }
            }

            var profile = new LattesProfile(name, pdfPath, destBase)
            {
                ImportDate = archive.ImportDate,
                LastUpdated = archive.LastUpdated,
                RawText = archive.RawText,
                RejectedLinks = archive.RejectedLinks,
            };
            db.Profiles.Add(profile);

            var certsDestDir = Path.Combine(destBase, "Comprovantes Importados");
            var certsDirCreated = false;

            Certificate Materialize(CertificateData c)
            {
                var finalPath = c.OriginalFilePath;
                if (c.BundledRelativePath is { } rel)
                {
                    var src = Path.Combine(root, rel.Replace('/', Path.DirectorySeparatorChar));
                    if (File.Exists(src))
                    {
                        if (!certsDirCreated) { Directory.CreateDirectory(certsDestDir); certsDirCreated = true; }
                        var dest = Path.Combine(certsDestDir, Path.GetFileName(src));
                        try
                        {
                            if (File.Exists(dest)) File.Delete(dest);
                            File.Copy(src, dest);
                            finalPath = dest;
                        }
                        catch { /* melhor esforço */ }
                    }
                }
                var cert = new Certificate(finalPath)
                {
                    ExtractedText = c.ExtractedText, Confidence = c.Confidence,
                    IsConfirmed = c.IsConfirmed, IsRejected = c.IsRejected,
                    ImportDate = c.ImportDate, Order = c.Order,
                    Profile = profile,
                };
                db.Certificates.Add(cert);
                return cert;
            }

            foreach (var s in archive.Sections)
            {
                var section = new LattesSection(s.Title, s.Order) { Profile = profile };
                db.Sections.Add(section);
                foreach (var e in s.Entries)
                {
                    var entry = new LattesEntry(e.RawText, e.Title, e.Kind, e.Year, e.Authors, e.Venue, e.Order)
                    {
                        Doi = e.Doi, Isbn = e.Isbn, Portaria = e.Portaria, Issn = e.Issn, Edital = e.Edital,
                        EndYear = e.EndYear, CertificateStatus = e.CertificateStatus, Section = section,
                    };
                    db.Entries.Add(entry);
                    foreach (var c in e.Certificates) Materialize(c).Entry = entry;
                }
            }
            foreach (var c in archive.LimboCertificates) Materialize(c); // sem entrada -> fica em limbo, igual ao original

            db.SaveChanges();
            return profile;
        }
        finally
        {
            try { Directory.Delete(workDir, recursive: true); } catch { /* melhor esforço */ }
        }
    }

    // MARK: - Helpers

    private static string DefaultSavePath(string name)
    {
        var docs = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
        return Path.Combine(docs, "ComprovantesLattes", name);
    }

    /// <summary>Evita colidir com um perfil já existente com o mesmo nome (ex.: reimportar um backup do mesmo currículo).</summary>
    private static string UniqueName(string baseName, AppDbContext db)
    {
        var existing = db.Profiles.Select(p => p.Name).ToHashSet();
        if (!existing.Contains(baseName)) return baseName;
        var n = 2;
        while (existing.Contains($"{baseName} ({n})")) n++;
        return $"{baseName} ({n})";
    }

    /// <summary>O <c>ZipFile.ExtractToDirectory</c> preserva a pasta de origem como raiz do zip às vezes — procura 1 nível abaixo também.</summary>
    private static string? FindManifest(string root)
    {
        var direct = Path.Combine(root, "manifest.json");
        if (File.Exists(direct)) return direct;
        foreach (var dir in Directory.EnumerateDirectories(root))
        {
            var candidate = Path.Combine(dir, "manifest.json");
            if (File.Exists(candidate)) return candidate;
        }
        return null;
    }
}
