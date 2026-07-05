import Foundation
import PDFKit
import Vision
import AppKit

/// Escaneia uma pasta, faz OCR nos arquivos e sugere vínculos com entradas do Lattes.
/// O trabalho pesado (leitura de PDF, OCR, similaridade) roda fora da thread principal;
/// apenas progresso e criação de modelos acontecem no MainActor.
final class CertificateIndexer: ObservableObject {

    @Published var progress: Double = 0          // 0.0–1.0
    @Published var statusMessage: String = ""
    @Published var logLines: [String] = []
    @Published var isRunning: Bool = false

    // Acima deste valor a sugestão é "confiável" (auto-marcada e contada).
    private let suggestThreshold = 0.90
    // Abaixo do confiável mas acima deste piso, mostramos o melhor palpite para
    // confirmação rápida (sem auto-marcar).
    private let guessFloor = 0.35
    private let maxLogLines = 250

    /// Um arquivo escaneado e sua melhor correspondência (pode não haver).
    struct ScanItem: Identifiable {
        let id = UUID()
        let certificate: Certificate
        var suggestedEntry: LattesEntry?    // melhor palpite (nil = nenhum)
        let score: Double
        let confident: Bool                 // score ≥ 90% → sugestão confiável
        let hasText: Bool
        var comboEntries: [LattesEntry] = []  // outras entradas que o arquivo também pode comprovar
        var noLikelyEntry: Bool = false       // tem texto mas nada correspondeu
    }

    /// Uma correspondência pontuada (índice da entrada + score).
    struct ScoredMatch: Sendable {
        let index: Int
        let score: Double
    }

    /// Dados calculados em background — sem referência a objetos @Model.
    private struct RawItem {
        let filePath: String
        let text: String
        var ranked: [ScoredMatch]   // melhores correspondências, desc
        let usedOCR: Bool
        let hasText: Bool
    }

    // MARK: - Scan principal

    @MainActor
    func scanFolder(at url: URL, for profile: LattesProfile) async -> [ScanItem] {
        isRunning = true
        progress = 0
        logLines = []
        statusMessage = "Iniciando…"
        defer { isRunning = false }

        // Snapshot dos campos das entradas (leitura de @Model só no main)
        let entries = profile.sections.flatMap { $0.sortedEntries }
        let entryFields: [EntryFields] = entries.map {
            EntryFields(title: $0.title, authors: $0.authors, venue: $0.venue,
                        kind: $0.kind, portaria: $0.portaria, edital: $0.edital,
                        issn: $0.issn, doi: $0.doi, year: $0.year, endYear: $0.endYear,
                        hashKey: $0.hashKey)
        }
        let rootPath = url.path

        guard !entries.isEmpty else {
            log("Nenhuma entrada no Lattes para comparar.")
            statusMessage = "Sem entradas"
            return []
        }

        // Pesos IDF (raridade) e rejeições aprendidas
        let idf = SimilarityMatcher.buildIDF(from: entries.map { $0.title })
        let rejected = Set(profile.rejectedLinks)

        // Arquivos já vinculados (evita reprocessar/duplicar)
        let existingPaths = Set(profile.certificates.map { $0.filePath })

        // 1 — Coleta de arquivos (background)
        log("📂 Lendo subpastas e listando arquivos…")
        let allFiles = await Task.detached(priority: .userInitiated) {
            Self.collectFiles(in: url)
        }.value
        let files = allFiles.filter { !existingPaths.contains($0.path) }

        log("🔎 \(allFiles.count) arquivo(s) encontrado(s); \(files.count) novo(s) a processar.")
        guard !files.isEmpty else {
            log("Nenhum arquivo novo para escanear.")
            statusMessage = "Nada novo para escanear"
            progress = 1
            return []
        }

        // 2 — Processa cada arquivo (extração + ranking em background)
        var rawItems: [RawItem] = []
        let total = files.count

        for (i, fileURL) in files.enumerated() {
            let name = fileURL.lastPathComponent
            progress = Double(i) / Double(total)
            statusMessage = "Lendo \(i + 1) de \(total): \(name)"
            log("📄 [\(i + 1)/\(total)] \(name)")

            let relFolder = String(fileURL.deletingLastPathComponent().path.dropFirst(rootPath.count))
            let folderKinds = Self.inferFolderKinds(relFolder)
            let baseName = fileURL.deletingPathExtension().lastPathComponent
            let nameText = baseName
                .replacingOccurrences(of: "_", with: " ")
                .replacingOccurrences(of: "-", with: " ")

            let raw = await Task.detached(priority: .userInitiated) {
                let extraction = Self.extractText(from: fileURL)
                let hasText = !extraction.text.isEmpty
                let matchText = extraction.text + " \n " + nameText
                // Anos do certificado: prefere os do nome do arquivo (mais confiável)
                let certYears = Self.yearsIn(nameText).isEmpty
                    ? Self.yearsIn(extraction.text) : Self.yearsIn(nameText)
                let ranked = Self.rankedMatches(
                    text: matchText, certKey: baseName, certYears: certYears,
                    entryFields: entryFields, folderKinds: folderKinds, idf: idf, rejected: rejected)
                return RawItem(filePath: fileURL.path, text: extraction.text,
                               ranked: ranked, usedOCR: extraction.usedOCR, hasText: hasText)
            }.value

            if raw.usedOCR { log("   🔬 OCR aplicado (documento digitalizado).") }
            if let top = raw.ranked.first {
                let tag = top.score >= suggestThreshold ? "✓ Sugestão" : "? Palpite"
                log("   \(tag) (\(Int(top.score * 100))%) → \(entries[top.index].displayTitle.prefix(48))")
            } else if !raw.hasText {
                log("   ⚠︎ Sem texto legível — vínculo manual.")
            } else {
                log("   • Sem correspondência no Lattes.")
            }
            rawItems.append(raw)
        }

        // 3 — Atribuição global: espalha empates próximos para entradas ainda descobertas
        progress = 1
        statusMessage = "Finalizando…"
        let chosen = Self.globalAssign(rawItems, guessFloor: guessFloor)

        // 4 — Cria os ScanItems (no main, seguro)
        var items: [ScanItem] = []
        var suggestedCount = 0
        for (i, r) in rawItems.enumerated() {
            let cert = Certificate(filePath: r.filePath)
            cert.extractedText = r.text

            let pick = chosen[i]
            let best: ScoredMatch? = (pick >= 0 && pick < r.ranked.count) ? r.ranked[pick] : nil
            let score = best?.score ?? 0
            cert.confidence = score
            let confident = score >= suggestThreshold
            let showGuess = score >= guessFloor
            if confident { suggestedCount += 1 }

            // Combos: outras entradas distintas com alta confiança (≥0.92)
            var combos: [LattesEntry] = []
            if let best {
                for m in r.ranked where m.index != best.index && m.score >= 0.92 {
                    combos.append(entries[m.index])
                    if combos.count >= 3 { break }
                }
            }

            items.append(ScanItem(
                certificate: cert,
                suggestedEntry: (showGuess && best != nil) ? entries[best!.index] : nil,
                score: score,
                confident: confident,
                hasText: r.hasText,
                comboEntries: combos,
                noLikelyEntry: r.hasText && !showGuess))
        }

        items.sort { $0.score > $1.score }
        log("✅ Concluído — \(total) arquivo(s), \(suggestedCount) sugestão(ões) ≥90%.")
        statusMessage = "Concluído — \(total) arquivo(s), \(suggestedCount) sugeridos"
        return items
    }

    /// Rechecagem dos comprovantes órfãos (sem entrada vinculada) — ex.: após um
    /// "Atualizar Lattes" cujo re-parse não encontrou hash correspondente para todos.
    /// Diferente de `scanFolder`, não lê arquivos: reaproveita o texto já extraído
    /// e guardado em cada `Certificate`.
    @MainActor
    func reviewLimbo(for profile: LattesProfile) -> [ScanItem] {
        let entries = profile.sections.flatMap { $0.sortedEntries }
        let limbo = profile.limboCertificates
        guard !entries.isEmpty, !limbo.isEmpty else { return [] }

        let entryFields: [EntryFields] = entries.map {
            EntryFields(title: $0.title, authors: $0.authors, venue: $0.venue,
                        kind: $0.kind, portaria: $0.portaria, edital: $0.edital,
                        issn: $0.issn, doi: $0.doi, year: $0.year, endYear: $0.endYear,
                        hashKey: $0.hashKey)
        }
        let idf = SimilarityMatcher.buildIDF(from: entries.map { $0.title })
        let rejected = Set(profile.rejectedLinks)

        var items: [ScanItem] = []
        for cert in limbo {
            let baseName = cert.fileNameNoExt
            let nameText = baseName
                .replacingOccurrences(of: "_", with: " ")
                .replacingOccurrences(of: "-", with: " ")
            let hasText = !cert.extractedText.isEmpty
            let matchText = cert.extractedText + " \n " + nameText
            let certYears = Self.yearsIn(nameText).isEmpty
                ? Self.yearsIn(cert.extractedText) : Self.yearsIn(nameText)
            let relFolder = cert.fileURL.deletingLastPathComponent().path
            let folderKinds = Self.inferFolderKinds(relFolder)
            let ranked = Self.rankedMatches(
                text: matchText, certKey: baseName, certYears: certYears,
                entryFields: entryFields, folderKinds: folderKinds, idf: idf, rejected: rejected)

            let best = ranked.first
            let score = best?.score ?? 0
            cert.confidence = score
            let confident = score >= suggestThreshold
            let showGuess = score >= guessFloor

            var combos: [LattesEntry] = []
            if let best {
                for m in ranked where m.index != best.index && m.score >= 0.92 {
                    combos.append(entries[m.index])
                    if combos.count >= 3 { break }
                }
            }

            items.append(ScanItem(
                certificate: cert,
                suggestedEntry: (showGuess && best != nil) ? entries[best!.index] : nil,
                score: score,
                confident: confident,
                hasText: hasText,
                comboEntries: combos,
                noLikelyEntry: hasText && !showGuess))
        }
        items.sort { $0.score > $1.score }
        return items
    }

    @MainActor
    private func log(_ line: String) {
        logLines.append(line)
        if logLines.count > maxLogLines {
            logLines.removeFirst(logLines.count - maxLogLines)
        }
    }

    // MARK: - Extração de texto (nonisolated, roda fora do main)

    struct Extraction {
        let text: String
        let usedOCR: Bool
    }

    nonisolated static func extractText(from url: URL) -> Extraction {
        let ext = url.pathExtension.lowercased()
        if ext == "pdf" {
            return extractTextFromPDF(url)
        } else if ["jpg", "jpeg", "png", "tiff", "tif", "heic"].contains(ext) {
            // Imagem → sempre OCR
            guard let cg = NSImage(contentsOf: url)?
                .cgImage(forProposedRect: nil, context: nil, hints: nil)
            else { return Extraction(text: "", usedOCR: true) }
            return Extraction(text: ocrImage(cg), usedOCR: true)
        }
        return Extraction(text: "", usedOCR: false)
    }

    nonisolated static func extractTextFromPDF(_ url: URL) -> Extraction {
        guard let doc = PDFDocument(url: url) else { return Extraction(text: "", usedOCR: false) }

        // 1 — tenta a camada de texto (PDF "nativo")
        var text = ""
        for i in 0..<min(doc.pageCount, 5) {
            if let str = doc.page(at: i)?.string {
                text += str + "\n"
            }
        }
        if text.trimmingCharacters(in: .whitespacesAndNewlines).count >= 20 {
            return Extraction(text: text, usedOCR: false)
        }

        // 2 — PDF digitalizado (sem texto): renderiza páginas e aplica OCR
        var ocrText = ""
        for i in 0..<min(doc.pageCount, 4) {
            if let page = doc.page(at: i), let cg = renderPageToCGImage(page) {
                ocrText += ocrImage(cg) + "\n"
            }
        }
        return Extraction(text: ocrText, usedOCR: true)
    }

    /// OCR síncrono (Vision) — `perform` bloqueia até concluir, então é seguro fora do main.
    nonisolated static func ocrImage(_ cgImage: CGImage) -> String {
        var output = ""
        let request = VNRecognizeTextRequest { req, _ in
            output = (req.results as? [VNRecognizedTextObservation])?.compactMap {
                $0.topCandidates(1).first?.string
            }.joined(separator: " ") ?? ""
        }
        request.recognitionLanguages = ["pt-BR", "en-US"]
        request.recognitionLevel = .accurate
        request.usesLanguageCorrection = true

        let handler = VNImageRequestHandler(cgImage: cgImage, options: [:])
        try? handler.perform([request])
        return output.trimmingCharacters(in: .whitespacesAndNewlines)
    }

    /// Renderiza uma página de PDF como bitmap para OCR (escala alta = OCR mais preciso).
    nonisolated static func renderPageToCGImage(_ page: PDFPage, scale: CGFloat = 3.0) -> CGImage? {
        let bounds = page.bounds(for: .mediaBox)
        // Limita o tamanho para não estourar memória em páginas grandes
        let cappedScale = min(scale, 4_000 / max(bounds.width, bounds.height, 1))
        let s = max(1.0, cappedScale)
        let width = Int(bounds.width * s)
        let height = Int(bounds.height * s)
        guard width > 0, height > 0,
              let ctx = CGContext(
                data: nil, width: width, height: height,
                bitsPerComponent: 8, bytesPerRow: 0,
                space: CGColorSpaceCreateDeviceRGB(),
                bitmapInfo: CGImageAlphaInfo.premultipliedLast.rawValue)
        else { return nil }

        ctx.setFillColor(CGColor(red: 1, green: 1, blue: 1, alpha: 1))
        ctx.fill(CGRect(x: 0, y: 0, width: width, height: height))
        ctx.scaleBy(x: s, y: s)
        page.draw(with: .mediaBox, to: ctx)
        return ctx.makeImage()
    }

    // MARK: - Similaridade (background)

    struct EntryFields: Sendable {
        let title: String
        let authors: String
        let venue: String
        let kind: String
        let portaria: String
        let edital: String
        let issn: String
        let doi: String
        let year: Int
        let endYear: Int
        let hashKey: String
    }

    /// Bônus aplicado quando o tipo da entrada combina com a pasta do certificado.
    private static let folderBonus = 0.20

    private static func tokens(_ s: String) -> Set<String> {
        Set(s.split(separator: " ").map(String.init))
    }

    /// Extrai anos plausíveis (1990–2035) de um texto.
    nonisolated static func yearsIn(_ text: String) -> Set<Int> {
        guard let re = try? NSRegularExpression(pattern: #"\b(19|20)\d{2}\b"#) else { return [] }
        let ns = text as NSString
        var years = Set<Int>()
        for m in re.matches(in: text, range: NSRange(location: 0, length: ns.length)) {
            if let y = Int(ns.substring(with: m.range)), y >= 1990, y <= 2035 { years.insert(y) }
        }
        return years
    }

    /// Ajusta o score conforme a proximidade entre o ano do certificado e o da entrada.
    private static func applyYear(_ score: Double, _ certYears: Set<Int>,
                                  _ entryYear: Int, _ entryEndYear: Int = 0) -> Double {
        guard score > 0, entryYear > 0, !certYears.isEmpty else { return score }
        // Período com faixa (vínculo/atividade): "Atual"/aberto vai até o ano corrente.
        if entryEndYear >= entryYear || entryEndYear == 0 {
            let endY = entryEndYear == 0
                ? Calendar.current.component(.year, from: Date()) : entryEndYear
            if entryEndYear != 0 || endY > entryYear {   // só trata como faixa se houver intervalo real
                if certYears.contains(where: { $0 >= entryYear && $0 <= endY }) {
                    return min(1.0, score + 0.06)        // dentro do período → reforço
                }
                let gap = certYears.map { min(abs($0 - entryYear), abs($0 - endY)) }.min() ?? 99
                if gap >= 3 { return score * 0.80 }
                return score
            }
        }
        // Ano único
        let gap = certYears.map { abs($0 - entryYear) }.min() ?? 99
        if gap == 0 { return min(1.0, score + 0.06) }   // mesmo ano → reforço
        if gap >= 3 { return score * 0.80 }             // distante → penaliza
        return score                                    // ±1/±2 → neutro
    }

    /// Retorna as melhores correspondências (desc) para um certificado.
    nonisolated static func rankedMatches(
        text: String, certKey: String, certYears: Set<Int>,
        entryFields: [EntryFields], folderKinds: Set<String>,
        idf: [String: Double], rejected: Set<String>
    ) -> [ScoredMatch] {
        guard !text.isEmpty else { return [] }
        let capped = String(text.prefix(4000))

        let certPort = SimilarityMatcher.portariaPairs(capped)
        let certEdital = SimilarityMatcher.editalNumbers(capped)
        let certDOI = SimilarityMatcher.doiNumbers(capped)
        let certISSN = SimilarityMatcher.issnNumbers(capped)
        let certIsPortaria = !certPort.isEmpty || capped.lowercased().contains("portaria")
        let certHasPubID = SimilarityMatcher.hasPublicationIdentifier(capped)

        var out: [ScoredMatch] = []
        for (idx, f) in entryFields.enumerated() {
            // Gates
            if (f.kind == "Artigo" || f.kind == "Livro/Capítulo"), !certHasPubID { continue }
            if certIsPortaria, f.kind == "Orientação" || f.kind == "Formação" { continue }
            // Rejeição aprendida (usuário já recusou este vínculo)
            if rejected.contains("\(certKey)||\(f.hashKey)") { continue }

            var score: Double
            var isIdentifier = false

            // Identificadores precisos (quase-certeza) — têm prioridade sobre texto.
            // Portaria casa por nº+ano (0.99) ou só nº quando falta o ano (0.95).
            let portScore = certPort.isEmpty || f.portaria.isEmpty
                ? 0 : SimilarityMatcher.portariaMatchScore(cert: certPort, entry: tokens(f.portaria))
            if portScore > 0 {
                score = portScore; isIdentifier = true
            } else if !certEdital.isEmpty, !f.edital.isEmpty, !certEdital.isDisjoint(with: tokens(f.edital)) {
                score = 0.99; isIdentifier = true
            } else if !certDOI.isEmpty, !f.doi.isEmpty, !certDOI.isDisjoint(with: tokens(f.doi)) {
                score = 1.0; isIdentifier = true
            } else {
                score = SimilarityMatcher.score(
                    certificateText: capped, title: f.title, authors: f.authors, venue: f.venue, idf: idf)
                // ISSN identifica o periódico (não o artigo) → reforça quando o título também casa
                if score > 0.2, !certISSN.isEmpty, !f.issn.isEmpty, !certISSN.isDisjoint(with: tokens(f.issn)) {
                    score = min(1.0, score + 0.15)
                }
            }

            // Ano (desambiguação) — usa a faixa do período quando há (vínculo/atividade)
            score = applyYear(score, certYears, f.year, f.endYear)

            if !isIdentifier {
                // Pasta indica a categoria provável; texto fica logo abaixo dos identificadores
                if !folderKinds.isEmpty, folderKinds.contains(f.kind), score > 0 {
                    score += folderBonus
                }
                score = min(0.97, score)
            }

            if score > 0 { out.append(ScoredMatch(index: idx, score: min(1.0, score))) }
        }
        return out.sorted { $0.score > $1.score }
    }

    /// Atribuição global: quando o top-1 e o top-2 de um certificado estão muito
    /// próximos e a entrada do top-1 já está bem coberta, prefere a entrada ainda
    /// descoberta — evita acúmulo de certificados numa mesma entrada genérica.
    private static func globalAssign(_ items: [RawItem], guessFloor: Double) -> [Int] {
        var coverage: [Int: Int] = [:]
        for r in items {
            if let top = r.ranked.first, top.score >= guessFloor {
                coverage[top.index, default: 0] += 1
            }
        }
        var chosen: [Int] = []
        for r in items {
            guard let top = r.ranked.first, top.score >= guessFloor else { chosen.append(-1); continue }
            var pick = 0
            if r.ranked.count >= 2 {
                let b = r.ranked[1]
                if top.score - b.score <= 0.08,
                   (coverage[top.index] ?? 0) >= 2, (coverage[b.index] ?? 0) == 0,
                   b.score >= guessFloor {
                    pick = 1
                    coverage[top.index]? -= 1
                    coverage[b.index, default: 0] += 1
                }
            }
            chosen.append(pick)
        }
        return chosen
    }

    /// Mapeia o nome da pasta (e subpastas) para os tipos de entrada prováveis.
    /// Ex.: pasta "Participação em Eventos" → tipos de evento.
    nonisolated static func inferFolderKinds(_ path: String) -> Set<String> {
        let n = path.folding(options: .diacriticInsensitive, locale: nil).lowercased()
        var k = Set<String>()
        func has(_ s: String) -> Bool { n.contains(s) }

        if has("banca")                              { k.insert("Banca") }
        if has("aprovacao")                          { k.insert("Vínculo institucional") }
        if has("evento") || has("apresenta") || has("poster") || has("debatedor")
            || has("mediador") || has("mesa") || has("palestra") || has("conferen")
            || has("congress") || has("coloquio") || has("simposio") || has("semana") {
            k.formUnion(["Evento", "Apresentação", "Organização de evento", "Trabalho em evento"])
        }
        if has("organizacao")                        { k.insert("Organização de evento") }
        if has("orienta") || has("monitoria")        { k.insert("Orientação") }
        if has("parecer") || has("tecnic")           { k.insert("Produção técnica") }
        if has("formacao") || has("curso") || has("alura") || has("lingua")
            || has("idioma") || has("capacita")      { k.insert("Formação") }
        if has("projeto") || has("extensao") || has("pesquisa") { k.insert("Projeto") }
        if has("premio") || has("titulo")            { k.insert("Prêmio/Título") }
        if has("edito")                              { k.formUnion(["Corpo editorial", "Mídia"]) }
        if has("didatica") || has("disciplina") || has("experiencia") || has("docencia") {
            k.formUnion(["Disciplina ministrada", "Vínculo institucional"])
        }
        if has("bolsa")                              { k.formUnion(["Formação", "Projeto"]) }
        return k
    }

    // MARK: - Coleta de arquivos (background)

    /// Coleta recursivamente TODAS as camadas de subpastas (sem limite de profundidade).
    nonisolated static func collectFiles(in url: URL) -> [URL] {
        let fm = FileManager.default
        let extensions = Set(["pdf", "jpg", "jpeg", "png", "tiff", "tif", "heic"])
        var results: [URL] = []

        // errorHandler: continua mesmo se um arquivo/pasta falhar (ex.: item do OneDrive
        // ainda não baixado), evitando que o escaneamento pare no meio.
        guard let enumerator = fm.enumerator(
            at: url,
            includingPropertiesForKeys: [.isRegularFileKey],
            options: [.skipsHiddenFiles],
            errorHandler: { _, _ in true }
        ) else { return [] }

        for case let fileURL as URL in enumerator {
            if extensions.contains(fileURL.pathExtension.lowercased()) {
                results.append(fileURL)
            }
        }
        return results.sorted { $0.path < $1.path }
    }
}
