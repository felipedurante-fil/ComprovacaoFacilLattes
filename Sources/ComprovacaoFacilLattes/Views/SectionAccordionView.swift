import SwiftUI

struct SectionAccordionView: View {
    let section: LattesSection
    let isExpanded: Bool
    var onlyPending: Bool = false
    @Binding var selectedEntry: LattesEntry?
    let onToggle: () -> Void

    /// Entradas exibidas — quando filtrando pendências, só as não confirmadas.
    private var visibleEntries: [LattesEntry] {
        onlyPending
            ? section.sortedEntries.filter { $0.certificateStatus != .confirmed }
            : section.sortedEntries
    }

    var body: some View {
        VStack(alignment: .leading, spacing: 0) {
            // Cabeçalho da seção
            Button(action: onToggle) {
                HStack(spacing: 10) {
                    Image(systemName: isExpanded ? "chevron.down" : "chevron.right")
                        .font(.system(size: 11, weight: .semibold))
                        .foregroundStyle(.secondary)
                        .frame(width: 14)

                    Text(section.title)
                        .font(.subheadline)
                        .fontWeight(.semibold)
                        .foregroundStyle(.primary)

                    Spacer()

                    // Mini semáforo da seção
                    sectionStatusPills

                    // Cobertura confirmados / total
                    Text("\(section.confirmedCount)/\(section.entries.count)")
                        .font(.caption)
                        .foregroundStyle(.secondary)
                        .monospacedDigit()
                }
                .padding(.horizontal, 16)
                .padding(.vertical, 10)
                .contentShape(Rectangle())
            }
            .buttonStyle(.plain)
            .background(Color(nsColor: .controlBackgroundColor))

            // Entradas (acordeão)
            if isExpanded {
                VStack(spacing: 0) {
                    ForEach(visibleEntries) { entry in
                        EntryRowView(
                            entry: entry,
                            isSelected: selectedEntry?.id == entry.id
                        ) {
                            selectedEntry = entry
                        }
                        if entry.id != visibleEntries.last?.id {
                            Divider().padding(.leading, 44)
                        }
                    }
                }
                .background(Color(nsColor: .textBackgroundColor))
            }
        }
    }

    private var sectionStatusPills: some View {
        HStack(spacing: 4) {
            if section.confirmedCount > 0 {
                StatusPill(count: section.confirmedCount, color: .green)
            }
            if section.suggestedCount > 0 {
                StatusPill(count: section.suggestedCount, color: .yellow)
            }
            if section.pendingCount > 0 {
                StatusPill(count: section.pendingCount, color: .red)
            }
        }
    }
}

private struct StatusPill: View {
    let count: Int
    let color: Color

    var body: some View {
        Text("\(count)")
            .font(.system(size: 10, weight: .medium))
            .foregroundStyle(.white)
            .padding(.horizontal, 5)
            .padding(.vertical, 2)
            .background(color, in: Capsule())
    }
}
