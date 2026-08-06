import SwiftUI
import SwiftData

struct ProfileSidebarView: View {
    @Environment(\.modelContext) private var modelContext
    @Query(sort: \LattesProfile.name) private var profiles: [LattesProfile]

    @Binding var selectedProfile: LattesProfile?
    @Binding var selectedEntry: LattesEntry?

    @State private var showNewProfile = false
    @State private var profileToDelete: LattesProfile?
    @State private var showDeleteConfirm = false
    @State private var profileToExport: LattesProfile?
    @State private var importError: String?

    var body: some View {
        List(selection: $selectedProfile) {
            ForEach(profiles) { profile in
                ProfileRow(profile: profile)
                    .tag(profile)
                    .contextMenu {
                        Button("Exportar comprovação…") {
                            profileToExport = profile
                        }
                        Divider()
                        Button("Excluir currículo", role: .destructive) {
                            profileToDelete = profile
                            showDeleteConfirm = true
                        }
                    }
            }
        }
        .navigationTitle("Comprovação Fácil do Lattes")
        .toolbar {
            ToolbarItem(placement: .primaryAction) {
                Menu {
                    Button("Importar novo currículo Lattes…") {
                        showNewProfile = true
                    }
                    Button("Importar arquivo de comprovação…") {
                        importArchive()
                    }
                } label: {
                    Image(systemName: "plus")
                }
                .help("Importar currículo ou restaurar backup")
            }
        }
        .sheet(isPresented: $showNewProfile) {
            NewProfileSheet { profile in
                selectedEntry = nil
                selectedProfile = profile
                showNewProfile = false
            } onCancel: {
                showNewProfile = false
            }
        }
        .sheet(item: $profileToExport) { profile in
            ExportProfileSheet(profile: profile)
        }
        .alert("Importar comprovação", isPresented: .init(
            get: { importError != nil }, set: { if !$0 { importError = nil } }
        )) {
            Button("OK", role: .cancel) { importError = nil }
        } message: {
            Text(importError ?? "")
        }
        .confirmationDialog(
            "Excluir currículo?",
            isPresented: $showDeleteConfirm,
            presenting: profileToDelete
        ) { profile in
            Button("Excluir \"\(profile.name)\"", role: .destructive) {
                if selectedProfile == profile {
                    selectedProfile = nil
                    selectedEntry = nil
                }
                modelContext.delete(profile)
            }
            Button("Cancelar", role: .cancel) { }
        } message: { profile in
            Text("Todos os vínculos de comprovantes de \"\(profile.name)\" serão removidos permanentemente.")
        }
    }

    private func importArchive() {
        let panel = NSOpenPanel()
        panel.allowedContentTypes = [.zip]
        panel.canChooseFiles = true
        panel.canChooseDirectories = false
        panel.allowsMultipleSelection = false
        panel.message = "Selecione o arquivo .zip exportado pelo app"
        panel.prompt = "Importar"
        guard panel.runModal() == .OK, let url = panel.urls.first else { return }

        do {
            let profile = try ProfileArchiver.importProfile(from: url, modelContext: modelContext)
            selectedEntry = nil
            selectedProfile = profile
        } catch {
            importError = error.localizedDescription
        }
    }
}

private struct ProfileRow: View {
    let profile: LattesProfile

    var body: some View {
        VStack(alignment: .leading, spacing: 4) {
            Text(profile.name).font(.headline).lineLimit(1)
            HStack(spacing: 6) {
                StatusDot(color: .green)
                Text("\(profile.confirmedCount)").font(.caption2)
                StatusDot(color: .yellow)
                Text("\(profile.suggestedCount)").font(.caption2)
                StatusDot(color: .red)
                Text("\(profile.pendingCount)").font(.caption2)
                Spacer()
                Text(profile.lastUpdated, style: .date)
                    .font(.caption2).foregroundStyle(.secondary)
            }
        }
        .padding(.vertical, 2)
    }
}

struct StatusDot: View {
    let color: Color
    var body: some View {
        Circle().fill(color).frame(width: 8, height: 8)
    }
}
