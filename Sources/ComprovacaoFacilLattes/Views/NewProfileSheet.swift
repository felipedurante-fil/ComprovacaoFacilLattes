import SwiftUI
import SwiftData

struct NewProfileSheet: View {
    @Environment(\.modelContext) private var modelContext

    let onCreated: (LattesProfile) -> Void
    let onCancel: () -> Void

    @State private var selectedPDFURL: URL?
    @State private var profileName = ""
    @State private var savePathOverride = ""
    @State private var isImporting = false
    @State private var importError: String?

    var canImport: Bool { selectedPDFURL != nil && !profileName.isEmpty }

    var effectiveSavePath: String {
        if !savePathOverride.isEmpty { return savePathOverride }
        return profileName.isEmpty ? baseSavePath : "\(baseSavePath)/\(profileName)"
    }

    var baseSavePath: String {
        (FileManager.default.urls(for: .documentDirectory, in: .userDomainMask).first?
            .appendingPathComponent("ComprovantesLattes").path)
            ?? "~/Documents/ComprovantesLattes"
    }

    var body: some View {
        VStack(alignment: .leading, spacing: 0) {

            // Cabeçalho
            HStack {
                Text("Importar Currículo Lattes").font(.title2).bold()
                Spacer()
                Button("Cancelar") { onCancel() }
            }
            .padding()

            Divider()

            VStack(alignment: .leading, spacing: 22) {

                // Arquivo
                FieldGroup(label: "Arquivo do Lattes") {
                    HStack(spacing: 10) {
                        if let url = selectedPDFURL {
                            Image(systemName: "doc.fill").foregroundStyle(.red)
                            Text(url.lastPathComponent)
                                .lineLimit(1).truncationMode(.middle)
                        } else {
                            Image(systemName: "doc").foregroundStyle(.secondary)
                            Text("Nenhum arquivo selecionado").foregroundStyle(.secondary)
                        }
                        Spacer()
                        Button("Selecionar PDF…") { pickPDF() }.buttonStyle(.bordered)
                    }
                    Text("Exporte em lattes.cnpq.br → Exportar → Formato PDF.")
                        .font(.caption).foregroundStyle(.secondary)
                }

                // Nome — botão limpar sempre presente (opacity controla visibilidade)
                FieldGroup(label: "Nome do perfil") {
                    HStack(spacing: 6) {
                        TextField("Nome do pesquisador", text: $profileName)
                            .textFieldStyle(.roundedBorder)
                        Button {
                            profileName = ""
                        } label: {
                            Image(systemName: "xmark.circle.fill")
                                .foregroundStyle(.secondary)
                        }
                        .buttonStyle(.plain)
                        .help("Limpar")
                        .opacity(profileName.isEmpty ? 0 : 1)
                        .disabled(profileName.isEmpty)
                    }
                }

                // Pasta de armazenamento
                FieldGroup(label: "Pasta de armazenamento") {
                    HStack(spacing: 8) {
                        VStack(alignment: .leading, spacing: 2) {
                            Text(effectiveSavePath)
                                .font(.caption)
                                .foregroundStyle(savePathOverride.isEmpty ? .secondary : .primary)
                                .lineLimit(2).truncationMode(.head)
                            Text("Criada automaticamente ao importar.")
                                .font(.caption2).foregroundStyle(.tertiary)
                        }
                        Spacer()
                        Button("Alterar…") { pickSavePath() }.buttonStyle(.bordered)
                        Button {
                            savePathOverride = ""
                        } label: {
                            Image(systemName: "arrow.uturn.backward.circle")
                                .foregroundStyle(.secondary)
                        }
                        .buttonStyle(.plain)
                        .help("Usar caminho padrão")
                        .opacity(savePathOverride.isEmpty ? 0 : 1)
                        .disabled(savePathOverride.isEmpty)
                    }
                    .padding(8)
                    .background(Color(nsColor: .controlBackgroundColor),
                                in: RoundedRectangle(cornerRadius: 6))
                }

                if let error = importError {
                    Label(error, systemImage: "exclamationmark.triangle")
                        .foregroundStyle(.red).font(.caption)
                }
            }
            .padding()

            Spacer()
            Divider()

            HStack {
                Spacer()
                if isImporting {
                    ProgressView().controlSize(.small)
                    Text("Analisando…").font(.caption).foregroundStyle(.secondary).padding(.trailing, 4)
                }
                Button("Importar e Analisar") { importProfile() }
                    .buttonStyle(.borderedProminent)
                    .disabled(!canImport || isImporting)
                    .keyboardShortcut(.defaultAction)
            }
            .padding()
        }
        .frame(width: 520, height: 440)
    }

    // MARK: - Pickers

    private func pickPDF() {
        let panel = NSOpenPanel()
        panel.allowedContentTypes = [.pdf]
        panel.canChooseFiles = true
        panel.canChooseDirectories = false
        panel.allowsMultipleSelection = false
        panel.message = "Selecione o PDF exportado do currículo Lattes"
        panel.prompt = "Selecionar"
        guard panel.runModal() == .OK, let url = panel.url else { return }
        selectedPDFURL = url
        profileName = cleanName(from: url)
        savePathOverride = ""
    }

    private func pickSavePath() {
        let panel = NSOpenPanel()
        panel.canChooseDirectories = true
        panel.canChooseFiles = false
        panel.allowsMultipleSelection = false
        panel.canCreateDirectories = true
        panel.message = "Selecione a pasta de armazenamento"
        panel.prompt = "Selecionar"
        guard panel.runModal() == .OK, let url = panel.url else { return }
        savePathOverride = url.path
    }

    // MARK: - Nome sugerido

    private func cleanName(from url: URL) -> String {
        var name = url.deletingPathExtension().lastPathComponent
            .replacingOccurrences(of: "_", with: " ")
            .replacingOccurrences(of: "-", with: " ")
            .trimmingCharacters(in: .whitespaces)

        // Remove prefixos típicos do nome de arquivo exportado
        let prefixes = ["curriculo lattes ", "curriculo ", "lattes "]
        let lower = name.lowercased()
        for prefix in prefixes where lower.hasPrefix(prefix) {
            name = String(name.dropFirst(prefix.count))
                .trimmingCharacters(in: .whitespaces)
            break
        }

        // Remove tokens que são apenas dígitos (anos) e capitaliza
        return name
            .split(separator: " ")
            .filter { !$0.allSatisfy(\.isNumber) }
            .map { $0.capitalized }
            .joined(separator: " ")
    }

    // MARK: - Importação

    private func importProfile() {
        guard let pdfURL = selectedPDFURL else { return }
        isImporting = true
        importError = nil

        Task.detached(priority: .userInitiated) {
            let parsed = LattesPDFParser.parse(url: pdfURL)

            await MainActor.run {
                guard let result = parsed else {
                    importError = "Não foi possível ler o PDF. Confirme que é um PDF do Lattes exportado corretamente."
                    isImporting = false
                    return
                }

                let finalName = profileName.isEmpty ? result.profileName : profileName
                let finalSavePath = savePathOverride.isEmpty
                    ? "\(baseSavePath)/\(finalName)"
                    : savePathOverride

                try? FileManager.default.createDirectory(
                    atPath: finalSavePath, withIntermediateDirectories: true)

                let profile = LattesProfile(name: finalName, pdfPath: pdfURL.path, savePath: finalSavePath)
                profile.rawText = result.rawText
                modelContext.insert(profile)

                for (idx, sec) in result.sections.enumerated() {
                    let section = LattesSection(title: sec.title, order: idx)
                    section.profile = profile
                    modelContext.insert(section)
                    for e in sec.entries {
                        let entry = LattesEntry(
                            rawText: e.rawText, title: e.title, kind: e.kind,
                            year: e.year, authors: e.authors,
                            venue: e.venue, order: e.order)
                        entry.doi = e.doi
                        entry.isbn = e.isbn
                        entry.portaria = e.portaria
                        entry.issn = e.issn
                        entry.edital = e.edital
                        entry.endYear = e.endYear
                        entry.section = section
                        modelContext.insert(entry)
                    }
                }

                try? modelContext.save()
                isImporting = false
                onCreated(profile)
            }
        }
    }
}

// MARK: - Reusable section wrapper

private struct FieldGroup<Content: View>: View {
    let label: String
    @ViewBuilder let content: () -> Content

    var body: some View {
        VStack(alignment: .leading, spacing: 6) {
            Text(label).font(.subheadline).fontWeight(.medium).foregroundStyle(.secondary)
            content()
        }
    }
}
