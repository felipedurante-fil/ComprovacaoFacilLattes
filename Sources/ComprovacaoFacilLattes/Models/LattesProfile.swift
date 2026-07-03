import Foundation
import SwiftData

@Model
final class LattesProfile {
    @Attribute(.unique) var id: UUID
    var name: String
    var pdfPath: String
    var importDate: Date
    var lastUpdated: Date
    var savePath: String
    var rawText: String

    /// Aprendizado: vínculos recusados pelo usuário ("nomeArquivo||hashEntrada").
    /// Em novos escaneamentos, essas combinações são evitadas.
    var rejectedLinks: [String] = []

    @Relationship(deleteRule: .cascade, inverse: \LattesSection.profile)
    var sections: [LattesSection]

    // Todos os certificados do perfil (assigned + limbo)
    @Relationship(deleteRule: .cascade, inverse: \Certificate.profile)
    var certificates: [Certificate]

    init(name: String, pdfPath: String, savePath: String) {
        self.id = UUID()
        self.name = name
        self.pdfPath = pdfPath
        self.savePath = savePath
        self.rawText = ""
        self.importDate = Date()
        self.lastUpdated = Date()
        self.sections = []
        self.certificates = []
    }

    var sortedSections: [LattesSection] {
        sections.sorted { $0.order < $1.order }
    }

    var totalEntries: Int {
        sections.reduce(0) { $0 + $1.entries.count }
    }

    var confirmedCount: Int {
        sections.reduce(0) { acc, s in
            acc + s.entries.filter { $0.certificateStatus == .confirmed }.count
        }
    }

    var suggestedCount: Int {
        sections.reduce(0) { acc, s in
            acc + s.entries.filter { $0.certificateStatus == .suggested }.count
        }
    }

    var pendingCount: Int {
        sections.reduce(0) { acc, s in
            acc + s.entries.filter { $0.certificateStatus == .none }.count
        }
    }

    var limboCertificates: [Certificate] {
        certificates.filter { $0.entry == nil && !$0.isRejected }
    }
}
