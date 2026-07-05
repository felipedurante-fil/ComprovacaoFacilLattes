import SwiftUI

/// Permite anexar documentos que não correspondem a nenhuma entrada do currículo
/// Lattes (ex.: certificados avulsos). Ficam numa seção manual, "Outros Documentos",
/// que sempre aparece por último e nunca é apagada pelo "Atualizar Lattes".
struct AddOtherDocumentSheet: View {
    @Environment(\.modelContext) private var modelContext
    @Environment(\.dismiss) private var dismiss
    let profile: LattesProfile

    @State private var title = ""
    @State private var fileURL: URL?

    static let sectionTitle = "Outros Documentos"

    var body: some View {
        VStack(alignment: .leading, spacing: 16) {
            Text("Adicionar Documento")
                .font(.title2).bold()
            Text("Para guardar comprovantes que não correspondem a nenhuma entrada do currículo Lattes.")
                .font(.caption)
                .foregroundStyle(.secondary)

            TextField("Título ou descrição", text: $title)
                .textFieldStyle(.roundedBorder)

            HStack(spacing: 8) {
                if let fileURL {
                    Image(systemName: fileURL.pathExtension.lowercased() == "pdf" ? "doc.fill" : "photo.fill")
                        .foregroundStyle(fileURL.pathExtension.lowercased() == "pdf" ? .red : .blue)
                    Text(fileURL.lastPathComponent)
                        .lineLimit(1).truncationMode(.middle)
                } else {
                    Text("Nenhum arquivo selecionado")
                        .foregroundStyle(.secondary)
                }
                Spacer()
                Button("Escolher arquivo…") { pickFile() }
            }

            Spacer()

            HStack {
                Spacer()
                Button("Cancelar") { dismiss() }
                Button("Adicionar") { addDocument() }
                    .buttonStyle(.borderedProminent)
                    .disabled(title.trimmingCharacters(in: .whitespaces).isEmpty || fileURL == nil)
            }
        }
        .padding(24)
        .frame(width: 420, height: 260)
    }

    private func pickFile() {
        let panel = NSOpenPanel()
        panel.allowedContentTypes = [.pdf, .jpeg, .png, .tiff]
        panel.canChooseFiles = true
        panel.canChooseDirectories = false
        panel.allowsMultipleSelection = false
        panel.message = "Selecione o arquivo do documento"
        panel.prompt = "Selecionar"
        guard panel.runModal() == .OK, let url = panel.urls.first else { return }
        fileURL = url
    }

    private func addDocument() {
        guard let fileURL else { return }
        let trimmedTitle = title.trimmingCharacters(in: .whitespacesAndNewlines)
        guard !trimmedTitle.isEmpty else { return }

        let section = existingOrNewSection()
        let entry = LattesEntry(
            rawText: trimmedTitle, title: trimmedTitle, kind: "Documento",
            order: (section.entries.map(\.order).max() ?? -1) + 1)
        entry.section = section
        modelContext.insert(entry)

        let cert = Certificate(filePath: fileURL.path)
        cert.order = 0
        cert.entry = entry
        cert.profile = profile
        cert.isConfirmed = true
        modelContext.insert(cert)
        entry.certificateStatus = .confirmed

        try? modelContext.save()
        dismiss()
    }

    /// A seção sempre ordena por último (order = Int.max), mesmo com o número de
    /// seções do currículo mudando entre atualizações.
    private func existingOrNewSection() -> LattesSection {
        if let existing = profile.sections.first(where: { $0.title == Self.sectionTitle }) {
            return existing
        }
        let section = LattesSection(title: Self.sectionTitle, order: Int.max)
        section.profile = profile
        modelContext.insert(section)
        return section
    }
}
