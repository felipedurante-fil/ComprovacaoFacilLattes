import Foundation
import PDFKit
import AppKit
import CoreText

/// Gera o PDF final em A4 retrato:
///   1. Sumário com a página de cada item comprobatório
///   2. Currículo Lattes completo
///   3. Comprovantes na ordem do Lattes, com página de legenda (divisória) por seção
/// Todas as páginas são numeradas (a partir de 1) no canto superior direito, com o
/// número em branco dentro de uma caixa preta para ficar sempre visível.
struct PDFReportGenerator {

    struct ReportConfig {
        var profile: LattesProfile
        var selectedSectionTitles: Set<String>   // vazio = todas
        var startYear: Int?
        var endYear: Int?
        var includeLattes: Bool = true
        var qualisByEntry: [UUID: String] = [:]  // id da entrada → "A1" etc.
    }

    private static let pageSize = CGSize(width: 595, height: 842)   // A4 retrato

    /// Uma página do documento final: conteúdo externo (PDF/imagem) ou desenhada pelo app.
    private enum SlabKind {
        case external(PDFPage)
        case custom((CGContext) -> Void)
    }

    /// `showsNumber == false` → a página é contada, mas o número não é impresso
    /// (caso das divisórias de seção).
    private struct Slab {
        let kind: SlabKind
        var showsNumber: Bool = true
    }

    private struct TOCItem {
        let title: String
        let level: Int          // 0 = seção, 1 = item
        let bodyIndex: Int      // posição no corpo (antes do sumário)
        let isSection: Bool
    }

    // MARK: - Geração

    static func generate(config: ReportConfig) -> Data? {
        var body: [Slab] = []
        var toc: [TOCItem] = []

        // 1 — Currículo Lattes
        if config.includeLattes,
           let lattesDoc = PDFDocument(url: URL(fileURLWithPath: config.profile.pdfPath)) {
            toc.append(TOCItem(title: "Currículo Lattes (completo)", level: 0,
                               bodyIndex: body.count, isSection: true))
            for i in 0..<lattesDoc.pageCount {
                if let p = lattesDoc.page(at: i) { body.append(Slab(kind: .external(p))) }
            }
        }

        // 2 — Seções e comprovantes
        let sections = config.profile.sortedSections.filter { section in
            config.selectedSectionTitles.isEmpty
                || config.selectedSectionTitles.contains(section.title)
        }

        for section in sections {
            let entries = filteredEntries(section.sortedEntries, config: config)
                .filter { !$0.confirmedCertificates.isEmpty }
            guard !entries.isEmpty else { continue }

            // Página de legenda (divisória) da seção — contada, mas sem número impresso
            let sectionTitle = section.title
            toc.append(TOCItem(title: sectionTitle, level: 0, bodyIndex: body.count, isSection: true))
            body.append(Slab(kind: .custom { ctx in drawDivider(ctx, title: sectionTitle) },
                             showsNumber: false))

            for entry in entries {
                let label = entry.displayTitle
                let qualis = config.qualisByEntry[entry.id]
                toc.append(TOCItem(title: label, level: 1, bodyIndex: body.count, isSection: false))
                body.append(Slab(kind: .custom { ctx in drawEntryHeader(ctx, entry: entry, qualis: qualis) }))

                for cert in entry.confirmedCertificates where cert.exists {
                    if cert.isPDF, let cdoc = PDFDocument(url: cert.fileURL) {
                        for i in 0..<cdoc.pageCount {
                            if let p = cdoc.page(at: i) { body.append(Slab(kind: .external(p))) }
                        }
                    } else if cert.isImage, let img = NSImage(contentsOf: cert.fileURL) {
                        body.append(Slab(kind: .custom { ctx in drawImage(ctx, image: img) }))
                    }
                }
            }
        }

        guard !body.isEmpty else { return nil }

        // 3 — Sumário (precisa do nº de páginas que ele próprio ocupa)
        let tocPageCount = tocPageCount(for: toc.count)
        let tocSlabs = buildTOCSlabs(toc, tocPageCount: tocPageCount)

        // 4 — Montagem final com numeração
        let all = tocSlabs + body
        return render(all)
    }

    // MARK: - Filtro por período

    private static func filteredEntries(_ entries: [LattesEntry], config: ReportConfig) -> [LattesEntry] {
        entries.filter { entry in
            if let s = config.startYear, entry.year > 0, entry.year < s { return false }
            if let e = config.endYear,   entry.year > 0, entry.year > e { return false }
            return true
        }
    }

    // MARK: - Sumário

    private static let tocLinesFirstPage = 30   // 1ª página tem cabeçalho
    private static let tocLinesPerPage = 36

    private static func tocPageCount(for count: Int) -> Int {
        if count <= tocLinesFirstPage { return 1 }
        return 1 + Int(ceil(Double(count - tocLinesFirstPage) / Double(tocLinesPerPage)))
    }

    private static func buildTOCSlabs(_ toc: [TOCItem], tocPageCount: Int) -> [Slab] {
        // Número final de cada item = páginas do sumário + posição no corpo + 1
        struct Line { let text: String; let page: Int; let level: Int; let isSection: Bool }
        let lines = toc.map {
            Line(text: $0.title, page: tocPageCount + $0.bodyIndex + 1,
                 level: $0.level, isSection: $0.isSection)
        }

        // Pagina as linhas
        var chunks: [[Line]] = []
        var idx = 0
        while idx < lines.count {
            let cap = chunks.isEmpty ? tocLinesFirstPage : tocLinesPerPage
            let end = min(idx + cap, lines.count)
            chunks.append(Array(lines[idx..<end]))
            idx = end
        }
        if chunks.isEmpty { chunks = [[]] }

        return chunks.enumerated().map { (pageIdx, chunk) in
            Slab(kind: .custom { ctx in
                var y = pageSize.height - 56
                if pageIdx == 0 {
                    drawText("Sumário", font: .boldSystemFont(ofSize: 24), color: .black,
                             in: CGRect(x: 50, y: y - 30, width: pageSize.width - 100, height: 30), ctx: ctx)
                    y -= 56
                }
                let lineH: CGFloat = 19
                for line in chunk {
                    let indent: CGFloat = line.level == 0 ? 50 : 78
                    let font: NSFont = line.isSection
                        ? .boldSystemFont(ofSize: 12.5) : .systemFont(ofSize: 11)
                    drawText(line.text, font: font, color: line.isSection ? .black : .darkGray,
                             in: CGRect(x: indent, y: y - lineH, width: pageSize.width - indent - 80, height: lineH),
                             ctx: ctx, truncate: true)
                    drawText("\(line.page)", font: font, color: .black,
                             in: CGRect(x: pageSize.width - 74, y: y - lineH, width: 56, height: lineH),
                             ctx: ctx, alignment: .right)
                    y -= lineH
                }
            })
        }
    }

    // MARK: - Páginas desenhadas

    private static func drawDivider(_ ctx: CGContext, title: String) {
        // Fundo branco (economia de tinta na impressão)
        ctx.setFillColor(.white)
        ctx.fill(CGRect(origin: .zero, size: pageSize))
        let midY = pageSize.height / 2
        // Rótulo "SEÇÃO" e linha fina
        drawText("SEÇÃO", font: .systemFont(ofSize: 13, weight: .semibold),
                 color: NSColor(red: 0.25, green: 0.50, blue: 0.90, alpha: 1),
                 in: CGRect(x: 40, y: midY + 64, width: pageSize.width - 80, height: 22),
                 ctx: ctx, alignment: .center)
        ctx.setFillColor(red: 0.25, green: 0.50, blue: 0.90, alpha: 1)
        ctx.fill(CGRect(x: 90, y: midY + 56, width: pageSize.width - 180, height: 2.5))
        // Título (quebra em várias linhas, ancorado logo abaixo da linha)
        drawText(title, font: .boldSystemFont(ofSize: 26), color: .black,
                 in: CGRect(x: 50, y: midY - 70, width: pageSize.width - 100, height: 120),
                 ctx: ctx, alignment: .center)
    }

    private static func drawEntryHeader(_ ctx: CGContext, entry: LattesEntry, qualis: String?) {
        ctx.setFillColor(red: 0.96, green: 0.97, blue: 1.0, alpha: 1)
        ctx.fill(CGRect(origin: .zero, size: pageSize))
        ctx.setFillColor(red: 0.25, green: 0.50, blue: 0.90, alpha: 1)
        ctx.fill(CGRect(x: 0, y: pageSize.height - 6, width: pageSize.width, height: 6))

        var header = entry.section?.title ?? ""
        if let qualis { header += "   •   Qualis \(qualis)" }
        drawText(header, font: .systemFont(ofSize: 13), color: .gray,
                 in: CGRect(x: 60, y: pageSize.height - 80, width: pageSize.width - 120, height: 20), ctx: ctx)
        drawText(entry.displayTitle, font: .boldSystemFont(ofSize: 18), color: .black,
                 in: CGRect(x: 60, y: pageSize.height - 230, width: pageSize.width - 120, height: 140), ctx: ctx)
        if !entry.authors.isEmpty {
            drawText(entry.authors, font: .systemFont(ofSize: 12), color: .darkGray,
                     in: CGRect(x: 60, y: pageSize.height - 320, width: pageSize.width - 120, height: 70), ctx: ctx)
        }
    }

    private static func drawImage(_ ctx: CGContext, image: NSImage) {
        guard let cg = image.cgImage(forProposedRect: nil, context: nil, hints: nil) else { return }
        let iw = CGFloat(cg.width), ih = CGFloat(cg.height)
        let scale = min((pageSize.width - 40) / iw, (pageSize.height - 60) / ih)
        let dw = iw * scale, dh = ih * scale
        ctx.draw(cg, in: CGRect(x: (pageSize.width - dw) / 2, y: (pageSize.height - dh) / 2, width: dw, height: dh))
    }

    // MARK: - Montagem + numeração

    private static func render(_ slabs: [Slab]) -> Data? {
        let data = NSMutableData()
        guard let consumer = CGDataConsumer(data: data as CFMutableData) else { return nil }
        var box = CGRect(origin: .zero, size: pageSize)
        guard let ctx = CGContext(consumer: consumer, mediaBox: &box, nil) else { return nil }

        for (i, slab) in slabs.enumerated() {
            ctx.beginPDFPage(nil)
            switch slab.kind {
            case .external(let page): drawFitted(page, ctx: ctx)
            case .custom(let draw):   draw(ctx)
            }
            // A página é sempre contada (i + 1); o número só é impresso quando permitido.
            if slab.showsNumber {
                drawPageNumber(ctx, number: i + 1)
            }
            ctx.endPDFPage()
        }
        ctx.closePDF()
        return data as Data
    }

    /// Encaixa uma página externa em A4 retrato, preservando proporção (e rotação).
    private static func drawFitted(_ page: PDFPage, ctx: CGContext) {
        let src = page.bounds(for: .mediaBox)
        guard src.width > 0, src.height > 0 else { return }
        var vw = src.width, vh = src.height
        if page.rotation == 90 || page.rotation == 270 { swap(&vw, &vh) }
        let scale = min(pageSize.width / vw, pageSize.height / vh)
        let dw = vw * scale, dh = vh * scale

        ctx.saveGState()
        ctx.translateBy(x: (pageSize.width - dw) / 2, y: (pageSize.height - dh) / 2)
        ctx.scaleBy(x: scale, y: scale)
        page.draw(with: .mediaBox, to: ctx)
        ctx.restoreGState()
    }

    /// Número de página: caixa preta no canto superior direito, número branco.
    private static func drawPageNumber(_ ctx: CGContext, number: Int) {
        let label = "\(number)"
        let boxH: CGFloat = 22
        let boxW: CGFloat = max(30, CGFloat(label.count) * 9 + 16)
        let margin: CGFloat = 16
        let rect = CGRect(x: pageSize.width - boxW - margin,
                          y: pageSize.height - boxH - margin, width: boxW, height: boxH)
        // Caixa preta com número branco; borda branca garante visibilidade em fundo escuro
        let path = CGPath(roundedRect: rect, cornerWidth: 4, cornerHeight: 4, transform: nil)
        ctx.setFillColor(.black)
        ctx.addPath(path); ctx.fillPath()
        ctx.setStrokeColor(.white)
        ctx.setLineWidth(1.5)
        ctx.addPath(path); ctx.strokePath()
        drawText(label, font: .boldSystemFont(ofSize: 13), color: .white,
                 in: rect.insetBy(dx: 0, dy: 3), ctx: ctx, alignment: .center)
    }

    // MARK: - Texto (CoreText com flip)

    private static func drawText(_ text: String, font: NSFont, color: NSColor,
                                 in rect: CGRect, ctx: CGContext,
                                 alignment: NSTextAlignment = .left, truncate: Bool = false) {
        let para = NSMutableParagraphStyle()
        para.alignment = alignment
        if truncate { para.lineBreakMode = .byTruncatingTail }
        let attrs: [NSAttributedString.Key: Any] = [
            .font: font, .foregroundColor: color, .paragraphStyle: para,
        ]
        let attr = NSAttributedString(string: text, attributes: attrs)

        // O contexto PDF do CGContext já tem origem no canto inferior esquerdo (y-up),
        // que é o esperado pelo CoreText — não é preciso inverter.
        ctx.saveGState()
        ctx.textMatrix = .identity
        let fs = CTFramesetterCreateWithAttributedString(attr)
        let frame = CTFramesetterCreateFrame(fs, CFRangeMake(0, 0),
                                             CGPath(rect: rect, transform: nil), nil)
        CTFrameDraw(frame, ctx)
        ctx.restoreGState()
    }
}
