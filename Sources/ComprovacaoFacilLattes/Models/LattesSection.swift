import Foundation
import SwiftData

@Model
final class LattesSection {
    @Attribute(.unique) var id: UUID
    var title: String
    var order: Int

    var profile: LattesProfile?

    @Relationship(deleteRule: .cascade, inverse: \LattesEntry.section)
    var entries: [LattesEntry]

    init(title: String, order: Int) {
        self.id = UUID()
        self.title = title
        self.order = order
        self.entries = []
    }

    var sortedEntries: [LattesEntry] {
        entries.sorted { $0.order < $1.order }
    }

    var confirmedCount: Int { entries.filter { $0.certificateStatus == .confirmed }.count }
    var suggestedCount: Int { entries.filter { $0.certificateStatus == .suggested }.count }
    var pendingCount: Int   { entries.filter { $0.certificateStatus == .none }.count }
}
