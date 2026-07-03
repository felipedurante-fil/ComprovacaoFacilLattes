import SwiftUI

struct GenerateReportSheet: View {
    let profile: LattesProfile
    var onClose: () -> Void = {}

    // Período
    @State private var useFullPeriod = true
    @State private var startYear: Int = Calendar.current.component(.year, from: Date()) - 5
    @State private var endYear: Int   = Calendar.current.component(.year, from: Date())

    // Seleção de seções
    @State private var selectedSections: Set<String> = []
    @State private var selectAll = true

    @State private var includeLattes = true
    @State private var isGenerating = false
    @State private var errorMessage: String?

    private var sectionTitles: [String] {
        profile.sortedSections.map { $0.title }
    }

    var body: some View {
        VStack(alignment: .leading, spacing: 0) {
            HStack {
                Text("Gerar arquivo com comprovantes").font(.title2).bold()
                Spacer()
                Button("Cancelar") { onClose() }
            }
            .padding()

            Divider()

            ScrollView {
                VStack(alignment: .leading, spacing: 20) {
                    // Período
                    GroupBox("Período") {
                        VStack(alignment: .leading, spacing: 10) {
                            Toggle("Período completo", isOn: $useFullPeriod)
                            if !useFullPeriod {
                                HStack {
                                    Text("De:")
                                    TextField("Ano", value: $startYear, format: .number.grouping(.never))
                                        .frame(width: 70).textFieldStyle(.roundedBorder)
                                    Text("Até:")
                                    TextField("Ano", value: $endYear, format: .number.grouping(.never))
                                        .frame(width: 70).textFieldStyle(.roundedBorder)
                                }
                            }
                        }
                        .padding(4)
                    }

                    // Seções
                    GroupBox("Seções a incluir") {
                        VStack(alignment: .leading, spacing: 6) {
                            Toggle("Selecionar todas", isOn: $selectAll)
                                .onChange(of: selectAll) { _, newVal in
                                    selectedSections = newVal ? Set(sectionTitles) : []
                                }
                                .font(.subheadline).bold()
                            Divider()
                            LazyVGrid(columns: [GridItem(.flexible(), alignment: .leading),
                                                GridItem(.flexible(), alignment: .leading)], spacing: 6) {
                                ForEach(sectionTitles, id: \.self) { title in
                                    Toggle(title, isOn: Binding(
                                        get: { selectedSections.contains(title) },
                                        set: { val in
                                            if val { selectedSections.insert(title) }
                                            else { selectedSections.remove(title); selectAll = false }
                                        }
                                    ))
                                    .toggleStyle(.checkbox)
                                    .font(.caption)
                                    .lineLimit(2)
                                }
                            }
                        }
                        .padding(4)
                    }

                    GroupBox("Opções") {
                        Toggle("Iniciar com o Currículo Lattes completo", isOn: $includeLattes)
                            .padding(4)
                    }

                    GroupBox("Resumo") {
                        HStack(spacing: 20) {
                            Label("\(totalConfirmedEntries) entradas com comprovantes",
                                  systemImage: "checkmark.circle.fill")
                                .foregroundStyle(.green).font(.caption)
                            Spacer()
                        }
                        .padding(4)
                    }

                    if let error = errorMessage {
                        Text(error).foregroundStyle(.red).font(.caption)
                    }
                }
                .padding()
            }
            .frame(maxWidth: .infinity, maxHeight: .infinity)

            Divider()

            HStack {
                Spacer()
                Button {
                    generate()
                } label: {
                    if isGenerating {
                        HStack { ProgressView().controlSize(.small); Text("Gerando…") }
                    } else {
                        Label("Gerar PDF", systemImage: "doc.richtext.fill")
                    }
                }
                .buttonStyle(.borderedProminent)
                .disabled(isGenerating || totalConfirmedEntries == 0)
            }
            .padding()
        }
        .frame(minWidth: 560, idealWidth: 720, maxWidth: .infinity,
               minHeight: 520, idealHeight: 760, maxHeight: .infinity)
        .onAppear { selectedSections = Set(sectionTitles) }
    }

    private var totalConfirmedEntries: Int {
        let sections = selectAll || selectedSections.isEmpty
            ? profile.sortedSections
            : profile.sortedSections.filter { selectedSections.contains($0.title) }
        return sections.reduce(0) { acc, s in
            acc + s.entries.filter { !$0.confirmedCertificates.isEmpty }.count
        }
    }

    private func generate() {
        isGenerating = true
        errorMessage = nil

        // Pré-calcula o Qualis dos artigos (QualisService é @MainActor)
        var qualisMap: [UUID: String] = [:]
        for section in profile.sortedSections {
            for entry in section.sortedEntries where entry.kind == "Artigo" {
                if let r = QualisService.shared.classify(journal: entry.venue, issn: entry.issn, year: entry.year) {
                    qualisMap[entry.id] = r.estrato
                }
            }
        }

        Task {
            let config = PDFReportGenerator.ReportConfig(
                profile: profile,
                selectedSectionTitles: selectAll ? [] : selectedSections,
                startYear: useFullPeriod ? nil : startYear,
                endYear:   useFullPeriod ? nil : endYear,
                includeLattes: includeLattes,
                qualisByEntry: qualisMap
            )

            guard let data = PDFReportGenerator.generate(config: config) else {
                await MainActor.run {
                    errorMessage = "Erro ao gerar o PDF. Verifique se os arquivos de comprovante ainda existem."
                    isGenerating = false
                }
                return
            }

            await MainActor.run {
                let panel = NSSavePanel()
                panel.allowedContentTypes = [.pdf]
                let name = profile.name.replacingOccurrences(of: " ", with: "_")
                panel.nameFieldStringValue = "Comprovantes_\(name).pdf"
                panel.directoryURL = URL(fileURLWithPath: profile.savePath)

                if panel.runModal() == .OK, let url = panel.url {
                    do {
                        try data.write(to: url)
                        NSWorkspace.shared.open(url)
                        onClose()
                    } catch {
                        errorMessage = "Não foi possível salvar o PDF: \(error.localizedDescription)"
                    }
                }
                isGenerating = false
            }
        }
    }
}
