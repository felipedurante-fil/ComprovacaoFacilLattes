import Foundation
import PDFKit

/// Converte o PDF exportado do Lattes em seções e entradas estruturadas.
/// Calibrado para o layout real do currículo (CNPq), incluindo:
///  • truncamento no resumo "Totais de produção";
///  • colunas de numeração achatadas ("1. 2. 3. … N. conteúdo");
///  • hierarquia concluídas / em andamento;
///  • Atuação profissional (vínculos + disciplinas ministradas);
///  • remoção de rodapés (URL/paginação) que poluem o texto.
struct LattesPDFParser {

    struct ParseResult {
        var profileName: String
        var sections: [(title: String, entries: [ParsedEntry])]
        var rawText: String
    }

    struct ParsedEntry {
        var rawText: String
        var title: String
        var kind: String
        var year: Int
        var authors: String
        var venue: String
        var doi: String
        var isbn: String
        var order: Int
        var portaria: String = ""   // portarias associadas, "nº/ano" (separadas por espaço)
        var issn: String = ""       // ISSN(s) do periódico
        var edital: String = ""     // número(s) de edital (ex.: "41/2024")
        var endYear: Int = 0        // ano final do período (vínculos/atividades); 0 = sem/aberto
    }

    // MARK: - Entrada principal

    static func parse(url: URL) -> ParseResult? {
        guard let doc = PDFDocument(url: url) else { return nil }
        var fullText = ""
        for i in 0..<doc.pageCount {
            if let str = doc.page(at: i)?.string { fullText += str + "\n" }
        }

        let name = extractName(from: fullText)
        var sections = buildSections(from: fullText)

        // Remove entradas-lixo (fragmentos sem conteúdo distintivo) que poluiriam
        // a indexação — ex.: "(Carga horária: 20h).", "01 12 2023.", "DURANTE, F.. Ep."
        let ownerStop = Set(SimilarityMatcher.normalize(name)
            .split(separator: " ").map(String.init).filter { $0.count >= 4 })
        sections = sections.compactMap { sec in
            let kept = sec.entries.filter { !isJunkEntry($0, ownerStop: ownerStop) }
            return kept.isEmpty ? nil : (title: sec.title, entries: kept)
        }

        return ParseResult(profileName: name, sections: sections, rawText: fullText)
    }

    private static let adminWords: Set<String> = [
        "carga", "horaria", "hora", "horas", "episodio", "vol", "num", "pagina",
        "paginas", "total", "certificado", "certificamos", "declaracao", "declaramos",
        "outras", "informacoes", "nivel", "regime", "atual",
    ]

    /// Uma entrada é "lixo" se, descontados nome do dono, termos administrativos e
    /// números, não sobra nenhuma palavra capaz de identificá-la.
    private static func isJunkEntry(_ e: ParsedEntry, ownerStop: Set<String>) -> Bool {
        let toks = SimilarityMatcher.normalize("\(e.title) \(e.venue)")
            .split(separator: " ").map(String.init)
            .filter { $0.count >= 4 && !adminWords.contains($0) && !ownerStop.contains($0)
                && !$0.allSatisfy(\.isNumber) }
        return toks.isEmpty
    }

    // MARK: - Nome

    private static func extractName(from text: String) -> String {
        let lines = text.components(separatedBy: "\n").map { $0.trimmingCharacters(in: .whitespaces) }
        // 1. O nome aparece imediatamente acima de "Endereço para acessar este CV".
        if let idx = lines.firstIndex(where: { $0.contains("Endereço para acessar este CV") }) {
            var j = idx - 1
            while j >= 0 {
                if looksLikeName(lines[j]) { return lines[j] }
                if !lines[j].isEmpty { break }   // linha não-vazia que não é nome → para
                j -= 1
            }
        }
        // 2. Linha "Nome <Fulano>" da Identificação
        for t in lines where t.hasPrefix("Nome ") {
            let rest = String(t.dropFirst(5)).trimmingCharacters(in: .whitespaces)
            if rest.count >= 3, rest.count < 70,
               !rest.lowercased().hasPrefix("em "), !rest.lowercased().contains("citaç") {
                return rest
            }
        }
        // 3. Fallback: primeira linha que pareça um nome
        for t in lines where looksLikeName(t) { return t }
        return "Currículo Lattes"
    }

    /// Heurística para reconhecer uma linha que é um nome de pessoa (e não cabeçalho,
    /// URL, frase do topo "… anotou o Qualis …", etc.).
    private static func looksLikeName(_ t: String) -> Bool {
        guard t.count >= 5, t.count <= 70 else { return false }
        if t.contains("http") || t.contains(":") || t.contains("@") || t.contains("/") { return false }
        if t.rangeOfCharacter(from: .decimalDigits) != nil { return false }
        let low = t.lowercased()
        for bad in ["curríc", "curric", "anotou", "ver artigos", "visualizar", "endereço"]
            where low.contains(bad) { return false }
        return t.split(separator: " ").filter { $0.count >= 2 }.count >= 2
    }

    // MARK: - Classificação de cabeçalhos

    private enum HeaderClass {
        case stop
        case excluded
        case groupConcluidas
        case groupAndamento
        case section(display: String, special: Special, isChild: Bool)
    }
    private enum Special { case none, atuacao, banca, projetos, organizacao, eventos, premios, formacao }

    /// Cabeçalhos de seções que CONTÊM entradas (alias normalizado → exibição).
    private static let sectionTable: [(alias: String, display: String, special: Special, isChild: Bool)] = [
        ("formacao academica/titulacao", "Formação acadêmica/titulação", .formacao, false),
        ("pos-doutorado", "Pós-doutorado", .formacao, false),
        ("formacao complementar", "Formação complementar", .none, false),
        ("premios e titulos", "Prêmios e títulos", .premios, false),
        ("atuacao profissional", "Atuação profissional", .atuacao, false),
        ("projetos de pesquisa", "Projetos de pesquisa", .projetos, false),
        ("projeto de pesquisa", "Projetos de pesquisa", .projetos, false),
        ("projetos de extensao", "Projetos de extensão", .projetos, false),
        ("projeto de extensao", "Projetos de extensão", .projetos, false),
        ("projetos de desenvolvimento", "Projetos de desenvolvimento", .projetos, false),
        ("membro de corpo editorial", "Membro de corpo editorial", .none, false),
        ("artigos completos publicados em periodicos", "Artigos completos publicados em periódicos", .none, false),
        ("artigos aceitos para publicacao", "Artigos aceitos para publicação", .none, false),
        ("livros publicados", "Livros publicados", .none, false),
        ("capitulos de livros publicados", "Capítulos de livros publicados", .none, false),
        ("trabalhos publicados em anais de eventos", "Trabalhos publicados em anais de eventos", .none, false),
        ("trabalhos completos publicados em anais de congressos", "Trabalhos completos publicados em anais", .none, false),
        ("resumos expandidos publicados em anais de congressos", "Resumos expandidos publicados em anais", .none, false),
        ("resumos publicados em anais de congressos", "Resumos publicados em anais", .none, false),
        ("textos em jornais de noticias/revistas", "Textos em jornais de notícias/revistas", .none, false),
        ("apresentacao de trabalho e palestra", "Apresentação de trabalho e palestra", .none, false),
        ("apresentacoes de trabalho", "Apresentação de trabalho e palestra", .none, false),
        ("apresentacao de trabalho", "Apresentação de trabalho e palestra", .none, false),
        ("outras producoes bibliograficas", "Outras produções bibliográficas", .none, false),
        ("trabalhos tecnicos", "Trabalhos técnicos", .none, false),
        ("demais tipos de producao tecnica", "Demais produções técnicas", .none, false),
        ("entrevistas, mesas redondas, programas e comentarios na midia",
         "Entrevistas, mesas redondas e programas na mídia", .none, false),
        ("demais producoes tecnicas", "Demais produções técnicas", .none, false),
        ("produtos tecnicos", "Produtos técnicos", .none, false),
        ("programas de computador", "Programas de computador", .none, false),
        ("participacao em eventos", "Participação em eventos", .eventos, false),
        ("organizacao de evento", "Organização de eventos", .organizacao, false),
        ("participacao em banca de trabalhos de conclusao", "Participação em bancas (trabalhos de conclusão)", .banca, false),
        ("participacao em bancas de trabalhos de conclusao", "Participação em bancas (trabalhos de conclusão)", .banca, false),
        ("participacao em banca de comissoes julgadoras", "Participação em bancas (comissões julgadoras)", .banca, false),
        ("participacao em bancas de comissoes julgadoras", "Participação em bancas (comissões julgadoras)", .banca, false),
        // Filhos de Orientações (recebem sufixo concluídas / em andamento)
        ("dissertacoes de mestrado: orientador principal", "Dissertações de mestrado", .none, true),
        ("dissertacoes de mestrado: coorientador", "Dissertações de mestrado (coorientação)", .none, true),
        ("teses de doutorado: orientador principal", "Teses de doutorado", .none, true),
        ("teses de doutorado: coorientador", "Teses de doutorado (coorientação)", .none, true),
        ("iniciacao cientifica", "Iniciação científica", .none, true),
        ("orientacao de outra natureza", "Orientações de outra natureza", .none, true),
        ("trabalho de conclusao de curso de graduacao", "TCC de graduação", .none, true),
        ("supervisao de pos-doutorado", "Supervisão de pós-doutorado", .none, true),
    ]

    /// Cabeçalhos que delimitam, mas NÃO geram seções com comprovação.
    private static let excludedHeaders: Set<String> = [
        "identificacao", "idiomas", "areas de atuacao", "endereco",
        "linhas de pesquisa", "linha de pesquisa",
        "producao", "producao bibliografica", "producao tecnica",
        "orientacoes e supervisoes", "orientacoes e supervisoes",
        "eventos", "bancas", "educacao e popularizacao de c&t",
        "outras informacoes relevantes", "dados complementares",
    ]

    private static func classify(_ norm: String) -> HeaderClass? {
        if norm == "totais de producao" { return .stop }
        if excludedHeaders.contains(norm) { return .excluded }
        if norm == "orientacoes e supervisoes concluidas" { return .groupConcluidas }
        if norm == "orientacoes e supervisoes em andamento" { return .groupAndamento }
        for row in sectionTable {
            if norm == row.alias { return .section(display: row.display, special: row.special, isChild: row.isChild) }
            // Prefix match (títulos partidos em duas linhas, ou com qualificador como
            // "(completo)") — mas nunca quando a linha tem um ")" sem "(" correspondente:
            // aí é o fecho de uma anotação de entrada que por coincidência começa com as
            // mesmas palavras do título da seção (ex.: linha solta "Organização de
            // evento)" fechando "(Congresso, Organização de evento)"), não um cabeçalho
            // de verdade — um cabeçalho real nunca tem parênteses desbalanceados.
            if row.alias.count >= 12, norm.hasPrefix(row.alias), !hasUnbalancedClosingParen(norm) {
                return .section(display: row.display, special: row.special, isChild: row.isChild)
            }
        }
        return nil
    }

    /// Verdadeiro quando a linha tem mais ")" do que "(" — sinal de que ela é o FECHO
    /// de um parêntese aberto numa linha anterior (anotação de entrada quebrada pela
    /// paginação), não um título/cabeçalho de verdade.
    private static func hasUnbalancedClosingParen(_ s: String) -> Bool {
        s.filter { $0 == ")" }.count > s.filter { $0 == "(" }.count
    }

    // MARK: - Construção das seções

    private struct RawSection { var title: String; var special: Special; var body: String }

    private static func buildSections(from text: String) -> [(title: String, entries: [ParsedEntry])] {
        var raws: [RawSection] = []
        var current: RawSection?
        var parentSuffix = ""

        func flush() {
            if var c = current {
                c.body = c.body.trimmingCharacters(in: .whitespacesAndNewlines)
                if !c.body.isEmpty { raws.append(c) }
                current = nil
            }
        }

        let lines = text.components(separatedBy: "\n")
        var i = 0
        while i < lines.count {
            let line = lines[i].trimmingCharacters(in: .whitespaces)
            if isNoise(line) { i += 1; continue }
            let norm = normalizeHeader(line)
            if norm.isEmpty {
                if current != nil { current?.body += "\n" }
                i += 1; continue
            }

            // Classifica a linha; se não for cabeçalho, tenta juntá-la com a próxima —
            // o Lattes às vezes quebra cabeçalhos em duas linhas ("Projetos de"/"pesquisa").
            // Nunca tenta a junção quando a própria linha já tem ")" sem "(" correspondente
            // — nesse caso ela é o fecho de uma anotação de entrada (ex.: "Organização de
            // evento)"), não o início de um cabeçalho partido.
            var hc: HeaderClass? = classify(norm)
            var consumed = 1
            if hc == nil, i + 1 < lines.count, !hasUnbalancedClosingParen(norm) {
                let next = lines[i + 1].trimmingCharacters(in: .whitespaces)
                if !next.isEmpty, !isNoise(next), let c2 = classify(normalizeHeader(line + " " + next)) {
                    switch c2 {
                    case .section, .excluded: hc = c2; consumed = 2
                    default: break
                    }
                }
            }

            switch hc {
            case .stop:
                flush()
                return finalize(raws)
            case .excluded:
                flush(); parentSuffix = ""
            case .groupConcluidas:
                flush(); parentSuffix = " (concluídas)"
            case .groupAndamento:
                flush(); parentSuffix = " (em andamento)"
            case .section(let display, let special, let isChild):
                flush()
                let title = isChild ? "Orientações - \(display)\(parentSuffix)" : display
                if !isChild { parentSuffix = "" }
                current = RawSection(title: title, special: special, body: "")
            case .none:
                if current != nil { current?.body += line + "\n" }
            }
            i += consumed
        }
        flush()
        return finalize(raws)
    }

    private static func finalize(_ raws: [RawSection]) -> [(title: String, entries: [ParsedEntry])] {
        // Agrupa seções de mesmo título (cabeçalho repetido em páginas/áreas
        // diferentes) mas parseia cada corpo SEPARADAMENTE: as listagens duplicadas
        // (ex.: Entrevistas em "Educação e Popularização de C&T") costumam vir com
        // layout diferente (achatado) e, concatenadas, contaminariam a detecção de
        // modo da listagem principal. O `append` deduplica entre os corpos.
        var order: [String] = []
        var grouped: [String: (special: Special, bodies: [String])] = [:]
        for raw in raws {
            if grouped[raw.title] != nil {
                grouped[raw.title]!.bodies.append(raw.body)
            } else {
                grouped[raw.title] = (raw.special, [raw.body])
                order.append(raw.title)
            }
        }

        var result: [(title: String, entries: [ParsedEntry])] = []
        for title in order {
            let info = grouped[title]!
            for body in info.bodies {
                switch info.special {
                case .atuacao:
                    // Separa por vínculo (instituição) e, dentro dele, por categoria
                    // (Vínculo institucional / Atividades administrativas /
                    // Disciplinas ministradas) — ordenado do mais recente ao mais antigo.
                    for (label, ents) in groupAtuacaoPorVinculo(parseAtuacao(body)) {
                        append(&result, "\(title) - \(label)", ents)
                    }
                case .banca:
                    // "Trabalhos de conclusão" costuma vir subdividido por nível
                    // (Mestrado/Doutorado/Qualificação/Graduação) — separa em seções
                    // próprias para identificar de qual se trata. "Comissões
                    // julgadoras" (concurso público etc.) não tem esses marcadores.
                    if title.contains("trabalhos de conclusão") {
                        for (nivel, ents) in parseBancaPorNivel(body) {
                            append(&result, nivel.isEmpty ? title : "\(title) - \(nivel)", ents)
                        }
                    } else {
                        append(&result, title, parseEntries(body, kind: "Banca", banca: true))
                    }
                case .projetos:
                    append(&result, title, parseProjetos(body, kind: "Projeto"))
                case .organizacao:
                    append(&result, title, parseOrganizacao(body))
                case .premios:
                    append(&result, title, parsePremios(body))
                case .formacao:
                    append(&result, title, parseFormacao(body))
                case .eventos:
                    // Distingue apresentação (apresentou trabalho) de ouvinte (só participou)
                    let all = parseEntries(body, kind: "Evento", banca: false)
                    append(&result, "Participação em Eventos - Apresentação", all.filter { !isOuvinte($0) })
                    append(&result, "Participação em Eventos - Ouvinte", all.filter { isOuvinte($0) })
                case .none:
                    append(&result, title, parseEntries(body, kind: kindLabel(for: title), banca: false))
                }
            }
        }
        return result
    }

    private static func append(_ list: inout [(title: String, entries: [ParsedEntry])],
                               _ title: String, _ entries: [ParsedEntry]) {
        guard !entries.isEmpty else { return }
        guard let idx = list.firstIndex(where: { $0.title == title }) else {
            let deduped = dedupeEntries(entries)
            if !deduped.isEmpty { list.append((title: title, entries: deduped)) }
            return
        }
        // Título já existe (listagem duplicada em outra área, ex.: "Educação e
        // Popularização de C&T"): funde, descartando candidatos que dupliquem
        // entradas existentes — mesmo com cauda diferente (URL/metadados) ou
        // fragmentos compostos que ENGOLIRAM uma entrada real. O sinal é o
        // prefixo normalizado longo (≥60) de um aparecer dentro do outro.
        var merged = list[idx].entries
        let existingNorms = merged.map { SimilarityMatcher.normalize($0.rawText) }
        let existingTitles = merged.map { SimilarityMatcher.normalize($0.title) }
        for e in entries {
            let n = SimilarityMatcher.normalize(e.rawText)
            let nTitle = SimilarityMatcher.normalize(e.title)
            var isDup = false
            for (k, ex) in existingNorms.enumerated() {
                if ex == n { isDup = true; break }
                // Prefixo longo compartilhado NÃO basta (entradas distintas repetem a
                // mesma lista de autores no início) — exige também que o TÍTULO de uma
                // apareça no texto da outra.
                let exPref = String(ex.prefix(60))
                let nPref = String(n.prefix(60))
                let prefixHit = (exPref.count >= 60 && n.contains(exPref))
                    || (nPref.count >= 60 && ex.contains(nPref))
                guard prefixHit else { continue }
                let exTitle = String(existingTitles[k].prefix(30))
                let candTitle = String(nTitle.prefix(30))
                if (exTitle.count >= 15 && n.contains(exTitle))
                    || (candTitle.count >= 15 && ex.contains(candTitle)) {
                    isDup = true; break
                }
            }
            if !isDup { merged.append(e) }
        }
        merged = dedupeEntries(merged)
        for i in merged.indices { merged[i].order = i }
        list[idx].entries = merged
    }

    /// Papéis que indicam participação ATIVA (apresentou/conduziu algo).
    private static let presentationRoles = [
        "conferencista", "apresentacao", "comunicacao", "moderador", "mediador",
        "palestrante", "debatedor", "expositor", "avaliador", "coordenador",
        "organizador", "relator", "painelista", "entrevistado", "instrutor",
    ]

    /// Uma participação é "ouvinte" quando NÃO há papel de apresentação — no PDF do
    /// Lattes isso aparece como a entrada começando direto pelo nome do evento
    /// ("NOME DO EVENTO, ANO. (Tipo).") em vez de "Conferencista no(a)…".
    private static func isOuvinte(_ e: ParsedEntry) -> Bool {
        let n = normalizeHeader(e.rawText)
            .replacingOccurrences(of: #"^\s*(\d{1,3}\.\s*)+"#, with: "", options: .regularExpression)
        if n.hasPrefix("ouvinte") || n.contains("(ouvinte)") { return true }
        for role in presentationRoles where n.hasPrefix(role) { return false }
        return true   // começa pelo nome do evento → ouvinte
    }

    /// Remove entradas idênticas que surgem da mescla de seções repetidas.
    /// Usa o título inteiro normalizado (não um prefixo) para não confundir
    /// itens que só diferem no final — ex.: "… Vol. 11, N. 1/2/3".
    private static func dedupeEntries(_ entries: [ParsedEntry]) -> [ParsedEntry] {
        // 1) Remove duplicatas exatas pelo texto bruto normalizado (mesma seção
        // listada em duas áreas), sem confundir itens distintos (ex.: pareceres v.1/2/3).
        var seen = Set<String>()
        var result: [ParsedEntry] = []
        for e in entries {
            let key = "\(e.year)|\(SimilarityMatcher.normalize(e.rawText))"
            if seen.insert(key).inserted { result.append(e) }
        }
        // 2) Remove versões TRUNCADAS: uma entrada que TERMINA no ano (ex.: cortada em
        // "…2021.") e é prefixo de outra completa do mesmo ano ("…2021. (Tipo)").
        // A exigência de terminar no ano evita remover eventos distintos.
        let norm = result.map { SimilarityMatcher.normalize($0.rawText) }
        var keep = Array(repeating: true, count: result.count)
        for i in 0..<result.count
        where norm[i].count >= 30 && norm[i].range(of: #"(19|20)\d{2}$"#, options: .regularExpression) != nil {
            for j in 0..<result.count where i != j {
                if result[i].year == result[j].year,
                   norm[j].count > norm[i].count + 4, norm[j].hasPrefix(norm[i]) {
                    keep[i] = false; break
                }
            }
        }
        return zip(result, keep).compactMap { $0.1 ? $0.0 : nil }
    }

    // MARK: - Rótulo curto por tipo de seção

    static func kindLabel(for title: String) -> String {
        let c = normalizeHeader(title)
        if c.contains("artigo")                          { return "Artigo" }
        if c.contains("livro") || c.contains("capitulo") { return "Livro/Capítulo" }
        if c.contains("anais")                           { return "Trabalho em evento" }
        if c.contains("banca")                           { return "Banca" }
        if c.contains("dissertacoes") || c.contains("teses") || c.contains("iniciacao")
            || c.contains("orientac") || c.contains("tcc") || c.contains("supervisao") { return "Orientação" }
        if c.contains("apresentac")                      { return "Apresentação" }
        if c.contains("participacao em eventos")         { return "Evento" }
        if c.contains("organizacao de evento")           { return "Organização de evento" }
        if c.contains("projeto")                         { return "Projeto" }
        if c.contains("premio") || c.contains("titulo")  { return "Prêmio/Título" }
        if c.contains("formacao") || c.contains("doutorado") { return "Formação" }
        if c.contains("corpo editorial")                 { return "Corpo editorial" }
        if c.contains("entrevista")                      { return "Mídia" }
        if c.contains("tecnic") || c.contains("produto") { return "Produção técnica" }
        return ""
    }

    // MARK: - Atuação profissional (vínculos + disciplinas)

    private static func parseAtuacao(_ body: String) -> [ParsedEntry] {
        let lines = body.components(separatedBy: "\n")
            .map { $0.trimmingCharacters(in: .whitespaces) }
            .filter { !isNoise($0) && !$0.isEmpty }

        var entries: [ParsedEntry] = []
        var order = 0
        var institution = ""
        var lastPeriodYear = 0        // ano "rótulo" (fim do período) — usado pelas disciplinas
        var lastPeriodStartYear = 0   // início do período — usado pelas atividades
        var lastPeriodEndYear = 0     // fim do período (0 = "Atual"/aberto)
        var lastVinculoIdx = -1

        func isInstitution(_ l: String) -> Bool {
            if l.range(of: #"^\d"#, options: .regularExpression) != nil { return false }
            let low = normalizeHeader(l)
            // Evita falsos positivos: PORTARIA, continuação de "Regime:", etc.
            if low.contains("portaria") || low.contains("lotado")
                || low.contains("outras informac") || low.contains("vinculo:")
                || low.contains("dedicac") || low.contains("regime")
                || low.contains("carga hor") { return false }
            return low.contains("universidade") || low.contains("instituto")
                || low.contains("faculdade") || low.contains("fundacao")
        }

        let vinculoRE = try! NSRegularExpression(pattern: #"^\d{4}\s*-\s*(\d{4}|Atual)\s+Vínculo:"#)

        var i = 0
        while i < lines.count {
            let line = lines[i]
            let norm = normalizeHeader(line)

            if isInstitution(line) {
                institution = line
                    .replacingOccurrences(of: ", Brasil.", with: "")
                    .replacingOccurrences(of: ", Brasil", with: "")
                    // O valor de "Regime: <Integral/Parcial/Dedicação exclusiva>" às
                    // vezes é reordenado pelo PDFKit e cola no fim do nome da
                    // instituição do vínculo SEGUINTE (com ou sem espaço) — remove.
                    .replacingOccurrences(
                        of: #"\s*(Integral|Parcial|Horista|Dedica[çc][ãa]o\s*[Ee]xclusiva)\s*$"#,
                        with: "", options: .regularExpression)
                    .trimmingCharacters(in: .whitespaces)
                lastVinculoIdx = -1
                i += 1; continue
            }

            // Após "Atividades", as portarias pertencem a atividades, não ao vínculo
            if norm == "atividades" { lastVinculoIdx = -1; i += 1; continue }

            // Vínculo institucional
            if vinculoRE.firstMatch(in: line, range: NSRange(line.startIndex..., in: line)) != nil {
                let enquad = firstMatch(in: line, pattern: #"Enquadramento funcional:\s*([^,]+)"#)
                    .trimmingCharacters(in: .whitespaces)
                let period = firstMatch(in: line, pattern: #"^(\d{4}\s*-\s*(?:\d{4}|Atual))"#)
                let startY = Int(firstMatch(in: period, pattern: #"^(\d{4})"#)) ?? 0
                let endY = Int(firstMatch(in: period, pattern: #"-\s*(\d{4})"#)) ?? 0
                let titleCore = enquad.isEmpty ? "Vínculo institucional" : enquad
                entries.append(ParsedEntry(
                    rawText: line,
                    title: institution.isEmpty ? titleCore : "\(titleCore) — \(institution)",
                    kind: "Vínculo institucional",
                    year: startY > 0 ? startY : extractYear(from: period),
                    authors: "", venue: institution,
                    doi: "", isbn: "", order: order,
                    portaria: SimilarityMatcher.portariaPairs(line).joined(separator: " "),
                    endYear: endY))
                lastVinculoIdx = entries.count - 1
                order += 1
                i += 1; continue
            }

            // Portaria (geralmente em "Outras informações:") → associa ao último vínculo
            if norm.contains("portaria"), lastVinculoIdx >= 0 {
                let nums = SimilarityMatcher.portariaPairs(line)
                if !nums.isEmpty {
                    let existing = Set(entries[lastVinculoIdx].portaria.split(separator: " ").map(String.init))
                    entries[lastVinculoIdx].portaria = existing.union(nums).joined(separator: " ")
                }
            }

            // Linha de período de atividade (ex.: "08/2019 - 12/2019 Graduação, Filosofia")
            if line.range(of: #"^\d{1,2}/\d{4}"#, options: .regularExpression) != nil {
                let y = extractYear(from: String(line.prefix(20)))
                if y > 0 { lastPeriodYear = y }
                lastPeriodStartYear = Int(firstMatch(in: String(line.prefix(20)), pattern: #"/(\d{4})"#)) ?? y
                lastPeriodEndYear = line.prefix(25).lowercased().contains("atual")
                    ? 0 : (Int(firstMatch(in: String(line.prefix(30)), pattern: #"-\s*\d{0,2}/?(\d{4})"#)) ?? 0)
            }

            // Atividades administrativas: "Especificação:" (Conselhos, Comissões e
            // Consultoria) e "Cargos ocupados:" (Direção e Administração). O detalhe
            // (cargo/função, com portarias) vem nas linhas seguintes, até o próximo
            // período / cabeçalho.
            if norm.hasPrefix("especificacao") || norm.hasPrefix("cargos ocupados") {
                var detail: [String] = []
                var j = i + 1
                while j < lines.count {
                    let l = lines[j]
                    let n = normalizeHeader(l)
                    // Próximo período de atividade ("MM/AAAA - …") ou novo cabeçalho.
                    if l.range(of: #"^\d{1,2}/\d{4}\s*-"#, options: .regularExpression) != nil { break }
                    if isInstitution(l) || n == "atividades" || l.hasPrefix("https://") { break }
                    if n.hasPrefix("outras informacoes") || n.hasPrefix("disciplinas ministradas")
                        || n.hasPrefix("especificacao") || n.hasPrefix("cargos ocupados") { break }
                    if vinculoRE.firstMatch(in: l, range: NSRange(l.startIndex..., in: l)) != nil { break }
                    detail.append(l)
                    j += 1
                }
                let content = detail.joined(separator: " ").trimmingCharacters(in: .whitespaces)
                let title = activityTitle(from: content)
                if title.count >= 3 {
                    entries.append(ParsedEntry(
                        rawText: content, title: title,
                        kind: "Atividade administrativa",
                        year: lastPeriodStartYear > 0 ? lastPeriodStartYear : lastPeriodYear,
                        authors: "", venue: institution,
                        doi: "", isbn: "", order: order,
                        portaria: SimilarityMatcher.portariaPairs(content).joined(separator: " "),
                        endYear: lastPeriodEndYear))
                    order += 1
                }
                i = j; continue
            }

            // Disciplinas ministradas
            if norm.contains("disciplinas ministradas") {
                // Conteúdo após ":" ou na próxima linha
                var disc = ""
                if let colon = line.firstIndex(of: ":") {
                    disc = String(line[line.index(after: colon)...]).trimmingCharacters(in: .whitespaces)
                }
                if disc.isEmpty, i + 1 < lines.count { disc = lines[i + 1]; i += 1 }
                disc = disc.trimmingCharacters(in: .whitespaces)
                if disc.count >= 3 {
                    let y = extractYear(from: disc)
                    entries.append(ParsedEntry(
                        rawText: disc,
                        title: disc,
                        kind: "Disciplina ministrada",
                        year: y > 0 ? y : lastPeriodYear,
                        authors: "", venue: institution,
                        doi: "", isbn: "", order: order))
                    order += 1
                }
                i += 1; continue
            }

            i += 1
        }

        if entries.isEmpty {
            return [ParsedEntry(rawText: body, title: institution.isEmpty ? "Atuação profissional" : institution,
                                kind: "Vínculo institucional", year: 0, authors: "", venue: institution,
                                doi: "", isbn: "", order: 0)]
        }
        return entries
    }

    /// Reorganiza a "Atuação profissional" por vínculo (instituição) e, dentro dele,
    /// por categoria (Vínculo institucional / Atividades administrativas / Disciplinas
    /// ministradas). Instituições e entradas dentro de cada grupo vêm da mais recente
    /// para a mais antiga (vínculo/atividade em aberto — "Atual" — conta como mais
    /// recente; disciplinas não têm período aberto, então usam só o próprio ano).
    private static func groupAtuacaoPorVinculo(_ flat: [ParsedEntry]) -> [(label: String, entries: [ParsedEntry])] {
        guard !flat.isEmpty else { return [] }

        func recency(_ e: ParsedEntry) -> Int {
            if e.kind != "Disciplina ministrada", e.endYear == 0, e.year > 0 { return 9999 }
            return max(e.year, e.endYear)
        }

        // Chave de agrupamento normalizada: o mesmo vínculo pode aparecer com ou sem
        // a sigla no fim ("Universidade Federal do Espírito Santo" vs "… - UFES") por
        // causa do artefato de reordenação do Regime (ver acima) — sem isso, a mesma
        // instituição vira dois grupos separados.
        func institutionKey(_ venue: String) -> String {
            normalizeHeader(venue.replacingOccurrences(
                of: #"\s*-\s*[A-ZÀ-Ú]{2,10}$"#, with: "", options: .regularExpression))
        }

        let byInstitution = Dictionary(grouping: flat) {
            $0.venue.isEmpty ? "outros vinculos" : institutionKey($0.venue)
        }
        // Rótulo de exibição: a variante mais completa (mais longa) do nome da
        // instituição encontrada no grupo — normalmente a que TEM a sigla.
        let displayName: [String: String] = byInstitution.mapValues { ents in
            ents.map(\.venue).filter { !$0.isEmpty }.max(by: { $0.count < $1.count }) ?? "Outros vínculos"
        }
        let institutionOrder = byInstitution.keys.sorted { a, b in
            let ra = byInstitution[a]!.map(recency).max() ?? 0
            let rb = byInstitution[b]!.map(recency).max() ?? 0
            return ra != rb ? ra > rb : displayName[a]! < displayName[b]!
        }

        let kindOrder: [(kind: String, display: String)] = [
            ("Vínculo institucional", "Vínculo institucional"),
            ("Atividade administrativa", "Atividades administrativas"),
            ("Disciplina ministrada", "Disciplinas ministradas"),
        ]

        var groups: [(label: String, entries: [ParsedEntry])] = []
        for inst in institutionOrder {
            let entriesForInst = byInstitution[inst]!
            for (kind, display) in kindOrder {
                var sub = entriesForInst.filter { $0.kind == kind }
                    .sorted { recency($0) > recency($1) }
                guard !sub.isEmpty else { continue }
                for i in sub.indices { sub[i].order = i }
                groups.append((label: "\(displayName[inst]!) - \(display)", entries: sub))
            }
        }
        return groups
    }

    /// Extrai um título curto do cargo/função de uma atividade administrativa,
    /// cortando no primeiro marcador de portaria/resolução ou separador de itens.
    private static func activityTitle(from content: String) -> String {
        var s = content.trimmingCharacters(in: .whitespaces)
        // remove data inicial "DD/MM/AAAA - " (Cargos ocupados)
        s = s.replacingOccurrences(
            of: #"^\d{1,2}/\d{1,2}/\d{4}\s*-\s*"#, with: "", options: .regularExpression)
        // localiza o marcador mais próximo: portaria/resolução ou " , "
        var cut = s.endIndex
        if let r = s.range(of: #"\s*[-,.]?\s*(?i:portaria|resolu[çc][ãa]o)\b"#,
                           options: .regularExpression), r.lowerBound < cut { cut = r.lowerBound }
        if let r = s.range(of: " , "), r.lowerBound < cut { cut = r.lowerBound }
        s = String(s[..<cut]).trimmingCharacters(in: CharacterSet(charactersIn: " -,.;"))
        return s
    }

    // MARK: - Projetos (pesquisa / extensão)

    /// Cada projeto tem um TÍTULO seguido de "Descrição:". Como a coluna de períodos
    /// costuma vir achatada (vários "AAAA - AAAA" numa linha), usamos a "Descrição:"
    /// como âncora: o título é a linha de conteúdo imediatamente anterior.
    private static func parseProjetos(_ body: String, kind: String) -> [ParsedEntry] {
        let lines = body.components(separatedBy: "\n")
            .map { $0.trimmingCharacters(in: .whitespaces) }
            .filter { !isNoise($0) && !$0.isEmpty }

        func isMeta(_ l: String) -> Bool {
            let n = normalizeHeader(l)
            for p in ["situacao", "natureza", "alunos", "integrantes", "financiador",
                      "descricao", "palavras", "numero de producoes", "numero de produc",
                      "coordenador", "membro"] where n.hasPrefix(p) { return true }
            // Continuação de lista de integrantes (nomes separados por ";")
            if l.filter({ $0 == ";" }).count >= 2 { return true }
            // Linha composta só de períodos (e, eventualmente, "Situação…" ou "Número…")
            let noPeriods = l.replacingOccurrences(
                of: #"\d{4}\s*-\s*(?:\d{4}|Atual)"#, with: "", options: .regularExpression)
                .trimmingCharacters(in: .whitespaces)
            if noPeriods.isEmpty { return true }
            let nn = normalizeHeader(noPeriods)
            if nn.hasPrefix("situacao") || nn.hasPrefix("numero") { return true }
            return false
        }

        // Para cada "Descrição:", o título é a linha de conteúdo anterior (juntando
        // a linha de cima se o título tiver quebrado e ficado curto).
        var projs: [(start: Int, title: String)] = []
        var seenStart = Set<Int>()
        for (idx, line) in lines.enumerated() where normalizeHeader(line).hasPrefix("descricao") {
            var t = idx - 1
            while t >= 0, isMeta(lines[t]) { t -= 1 }
            guard t >= 0 else { continue }
            var titleLines = [lines[t]]
            var start = t
            if lines[t].count < 28, t - 1 >= 0, !isMeta(lines[t - 1]) {
                titleLines.insert(lines[t - 1], at: 0); start = t - 1
            }
            guard seenStart.insert(start).inserted else { continue }
            var title = titleLines.joined(separator: " ").replacingOccurrences(
                of: #"^(\d{4}\s*-\s*(?:\d{4}|Atual)\s*)+"#, with: "", options: .regularExpression)
                .trimmingCharacters(in: .whitespaces)
            if title.count < 3 { title = titleLines.joined(separator: " ") }
            projs.append((start, title))
        }
        projs.sort { $0.start < $1.start }

        guard !projs.isEmpty else {
            // Sem âncoras "Descrição:". Se o corpo é conteúdo de Atuação profissional
            // que vazou (subseção "Projetos de…" dentro do bloco da instituição,
            // seguida de Atividades), não há projetos aqui — descarta.
            let n = normalizeHeader(body)
            if n.contains("disciplinas ministradas") || n.contains("vinculo:")
                || n.contains("conselhos, comissoes") { return [] }
            return parseEntries(body, kind: kind, banca: false)
        }

        var entries: [ParsedEntry] = []
        for (k, p) in projs.enumerated() {
            let end = k + 1 < projs.count ? projs[k + 1].start : lines.count
            let block = lines[p.start..<end].joined(separator: " ")
            let year = extractYear(from: p.title) > 0 ? extractYear(from: p.title) : extractYear(from: block)
            entries.append(ParsedEntry(
                rawText: block, title: p.title, kind: kind, year: year,
                authors: "", venue: "", doi: "", isbn: "", order: k))
        }
        return entries
    }

    // MARK: - Organização de eventos

    /// Cada organização termina com "(…, Organização de evento)". Como a coluna de
    /// numeração às vezes vem achatada, dividimos no terminador "evento)".
    private static func parseOrganizacao(_ body: String) -> [ParsedEntry] {
        let kind = "Organização de evento"
        let joined = body.components(separatedBy: "\n")
            .map { $0.trimmingCharacters(in: .whitespaces) }
            .filter { !isNoise($0) }
            .joined(separator: " ")

        guard let re = try? NSRegularExpression(pattern: #"evento\s*\)"#, options: .caseInsensitive) else {
            return parseEntries(body, kind: kind, banca: false)
        }
        let ns = joined as NSString
        let matches = re.matches(in: joined, range: NSRange(location: 0, length: ns.length))
        guard matches.count >= 2 else {
            return parseEntries(body, kind: kind, banca: false)
        }

        var entries: [ParsedEntry] = []
        var start = 0
        for (i, m) in matches.enumerated() {
            let end = m.range.location + m.range.length
            var chunk = ns.substring(with: NSRange(location: start, length: end - start))
                .replacingOccurrences(of: #"^\s*(\d+\.\s*)+"#, with: "", options: .regularExpression)
                .trimmingCharacters(in: .whitespaces)
            // remove cluster de números que tenha sobrado no meio (coluna achatada)
            chunk = chunk.replacingOccurrences(of: #"\s(\d+\.\s){2,}"#, with: " ", options: .regularExpression)
            start = end
            guard chunk.count >= 8 else { continue }
            entries.append(ParsedEntry(
                rawText: chunk, title: orgEventTitle(from: chunk), kind: kind,
                year: extractYear(from: chunk), authors: "", venue: "",
                doi: "", isbn: "", order: i))
        }
        return entries
    }

    /// Extrai o nome do evento de uma entrada de organização:
    /// "AUTORES.. NOME DO EVENTO, AAAA. (Tipo, Organização de evento)".
    private static func orgEventTitle(from chunk: String) -> String {
        var s = chunk
        // Após a lista de autores (termina com ".. ")
        if let r = s.range(of: ".. ") { s = String(s[r.upperBound...]) }
        // Corta no ano de realização
        if let r = s.range(of: #",\s*(19|20)\d{2}"#, options: .regularExpression) {
            s = String(s[..<r.lowerBound])
        }
        s = s.trimmingCharacters(in: .whitespacesAndNewlines)
        return s.count >= 3 ? s : chunk
    }

    // MARK: - Formação acadêmica/titulação

    /// Cada diploma começa por um nível ("Doutorado/Mestrado/Graduação/…"). A coluna
    /// de períodos costuma vir achatada no topo ("2017-2021 2015-2016 2009-2014"),
    /// então dividimos pelo NÍVEL (marcador confiável) e tiramos o ano de
    /// "Ano de obtenção:" quando houver.
    private static let formacaoLevelRE = try! NSRegularExpression(
        pattern: #"^(P[óo]s[- ][Dd]outorado|Doutorado|Mestrado Profissional|Mestrado|Gradua[çc][ãa]o|Especializa[çc][ãa]o|Aperfei[çc]oamento|Curso [Tt]écnico|Ensino Fundamental|Ensino Médio|Livre-doc[êe]ncia|Resid[êe]ncia|Habilita[çc][ãa]o)\b"#)

    private static func parseFormacao(_ body: String) -> [ParsedEntry] {
        let lines = body.components(separatedBy: "\n")
            .map { $0.trimmingCharacters(in: .whitespaces) }
            .filter { !isNoise($0) && !$0.isEmpty }
        guard !lines.isEmpty else { return [] }

        // Remove a coluna de períodos do início da linha — pode vir agrupada
        // ("2017-2021 2015-2016 2009-2014 Doutorado…") ou inline em cada diploma
        // ("2014 - 2016 Mestrado…"). Tiramos para o nível ficar no início.
        func stripPeriods(_ l: String) -> String {
            l.replacingOccurrences(
                of: #"^\s*(\d{4}\s*-\s*(?:\d{4}|[Aa]tual)\s*)+"#, with: "", options: .regularExpression)
                .trimmingCharacters(in: .whitespaces)
        }
        func isLevel(_ l: String) -> Bool {
            let s = stripPeriods(l)
            return formacaoLevelRE.firstMatch(in: s, range: NSRange(s.startIndex..., in: s)) != nil
        }
        // Sem marcadores reconhecíveis → cai no parser genérico.
        guard lines.contains(where: isLevel) else {
            return parseEntries(body, kind: "Formação", banca: false)
        }

        var chunks: [[String]] = []
        var buf: [String] = []
        for l in lines {
            if isLevel(l), !buf.isEmpty { chunks.append(buf); buf = [] }
            buf.append(l)
        }
        if !buf.isEmpty { chunks.append(buf) }

        var entries: [ParsedEntry] = []
        for (i, c) in chunks.enumerated() where isLevel(c[0]) {
            let text = c.joined(separator: " ")
            let title = stripPeriods(c[0]).trimmingCharacters(in: CharacterSet(charactersIn: " ."))
            // instituição = linha "Universidade…/Instituto…/Faculdade…" do bloco
            let inst = c.first { l in
                let n = normalizeHeader(l)
                return n.hasPrefix("universidade") || n.hasPrefix("instituto")
                    || n.hasPrefix("faculdade") || n.hasPrefix("fundacao") || n.hasPrefix("centro")
            }?.components(separatedBy: ",").first?.trimmingCharacters(in: .whitespaces) ?? ""
            let yStr = firstMatch(in: text, pattern: #"Ano de obten[çc][ãa]o:\s*(\d{4})"#)
            var y = Int(yStr) ?? extractYear(from: text)
            // Diplomas duplos no mesmo período (ex.: licenciatura + bacharelado) às
            // vezes perdem a própria coluna de período por um artefato de quebra de
            // página que a duplica no diploma anterior — herda o ano do diploma
            // imediatamente anterior em vez de ficar sem ano.
            if y == 0, let last = entries.last { y = last.year }
            entries.append(ParsedEntry(
                rawText: text, title: inst.isEmpty ? title : "\(title) — \(inst)",
                kind: "Formação", year: y, authors: "", venue: inst,
                doi: "", isbn: "", order: i))
        }
        return entries
    }

    // MARK: - Prêmios e títulos

    /// Os prêmios vêm com a coluna de anos achatada numa única linha
    /// ("2023 2017 2012 <prêmio1> …") e cada prêmio termina no nome da
    /// instituição concedente (última palavra Capitalizada / acrônimo).
    private static func parsePremios(_ body: String) -> [ParsedEntry] {
        var lines = body.components(separatedBy: "\n")
            .map { $0.trimmingCharacters(in: .whitespaces) }
            .filter { !isNoise($0) && !$0.isEmpty }
        guard !lines.isEmpty else { return [] }

        // Remove conteúdo que vaza de "Áreas de atuação"/"Idiomas" por achatamento de
        // colunas (Grande área:…, Subárea:…, "Compreende/Fala/Lê/Escreve…", Periódico:…).
        func isLeak(_ l: String) -> Bool {
            let n = normalizeHeader(l)
            if n.contains("grande area") || n.contains("subarea") || n.hasPrefix("area:")
                || n.hasPrefix("/ area") { return true }
            if n.hasPrefix("compreende") || n.hasPrefix("fala ") || n.hasPrefix("le ")
                || n.hasPrefix("escreve") { return true }
            if n.hasPrefix("periodico") || n.hasPrefix("ordenar por") || n.hasPrefix("ordem ") { return true }
            return false
        }
        lines = lines.filter { !isLeak($0) }
        guard !lines.isEmpty else { return [] }

        // Coluna de anos: na 1ª linha ("2023 2017 2012 …") OU linhas isoladas só com o ano.
        var years: [Int] = []
        var rest: [String] = []
        if let m = lines[0].range(of: #"^((?:19|20)\d{2}[\s,]+)+"#, options: .regularExpression) {
            years = lines[0][m].split(whereSeparator: { !$0.isNumber }).compactMap { Int($0) }
            let head = String(lines[0][m.upperBound...]).trimmingCharacters(in: .whitespaces)
            rest = (head.isEmpty ? [] : [head]) + Array(lines.dropFirst())
        } else {
            for l in lines {
                if l.range(of: #"^(19|20)\d{2}$"#, options: .regularExpression) != nil, let y = Int(l) {
                    years.append(y)
                } else {
                    rest.append(l)
                }
            }
        }

        // Delimitador entre prêmios. Dois layouts:
        //  • prêmios de 1 linha sem ponto final (terminam no nome da instituição) →
        //    quebra quando a linha acaba numa palavra Capitalizada;
        //  • prêmios multi-linha que terminam em ponto → quebra no ponto final.
        // Se o corpo tem linhas terminando em ".", usa o ponto (mais confiável).
        func endsAtInstitution(_ line: String) -> Bool {
            guard let last = line.split(separator: " ").last else { return false }
            let w = String(last).trimmingCharacters(in: CharacterSet(charactersIn: ".,;)"))
            guard let f = w.first else { return false }
            return f.isUppercase
        }
        let usePeriod = rest.contains { $0.hasSuffix(".") }
        let isBoundary: (String) -> Bool = usePeriod ? { $0.hasSuffix(".") } : endsAtInstitution

        var awards: [String] = []
        var buf: [String] = []
        for line in rest {
            buf.append(line)
            if isBoundary(line) {
                awards.append(buf.joined(separator: " ")); buf = []
            }
        }
        if !buf.isEmpty { awards.append(buf.joined(separator: " ")) }

        var entries: [ParsedEntry] = []
        for (idx, a) in awards.enumerated() where a.count >= 6 {
            let inlineYear = extractYear(from: a)
            let y = idx < years.count ? years[idx] : (inlineYear > 0 ? inlineYear : (years.last ?? 0))
            entries.append(ParsedEntry(
                rawText: a, title: a, kind: "Prêmio/Título",
                year: y, authors: "", venue: "", doi: "", isbn: "", order: idx))
        }
        return entries
    }

    // MARK: - Entradas genéricas (artigos, orientações, eventos, bancas…)

    private static func parseEntries(_ body: String, kind: String, banca: Bool) -> [ParsedEntry] {
        var lines = body.components(separatedBy: "\n")
            .map { $0.trimmingCharacters(in: .whitespaces) }
            .filter { !isNoise($0) && !isQualisAnnotation($0) }

        guard !lines.isEmpty else { return [] }

        // Numeração DESTACADA: quando a coluna de números vem empilhada ("1.\n2.\n3.")
        // separada dos corpos (achatamento vertical), os marcadores ficam órfãos.
        // Detecta ≥2 marcadores "N." isolados seguidos e os remove — aí a divisão
        // recai na âncora de ano (os artigos terminam em "…, AAAA.").
        let bareNumRE = try! NSRegularExpression(pattern: #"^\s*\d{1,3}\.\s*$"#)
        func isBareNum(_ l: String) -> Bool {
            bareNumRE.firstMatch(in: l, range: NSRange(l.startIndex..., in: l)) != nil
        }
        // Só considera "destacada" uma sequência CRESCENTE de ≥2 marcadores ("3."␤"4.").
        // Números soltos não sequenciais (ex.: "246." de um intervalo de páginas
        // quebrado + "6." do próximo item) não disparam a remoção.
        var runLen = 0, maxRun = 0
        var prevVal = Int.min
        for l in lines where !l.isEmpty {
            if isBareNum(l), let v = Int(l.trimmingCharacters(in: CharacterSet(charactersIn: " ."))) {
                runLen = (v == prevVal + 1) ? runLen + 1 : 1
                prevVal = v
                maxRun = max(maxRun, runLen)
            } else {
                runLen = 0; prevVal = Int.min
            }
        }
        let detachedNumbering = maxRun >= 2
        if detachedNumbering { lines = lines.filter { !isBareNum($0) } }

        // "N. conteúdo" ou "N." sozinho na linha (numeração quebrada em duas linhas).
        // Limita a 1–3 dígitos para não confundir anos ("2025.") com número de entrada.
        let numberStartRE = try! NSRegularExpression(pattern: #"^\s*\d{1,3}\.(\s+\S|\s*$)"#)
        let clusterRE = try! NSRegularExpression(pattern: #"^\s*(\d{1,3}\.\s+){2,}"#)

        func isNumberStart(_ l: String) -> Bool {
            numberStartRE.firstMatch(in: l, range: NSRange(l.startIndex..., in: l)) != nil
        }
        func stripCluster(_ l: String) -> String {
            if let m = clusterRE.firstMatch(in: l, range: NSRange(l.startIndex..., in: l)),
               let r = Range(m.range, in: l) {
                return String(l[r.upperBound...])
            }
            return l
        }

        let hasCluster = lines.contains {
            clusterRE.firstMatch(in: $0, range: NSRange($0.startIndex..., in: $0)) != nil
        }
        let numberedCount = lines.filter { isNumberStart($0) }.count

        // Intervalo de anos "AAAA - AAAA" / "AAAA - Atual" (formação, projetos, editorial)
        let periodRE = try! NSRegularExpression(pattern: #"\b\d{4}\s*-\s*(?:\d{4}|[Aa]tual)\b"#)
        let joinedAll = lines.joined(separator: " ")
        let periodMatches = periodRE.matches(in: joinedAll, range: NSRange(joinedAll.startIndex..., in: joinedAll))

        var chunks: [String] = []
        var strippedPeriod = false

        // Bancas: o marcador "Participação em banca de" é o delimitador mais confiável
        // (1 por banca), robusto ao achatamento extremo (numeração empilhada + multi-
        // linha) que faz números e anos falharem.
        let bancaMarkRE = try! NSRegularExpression(
            pattern: #"Participaç[ãa]o\s+em\s+[Bb]anca\s+de"#, options: .caseInsensitive)
        let bancaJoined = lines.joined(separator: " ")
        let bancaMarks = banca
            ? bancaMarkRE.matches(in: bancaJoined, range: NSRange(bancaJoined.startIndex..., in: bancaJoined))
            : []

        if banca, detachedNumbering, bancaMarks.count >= 2 {
            // Numeração destacada → usa o marcador de banca (a numeração não é confiável).
            let ns = bancaJoined as NSString
            for (i, m) in bancaMarks.enumerated() {
                let start = m.range.location
                let end = i + 1 < bancaMarks.count ? bancaMarks[i + 1].range.location : ns.length
                var chunk = ns.substring(with: NSRange(location: start, length: end - start))
                // remove números soltos remanescentes ("3. 4. 5.")
                chunk = chunk.replacingOccurrences(
                    of: #"\s(\d{1,3}\.\s){1,}"#, with: " ", options: .regularExpression)
                chunks.append(chunk)
            }
        } else if !hasCluster && numberedCount >= 2 {
            // Divisão por numeração de linha "N. "
            var buf: [String] = []
            for line in lines where !line.isEmpty {
                if isNumberStart(line), !buf.isEmpty {
                    chunks.append(buf.joined(separator: " ")); buf = []
                }
                buf.append(stripLeadingNumber(line))
            }
            if !buf.isEmpty { chunks.append(buf.joined(separator: " ")) }
        } else if !hasCluster && periodMatches.count >= 2 {
            // Divisão no início de cada intervalo de anos (colunas período/conteúdo achatadas)
            strippedPeriod = true
            let ns = joinedAll as NSString
            for (i, m) in periodMatches.enumerated() {
                let start = m.range.location
                let end = i + 1 < periodMatches.count ? periodMatches[i + 1].range.location : ns.length
                if end > start { chunks.append(ns.substring(with: NSRange(location: start, length: end - start))) }
            }
        } else {
            // Divisão por âncora de ano (colunas achatadas / sem numeração).
            // Uma entrada termina ao fim de uma frase OU ao terminar com um ano
            // (ex.: "… Parecerista ad hoc …, 2026") — mas NÃO quando a próxima linha
            // começa com "(", pois o tipo/título do evento ainda vem a seguir
            // (ex.: "… 2023." seguido de "(Congresso) Título.").
            let cleaned = lines.map { stripLeadingNumber(stripCluster($0)) }
                .filter { !$0.trimmingCharacters(in: .whitespaces).isEmpty }
            // Nos tipos "de citação" (Lattes sempre lista SOBRENOME, Iniciais.. antes do
            // título), uma linha sem vírgula perto do início E sem ano nunca é o começo
            // de uma entrada nova — é metadado solto (ex.: "Cuadernos de Pesimismo
            // (Ciudad de México)", uma anotação de local/veículo sem rótulo) ou a
            // primeira linha de um resumo/abstract sem rótulo (ex.: "Este artigo tem por
            // objetivo…") que, sem isso, vaza como prefixo do título da PRÓXIMA entrada.
            let citationKinds: Set<String> = [
                "Artigo", "Livro/Capítulo", "Trabalho em evento", "Produção técnica",
                "Mídia", "Corpo editorial",
            ]
            func looksLikeStrayFragment(_ l: String) -> Bool {
                guard citationKinds.contains(kind), l.count <= 160 else { return false }
                let prefix = l.prefix(50)
                return !prefix.contains(",") && !hasYear(l)
            }
            var buf: [String] = []
            for (li, line) in cleaned.enumerated() {
                // Metadados ("Palavras-chave:", "Referências adicionais:"…) e
                // continuações (URL quebrada em várias linhas, "PORTARIA…" de uma
                // referência) após o fim de uma entrada pertencem a ela — anexa ao
                // último chunk em vez de iniciar um novo (senão viram entradas falsas).
                if buf.isEmpty, !chunks.isEmpty,
                   isMetadataLine(line) || isContinuationStart(line) || looksLikeStrayFragment(line) {
                    chunks[chunks.count - 1] += " " + line
                    continue
                }
                buf.append(line)
                let joined = buf.joined(separator: " ")
                let nextOpensParen = li + 1 < cleaned.count
                    && cleaned[li + 1].trimmingCharacters(in: .whitespaces).hasPrefix("(")
                // Ignora a anotação "Citações: N | N" no fim da linha ao testar o
                // fechamento ("…2023. Citações: 1 | 1" ainda termina a entrada).
                let testLine = line.replacingOccurrences(
                    of: #"\s*Citações:\s*\d+(\s*\|\s*\d+)?\s*$"#, with: "", options: .regularExpression)
                // A linha termina no ano de realização? ("…2023." / "…2023")
                let endsAtYear = testLine.trimmingCharacters(in: .whitespaces)
                    .range(of: #"(19|20)\d{2}\.?$"#, options: .regularExpression) != nil
                // Não quebra se o ano fecha a linha e o tipo/título "(…)" vem na próxima
                let suppress = endsAtYear && nextOpensParen
                if hasYear(joined), endsSentence(testLine) || endsWithYear(testLine), !suppress {
                    chunks.append(joined); buf = []
                }
            }
            if !buf.isEmpty { chunks.append(buf.joined(separator: " ")) }
        }

        // Rede de segurança (todos os modos): chunk que COMEÇA com metadados
        // pertence ao chunk anterior — funde.
        var mergedChunks: [String] = []
        for c in chunks {
            let t = c.trimmingCharacters(in: .whitespacesAndNewlines)
            if isMetadataLine(t), !mergedChunks.isEmpty {
                mergedChunks[mergedChunks.count - 1] += " " + t
            } else {
                mergedChunks.append(c)
            }
        }
        chunks = mergedChunks

        // Converte chunks em entradas
        var entries: [ParsedEntry] = []
        for (i, chunk) in chunks.enumerated() {
            var text = chunk.trimmingCharacters(in: .whitespacesAndNewlines)
            let periodYear = strippedPeriod ? extractYear(from: String(text.prefix(14))) : 0
            if strippedPeriod {
                // Remove o prefixo "AAAA - AAAA " para o título ser o nome do curso/projeto
                text = text.replacingOccurrences(
                    of: #"^\s*\d{4}\s*-\s*(?:\d{4}|[Aa]tual)\s*"#, with: "", options: .regularExpression)
            }
            guard text.count >= 8 else { continue }
            // Fragmento curto sem ano (ex.: continuação do nome de uma instituição
            // quebrado pela paginação) — não é uma entrada real.
            if !strippedPeriod, text.count < 50, extractYear(from: text) == 0 { continue }
            if banca {
                entries.append(makeBancaEntry(text, order: i))
            } else {
                var e = makeEntry(text, kind: kind, order: i)
                if periodYear > 0 { e.year = periodYear }
                entries.append(e)
            }
        }
        return entries
    }

    private static func makeEntry(_ text: String, kind: String, order: Int) -> ParsedEntry {
        let (title, authors, venue) = extractTitleAuthorsVenue(from: text)
        return ParsedEntry(
            rawText: text, title: title, kind: kind, year: extractYear(from: text),
            authors: authors, venue: venue,
            doi: extractDOI(from: text), isbn: extractISBN(from: text), order: order,
            portaria: SimilarityMatcher.portariaPairs(text).joined(separator: " "),
            issn: SimilarityMatcher.issnNumbers(text).joined(separator: " "),
            edital: SimilarityMatcher.editalNumbers(text).joined(separator: " "))
    }

    private static func makeBancaEntry(_ text: String, order: Int) -> ParsedEntry {
        // "… Participação em banca de <Candidato>. <Título>, <ano>. (<Área>) <Instituição>."
        let candidate = firstMatch(in: text, pattern: #"banca de\s+([^.]+)\."#)
            .trimmingCharacters(in: .whitespaces)
        let institution = firstMatch(in: text, pattern: #"\)\s*([^.()]+)\.?\s*$"#)
            .trimmingCharacters(in: .whitespaces)
        let titleCore = candidate.isEmpty ? text : candidate
        return ParsedEntry(
            rawText: text,
            title: titleCore,
            kind: "Banca",
            year: extractYear(from: text),
            authors: "",
            venue: institution,
            doi: "", isbn: "", order: order,
            portaria: SimilarityMatcher.portariaPairs(text).joined(separator: " "),
            issn: "",
            edital: SimilarityMatcher.editalNumbers(text).joined(separator: " "))
    }

    /// O Lattes agrupa "Participação em banca de trabalhos de conclusão" por nível,
    /// cada um com sua própria linha-marcador solta no corpo ("Mestrado", "Doutorado",
    /// "Exame de qualificação de mestrado/doutorado", "Graduação"). Divide o corpo por
    /// esses marcadores para identificar de qual se trata; sem marcadores, mantém tudo
    /// num único grupo (nível vazio) como antes.
    private static let bancaLevelMarkers: [(match: String, display: String)] = [
        ("mestrado", "Mestrado"),
        ("doutorado", "Doutorado"),
        ("exame de qualificacao de mestrado", "Qualificação de Mestrado"),
        ("exame de qualificacao de doutorado", "Qualificação de Doutorado"),
        ("graduacao", "Graduação"),
    ]

    private static func parseBancaPorNivel(_ body: String) -> [(nivel: String, entries: [ParsedEntry])] {
        let lines = body.components(separatedBy: "\n").map { $0.trimmingCharacters(in: .whitespaces) }
        let numClusterRE = try! NSRegularExpression(pattern: #"^(\d{1,3}\.\s*)+"#)
        // Retorna o nível da linha e, se veio com uma coluna de numeração destacada
        // colada ("1. 2. 3. 4. 5. 6. Doutorado"), quantos números tinha ("6").
        func levelFor(_ l: String) -> (display: String, numCount: Int)? {
            let n = normalizeHeader(l)
            guard let m = numClusterRE.firstMatch(in: n, range: NSRange(n.startIndex..., in: n)),
                  let r = Range(m.range, in: n) else {
                return bancaLevelMarkers.first(where: { $0.match == n }).map { ($0.display, 0) }
            }
            let rest = String(n[r.upperBound...])
            let cluster = String(n[r])
            let count = cluster.filter { $0 == "." }.count
            return bancaLevelMarkers.first(where: { $0.match == rest }).map { ($0.display, count) }
        }
        guard lines.contains(where: { levelFor($0) != nil }) else {
            return [(nivel: "", entries: parseEntries(body, kind: "Banca", banca: true))]
        }

        // Linhas onde cada entrada real começa ("Participação em banca de…"). A busca é
        // no texto UNIDO por espaço — a frase às vezes quebra entre duas linhas
        // ("…Participação em banca" / "de Fulano…") e escaparia de uma checagem linha a
        // linha, fazendo a contagem abaixo perder entradas.
        var offsets: [Int] = []
        var acc = 0
        for l in lines { offsets.append(acc); acc += l.count + 1 }
        let joined = lines.joined(separator: " ")
        let bancaMarkRE = try! NSRegularExpression(
            pattern: #"Participaç[ãa]o\s+em\s+[Bb]anca\s+de"#, options: .caseInsensitive)
        let ns = joined as NSString
        let matchLocations = bancaMarkRE.matches(in: joined, range: NSRange(location: 0, length: ns.length))
            .map(\.range.location)
        var entryStartLines = Set<Int>()
        for loc in matchLocations {
            var idx = 0
            for (i, off) in offsets.enumerated() where off <= loc { idx = i }
            entryStartLines.insert(idx)
        }

        var groups: [(nivel: String, lines: [String])] = []
        var current = ""
        var buf: [String] = []
        // O PDFKit às vezes reordena o texto e cola a coluna de numeração destacada do
        // nível ATUAL no marcador do PRÓXIMO nível (ex.: "Mestrado" seguido imediatamente,
        // sem nenhum conteúdo real, por "1. 2. 3. 4. 5. 6. Doutorado" — mas essas 6
        // entradas que vêm a seguir ainda são de Mestrado). Adia a troca até que comece a
        // (N+1)-ésima entrada real — não na N-ésima, para não cortar no meio do resto do
        // texto da última entrada ainda pertencente ao nível anterior.
        var pendingLevel: String? = nil
        var pendingCount = 0
        var pendingSeen = 0
        for (li, l) in lines.enumerated() {
            if let (lvl, numCount) = levelFor(l) {
                if buf.isEmpty, numCount > 0, !current.isEmpty, pendingLevel == nil {
                    pendingLevel = lvl; pendingCount = numCount; pendingSeen = 0
                    continue
                }
                groups.append((current, buf))
                current = lvl; buf = []
                pendingLevel = nil
                continue
            }
            if pendingLevel != nil, entryStartLines.contains(li) {
                pendingSeen += 1
                if pendingSeen > pendingCount {
                    groups.append((current, buf))
                    current = pendingLevel!; buf = [l]
                    pendingLevel = nil
                    continue
                }
            }
            buf.append(l)
        }
        groups.append((current, buf))

        return groups.compactMap { g in
            guard !g.nivel.isEmpty else { return nil }
            let entries = parseEntries(g.lines.joined(separator: "\n"), kind: "Banca", banca: true)
            return entries.isEmpty ? nil : (nivel: g.nivel, entries: entries)
        }
    }

    // MARK: - Helpers de texto

    private static func normalizeHeader(_ s: String) -> String {
        s.folding(options: [.diacriticInsensitive, .caseInsensitive],
                  locale: Locale(identifier: "pt_BR"))
            .trimmingCharacters(in: .whitespacesAndNewlines)
            .lowercased()
    }

    private static func isNoise(_ line: String) -> Bool {
        if line.contains("wwws.cnpq.br") { return true }
        if line.hasPrefix("impcv.trata") { return true }
        if line.range(of: #"^\d{2}/\d{2}/\d{4},?\s*\d{1,2}:\d{2}"#, options: .regularExpression) != nil { return true }
        if line == "Currículo Lattes" { return true }
        if line == "Ordenar por" || line == "Ordem Cronológica"
            || line == "Ordem de Importância" { return true }
        return false
    }

    /// Linha de metadados que alguns exports do Lattes inserem sob cada entrada
    /// ("Palavras-chave: …", "Referências adicionais: …", "Home page: …").
    /// Pertence à entrada ANTERIOR — nunca deve iniciar uma nova.
    private static func isMetadataLine(_ line: String) -> Bool {
        let n = normalizeHeader(line)
        return n.hasPrefix("palavras-chave") || n.hasPrefix("referencias adicionais")
            || n.hasPrefix("home page") || n.hasPrefix("meio de divulgacao")
    }

    /// Linha que claramente CONTINUA a entrada anterior (nunca inicia uma nova):
    /// URLs quebradas ("https://…", "page: …", "si=…"), fragmentos iniciando em
    /// minúscula ou travessão, e referências de portaria/edital sob "Referências
    /// adicionais". Entradas reais do Lattes começam com Maiúscula, número ou "(".
    private static func isContinuationStart(_ line: String) -> Bool {
        guard let f = line.first else { return false }
        if line.hasPrefix("http") || line.hasPrefix("www.") { return true }
        // "Home page:" quebrado em duas linhas deixa a URL sozinha, entre colchetes,
        // na linha seguinte ("[http://…]") — sem isso vira uma entrada órfã sem título.
        if line.hasPrefix("[http") || line.hasPrefix("[www.") { return true }
        if f.isLowercase { return true }
        if f == "-" || f == "–" { return true }
        let n = normalizeHeader(line)
        if n.hasPrefix("portaria") || n.hasPrefix("edital") { return true }
        // Anotação de referência curta sob "Referências adicionais"
        // (ex.: "Presidente da Comissão - PORTARIA Nº 3706, DE 24 DE OUTUBRO DE 2023")
        if line.count <= 90,
           n.range(of: #"(portaria|edital)\s*n"#, options: .regularExpression) != nil { return true }
        return false
    }

    /// Linha de anotação Qualis que o Lattes insere sob artigos quando o autor
    /// anotou o estrato (ex.: "B1, ISSN 2358-2472, fonte Qualis/CAPES (2021-2024)"
    /// ou "Não classificado, ISSN 2526-0103"). Descartada para não poluir
    /// título/ano do artigo (o Qualis é recalculado pelo app).
    private static func isQualisAnnotation(_ line: String) -> Bool {
        if line.lowercased().contains("fonte qualis") { return true }
        return line.range(
            of: #"^(A[1-4]|B[1-5]|C|N[ãa]o classificado)\s*,\s*ISSN"#,
            options: [.regularExpression, .caseInsensitive]) != nil
    }

    private static func hasYear(_ s: String) -> Bool {
        s.range(of: #"\b(19|20)\d{2}\b"#, options: .regularExpression) != nil
    }

    private static func endsSentence(_ line: String) -> Bool {
        let t = line.trimmingCharacters(in: .whitespaces)
        return t.hasSuffix(".") || t.hasSuffix(".\"") || t.hasSuffix("\".") || t.hasSuffix(").")
    }

    /// Linha que termina com um ano (terminador de entrada "…, 2026" / "…2013.").
    private static func endsWithYear(_ line: String) -> Bool {
        line.trimmingCharacters(in: .whitespaces)
            .range(of: #"(19|20)\d{2}\.?$"#, options: .regularExpression) != nil
    }

    private static func stripLeadingNumber(_ l: String) -> String {
        // 1–3 dígitos: numeração de item ("12. "), nunca anos ("2023." quebrado
        // pela paginação deve permanecer — é o fim de uma entrada).
        l.replacingOccurrences(of: #"^\s*\d{1,3}\.\s*"#, with: "", options: .regularExpression)
    }

    private static func firstMatch(in text: String, pattern: String) -> String {
        guard let re = try? NSRegularExpression(pattern: pattern),
              let m = re.firstMatch(in: text, range: NSRange(text.startIndex..., in: text)),
              m.numberOfRanges > 1,
              let r = Range(m.range(at: 1), in: text) else { return "" }
        return String(text[r])
    }

    static func extractYear(from text: String) -> Int {
        guard let re = try? NSRegularExpression(pattern: #"\b(19|20)\d{2}\b"#) else { return 0 }
        let ms = re.matches(in: text, range: NSRange(text.startIndex..., in: text))
        if let last = ms.last, let r = Range(last.range, in: text) { return Int(text[r]) ?? 0 }
        return 0
    }

    private static func extractTitleAuthorsVenue(from text: String) -> (title: String, authors: String, venue: String) {
        let sentences = splitSentences(text)
        guard !sentences.isEmpty else { return (text, "", "") }

        var authors = ""
        var title = ""
        var venue = ""

        if sentences.count == 1 {
            title = sentences[0]
        } else {
            let first = sentences[0]
            let authorPattern = #"^[A-ZÁÀÂÃÉÊÍÓÔÕÚÜÇ][A-ZÁÀÂÃÉÊÍÓÔÕÚÜÇ\s,\.;\-']{4,}$"#
            if first.range(of: authorPattern, options: .regularExpression) != nil
                || first.contains(";") && first == first.uppercased() {
                authors = first
                title = sentences.count > 1 ? sentences[1] : ""
                venue = sentences.count > 2 ? sentences[2] : ""
            } else {
                title = first
                venue = sentences.count > 1 ? sentences[1] : ""
            }
        }

        title = title
            .replacingOccurrences(of: #"\s*\(Citações:.*?\)"#, with: "", options: .regularExpression)
            .trimmingCharacters(in: .whitespacesAndNewlines)
        return (title.isEmpty ? (sentences.first ?? text) : title, authors, venue)
    }

    private static func splitSentences(_ text: String) -> [String] {
        var parts: [String] = []
        var current = ""
        let abbreviations = ["v", "n", "p", "ed", "org", "vol", "pp", "op", "cit", "et", "al", "dr", "ph"]
        let chars = Array(text)
        var i = 0
        while i < chars.count {
            current.append(chars[i])
            if chars[i] == ".", i + 1 < chars.count, chars[i + 1] == " " {
                let word = current.components(separatedBy: .whitespaces).last?
                    .lowercased().replacingOccurrences(of: ".", with: "") ?? ""
                let isAbbrev = abbreviations.contains(word) || word.count == 1
                if !isAbbrev {
                    parts.append(current.trimmingCharacters(in: .whitespaces))
                    current = ""; i += 2; continue
                }
            }
            i += 1
        }
        let tail = current.trimmingCharacters(in: .whitespacesAndNewlines)
        if !tail.isEmpty { parts.append(tail) }
        return parts.filter { !$0.isEmpty }
    }

    private static func extractDOI(from text: String) -> String {
        firstMatch(in: text, pattern: #"(10\.\d{4,}/[^\s,;]+)"#)
    }

    private static func extractISBN(from text: String) -> String {
        firstMatch(in: text, pattern: #"ISBN[:\s]*([\dXx\-]{10,17})"#)
    }
}
