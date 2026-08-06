import SwiftUI

/// Exporta o currículo (seções, entradas, status, comprovantes) como um único
/// .zip — para backup ou para abrir este mesmo perfil em outro computador.
struct ExportProfileSheet: View {
    @Environment(\.dismiss) private var dismiss
    let profile: LattesProfile

    @State private var includeFiles = true
    @State private var isExporting = false
    @State private var errorMessage: String?

    var body: some View {
        VStack(alignment: .leading, spacing: 16) {
            Text("Exportar Comprovação")
                .font(.title2).bold()
            Text("Gera um único arquivo .zip com o currículo \"\(profile.name)\", suas entradas e o status de cada comprovante — para guardar de backup ou abrir neste app em outro computador.")
                .font(.caption)
                .foregroundStyle(.secondary)

            VStack(alignment: .leading, spacing: 6) {
                Toggle("Incluir os arquivos de comprovante no pacote", isOn: $includeFiles)
                Text(includeFiles
                     ? "Pacote autocontido — funciona em qualquer computador, mesmo sem os arquivos originais. Pode ficar grande."
                     : "Só a base de dados — bem menor, mas os comprovantes só aparecerão no destino se os arquivos já estiverem acessíveis nos mesmos caminhos originais.")
                    .font(.caption2)
                    .foregroundStyle(.secondary)
            }

            if let errorMessage {
                Text(errorMessage).font(.caption).foregroundStyle(.red)
            }

            Spacer()

            HStack {
                Spacer()
                Button("Cancelar") { dismiss() }
                Button {
                    export()
                } label: {
                    if isExporting {
                        HStack { ProgressView().controlSize(.small); Text("Exportando…") }
                    } else {
                        Label("Exportar…", systemImage: "square.and.arrow.up")
                    }
                }
                .buttonStyle(.borderedProminent)
                .disabled(isExporting)
            }
        }
        .padding(24)
        .frame(width: 460, height: 260)
    }

    private func export() {
        isExporting = true
        errorMessage = nil

        // Fica no ator principal (como o restante do app faz ao ler os modelos
        // SwiftData) — só a cópia de arquivos pode demorar em coleções grandes.
        Task {
            do {
                let zipURL = try ProfileArchiver.export(profile: profile, includeFiles: includeFiles)
                isExporting = false
                let panel = NSSavePanel()
                panel.allowedContentTypes = [.zip]
                let safeName = profile.name.replacingOccurrences(of: " ", with: "_")
                panel.nameFieldStringValue = "Comprovacao_\(safeName).zip"
                if panel.runModal() == .OK, let dest = panel.url {
                    do {
                        if FileManager.default.fileExists(atPath: dest.path) {
                            try FileManager.default.removeItem(at: dest)
                        }
                        try FileManager.default.moveItem(at: zipURL, to: dest)
                        NSWorkspace.shared.activateFileViewerSelecting([dest])
                        dismiss()
                    } catch {
                        errorMessage = "Não foi possível salvar: \(error.localizedDescription)"
                    }
                } else {
                    try? FileManager.default.removeItem(at: zipURL)
                }
            } catch {
                isExporting = false
                errorMessage = "Não foi possível exportar: \(error.localizedDescription)"
            }
        }
    }
}
