import SwiftUI
import UniformTypeIdentifiers

struct EntryRowView: View {
    @Environment(\.modelContext) private var modelContext
    @Bindable var entry: LattesEntry
    let isSelected: Bool
    let onSelect: () -> Void

    @State private var isDropTargeted = false
    @State private var showCertificateList = false

    var body: some View {
        HStack(spacing: 12) {
            // Indicador de status (semáforo)
            StatusIndicator(status: entry.certificateStatus)
                .frame(width: 12)

            // Conteúdo
            VStack(alignment: .leading, spacing: 2) {
                HStack(spacing: 6) {
                    Text(entry.displayTitle)
                        .font(.system(size: 13))
                        .lineLimit(2)
                        .foregroundStyle(isSelected ? Color.accentColor : .primary)
                    QualisBadge(entry: entry)
                }
                if !entry.authors.isEmpty {
                    Text(entry.authors)
                        .font(.caption)
                        .foregroundStyle(.secondary)
                        .lineLimit(1)
                }
            }

            Spacer()

            // Indicador de quantos comprovantes
            if !entry.certificates.isEmpty {
                HStack(spacing: 4) {
                    Image(systemName: "paperclip")
                        .font(.caption)
                    Text("\(entry.certificates.count)")
                        .font(.caption)
                }
                .foregroundStyle(.secondary)
            }

            // Botão de ação
            Menu {
                Button {
                    pickCertificates()
                } label: {
                    Label("Vincular arquivo…", systemImage: "plus.circle")
                }

                if !entry.certificates.isEmpty {
                    Button {
                        showCertificateList = true
                    } label: {
                        Label("Gerenciar comprovantes (\(entry.certificates.count))…", systemImage: "slider.horizontal.3")
                    }
                    Divider()
                    Button("Confirmar todos", role: .none) {
                        for cert in entry.certificates {
                            cert.isConfirmed = true
                        }
                        entry.certificateStatus = .confirmed
                        try? modelContext.save()
                    }
                    Button("Excluir todos os comprovantes", role: .destructive) {
                        for cert in entry.certificates {
                            modelContext.delete(cert)
                        }
                        entry.certificateStatus = .none
                        try? modelContext.save()
                    }
                }
            } label: {
                Image(systemName: "ellipsis.circle")
                    .font(.system(size: 16))
                    .foregroundStyle(.secondary)
            }
            .menuStyle(.borderlessButton)
            .fixedSize()
        }
        .padding(.horizontal, 16)
        .padding(.vertical, 8)
        .background(
            RoundedRectangle(cornerRadius: 6)
                .fill(
                    isDropTargeted ? Color.accentColor.opacity(0.15) :
                    isSelected     ? Color.accentColor.opacity(0.08) :
                                     Color.clear
                )
                .padding(.horizontal, 4)
        )
        .contentShape(Rectangle())
        .onTapGesture { onSelect() }
        .onDrop(of: [.fileURL], isTargeted: $isDropTargeted) { providers in
            handleDrop(providers: providers)
        }
        .sheet(isPresented: $showCertificateList) {
            CertificateListSheet(entry: entry)
        }
    }

    // MARK: - File pickers

    private func pickCertificates() {
        let panel = NSOpenPanel()
        panel.allowedContentTypes = [.pdf, .jpeg, .png, .tiff]
        panel.canChooseFiles = true
        panel.canChooseDirectories = false
        panel.allowsMultipleSelection = true
        panel.message = "Selecione o(s) comprovante(s) para esta entrada"
        panel.prompt = "Vincular"
        guard panel.runModal() == .OK else { return }
        addCertificates(urls: panel.urls)
    }

    // MARK: - Drop handler

    private func handleDrop(providers: [NSItemProvider]) -> Bool {
        var handled = false
        for provider in providers {
            if provider.hasItemConformingToTypeIdentifier(UTType.fileURL.identifier) {
                provider.loadItem(forTypeIdentifier: UTType.fileURL.identifier) { item, _ in
                    guard let data = item as? Data,
                          let url = URL(dataRepresentation: data, relativeTo: nil) else { return }
                    Task { @MainActor in
                        addCertificates(urls: [url])
                    }
                }
                handled = true
            }
        }
        return handled
    }

    private func addCertificates(urls: [URL]) {
        for url in urls {
            let cert = Certificate(filePath: url.path)
            cert.order = entry.nextCertificateOrder()
            cert.entry = entry
            cert.profile = entry.section?.profile
            cert.isConfirmed = true
            modelContext.insert(cert)
        }
        entry.certificateStatus = .confirmed
        try? modelContext.save()
    }
}

// MARK: - Status indicator

struct StatusIndicator: View {
    let status: EntryStatus

    var body: some View {
        Circle()
            .fill(color)
            .frame(width: 10, height: 10)
            .shadow(color: color.opacity(0.4), radius: 2)
    }

    var color: Color {
        switch status {
        case .confirmed: return .green
        case .suggested: return .yellow
        case .none:      return .red
        }
    }
}

// MARK: - Lista de comprovantes da entrada

private struct CertificateListSheet: View {
    @Environment(\.modelContext) private var modelContext
    @Environment(\.dismiss) private var dismiss
    @Bindable var entry: LattesEntry

    @State private var certToDelete: Certificate?

    private var certs: [Certificate] { entry.sortedCertificates }

    var body: some View {
        VStack(alignment: .leading, spacing: 0) {
            HStack {
                VStack(alignment: .leading, spacing: 2) {
                    Text("Comprovantes").font(.title2).bold()
                    Text(entry.displayTitle)
                        .font(.caption).foregroundStyle(.secondary).lineLimit(2)
                }
                Spacer()
                Button("Fechar") { dismiss() }
            }
            .padding()
            Divider()

            if certs.count > 1 {
                Text("Arraste pela alça ⠿ ou use ↑ ↓ para mudar a ordem. A ordem é usada na geração do PDF.")
                    .font(.caption).foregroundStyle(.secondary)
                    .padding(.horizontal).padding(.top, 8)
            }

            List {
                ForEach(certs) { cert in
                    CertificateRow(
                        cert: cert,
                        index: certs.firstIndex(where: { $0.id == cert.id }) ?? 0,
                        total: certs.count,
                        onToggleConfirm: { toggleConfirm(cert) },
                        onMoveUp: { move(cert, up: true) },
                        onMoveDown: { move(cert, up: false) },
                        onDelete: { certToDelete = cert }
                    )
                }
                .onMove { from, to in moveOffsets(from, to) }
            }
        }
        .frame(minWidth: 600, minHeight: 400)
        .confirmationDialog("Excluir comprovante?", isPresented: .init(
            get: { certToDelete != nil }, set: { if !$0 { certToDelete = nil } }
        ), presenting: certToDelete) { cert in
            Button("Excluir \"\(cert.fileName)\"", role: .destructive) { delete(cert) }
            Button("Cancelar", role: .cancel) { certToDelete = nil }
        } message: { _ in
            Text("O vínculo deste arquivo com a entrada será removido. O arquivo original no disco não é apagado.")
        }
    }

    // MARK: - Ações

    private func toggleConfirm(_ cert: Certificate) {
        cert.isConfirmed.toggle()
        refreshStatus()
        try? modelContext.save()
    }

    private func delete(_ cert: Certificate) {
        modelContext.delete(cert)
        reindex()
        refreshStatus()
        try? modelContext.save()
        certToDelete = nil
    }

    private func move(_ cert: Certificate, up: Bool) {
        var arr = certs
        guard let i = arr.firstIndex(where: { $0.id == cert.id }) else { return }
        let j = up ? i - 1 : i + 1
        guard arr.indices.contains(j) else { return }
        arr.swapAt(i, j)
        for (k, c) in arr.enumerated() { c.order = k }
        try? modelContext.save()
    }

    private func moveOffsets(_ from: IndexSet, _ to: Int) {
        var arr = certs
        arr.move(fromOffsets: from, toOffset: to)
        for (k, c) in arr.enumerated() { c.order = k }
        try? modelContext.save()
    }

    private func reindex() {
        for (k, c) in entry.sortedCertificates.enumerated() { c.order = k }
    }

    private func refreshStatus() {
        let certs = entry.certificates
        entry.certificateStatus = certs.contains { $0.isConfirmed }
            ? .confirmed : (certs.isEmpty ? .none : .suggested)
    }
}

private struct CertificateRow: View {
    let cert: Certificate
    let index: Int
    let total: Int
    let onToggleConfirm: () -> Void
    let onMoveUp: () -> Void
    let onMoveDown: () -> Void
    let onDelete: () -> Void

    var body: some View {
        HStack(spacing: 10) {
            if total > 1 {
                Image(systemName: "line.3.horizontal")
                    .foregroundStyle(.tertiary).font(.caption)
                Text("\(index + 1).")
                    .font(.caption).monospacedDigit().foregroundStyle(.secondary)
            }
            Image(systemName: cert.isPDF ? "doc.fill" : "photo.fill")
                .foregroundStyle(cert.isPDF ? .red : .blue)

            VStack(alignment: .leading, spacing: 2) {
                Text(cert.fileName)
                    .font(.system(size: 13))
                    .lineLimit(1).truncationMode(.middle)
                if cert.confidence > 0 {
                    Text("Confiança: \(Int(cert.confidence * 100))%")
                        .font(.caption).foregroundStyle(.secondary)
                }
            }
            Spacer()

            if total > 1 {
                Button(action: onMoveUp) { Image(systemName: "chevron.up") }
                    .buttonStyle(.borderless).disabled(index == 0).help("Mover para cima")
                Button(action: onMoveDown) { Image(systemName: "chevron.down") }
                    .buttonStyle(.borderless).disabled(index == total - 1).help("Mover para baixo")
            }

            Toggle("Confirmado", isOn: .init(get: { cert.isConfirmed }, set: { _ in onToggleConfirm() }))
                .toggleStyle(.checkbox).labelsHidden().help("Confirmado")

            Button { NSWorkspace.shared.open(cert.fileURL) } label: {
                Image(systemName: "eye")
            }
            .buttonStyle(.borderless).help("Abrir arquivo")

            Button(role: .destructive, action: onDelete) {
                Image(systemName: "trash")
            }
            .buttonStyle(.borderless).help("Excluir este comprovante")
        }
        .padding(.vertical, 2)
    }
}
