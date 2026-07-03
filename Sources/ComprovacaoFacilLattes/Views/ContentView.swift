import SwiftUI
import SwiftData

struct ContentView: View {
    @State private var selectedProfile: LattesProfile?
    @State private var selectedEntry: LattesEntry?

    var body: some View {
        NavigationSplitView(columnVisibility: .constant(.all)) {
            // Coluna 1 — a menor: lista de currículos
            ProfileSidebarView(selectedProfile: $selectedProfile, selectedEntry: $selectedEntry)
                .navigationSplitViewColumnWidth(min: 180, ideal: 210, max: 250)
        } content: {
            // Coluna 2 — a maior: o fluxo do Lattes (flexível, ocupa o espaço restante)
            Group {
                if let profile = selectedProfile {
                    LattesMainView(profile: profile, selectedEntry: $selectedEntry)
                        .id(profile.id)
                } else {
                    EmptyProfilePlaceholder()
                }
            }
            .navigationSplitViewColumnWidth(min: 560, ideal: 920)
        } detail: {
            // Coluna 3 — suficiente para conferir o certificado (mais estreita)
            Group {
                if let entry = selectedEntry {
                    PDFPreviewView(entry: entry)
                        .id(entry.id)
                } else {
                    EmptyDetailPlaceholder()
                }
            }
            .navigationSplitViewColumnWidth(min: 300, ideal: 360, max: 460)
        }
        .navigationSplitViewStyle(.balanced)
    }
}

private struct EmptyProfilePlaceholder: View {
    var body: some View {
        VStack(spacing: 16) {
            Image(systemName: "doc.text.magnifyingglass")
                .font(.system(size: 56))
                .foregroundStyle(.secondary)
            Text("Selecione ou importe um currículo Lattes")
                .foregroundStyle(.secondary)
        }
        .frame(maxWidth: .infinity, maxHeight: .infinity)
    }
}

private struct EmptyDetailPlaceholder: View {
    var body: some View {
        VStack(spacing: 12) {
            Image(systemName: "rectangle.and.paperclip")
                .font(.system(size: 48))
                .foregroundStyle(.secondary)
            Text("Selecione uma entrada para visualizar o comprovante")
                .foregroundStyle(.secondary)
                .multilineTextAlignment(.center)
                .padding(.horizontal)
        }
        .frame(maxWidth: .infinity, maxHeight: .infinity)
    }
}
