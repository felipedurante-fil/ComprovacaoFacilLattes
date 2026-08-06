import Foundation
import SwiftData

/// Exporta/importa um perfil completo (currículo + seções + entradas + comprovantes)
/// como um único arquivo .zip — para backup ou para abrir em outro computador.
/// Usa `ditto` (sempre presente no macOS) em vez de uma dependência externa de zip.
enum ProfileArchiver {

    enum ArchiveError: LocalizedError {
        case zipFailed
        case unzipFailed
        case manifestNotFound

        var errorDescription: String? {
            switch self {
            case .zipFailed: return "Não foi possível compactar o arquivo exportado."
            case .unzipFailed: return "Não foi possível abrir o arquivo — verifique se é um .zip exportado por este app."
            case .manifestNotFound: return "Arquivo inválido: não contém um currículo exportado por este app."
            }
        }
    }

    // MARK: - Modelo serializável

    private struct ProfileArchiveData: Codable {
        var formatVersion: Int = 1
        var name: String
        var pdfPath: String
        var importDate: Date
        var lastUpdated: Date
        var savePath: String
        var rawText: String
        var rejectedLinks: [String]
        var sections: [SectionData]
        var limboCertificates: [CertificateData]   // sem entrada vinculada
        var includesFiles: Bool
    }

    private struct SectionData: Codable {
        var title: String
        var order: Int
        var entries: [EntryData]
    }

    private struct EntryData: Codable {
        var rawText: String
        var title: String
        var kind: String
        var year: Int
        var authors: String
        var venue: String
        var doi: String
        var isbn: String
        var portaria: String
        var issn: String
        var edital: String
        var endYear: Int
        var order: Int
        var certificateStatus: EntryStatus
        var certificates: [CertificateData]
    }

    private struct CertificateData: Codable {
        var originalFilePath: String
        var bundledRelativePath: String?   // presente só quando includesFiles == true
        var extractedText: String
        var confidence: Double
        var isConfirmed: Bool
        var isRejected: Bool
        var importDate: Date
        var order: Int
    }

    // MARK: - Exportação

    /// Monta o pacote num arquivo .zip temporário e retorna sua URL — o chamador
    /// decide onde salvá-lo (ex.: via NSSavePanel) e deve removê-lo depois.
    static func export(profile: LattesProfile, includeFiles: Bool) throws -> URL {
        let fm = FileManager.default
        let workDir = fm.temporaryDirectory.appendingPathComponent("LattesExport_\(UUID().uuidString)")
        let filesDir = workDir.appendingPathComponent("files")
        try fm.createDirectory(at: filesDir, withIntermediateDirectories: true)

        func archiveCert(_ cert: Certificate) -> CertificateData {
            var bundledRel: String? = nil
            if includeFiles, cert.exists {
                let destName = "\(cert.id.uuidString)_\(cert.fileName)"
                let dest = filesDir.appendingPathComponent(destName)
                if (try? fm.copyItem(at: cert.fileURL, to: dest)) != nil {
                    bundledRel = "files/\(destName)"
                }
            }
            return CertificateData(
                originalFilePath: cert.filePath, bundledRelativePath: bundledRel,
                extractedText: cert.extractedText, confidence: cert.confidence,
                isConfirmed: cert.isConfirmed, isRejected: cert.isRejected,
                importDate: cert.importDate, order: cert.order)
        }

        let sections = profile.sortedSections.map { section in
            SectionData(title: section.title, order: section.order, entries: section.sortedEntries.map { entry in
                EntryData(
                    rawText: entry.rawText, title: entry.title, kind: entry.kind, year: entry.year,
                    authors: entry.authors, venue: entry.venue, doi: entry.doi, isbn: entry.isbn,
                    portaria: entry.portaria, issn: entry.issn, edital: entry.edital, endYear: entry.endYear,
                    order: entry.order, certificateStatus: entry.certificateStatus,
                    certificates: entry.sortedCertificates.map(archiveCert))
            })
        }
        let limbo = profile.limboCertificates.map(archiveCert)

        let archive = ProfileArchiveData(
            name: profile.name, pdfPath: profile.pdfPath, importDate: profile.importDate,
            lastUpdated: profile.lastUpdated, savePath: profile.savePath, rawText: profile.rawText,
            rejectedLinks: profile.rejectedLinks, sections: sections, limboCertificates: limbo,
            includesFiles: includeFiles)

        let encoder = JSONEncoder()
        encoder.dateEncodingStrategy = .iso8601
        encoder.outputFormatting = [.prettyPrinted, .sortedKeys]
        try encoder.encode(archive).write(to: workDir.appendingPathComponent("manifest.json"))

        // O PDF original do Lattes é sempre incluído (pequeno e essencial — sem ele
        // o relatório final não consegue embutir o "Currículo Lattes completo").
        if fm.fileExists(atPath: profile.pdfPath) {
            try? fm.copyItem(at: URL(fileURLWithPath: profile.pdfPath),
                             to: workDir.appendingPathComponent("curriculo.pdf"))
        }

        let safeName = profile.name.isEmpty ? "Curriculo" : profile.name
            .replacingOccurrences(of: "/", with: "-")
        let zipDest = fm.temporaryDirectory
            .appendingPathComponent("\(safeName)_export_\(UUID().uuidString).zip")
        try zip(folder: workDir, to: zipDest)
        try? fm.removeItem(at: workDir)
        return zipDest
    }

    // MARK: - Importação

    @discardableResult
    static func importProfile(from zipURL: URL, modelContext: ModelContext) throws -> LattesProfile {
        let fm = FileManager.default
        let workDir = fm.temporaryDirectory.appendingPathComponent("LattesImport_\(UUID().uuidString)")
        try fm.createDirectory(at: workDir, withIntermediateDirectories: true)
        defer { try? fm.removeItem(at: workDir) }
        try unzip(zipURL, to: workDir)

        guard let manifestURL = findManifest(in: workDir) else { throw ArchiveError.manifestNotFound }
        let root = manifestURL.deletingLastPathComponent()
        let decoder = JSONDecoder()
        decoder.dateDecodingStrategy = .iso8601
        let archive = try decoder.decode(ProfileArchiveData.self, from: Data(contentsOf: manifestURL))

        let name = uniqueName(archive.name, modelContext: modelContext)
        let destBase = defaultSavePath(for: name)
        try? fm.createDirectory(at: destBase, withIntermediateDirectories: true)

        var pdfPath = archive.pdfPath
        let bundledPDF = root.appendingPathComponent("curriculo.pdf")
        if fm.fileExists(atPath: bundledPDF.path) {
            let dest = destBase.appendingPathComponent("Currículo Lattes.pdf")
            try? fm.removeItem(at: dest)
            try? fm.copyItem(at: bundledPDF, to: dest)
            pdfPath = dest.path
        }

        let profile = LattesProfile(name: name, pdfPath: pdfPath, savePath: destBase.path)
        profile.importDate = archive.importDate
        profile.lastUpdated = archive.lastUpdated
        profile.rawText = archive.rawText
        profile.rejectedLinks = archive.rejectedLinks
        modelContext.insert(profile)

        let certsDestDir = destBase.appendingPathComponent("Comprovantes Importados")
        var certsDirCreated = false
        func materialize(_ c: CertificateData) -> Certificate {
            var finalPath = c.originalFilePath
            if let rel = c.bundledRelativePath {
                let src = root.appendingPathComponent(rel)
                if fm.fileExists(atPath: src.path) {
                    if !certsDirCreated {
                        try? fm.createDirectory(at: certsDestDir, withIntermediateDirectories: true)
                        certsDirCreated = true
                    }
                    let dest = certsDestDir.appendingPathComponent(src.lastPathComponent)
                    try? fm.removeItem(at: dest)
                    if (try? fm.copyItem(at: src, to: dest)) != nil {
                        finalPath = dest.path
                    }
                }
            }
            let cert = Certificate(filePath: finalPath)
            cert.extractedText = c.extractedText
            cert.confidence = c.confidence
            cert.isConfirmed = c.isConfirmed
            cert.isRejected = c.isRejected
            cert.importDate = c.importDate
            cert.order = c.order
            cert.profile = profile
            modelContext.insert(cert)
            return cert
        }

        for s in archive.sections {
            let section = LattesSection(title: s.title, order: s.order)
            section.profile = profile
            modelContext.insert(section)
            for e in s.entries {
                let entry = LattesEntry(
                    rawText: e.rawText, title: e.title, kind: e.kind, year: e.year,
                    authors: e.authors, venue: e.venue, order: e.order)
                entry.doi = e.doi; entry.isbn = e.isbn; entry.portaria = e.portaria
                entry.issn = e.issn; entry.edital = e.edital; entry.endYear = e.endYear
                entry.certificateStatus = e.certificateStatus
                entry.section = section
                modelContext.insert(entry)
                for c in e.certificates {
                    materialize(c).entry = entry
                }
            }
        }
        for c in archive.limboCertificates {
            _ = materialize(c)   // sem entrada -> fica em limbo, igual ao original
        }

        try modelContext.save()
        return profile
    }

    // MARK: - Helpers

    private static func defaultSavePath(for name: String) -> URL {
        let base = FileManager.default.urls(for: .documentDirectory, in: .userDomainMask).first?
            .appendingPathComponent("ComprovantesLattes")
            ?? URL(fileURLWithPath: NSString(string: "~/Documents/ComprovantesLattes").expandingTildeInPath)
        return base.appendingPathComponent(name)
    }

    /// Evita colidir com um perfil já existente com o mesmo nome (ex.: reimportar
    /// um backup do mesmo currículo).
    private static func uniqueName(_ base: String, modelContext: ModelContext) -> String {
        let existing = (try? modelContext.fetch(FetchDescriptor<LattesProfile>()))?
            .map(\.name) ?? []
        guard existing.contains(base) else { return base }
        var n = 2
        while existing.contains("\(base) (\(n))") { n += 1 }
        return "\(base) (\(n))"
    }

    private static func findManifest(in root: URL) -> URL? {
        let fm = FileManager.default
        let direct = root.appendingPathComponent("manifest.json")
        if fm.fileExists(atPath: direct.path) { return direct }
        // ditto costuma preservar a pasta de origem como raiz do zip — procura 1 nível abaixo.
        guard let items = try? fm.contentsOfDirectory(at: root, includingPropertiesForKeys: nil) else { return nil }
        for item in items {
            let candidate = item.appendingPathComponent("manifest.json")
            if fm.fileExists(atPath: candidate.path) { return candidate }
        }
        return nil
    }

    private static func zip(folder: URL, to destination: URL) throws {
        let proc = Process()
        proc.executableURL = URL(fileURLWithPath: "/usr/bin/ditto")
        proc.arguments = ["-c", "-k", "--sequesterRsrc", folder.path, destination.path]
        try proc.run()
        proc.waitUntilExit()
        guard proc.terminationStatus == 0 else { throw ArchiveError.zipFailed }
    }

    private static func unzip(_ zipURL: URL, to destination: URL) throws {
        let proc = Process()
        proc.executableURL = URL(fileURLWithPath: "/usr/bin/ditto")
        proc.arguments = ["-x", "-k", zipURL.path, destination.path]
        try proc.run()
        proc.waitUntilExit()
        guard proc.terminationStatus == 0 else { throw ArchiveError.unzipFailed }
    }
}
