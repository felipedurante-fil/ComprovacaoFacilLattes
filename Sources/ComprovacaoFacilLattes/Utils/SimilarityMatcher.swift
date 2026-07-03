import Foundation

/// Mede o quanto um certificado comprova uma entrada do Lattes.
///
/// Lições aprendidas (currículo do próprio docente):
///  • O nome do autor NÃO é discriminante — ele aparece em quase todas as entradas
///    e em quase todos os seus certificados. Por isso o autor é ignorado no score.
///  • O sinal forte é o **título** aparecendo no certificado, com destaque para
///    trechos contíguos (frase) do título. Para eventos, o **nome do evento/local**
///    é o sinal alternativo (o título do trabalho pode não constar no certificado).
enum SimilarityMatcher {

    private static let stopwords: Set<String> = [
        "para", "por", "com", "dos", "das", "uma", "que", "nas", "nos",
        "ao", "aos", "pela", "pelo", "the", "and", "of", "in", "on", "de",
        "do", "da", "em", "no", "na", "os", "as", "um", "se", "sua", "seu",
        "como", "sobre", "entre", "ou", "etc", "este", "esta", "pelos", "pelas",
        "anais", "revista", "journal", "vol",
        // Termos administrativos — não identificam um comprovante específico
        "carga", "horaria", "hora", "horas", "outras", "informacoes",
        "atual", "nivel", "regime", "certificado", "certificamos",
        "declaracao", "declaramos", "total",
    ]

    // MARK: - IDF (raridade de palavras)

    /// Constrói pesos IDF a partir dos títulos das entradas: palavras raras valem mais,
    /// palavras comuns ("filosofia", "universidade", "silva") valem quase nada.
    static func buildIDF(from titles: [String]) -> [String: Double] {
        let n = max(titles.count, 1)
        var df: [String: Int] = [:]
        for title in titles {
            let toks = Set(normalize(title).split(separator: " ").map(String.init).filter { isMeaningful($0) })
            for t in toks { df[t, default: 0] += 1 }
        }
        var idf: [String: Double] = [:]
        for (t, c) in df {
            idf[t] = log(Double(n) / Double(1 + c)) + 0.5   // sempre ≥ ~0.5
        }
        return idf
    }

    private static func weight(_ token: String, _ idf: [String: Double]?) -> Double {
        guard let idf else { return 1.0 }
        return idf[token] ?? (log(Double(idf.count == 0 ? 2 : idf.count)) + 0.5)  // raro/desconhecido: peso alto
    }

    // MARK: - API

    /// Score 0.0–1.0 entre o texto do certificado e os campos da entrada.
    /// `idf` (opcional) pondera as palavras pela raridade.
    static func score(certificateText: String, title: String, authors: String, venue: String,
                      idf: [String: Double]? = nil) -> Double {
        let cert = normalize(certificateText)
        guard !cert.isEmpty else { return 0 }
        let certTokens = Set(cert.split(separator: " ").map(String.init))

        // --- Sinal de TÍTULO (cobertura ponderada por IDF) ---
        let titleWords = normalize(title).split(separator: " ").map(String.init)
        let titleSig = titleWords.filter { isMeaningful($0) }
        let matched = titleSig.filter { certTokens.contains($0) }
        let totalWeight = titleSig.reduce(0.0) { $0 + weight($1, idf) }
        let matchedWeight = matched.reduce(0.0) { $0 + weight($1, idf) }
        let tokenCov = totalWeight > 0 ? matchedWeight / totalWeight : 0

        var phrase = 0.0
        if !matched.isEmpty { phrase = phraseFraction(titleWords, cert) }

        // Para títulos curtos (≤2 palavras) só vale a FRASE contígua — palavras soltas
        // genéricas ("Doutorado em Filosofia") não devem casar.
        var titleScore = phrase
        if titleSig.count >= 3 { titleScore = max(titleScore, tokenCov) }
        if matched.count < 2 && phrase < 0.6 { titleScore = 0 }

        // --- Sinal de LOCAL/EVENTO ---
        let venueSig = normalize(venue).split(separator: " ").map(String.init)
            .filter { isMeaningful($0) }
        let venueMatched = venueSig.filter { certTokens.contains($0) }
        let venueTotalW = venueSig.reduce(0.0) { $0 + weight($1, idf) }
        let venueMatchedW = venueMatched.reduce(0.0) { $0 + weight($1, idf) }
        let venueCov = venueTotalW > 0 ? venueMatchedW / venueTotalW : 0

        var score = titleScore
        if titleScore < 0.4 && venueCov >= 0.6 && venueMatched.count >= 2 {
            // Eventos: o certificado bate o nome do evento, não o título do trabalho
            score = venueCov * 0.8
        } else if venueCov >= 0.5 && titleScore >= 0.3 {
            // Reforço quando título e local aparecem juntos
            score = min(1.0, titleScore + 0.1)
        }
        return min(1.0, score)
    }

    /// Overload simples (texto único como "título").
    static func score(certificateText: String, entryText: String) -> Double {
        score(certificateText: certificateText, title: entryText, authors: "", venue: "")
    }

    // MARK: - Internos

    /// Indica se o texto contém um identificador de publicação (ISSN, ISBN ou DOI).
    /// Usado para impedir que documentos sem esses dados sejam vinculados a artigos/livros.
    static func hasPublicationIdentifier(_ text: String) -> Bool {
        let lower = text.lowercased()
        // Exige a palavra-chave (ISSN/ISBN/DOI) ou um DOI explícito — evita confundir
        // intervalos de ano ("2019-2024") com ISSN.
        if lower.contains("issn") || lower.contains("isbn") || lower.contains("doi") { return true }
        if text.range(of: #"10\.\d{4,}/"#, options: .regularExpression) != nil { return true } // DOI
        return false
    }

    /// Extrai números de portaria de um texto (ex.: "PORTARIA N 2090, DE…" → {"2090"}).
    static func portariaNumbers(_ text: String) -> Set<String> {
        Set(captureGroups(text, pattern: #"portaria\s*(?:n[º°o.]?\s*)?(\d{2,6})"#))
    }

    /// Extrai pares portaria nº+ano (ex.: "PORTARIA N 2891, DE 06 DE OUTUBRO DE 2022"
    /// → {"2891/2022"}). Quando não há ano próximo, devolve só o número ("2891").
    /// O ano é buscado nos ~50 caracteres seguintes ao número.
    static func portariaPairs(_ text: String) -> Set<String> {
        guard let re = try? NSRegularExpression(
            pattern: #"portaria\s*(?:n[º°o.]?\s*)?(\d{2,6})([\s\S]{0,50}?\b(?:19|20)\d{2}\b)?"#,
            options: .caseInsensitive) else { return [] }
        let ns = text as NSString
        var out = Set<String>()
        for m in re.matches(in: text, range: NSRange(location: 0, length: ns.length)) where m.numberOfRanges > 1 {
            let num = ns.substring(with: m.range(at: 1))
            var year = ""
            if m.numberOfRanges > 2, m.range(at: 2).location != NSNotFound,
               let yr = ns.substring(with: m.range(at: 2)).range(of: #"(19|20)\d{2}"#, options: .regularExpression) {
                year = String(ns.substring(with: m.range(at: 2))[yr])
            }
            out.insert(year.isEmpty ? num : "\(num)/\(year)")
        }
        return out
    }

    /// Pontua a coincidência de portarias entre certificado e entrada:
    /// nº+ano iguais → 0.99; só o número (um dos lados sem ano) → 0.95;
    /// mesmo número mas anos diferentes → 0 (não é a mesma portaria).
    static func portariaMatchScore(cert: Set<String>, entry: Set<String>) -> Double {
        func split(_ s: String) -> (String, String?) {
            let p = s.split(separator: "/", maxSplits: 1).map(String.init)
            return (p[0], p.count > 1 ? p[1] : nil)
        }
        var best = 0.0
        for c in cert {
            let (cn, cy) = split(c)
            for e in entry {
                let (en, ey) = split(e)
                guard cn == en else { continue }
                if let cy, let ey {
                    if cy == ey { best = max(best, 0.99) }   // nº+ano exatos
                    // anos presentes e diferentes → ignora (portaria distinta)
                } else {
                    best = max(best, 0.95)                    // só o número
                }
            }
        }
        return best
    }

    /// Extrai números de edital (ex.: "Edital nº 41/2024-PROGRAD" → {"41/2024"}).
    static func editalNumbers(_ text: String) -> Set<String> {
        captureGroups(text, pattern: #"edital\s*(?:n[º°o.]?\s*)?(\d{1,4}\s*/\s*\d{2,4})"#)
            .map { $0.replacingOccurrences(of: " ", with: "") }
            .reduce(into: Set<String>()) { $0.insert($1) }
    }

    /// Extrai ISSNs rotulados (ex.: "ISSN: 2179-3786"). Exige a palavra "ISSN" por
    /// perto para não confundir com intervalos de ano ("2019-2024").
    static func issnNumbers(_ text: String) -> Set<String> {
        captureGroups(text, pattern: #"issn[:\s]*(\d{4}\s*-\s*\d{3}[\dxX])"#)
            .map { $0.replacingOccurrences(of: " ", with: "").uppercased() }
            .reduce(into: Set<String>()) { $0.insert($1) }
    }

    /// Extrai DOIs (ex.: "10.1234/abc").
    static func doiNumbers(_ text: String) -> Set<String> {
        captureGroups(text, pattern: #"(10\.\d{4,}/[^\s,;]+)"#)
            .map { $0.lowercased() }
            .reduce(into: Set<String>()) { $0.insert($1) }
    }

    private static func captureGroups(_ text: String, pattern: String) -> [String] {
        guard let re = try? NSRegularExpression(pattern: pattern, options: .caseInsensitive) else { return [] }
        let ns = text as NSString
        var out: [String] = []
        for m in re.matches(in: text, range: NSRange(location: 0, length: ns.length)) where m.numberOfRanges > 1 {
            out.append(ns.substring(with: m.range(at: 1)))
        }
        return out
    }

    /// Palavra capaz de identificar (≥4 letras, não stopword, não puramente numérica).
    private static func isMeaningful(_ token: String) -> Bool {
        token.count >= 4 && !stopwords.contains(token) && !token.allSatisfy(\.isNumber)
    }

    static func normalize(_ text: String) -> String {
        text
            .folding(options: [.diacriticInsensitive, .caseInsensitive],
                     locale: Locale(identifier: "pt_BR"))
            .lowercased()
            .components(separatedBy: CharacterSet.alphanumerics.inverted)
            .filter { !$0.isEmpty }
            .joined(separator: " ")
    }

    /// Maior trecho contíguo de palavras do título presente no certificado (normalizado).
    /// Usa TODAS as palavras (inclusive preposições), pois o certificado também as tem.
    private static func phraseFraction(_ words: [String], _ cert: String) -> Double {
        guard words.count >= 2 else { return 0 }
        let maxLen = min(words.count, 10)
        var best = 0
        for start in 0..<words.count {
            var len = min(maxLen, words.count - start)
            while len >= 2 {
                let phrase = words[start..<start + len].joined(separator: " ")
                if cert.contains(phrase) { best = max(best, len); break }
                len -= 1
            }
            if best == maxLen { break }
        }
        return Double(best) / Double(maxLen)
    }
}
