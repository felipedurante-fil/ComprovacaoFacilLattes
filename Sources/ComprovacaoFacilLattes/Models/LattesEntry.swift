import Foundation
import SwiftData

enum EntryStatus: String, Codable {
    case none      = "none"
    case suggested = "suggested"
    case confirmed = "confirmed"
}

@Model
final class LattesEntry {
    @Attribute(.unique) var id: UUID
    var rawText: String
    var title: String
    var kind: String = ""
    var year: Int
    var authors: String
    var venue: String
    var doi: String
    var isbn: String
    var portaria: String = ""
    var issn: String = ""
    var edital: String = ""
    var endYear: Int = 0        // ano final do período (vínculos/atividades); 0 = aberto/sem
    var hashKey: String
    var order: Int
    var certificateStatus: EntryStatus

    var section: LattesSection?

    // Nullify (não cascade): ao deletar entry, certificados voltam ao limbo
    @Relationship(inverse: \Certificate.entry)
    var certificates: [Certificate]

    init(rawText: String, title: String, kind: String = "", year: Int = 0,
         authors: String = "", venue: String = "", order: Int = 0) {
        self.id = UUID()
        self.rawText = rawText
        self.title = title
        self.kind = kind
        self.year = year
        self.authors = authors
        self.venue = venue
        self.doi = ""
        self.isbn = ""
        self.order = order
        self.certificateStatus = .none
        self.certificates = []
        self.hashKey = Self.makeHash(year: year, title: title)
    }

    private static func makeHash(year: Int, title: String) -> String {
        let clean = title.lowercased()
            .trimmingCharacters(in: .whitespacesAndNewlines)
            .prefix(60)
        return "\(year)_\(clean)"
    }

    var yearString: String { year > 0 ? String(year) : "" }

    /// Todos os certificados da entrada, na ordem definida pelo usuário.
    var sortedCertificates: [Certificate] {
        certificates.sorted {
            $0.order != $1.order ? $0.order < $1.order : $0.importDate < $1.importDate
        }
    }

    var confirmedCertificates: [Certificate] {
        sortedCertificates.filter { $0.isConfirmed }
    }

    /// Próxima posição livre (para anexar um novo comprovante ao final).
    func nextCertificateOrder() -> Int {
        (certificates.map { $0.order }.max() ?? -1) + 1
    }

    /// Título descritivo que identifica a entrada na interface e nos relatórios.
    /// Ex.: "Artigo — Título — Revista (2024)" ou "Banca — Fulano (2023)".
    var displayTitle: String {
        let core = (title.isEmpty ? rawText : title)
            .trimmingCharacters(in: .whitespacesAndNewlines)
            .replacingOccurrences(of: "\n", with: " ")
        let shortCore = core.count > 150 ? String(core.prefix(150)) + "…" : core

        var parts: [String] = []
        if !kind.isEmpty { parts.append(kind) }
        if !shortCore.isEmpty { parts.append(shortCore) }
        if !venue.isEmpty,
           !shortCore.localizedCaseInsensitiveContains(venue),
           venue.count < 80 {
            parts.append(venue)
        }
        var result = parts.joined(separator: " — ")
        if year > 0 { result += " (\(year))" }
        return result
    }
}
