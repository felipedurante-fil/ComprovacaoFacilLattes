import SwiftUI
import SwiftData

/// Mantém uma janela utilitária (ex.: gerar relatório) fora do ciclo do SwiftUI.
final class UtilityWindowHolder: NSObject, ObservableObject, NSWindowDelegate {
    var window: NSWindow? {
        didSet { window?.delegate = self }
    }
    func windowWillClose(_ notification: Notification) {
        DispatchQueue.main.async { self.window = nil }
    }
}

struct LattesMainView: View {
    @Environment(\.modelContext) private var modelContext
    @StateObject private var indexer = CertificateIndexer()
    @StateObject private var reportWindow = UtilityWindowHolder()

    var profile: LattesProfile
    @Binding var selectedEntry: LattesEntry?

    @State private var expandedSections: Set<String> = []
    @State private var scanItems: [CertificateIndexer.ScanItem] = []
    @State private var showSuggestions = false
    @State private var showUpdateConfirm = false
    @State private var noFilesMessage: String?
    @State private var showOnlyPending = false
    @State private var showAddDocument = false

    /// Seções exibidas — quando o filtro está ligado, só as que têm pendências.
    private var displayedSections: [LattesSection] {
        let secs = profile.sortedSections
        guard showOnlyPending else { return secs }
        return secs.filter { $0.entries.contains { $0.certificateStatus != .confirmed } }
    }

    var body: some View {
        VStack(spacing: 0) {
            // Barra de ações
            actionBar

            Divider()

            // Contador de status
            statusBar

            Divider()

            // Lista de seções em acordeão
            if profile.sections.isEmpty {
                emptyLattes
            } else if showOnlyPending && displayedSections.isEmpty {
                allCoveredPlaceholder
            } else {
                ScrollView {
                    LazyVStack(alignment: .leading, spacing: 0) {
                        ForEach(displayedSections) { section in
                            SectionAccordionView(
                                section: section,
                                isExpanded: showOnlyPending || expandedSections.contains(section.id.uuidString),
                                onlyPending: showOnlyPending,
                                selectedEntry: $selectedEntry
                            ) {
                                toggleSection(section)
                            }
                            Divider()
                        }
                    }
                }
            }
        }
        .navigationTitle(profile.name)
        .sheet(isPresented: $showSuggestions) {
            ScanReviewSheet(items: scanItems, profile: profile) {
                showSuggestions = false
            }
        }
        .sheet(isPresented: $showAddDocument) {
            AddOtherDocumentSheet(profile: profile)
        }
        .overlay {
            if indexer.isRunning {
                IndexingProgressOverlay(indexer: indexer)
            }
        }
        .alert("Escaneamento", isPresented: .init(
            get: { noFilesMessage != nil },
            set: { if !$0 { noFilesMessage = nil } }
        )) {
            Button("OK", role: .cancel) { noFilesMessage = nil }
        } message: {
            Text(noFilesMessage ?? "")
        }
    }

    // MARK: - Sub-views

    private var actionBar: some View {
        HStack(spacing: 8) {
            Button {
                pickCertificateFolder()
            } label: {
                Label("Escanear Pasta por Certificados", systemImage: "folder.badge.plus")
            }
            .buttonStyle(.borderedProminent)

            Button {
                showUpdateConfirm = true
            } label: {
                Label("Atualizar Lattes", systemImage: "arrow.clockwise")
            }
            .buttonStyle(.bordered)
            .confirmationDialog(
                "Atualizar currículo?",
                isPresented: $showUpdateConfirm
            ) {
                Button("Importar novo PDF do Lattes") { updateLattes() }
                Button("Cancelar", role: .cancel) { }
            } message: {
                Text("Escolha um PDF atualizado. Entradas novas serão adicionadas; entradas existentes serão preservadas.")
            }

            Button {
                showAddDocument = true
            } label: {
                Label("Adicionar Documento", systemImage: "doc.badge.plus")
            }
            .buttonStyle(.bordered)

            Spacer()

            QualisAreaMenu()
                .buttonStyle(.bordered)
                .fixedSize()

            Button {
                openReportWindow()
            } label: {
                Label("Gerar arquivo com comprovantes", systemImage: "doc.richtext")
            }
            .buttonStyle(.borderedProminent)
            .tint(.green)
        }
        .padding(.horizontal, 16)
        .padding(.vertical, 10)
    }

    private var statusBar: some View {
        HStack(spacing: 16) {
            StatusCounter(label: "Confirmados", count: profile.confirmedCount, color: .green)
            StatusCounter(label: "Sugeridos",   count: profile.suggestedCount,  color: .yellow)
            StatusCounter(label: "Pendentes",   count: profile.pendingCount,    color: .red)

            coverageBar

            Spacer()

            if !profile.limboCertificates.isEmpty {
                Button {
                    reviewLimboCertificates()
                } label: {
                    Text("\(profile.limboCertificates.count) arquivo(s) sem vínculo")
                        .font(.caption)
                }
                .buttonStyle(.plain)
                .foregroundStyle(.orange)
                .help("Tentar re-vincular os comprovantes que ficaram sem entrada correspondente")
            }

            Toggle(isOn: $showOnlyPending) {
                Label("Só pendências", systemImage: "exclamationmark.triangle")
                    .font(.caption)
            }
            .toggleStyle(.button)
            .controlSize(.small)
            .help("Mostrar apenas entradas ainda não comprovadas")
        }
        .padding(.horizontal, 16)
        .padding(.vertical, 6)
    }

    /// Barra de cobertura: quantas entradas já têm comprovante confirmado.
    private var coverageBar: some View {
        let total = profile.totalEntries
        let done = profile.confirmedCount
        let frac = total > 0 ? Double(done) / Double(total) : 0
        return HStack(spacing: 6) {
            ProgressView(value: frac)
                .progressViewStyle(.linear)
                .frame(width: 120)
                .tint(frac >= 1 ? .green : .accentColor)
            Text("\(done)/\(total) (\(Int((frac * 100).rounded()))%)")
                .font(.caption).monospacedDigit()
                .foregroundStyle(.secondary)
        }
        .help("Entradas com comprovante confirmado")
    }

    private var allCoveredPlaceholder: some View {
        VStack(spacing: 14) {
            Image(systemName: "checkmark.seal.fill")
                .font(.system(size: 52)).foregroundStyle(.green)
            Text("Tudo comprovado!")
                .font(.title3).fontWeight(.semibold)
            Text("Nenhuma entrada pendente de comprovante.")
                .foregroundStyle(.secondary)
        }
        .frame(maxWidth: .infinity, maxHeight: .infinity)
    }

    private var emptyLattes: some View {
        VStack(spacing: 16) {
            Image(systemName: "doc.text")
                .font(.system(size: 48))
                .foregroundStyle(.secondary)
            Text("Nenhuma entrada encontrada")
                .foregroundStyle(.secondary)
            Text("O arquivo pode estar em formato não suportado ou não contém seções reconhecidas.")
                .font(.caption)
                .foregroundStyle(.secondary)
                .multilineTextAlignment(.center)
                .padding(.horizontal, 40)
        }
        .frame(maxWidth: .infinity, maxHeight: .infinity)
    }

    // MARK: - Actions

    private func toggleSection(_ section: LattesSection) {
        let key = section.id.uuidString
        if expandedSections.contains(key) {
            expandedSections.remove(key)
        } else {
            expandedSections.insert(key)
        }
    }

    private func pickCertificateFolder() {
        let panel = NSOpenPanel()
        panel.canChooseDirectories = true
        panel.canChooseFiles = false
        panel.allowsMultipleSelection = false
        panel.message = "Selecione a pasta com os certificados"
        panel.prompt = "Escanear pasta"

        guard panel.runModal() == .OK, let url = panel.url else { return }
        startIndexing(folderURL: url)
    }

    // Abre a geração de comprovantes numa janela própria, redimensionável.
    private func openReportWindow() {
        if let existing = reportWindow.window {
            existing.makeKeyAndOrderFront(nil)
            NSApp.activate(ignoringOtherApps: true)
            return
        }
        let view = GenerateReportSheet(profile: profile, onClose: { reportWindow.window?.close() })
            .modelContainer(modelContext.container)

        let vc = NSHostingController(rootView: view)
        let window = NSWindow(contentViewController: vc)
        window.title = "Gerar arquivo com comprovantes"
        window.styleMask = [.titled, .closable, .resizable]
        window.setContentSize(NSSize(width: 720, height: 760))
        window.minSize = NSSize(width: 560, height: 520)
        window.isReleasedWhenClosed = false
        window.center()
        reportWindow.window = window
        NSApp.activate(ignoringOtherApps: true)
        window.makeKeyAndOrderFront(nil)
    }

    private func reviewLimboCertificates() {
        let results = indexer.reviewLimbo(for: profile)
        guard !results.isEmpty else { return }
        scanItems = results
        showSuggestions = true
    }

    private func startIndexing(folderURL: URL) {
        Task {
            let results = await indexer.scanFolder(at: folderURL, for: profile)
            if results.isEmpty {
                noFilesMessage = "Nenhum arquivo novo (PDF ou imagem) foi encontrado na pasta selecionada, ou todos já estavam vinculados."
            } else {
                scanItems = results
                showSuggestions = true
            }
        }
    }

    private func updateLattes() {
        let panel = NSOpenPanel()
        panel.allowedContentTypes = [.pdf]
        panel.message = "Selecione o PDF atualizado do Lattes"
        panel.prompt = "Importar"

        guard panel.runModal() == .OK, let url = panel.url else { return }

        // Parse em background (PDFs grandes travariam a interface)
        Task {
            let result = await Task.detached(priority: .userInitiated) {
                LattesPDFParser.parse(url: url)
            }.value
            guard let result else { return }
            await MainActor.run { applyUpdate(result, url: url) }
        }
    }

    @MainActor
    private func applyUpdate(_ result: LattesPDFParser.ParseResult, url: URL) {
        profile.pdfPath = url.path
        profile.lastUpdated = Date()
        profile.rawText = result.rawText

        // Re-parse LIMPO: reconstrói todas as seções a partir do PDF atual para
        // refletir melhorias do parser (ex.: divisão de prêmios, atividades,
        // ouvinte/apresentação). Os comprovantes já vinculados são preservados
        // re-ligando-os à nova entrada de mesmo hashKey (ano + título). Os que
        // não encontrarem entrada correspondente voltam para a área de revisão.
        // "Outros Documentos" é uma seção manual (não vem do PDF) — preservada
        // intacta, nunca apagada nem re-vinculada por hash.
        var certsByHash: [String: [Certificate]] = [:]
        for section in profile.sections where section.title != AddOtherDocumentSheet.sectionTitle {
            for entry in section.entries where !entry.certificates.isEmpty {
                certsByHash[entry.hashKey, default: []].append(contentsOf: entry.certificates)
            }
        }

        // Remove as seções antigas (cascata apaga entradas; os comprovantes
        // ficam órfãos com entry=nil e pertencem ao profile, então sobrevivem).
        for section in profile.sections where section.title != AddOtherDocumentSheet.sectionTitle {
            modelContext.delete(section)
        }

        var newEntries: [LattesEntry] = []
        for (idx, parsedSection) in result.sections.enumerated() {
            let section = LattesSection(title: parsedSection.title, order: idx)
            section.profile = profile
            modelContext.insert(section)

            for parsedEntry in parsedSection.entries {
                let entry = LattesEntry(
                    rawText: parsedEntry.rawText,
                    title: parsedEntry.title,
                    kind: parsedEntry.kind,
                    year: parsedEntry.year,
                    authors: parsedEntry.authors,
                    venue: parsedEntry.venue,
                    order: parsedEntry.order
                )
                entry.doi = parsedEntry.doi
                entry.isbn = parsedEntry.isbn
                entry.portaria = parsedEntry.portaria
                entry.issn = parsedEntry.issn
                entry.edital = parsedEntry.edital
                entry.endYear = parsedEntry.endYear
                entry.section = section
                modelContext.insert(entry)
                newEntries.append(entry)

                // Re-vincula os comprovantes desta entrada (mesmo hashKey).
                if let certs = certsByHash.removeValue(forKey: entry.hashKey) {
                    for c in certs { c.entry = entry }
                    refreshStatus(entry)
                }
            }
        }

        // Fallback por similaridade: quando o parser muda a extração de título/ano
        // entre versões, o hashKey deixa de bater mesmo para o mesmo certificado.
        // Tenta re-vincular pelo próprio texto do comprovante antes de desistir —
        // sem isso, uma evolução no parser derruba vínculos já confirmados.
        let unresolved = certsByHash.values.flatMap { $0 }.filter { !$0.extractedText.isEmpty }
        if !unresolved.isEmpty, !newEntries.isEmpty {
            let entryFields = newEntries.map {
                CertificateIndexer.EntryFields(
                    title: $0.title, authors: $0.authors, venue: $0.venue, kind: $0.kind,
                    portaria: $0.portaria, edital: $0.edital, issn: $0.issn, doi: $0.doi,
                    year: $0.year, endYear: $0.endYear, hashKey: $0.hashKey)
            }
            let idf = SimilarityMatcher.buildIDF(from: newEntries.map { $0.title })
            let rejected = Set(profile.rejectedLinks)
            for cert in unresolved {
                let baseName = cert.fileNameNoExt
                let nameText = baseName
                    .replacingOccurrences(of: "_", with: " ")
                    .replacingOccurrences(of: "-", with: " ")
                let matchText = cert.extractedText + " \n " + nameText
                let certYears = CertificateIndexer.yearsIn(nameText).isEmpty
                    ? CertificateIndexer.yearsIn(cert.extractedText) : CertificateIndexer.yearsIn(nameText)
                let folderKinds = CertificateIndexer.inferFolderKinds(cert.fileURL.deletingLastPathComponent().path)
                let ranked = CertificateIndexer.rankedMatches(
                    text: matchText, certKey: baseName, certYears: certYears,
                    entryFields: entryFields, folderKinds: folderKinds, idf: idf, rejected: rejected)
                if let best = ranked.first, best.score >= 0.90 {
                    let entry = newEntries[best.index]
                    cert.entry = entry
                    refreshStatus(entry)
                }
            }
        }

        try? modelContext.save()
    }

    /// Recalcula o status (semáforo) de uma entrada a partir dos certificados vinculados.
    private func refreshStatus(_ entry: LattesEntry) {
        if entry.certificates.contains(where: { $0.isConfirmed }) {
            entry.certificateStatus = .confirmed
        } else if !entry.certificates.isEmpty {
            entry.certificateStatus = .suggested
        } else {
            entry.certificateStatus = .none
        }
    }
}

private struct StatusCounter: View {
    let label: String
    let count: Int
    let color: Color

    var body: some View {
        HStack(spacing: 4) {
            Circle().fill(color).frame(width: 10, height: 10)
            Text("\(count) \(label)")
                .font(.caption)
                .foregroundStyle(.secondary)
        }
    }
}

// MARK: - Revisão do escaneamento (todos os arquivos)

enum ScanItemState { case pending, confirmed, skipped }

private struct ScanReviewSheet: View {
    @Environment(\.modelContext) private var modelContext
    let items: [CertificateIndexer.ScanItem]
    let profile: LattesProfile
    let onDismiss: () -> Void

    @State private var assignments: [UUID: LattesEntry] = [:]
    @State private var states: [UUID: ScanItemState] = [:]
    @State private var filterText = ""

    private var allEntries: [LattesEntry] {
        profile.sortedSections.flatMap { $0.sortedEntries }
    }

    private var handledCount: Int {
        states.values.filter { $0 != .pending }.count
    }

    private var confidentCount: Int {
        items.filter { $0.confident }.count
    }

    private var visibleItems: [CertificateIndexer.ScanItem] {
        guard !filterText.isEmpty else { return items }
        let f = filterText.lowercased()
        return items.filter { $0.certificate.fileName.lowercased().contains(f) }
    }

    var body: some View {
        VStack(alignment: .leading, spacing: 0) {
            HStack {
                VStack(alignment: .leading, spacing: 2) {
                    Text("Revisão de Comprovantes").font(.title2).bold()
                    Text("\(items.count) arquivo(s) · \(confidentCount) sugestão(ões) ≥90% · \(handledCount) tratado(s)")
                        .font(.caption).foregroundStyle(.secondary)
                }
                Spacer()
                Button("Vincular sugestões ≥90%") { confirmAllSuggested() }
                    .buttonStyle(.bordered)
                    .disabled(confidentCount == 0)
                Button("Concluir") { onDismiss() }
                    .buttonStyle(.borderedProminent)
            }
            .padding()

            Divider()

            HStack(spacing: 6) {
                Image(systemName: "magnifyingglass").foregroundStyle(.secondary)
                TextField("Filtrar por nome de arquivo…", text: $filterText)
                    .textFieldStyle(.roundedBorder)
            }
            .padding(.horizontal).padding(.vertical, 8)

            List(visibleItems) { item in
                ScanReviewRow(
                    item: item,
                    allEntries: allEntries,
                    assignedEntry: assignments[item.id] ?? item.suggestedEntry,
                    state: states[item.id] ?? .pending,
                    onAssign: { entry in
                        assignments[item.id] = entry
                        confirm(item: item, entry: entry)
                    },
                    onConfirm: {
                        if let e = assignments[item.id] ?? item.suggestedEntry {
                            confirm(item: item, entry: e)
                        }
                    },
                    onSkip: { skip(item: item) },
                    onUndo: { undo(item: item) },
                    onLinkCombo: { entry in linkCombo(item: item, entry: entry) }
                )
            }
        }
        .frame(minWidth: 860, minHeight: 560)
    }

    // MARK: - Ações (reversíveis)

    private func confirm(item: CertificateIndexer.ScanItem, entry: LattesEntry) {
        let cert = item.certificate
        // Se estava vinculado a outra entrada, libera a anterior
        let previous = cert.entry
        if cert.modelContext == nil { modelContext.insert(cert) }
        cert.profile = profile
        if previous !== entry { cert.order = entry.nextCertificateOrder() }
        cert.entry = entry
        cert.isConfirmed = true
        cert.isRejected = false
        entry.certificateStatus = .confirmed
        if let p = previous, p !== entry { refreshStatus(p) }
        // Aprendizado: se o usuário escolheu outra entrada que não a sugerida, registra a recusa
        if let suggested = item.suggestedEntry, suggested.hashKey != entry.hashKey {
            recordRejection(item: item, entry: suggested)
        }
        states[item.id] = .confirmed
        try? modelContext.save()
    }

    private func skip(item: CertificateIndexer.ScanItem) {
        // "Ignorar" = não vincular. Remove qualquer vínculo prévio do arquivo.
        let cert = item.certificate
        if cert.modelContext != nil {
            let previous = cert.entry
            cert.entry = nil
            cert.isConfirmed = false
            if let p = previous { refreshStatus(p) }
            modelContext.delete(cert)
        }
        // Aprendizado: recusa da sugestão pendente
        if let suggested = item.suggestedEntry {
            recordRejection(item: item, entry: suggested)
        }
        states[item.id] = .skipped
        try? modelContext.save()
    }

    /// Vincula o arquivo ADICIONALMENTE a outra entrada (combo: 1 arquivo → N entradas).
    private func linkCombo(item: CertificateIndexer.ScanItem, entry: LattesEntry) {
        let path = item.certificate.filePath
        // Evita duplicar: já existe um comprovante deste arquivo nesta entrada?
        if entry.certificates.contains(where: { $0.filePath == path }) { return }
        let extra = Certificate(filePath: path)
        extra.extractedText = item.certificate.extractedText
        extra.order = entry.nextCertificateOrder()
        modelContext.insert(extra)
        extra.profile = profile
        extra.entry = entry
        extra.isConfirmed = true
        entry.certificateStatus = .confirmed
        try? modelContext.save()
    }

    /// Registra (e persiste) que o vínculo arquivo→entrada foi recusado.
    private func recordRejection(item: CertificateIndexer.ScanItem, entry: LattesEntry) {
        let key = "\(item.certificate.fileNameNoExt)||\(entry.hashKey)"
        if !profile.rejectedLinks.contains(key) {
            profile.rejectedLinks.append(key)
        }
    }

    /// Desfaz a decisão: volta o arquivo ao estado pendente.
    private func undo(item: CertificateIndexer.ScanItem) {
        let cert = item.certificate
        if cert.modelContext != nil {
            let previous = cert.entry
            cert.entry = nil
            modelContext.delete(cert)
            if let p = previous { refreshStatus(p) }
        }
        states[item.id] = .pending
        try? modelContext.save()
    }

    private func confirmAllSuggested() {
        // Só vincula automaticamente as sugestões CONFIÁVEIS (≥ 90%)
        for item in items where (states[item.id] ?? .pending) == .pending && item.confident {
            if let entry = assignments[item.id] ?? item.suggestedEntry {
                confirm(item: item, entry: entry)
            }
        }
    }

    /// Recalcula o status (semáforo) de uma entrada a partir dos certificados restantes.
    private func refreshStatus(_ entry: LattesEntry) {
        if entry.certificates.contains(where: { $0.isConfirmed }) {
            entry.certificateStatus = .confirmed
        } else if !entry.certificates.isEmpty {
            entry.certificateStatus = .suggested
        } else {
            entry.certificateStatus = .none
        }
    }
}

private struct ScanReviewRow: View {
    let item: CertificateIndexer.ScanItem
    let allEntries: [LattesEntry]
    let assignedEntry: LattesEntry?
    let state: ScanItemState
    let onAssign: (LattesEntry) -> Void
    let onConfirm: () -> Void
    let onSkip: () -> Void
    let onUndo: () -> Void
    let onLinkCombo: (LattesEntry) -> Void

    var body: some View {
        HStack(spacing: 12) {
            Image(systemName: item.certificate.isPDF ? "doc.fill" : "photo.fill")
                .foregroundStyle(item.certificate.isPDF ? .red : .blue)
                .frame(width: 18)

            VStack(alignment: .leading, spacing: 3) {
                Text(item.certificate.fileName)
                    .font(.system(size: 13, weight: .medium))
                    .lineLimit(1).truncationMode(.middle)

                if let entry = assignedEntry {
                    let guessColor: Color = state == .confirmed ? .green
                        : (item.confident ? .green : .orange)
                    HStack(spacing: 4) {
                        if state != .confirmed, !item.confident {
                            Image(systemName: "questionmark.circle").font(.caption2).foregroundStyle(.orange)
                        }
                        Text("→ \(entry.displayTitle)")
                            .font(.caption).foregroundStyle(guessColor).lineLimit(2)
                    }
                } else if item.noLikelyEntry {
                    Text("Provavelmente sem entrada correspondente no Lattes")
                        .font(.caption).foregroundStyle(.secondary)
                } else if !item.hasText {
                    Label("Sem texto legível — vincule manualmente", systemImage: "exclamationmark.triangle")
                        .font(.caption).foregroundStyle(.orange)
                } else {
                    Text("Sem sugestão — escolha a entrada")
                        .font(.caption).foregroundStyle(.secondary)
                }

                // Combos: o arquivo também pode comprovar outras entradas
                ForEach(item.comboEntries.prefix(2), id: \.id) { combo in
                    HStack(spacing: 4) {
                        Button { onLinkCombo(combo) } label: {
                            Image(systemName: "plus.circle").font(.caption2)
                        }
                        .buttonStyle(.plain).foregroundStyle(.blue)
                        .help("Vincular também a esta entrada")
                        Text("também: \(combo.displayTitle)")
                            .font(.caption2).foregroundStyle(.blue).lineLimit(1)
                    }
                }
            }

            Spacer()

            // Selo de estado
            switch state {
            case .confirmed:
                Label("Vinculado", systemImage: "checkmark.seal.fill")
                    .labelStyle(.iconOnly).foregroundStyle(.green)
            case .skipped:
                Label("Ignorado", systemImage: "minus.circle.fill")
                    .labelStyle(.iconOnly).foregroundStyle(.secondary)
            case .pending:
                if item.score > 0 {
                    Text("\(Int(item.score * 100))%")
                        .font(.caption2).monospacedDigit()
                        .foregroundStyle(item.confident ? .green : .secondary)
                }
            }

            // Visualizar
            Button { NSWorkspace.shared.open(item.certificate.fileURL) } label: {
                Image(systemName: "eye")
            }
            .buttonStyle(.borderless).help("Abrir arquivo")

            // Escolher/trocar entrada
            EntryPickerMenu(allEntries: allEntries, onPick: onAssign)

            if state == .pending {
                Button(action: onConfirm) {
                    Image(systemName: "checkmark.circle.fill").foregroundStyle(.green)
                }
                .buttonStyle(.plain).disabled(assignedEntry == nil).help("Confirmar vínculo")

                Button(action: onSkip) {
                    Image(systemName: "xmark.circle.fill").foregroundStyle(.red)
                }
                .buttonStyle(.plain).help("Ignorar (sem vínculo)")
            } else {
                // Desfazer — disponível após confirmar ou ignorar
                Button(action: onUndo) {
                    Image(systemName: "arrow.uturn.backward.circle.fill")
                        .foregroundStyle(.orange)
                }
                .buttonStyle(.plain).help("Desfazer")
            }
        }
        .padding(.vertical, 4)
        .opacity(state == .skipped ? 0.5 : 1)
    }
}

/// Menu de escolha de entrada, agrupado por seção.
private struct EntryPickerMenu: View {
    let allEntries: [LattesEntry]
    let onPick: (LattesEntry) -> Void

    private var bySection: [(section: String, entries: [LattesEntry])] {
        let grouped = Dictionary(grouping: allEntries) { $0.section?.title ?? "Outros" }
        return grouped
            .map { (section: $0.key, entries: $0.value.sorted { $0.order < $1.order }) }
            .sorted { $0.section < $1.section }
    }

    var body: some View {
        Menu {
            ForEach(bySection, id: \.section) { group in
                Menu(group.section) {
                    ForEach(group.entries) { entry in
                        Button {
                            onPick(entry)
                        } label: {
                            Text(entry.displayTitle)
                        }
                    }
                }
            }
        } label: {
            Image(systemName: "link.badge.plus")
        }
        .menuStyle(.borderlessButton)
        .fixedSize()
        .help("Vincular a uma entrada específica")
    }
}

// MARK: - Progresso de indexação

private struct IndexingProgressOverlay: View {
    @ObservedObject var indexer: CertificateIndexer

    var body: some View {
        ZStack {
            Color.black.opacity(0.45).ignoresSafeArea()

            VStack(alignment: .leading, spacing: 14) {
                // Título + percentual
                HStack(spacing: 10) {
                    ProgressView().controlSize(.small)
                    Text("Escaneando certificados…")
                        .font(.headline)
                    Spacer()
                    Text("\(Int(indexer.progress * 100))%")
                        .font(.subheadline).monospacedDigit()
                        .foregroundStyle(.secondary)
                }

                // Barra de progresso
                ProgressView(value: indexer.progress)

                // Status atual
                Text(indexer.statusMessage)
                    .font(.caption)
                    .foregroundStyle(.secondary)
                    .lineLimit(1)
                    .truncationMode(.middle)

                Divider()

                // Log de atividades (auto-scroll)
                ScrollViewReader { proxy in
                    ScrollView {
                        VStack(alignment: .leading, spacing: 2) {
                            ForEach(Array(indexer.logLines.enumerated()), id: \.offset) { idx, line in
                                Text(line)
                                    .font(.system(size: 11, design: .monospaced))
                                    .foregroundStyle(.secondary)
                                    .frame(maxWidth: .infinity, alignment: .leading)
                                    .id(idx)
                            }
                        }
                        .padding(8)
                    }
                    .frame(height: 200)
                    .background(Color(nsColor: .textBackgroundColor).opacity(0.6),
                                in: RoundedRectangle(cornerRadius: 8))
                    .onChange(of: indexer.logLines.count) { _, count in
                        guard count > 0 else { return }
                        withAnimation(.easeOut(duration: 0.15)) {
                            proxy.scrollTo(count - 1, anchor: .bottom)
                        }
                    }
                }
            }
            .padding(24)
            .frame(width: 540)
            .background(.regularMaterial, in: RoundedRectangle(cornerRadius: 16))
            .shadow(radius: 30)
        }
    }
}
