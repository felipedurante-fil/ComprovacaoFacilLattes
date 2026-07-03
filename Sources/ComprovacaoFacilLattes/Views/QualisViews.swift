import SwiftUI

/// Selo de classificação Qualis exibido em entradas de artigo.
struct QualisBadge: View {
    @ObservedObject private var qualis = QualisService.shared
    let entry: LattesEntry

    var body: some View {
        if entry.kind == "Artigo" {
            // observa o carregamento das tabelas
            let _ = qualis.version
            if let r = qualis.classify(journal: entry.venue, issn: entry.issn, year: entry.year) {
                Text("Qualis \(r.estrato)")
                    .font(.caption2).bold()
                    .padding(.horizontal, 6).padding(.vertical, 1)
                    .background(Self.color(r.estrato).opacity(0.18), in: Capsule())
                    .foregroundStyle(Self.color(r.estrato))
                    .help("\(r.estrato) em \(r.area) (quadriênio \(r.quadriennium))")
                    .fixedSize()
            } else if qualis.isLoading {
                Text("Qualis…")
                    .font(.caption2).foregroundStyle(.tertiary).fixedSize()
            }
        }
    }

    static func color(_ estrato: String) -> Color {
        switch estrato.uppercased().prefix(2) {
        case "A1", "A2": return .green
        case "A3", "A4": return .teal
        case "B1", "B2": return .blue
        case "B3", "B4": return .orange
        default:         return .secondary
        }
    }
}

/// Botão que abre um seletor de área de avaliação (Qualis) com busca.
struct QualisAreaMenu: View {
    @ObservedObject private var qualis = QualisService.shared
    @State private var showPicker = false
    @State private var search = ""

    var body: some View {
        Button {
            showPicker = true
        } label: {
            Label("Qualis: \(shortArea)", systemImage: "rosette")
        }
        .help("Área de avaliação usada para classificar o Qualis dos artigos")
        .popover(isPresented: $showPicker, arrowEdge: .bottom) {
            VStack(spacing: 0) {
                HStack(spacing: 6) {
                    Image(systemName: "magnifyingglass").foregroundStyle(.secondary)
                    TextField("Buscar área (ex.: filosofia)…", text: $search)
                        .textFieldStyle(.roundedBorder)
                }
                .padding(8)
                Divider()

                if qualis.allAreas.isEmpty {
                    Text("Carregando áreas…").foregroundStyle(.secondary).padding(20)
                } else {
                    ScrollView {
                        VStack(alignment: .leading, spacing: 0) {
                            ForEach(filtered, id: \.self) { area in
                                Button {
                                    qualis.area = area
                                    showPicker = false
                                } label: {
                                    HStack(spacing: 6) {
                                        Image(systemName: area == qualis.area ? "checkmark.circle.fill" : "circle")
                                            .foregroundStyle(area == qualis.area ? .green : .secondary)
                                            .font(.caption)
                                        Text(area).font(.system(size: 12)).lineLimit(1)
                                        Spacer()
                                    }
                                    .padding(.vertical, 5).padding(.horizontal, 8)
                                    .frame(maxWidth: .infinity, alignment: .leading)
                                    .background(area == qualis.area ? Color.accentColor.opacity(0.15) : .clear)
                                    .contentShape(Rectangle())
                                }
                                .buttonStyle(.plain)
                                Divider()
                            }
                            if filtered.isEmpty {
                                Text("Nenhuma área encontrada").font(.caption)
                                    .foregroundStyle(.secondary).padding(12)
                            }
                        }
                    }
                    .frame(width: 380, height: 340)
                }
            }
            .frame(width: 380)
        }
    }

    private var filtered: [String] {
        guard !search.isEmpty else { return qualis.allAreas }
        let s = fold(search)
        return qualis.allAreas.filter { fold($0).contains(s) }
    }

    private func fold(_ s: String) -> String {
        s.folding(options: [.diacriticInsensitive, .caseInsensitive], locale: .init(identifier: "pt_BR")).lowercased()
    }

    private var shortArea: String {
        let a = qualis.area
        return a.count > 22 ? String(a.prefix(20)) + "…" : a
    }
}
