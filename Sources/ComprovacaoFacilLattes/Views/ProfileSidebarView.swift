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

    var body: some View {
        List(selection: $selectedProfile) {
            ForEach(profiles) { profile in
                ProfileRow(profile: profile)
                    .tag(profile)
                    .contextMenu {
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
                Button {
                    showNewProfile = true
                } label: {
                    Image(systemName: "plus")
                }
                .help("Importar novo currículo Lattes")
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
