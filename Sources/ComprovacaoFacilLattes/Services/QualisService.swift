import Foundation
import Compression

/// Classificação Qualis (CAPES) de periódicos por quadriênio e área de avaliação.
/// As tabelas (2016-2019, 2017-2020, 2021-2024) são empacotadas comprimidas e
/// indexadas sob demanda para a área escolhida pelo usuário.
@MainActor
final class QualisService: ObservableObject {
    static let shared = QualisService()

    @Published private(set) var isLoading = false
    @Published private(set) var version = 0          // muda quando um índice termina de carregar
    @Published private(set) var allAreas: [String] = []

    /// Área de avaliação selecionada (padrão: Filosofia).
    @Published var area: String {
        didSet {
            UserDefaults.standard.set(area, forKey: "qualisArea")
            if area != oldValue { reload() }
        }
    }

    private struct QuadIndex: Sendable {
        var byISSN: [String: String] = [:]
        var byTitle: [String: String] = [:]
        var fuzzy: [(tokens: Set<String>, estrato: String)] = []
    }
    private var cache: [String: QuadIndex] = [:]     // "quad|area" -> índice
    private var titleToISSN: [String: String] = [:]  // título normalizado -> ISSN (todas as áreas/quadriênios)

    nonisolated private static let quads = ["2016_2019", "2017_2020", "2021_2024"]

    private init() {
        self.area = UserDefaults.standard.string(forKey: "qualisArea") ?? "FILOSOFIA"
    }

    struct Result {
        let estrato: String      // ex.: "A1"
        let quadriennium: String // ex.: "2021-2024"
        let area: String
    }

    // MARK: - Carregamento

    /// Carrega (em background) os índices da área atual e a lista de áreas.
    func start() {
        reload()
    }

    private func reload() {
        let area = self.area
        isLoading = true
        Task.detached(priority: .utility) {
            var built: [String: QuadIndex] = [:]
            var areas: Set<String> = []
            var titleISSN: [String: String] = [:]
            for quad in Self.quads {
                if let r = Self.buildIndex(quad: quad, area: area) {
                    built["\(quad)|\(area)"] = r.index
                    areas.formUnion(r.areas)
                    titleISSN.merge(r.titleISSN) { a, _ in a }
                }
            }
            let result = built
            let areasList = areas.sorted()
            let titles = titleISSN
            await MainActor.run {
                self.cache = result
                self.titleToISSN = titles
                if !areasList.isEmpty { self.allAreas = areasList }
                self.isLoading = false
                self.version += 1
            }
        }
    }

    // MARK: - Classificação

    /// Classifica um periódico pelo ISSN (preferencial), título e ano de publicação.
    func classify(journal venue: String, issn: String, year: Int) -> Result? {
        let quad = Self.quadKey(forYear: year)
        guard let idx = cache["\(quad)|\(area)"] else { return nil }

        // 1) ISSN exato
        let issnKey = Self.normISSN(issn)
        if !issnKey.isEmpty, let e = idx.byISSN[issnKey] {
            return Result(estrato: e, quadriennium: Self.label(quad), area: area)
        }
        // 2) Título do periódico exato
        let journal = Self.normTitle(Self.journalName(from: venue))
        if !journal.isEmpty, let e = idx.byTitle[journal] {
            return Result(estrato: e, quadriennium: Self.label(quad), area: area)
        }
        // 2b) Resolve via ISSN cruzado (periódico renomeado entre quadriênios)
        if !journal.isEmpty, let crossISSN = titleToISSN[journal], let e = idx.byISSN[crossISSN] {
            return Result(estrato: e, quadriennium: Self.label(quad), area: area)
        }
        // 3) Aproximado por sobreposição de palavras
        let vTokens = Set(Self.normTitle(venue).split(separator: " ").map(String.init).filter { $0.count >= 3 })
        guard vTokens.count >= 2 else { return nil }
        var best: (Double, String)? = nil
        for entry in idx.fuzzy {
            let inter = entry.tokens.intersection(vTokens).count
            guard inter >= 2 else { continue }
            // cobertura em relação ao título do periódico (mais curto)
            let cov = Double(inter) / Double(min(entry.tokens.count, vTokens.count))
            if cov >= 0.8, best == nil || cov > best!.0 {
                best = (cov, entry.estrato)
            }
        }
        if let best { return Result(estrato: best.1, quadriennium: Self.label(quad), area: area) }
        return nil
    }

    // MARK: - Construção do índice

    nonisolated private static func buildIndex(
        quad: String, area: String
    ) -> (index: QuadIndex, areas: Set<String>, titleISSN: [String: String])? {
        guard let url = Bundle.module.url(forResource: "qualis_\(quad)", withExtension: "tsv.gz"),
              let gz = try? Data(contentsOf: url),
              let data = gunzip(gz),
              let text = String(data: data, encoding: .utf8)
        else { return nil }

        var idx = QuadIndex()
        var areas = Set<String>()
        var titleISSN: [String: String] = [:]
        text.enumerateLines { line, _ in
            let f = line.split(separator: "\t", omittingEmptySubsequences: false).map(String.init)
            guard f.count >= 4 else { return }
            let (issn, title, rowArea, estrato) = (f[0], f[1], f[2], f[3])
            areas.insert(rowArea)
            // Mapa global título→ISSN (todas as áreas) para resolver renomeações
            if !title.isEmpty, !issn.isEmpty { titleISSN[title] = issn }
            guard rowArea == area, !estrato.isEmpty else { return }
            if !issn.isEmpty { idx.byISSN[issn] = estrato }
            if !title.isEmpty {
                idx.byTitle[title] = estrato
                let toks = Set(title.split(separator: " ").map(String.init).filter { $0.count >= 3 })
                if toks.count >= 2 { idx.fuzzy.append((toks, estrato)) }
            }
        }
        return (idx, areas, titleISSN)
    }

    // MARK: - Helpers

    nonisolated static func quadKey(forYear year: Int) -> String {
        if year >= 2021 { return "2021_2024" }
        if year >= 2017 { return "2017_2020" }
        if year > 0 { return "2016_2019" }
        return "2021_2024"
    }

    nonisolated private static func label(_ quad: String) -> String {
        quad.replacingOccurrences(of: "_", with: "-")
    }

    nonisolated static func normISSN(_ s: String) -> String {
        s.uppercased().filter { $0.isNumber || $0 == "X" }
    }

    nonisolated static func normTitle(_ s: String) -> String {
        s.folding(options: [.diacriticInsensitive, .caseInsensitive], locale: Locale(identifier: "pt_BR"))
            .uppercased()
            .components(separatedBy: CharacterSet(charactersIn: "ABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789 ").inverted)
            .joined()
            .components(separatedBy: .whitespaces)
            .filter { !$0.isEmpty }
            .joined(separator: " ")
    }

    /// Extrai o nome do periódico do campo "venue", cortando volume/página/ano.
    nonisolated private static func journalName(from venue: String) -> String {
        var s = venue
        if let r = s.range(of: #"[.,]?\s*(v\.|n\.|p\.|vol\.|\bv\s*\d)"#,
                           options: [.regularExpression, .caseInsensitive]) {
            s = String(s[..<r.lowerBound])
        }
        return s.trimmingCharacters(in: .whitespacesAndNewlines)
    }

    // MARK: - gunzip (Compression / raw DEFLATE)

    nonisolated private static func gunzip(_ data: Data) -> Data? {
        let b = [UInt8](data)
        guard b.count > 18, b[0] == 0x1f, b[1] == 0x8b, b[2] == 0x08 else { return nil }
        // Cabeçalho gzip de tamanho variável: pula campos opcionais (FEXTRA/FNAME/…)
        let flg = b[3]
        var pos = 10
        if flg & 0x04 != 0 {                                  // FEXTRA
            guard pos + 2 <= b.count else { return nil }
            pos += 2 + (Int(b[pos]) | (Int(b[pos + 1]) << 8))
        }
        if flg & 0x08 != 0 { while pos < b.count, b[pos] != 0 { pos += 1 }; pos += 1 } // FNAME
        if flg & 0x10 != 0 { while pos < b.count, b[pos] != 0 { pos += 1 }; pos += 1 } // FCOMMENT
        if flg & 0x02 != 0 { pos += 2 }                        // FHCRC
        guard pos < b.count - 8 else { return nil }

        // ISIZE = tamanho descomprimido (mod 2^32), nos últimos 4 bytes (little-endian)
        let tail = Array(data.suffix(4))
        let isize = Int(tail[0]) | (Int(tail[1]) << 8) | (Int(tail[2]) << 16) | (Int(tail[3]) << 24)
        let deflate = data.subdata(in: (data.startIndex + pos)..<(data.endIndex - 8))
        let capacity = max(isize, deflate.count * 6) + 4096
        var dst = Data(count: capacity)
        let count = dst.withUnsafeMutableBytes { dstPtr -> Int in
            deflate.withUnsafeBytes { srcPtr in
                compression_decode_buffer(
                    dstPtr.bindMemory(to: UInt8.self).baseAddress!, capacity,
                    srcPtr.bindMemory(to: UInt8.self).baseAddress!, deflate.count,
                    nil, COMPRESSION_ZLIB)
            }
        }
        guard count > 0 else { return nil }
        return dst.prefix(count)
    }
}
