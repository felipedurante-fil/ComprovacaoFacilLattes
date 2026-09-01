# Comprovação Fácil do Lattes — Especificação para versão Windows

> Este arquivo foi gerado a partir do código-fonte real do app macOS (Swift/SwiftUI/SwiftData),
> para servir de referência a uma reimplementação em outra stack (Windows). Contém: o que é
> portável x o que precisa ser refeito, o modelo de dados, o inventário de funcionalidades, uma
> explicação em prosa de cada algoritmo (com as armadilhas já descobertas e corrigidas — vale a
> pena não redescobri-las), e o código-fonte completo dos arquivos de lógica pura (Foundation-only,
> sem AppKit/PDFKit/Vision) que podem ser transliterados quase 1:1 para C#/Python/TypeScript.

## 0. O que o app faz

App nativo de macOS para professores/servidores públicos organizarem os **comprovantes**
(certificados, portarias, diplomas) que embasam cada linha do currículo Lattes — usado em
concursos e progressão na carreira docente. Fluxo:

1. Importa o PDF do currículo Lattes → o app faz o **parsing** (extrai todas as seções e
   entradas: artigos, bancas, orientações, atuação profissional, eventos, etc.).
2. Usuário aponta uma pasta com os arquivos de comprovante (PDFs/imagens escaneadas) → o app faz
   **OCR** (quando necessário) e **casa automaticamente** cada arquivo com a entrada do currículo
   que ele comprova, com uma pontuação de confiança.
3. Usuário revisa/confirma os vínculos sugeridos, ou vincula manualmente (drag & drop).
4. Usuário gera um **PDF final único**: currículo Lattes completo + sumário + cada comprovante
   organizado atrás da entrada correspondente, pronto para anexar num processo de progressão.
5. Pode exportar/importar tudo (currículo + vínculos + comprovantes) como backup.

## 1. Arquitetura atual (macOS) e o que é portável

| Componente | Portável? | Tecnologia macOS | Observação p/ Windows |
|---|---|---|---|
| Parsing do PDF do Lattes → seções/entradas | ✅ **100% portável** | Foundation puro (`LattesPDFParser.swift`) | Só usa `NSRegularExpression`/`String` — troca 1:1 por regex da stack nova |
| Matching comprovante↔entrada (scoring) | ✅ **100% portável** | Foundation puro (`SimilarityMatcher.swift`, parte de `CertificateIndexer.swift`) | Idem |
| Classificação Qualis (CAPES) | ✅ **~95% portável** | Foundation + `Compression` (gunzip) | Só o gunzip é Apple-specific (`compression_decode_buffer`); resto é lookup em tabela |
| Export/Import (backup) — formato do manifesto | ✅ **100% portável** (o *formato*) | JSON (Codable) | O *código* usa `ditto` (zip) — trocar por `System.IO.Compression` (.NET) ou `zipfile` (Python) |
| Extração de texto do PDF do Lattes | ❌ Apple-only | `PDFKit` (`PDFDocument.page(at:).string`) | Trocar por **PdfPig** (.NET/C#), **pdfminer.six**/**PyMuPDF** (Python), ou **pdf.js** (Node) |
| OCR de comprovantes escaneados | ❌ Apple-only | `Vision` (`VNRecognizeTextRequest`) | Trocar por **Tesseract** (via `Tesseract.NET`/`pytesseract`) ou **Windows.Media.Ocr** (UWP) |
| Geração do PDF final (relatório) | ⚠️ Estrutura portável, desenho não | `CoreGraphics`/`CoreText` (`PDFReportGenerator.swift`) | A LÓGICA (montagem de páginas, sumário, numeração) é portável; o desenho usa **QuestPDF** (.NET), **iText**/**iTextSharp**, ou **ReportLab** (Python) |
| Persistência (banco local) | ⚠️ Conceito portável | `SwiftData` (`@Model`) | Trocar por **SQLite** direto, **Entity Framework Core** (.NET), ou **SQLAlchemy** (Python) |
| Interface (toda a UI) | ❌ 100% Apple-only | `SwiftUI` + `AppKit` (`NSOpenPanel`, `NSWorkspace`, `NSWindow`…) | Reescrever do zero: **WinUI 3**/**WPF** (.NET), **Avalonia** (.NET cross-platform), ou **Electron** (web) |

**Resumo prático**: ~3.240 linhas de lógica pura (parsing + matching + Qualis + relatório +
export) podem ser reaproveitadas quase sem alteração conceitual. O que precisa ser 100% refeito é
a camada de UI, a leitura de PDF, o OCR e o desenho do PDF final.

## 2. Modelo de dados

Quatro entidades, relacionadas assim: `LattesProfile` 1—N `LattesSection` 1—N `LattesEntry` 1—N
`Certificate`. Um `Certificate` pode também não ter `entry` (fica "em limbo" — arquivo escaneado
sem correspondência encontrada, ou vínculo desfeito).

### LattesProfile (um currículo/perfil — o app suporta vários em paralelo)
| Campo | Tipo | Notas |
|---|---|---|
| id | UUID | |
| name | String | Nome de exibição na barra lateral |
| pdfPath | String | Caminho absoluto do PDF do Lattes original |
| importDate | Date | |
| lastUpdated | Date | Atualizado a cada "Atualizar Lattes" |
| savePath | String | Pasta padrão sugerida para salvar o relatório e para onde comprovantes importados são copiados |
| rawText | String | Texto completo extraído do PDF (cache, evita reabrir o PDF para exibir resumo) |
| rejectedLinks | [String] | Aprendizado: `"nomeArquivoSemExtensao||hashKeyDaEntrada"` — vínculos que o usuário já recusou explicitamente, para não sugerir de novo |
| sections | [LattesSection] | |
| certificates | [Certificate] | TODOS os certificados do perfil (vinculados + em limbo) |

Computados: `sortedSections` (por `order`), `totalEntries`, `confirmedCount`/`suggestedCount`/
`pendingCount` (soma das entradas por status), `limboCertificates` (certs com `entry == nil` e
`isRejected == false`).

### LattesSection (uma seção do currículo — "Artigos completos…", "Participação em bancas… -
Mestrado", etc. — ver §4 sobre como o parser SUBDIVIDE seções do Lattes em várias seções do app)
| Campo | Tipo | Notas |
|---|---|---|
| id | UUID | |
| title | String | Título de exibição — já inclui sufixos de subdivisão, ex. `"Atuação profissional - UFAC - Disciplinas ministradas"` |
| order | Int | Ordem de exibição na lista de seções |
| entries | [LattesEntry] | |

Computados: `sortedEntries` (por `order`), `confirmedCount`/`suggestedCount`/`pendingCount`.

### LattesEntry (uma linha comprovável do currículo — um artigo, uma banca, uma disciplina…)
| Campo | Tipo | Notas |
|---|---|---|
| id | UUID | |
| rawText | String | Texto bruto extraído (para depuração/matching) |
| title | String | Título de exibição extraído (pode ser o título do trabalho, o nome do candidato numa banca, etc. — depende do tipo) |
| kind | String | Rótulo curto do tipo: `"Artigo"`, `"Banca"`, `"Orientação"`, `"Vínculo institucional"`, `"Atividade administrativa"`, `"Disciplina ministrada"`, `"Organização de evento"`, `"Formação"`, `"Prêmio/Título"`, `"Projeto"`, `"Evento"`, `"Apresentação"`, `"Corpo editorial"`, `"Mídia"`, `"Produção técnica"`, `"Livro/Capítulo"`, `"Trabalho em evento"`, `"Documento"` (seção manual) |
| year | Int | Ano principal (0 = não identificado) |
| endYear | Int | Ano final de um período (vínculos/atividades); 0 = sem período OU período aberto ("Atual") — **cuidado**: essa dualidade de significado do 0 é tratada caso a caso, ver §5 |
| authors | String | |
| venue | String | Revista/instituição/local, dependendo do tipo |
| doi | String | |
| isbn | String | |
| issn | String | |
| edital | String | Número de edital, ex. `"41/2024"` |
| portaria | String | Portarias associadas, formato `"nº/ano"` separadas por espaço, ex. `"2891/2022 3706/2023"` |
| order | Int | Ordem de exibição dentro da seção |
| certificateStatus | enum `EntryStatus` | `.none` (vermelho/pendente) \| `.suggested` (amarelo) \| `.confirmed` (verde) — **armazenado, não recalculado automaticamente**; precisa ser atualizado manualmente sempre que os certificados da entrada mudam (isso já causou um bug real no app original — ver §5) |
| hashKey | String | `"\(year)_\(title.lowercased().trimmed.prefix(60))"` — chave usada para tentar re-vincular certificados automaticamente após um re-parse (ex.: ao importar um Lattes atualizado). É FRÁGIL: qualquer mudança no algoritmo de extração de título muda o hash e quebra o link — por isso o app tem um fallback por similaridade de texto quando o hash não bate (ver §5) |
| certificates | [Certificate] | |

Computados: `sortedCertificates` (por `order`, depois `importDate`), `confirmedCertificates`
(apenas `isConfirmed == true`), `nextCertificateOrder()`, `displayTitle` (monta
`"kind — título — venue (ano)"` para exibição/relatório).

### Certificate (um arquivo de comprovante)
| Campo | Tipo | Notas |
|---|---|---|
| id | UUID | |
| filePath | String | Caminho ABSOLUTO no disco — o app nunca move/copia os arquivos originais ao vincular (só ao importar um backup que embute os arquivos) |
| fileName / fileExtension | String | Derivados de `filePath` |
| extractedText | String | Texto extraído (nativo do PDF ou via OCR) — cacheado para não reprocessar |
| confidence | Double | 0.0–1.0, score do matching automático (0 = vínculo manual) |
| isConfirmed | Bool | Usuário confirmou este vínculo específico |
| isRejected | Bool | Usuário descartou este arquivo explicitamente (não deve reaparecer em buscas) |
| importDate | Date | |
| order | Int | Ordem entre os vários comprovantes de uma mesma entrada (controla ordem no PDF final) |
| entry | LattesEntry? | `nil` = "em limbo" |
| profile | LattesProfile? | Sempre setado (mesmo em limbo) |

Computados: `isPDF`/`isImage` (por extensão), `fileURL`, `exists` (checa se o arquivo ainda existe
no disco — pode ter sido movido/apagado fora do app), `fileNameNoExt`.

## 3. Inventário de telas/funcionalidades (para reconstruir a UI)

**Layout geral**: janela com 3 colunas (`NavigationSplitView`):
1. **Barra lateral** — lista de perfis/currículos (multi-perfil). Botão "+" com menu: "Importar
   novo currículo Lattes…" (abre PDF, roda o parser, cria o perfil) ou "Importar arquivo de
   comprovação…" (restaura um backup `.zip`, ver §8). Clique direito num perfil: "Exportar
   comprovação…", "Excluir currículo".
2. **Coluna central** — o currículo aberto: barra de ações no topo, barra de status, lista de
   seções em acordeão (cada seção expande para mostrar suas entradas).
3. **Coluna direita** — preview do comprovante da entrada selecionada (PDF/imagem), com lista
   lateral se a entrada tiver mais de um arquivo.

**Barra de ações** (topo da coluna central):
- "Escanear Pasta por Certificados" → escolhe uma pasta, o app varre recursivamente, faz
  OCR/extração e sugere vínculos (ver §6) → abre a "Revisão de Comprovantes" (lista todos os
  arquivos com sugestão + score; botão "Vincular sugestões ≥90%"; cada linha permite trocar a
  entrada sugerida, ignorar, ou (se a confiança é alta para MAIS de uma entrada) vincular como
  "combo" a várias entradas ao mesmo tempo).
- "Atualizar Lattes" → importa um PDF novo do mesmo currículo: reconstrói TODAS as seções do zero
  (para refletir qualquer entrada nova/removida), mas tenta re-vincular os certificados já
  confirmados às novas entradas (por hash exato, com fallback por similaridade — ver §5.7).
  Preserva intacta a seção manual "Outros Documentos" (nunca é apagada/reprocessada).
- "Adicionar Documento" → cria uma entrada manual (título livre + 1 arquivo) na seção especial
  "Outros Documentos", que sempre aparece por último e nunca é tocada por "Atualizar Lattes".
- Menu de área Qualis (ver §7) — escolhe a área de avaliação CAPES usada para classificar artigos.
- "Gerar arquivo com comprovantes" → abre a tela de configuração do relatório final (ver §7 do
  gerador): período, quais seções incluir, incluir o Lattes completo, gerar sumário (opcional),
  numerar páginas (opcional) → gera o PDF e abre o painel de salvar.

**Barra de status**: contadores confirmados/sugeridos/pendentes, barra de cobertura (%
confirmado), botão "N arquivo(s) sem vínculo" (quando há certificados em limbo — reabre a
sugestão automática só para eles, sem re-escanear o disco), toggle "Só pendências" (filtra a
lista de seções para mostrar só entradas não confirmadas).

**Linha de uma entrada** (dentro de uma seção expandida): indicador de status (bolinha
verde/amarela/vermelha), título, badge Qualis (só em artigos), contador de anexos (clipe), menu
"⋯": "Vincular arquivo…" (abre seletor de arquivo), "Gerenciar comprovantes…" (lista todos os
arquivos da entrada, permite reordenar por arrastar/setas, confirmar/desconfirmar individualmente,
abrir, excluir), "Confirmar todos", "Excluir todos os comprovantes"; se a entrada é da seção
manual "Outros Documentos", aparece também "Excluir documento" (remove a entrada inteira). Suporta
arrastar-e-soltar um arquivo diretamente na linha para vincular.

**Preview (coluna direita)**: mostra o PDF/imagem do certificado selecionado (troca
automaticamente para o primeiro confirmado quando a entrada muda).

## 4. Pipeline de parsing do currículo Lattes (`LattesPDFParser`) — o coração do app

Entrada: o texto extraído do PDF (concatenação simples de todas as páginas). Saída: lista de
`(título da seção, [entradas])`.

### 4.1 Formato de origem — duas variantes, ambas precisam funcionar
- **Exportação oficial do Lattes** (botão "Gerar currículo" na plataforma): layout mais limpo, mas
  ainda assim com colunas achatadas (ver 4.4).
- **"Imprimir" do navegador (Cmd+P) apontando pra página do Lattes**: insere cabeçalho/rodapé de
  impressão em CADA página (data/hora, "Currículo Lattes", URL + número de página) que ficam
  intercalados NO MEIO do texto das seções — precisa ser filtrado (`isNoise`). **Esse formato
  também introduz um bug de reordenação do extrator de texto**: pedaços de texto que deveriam
  ficar em sequência às vezes trocam de posição entre si na extração (ver 4.6 e 4.7 — dois bugs
  reais causados exatamente por isso, já corrigidos, cuja lição vale para qualquer extrator de PDF
  usado no port).

### 4.2 Extração do nome do titular
Âncora principal: a linha imediatamente ACIMA de "Endereço para acessar este CV" (nesse formato,
o nome sempre precede essa frase). Fallback 1: linha começando com "Nome " na seção
"Identificação". Fallback 2: primeira linha que "parece um nome" (2+ palavras capitalizadas, sem
dígitos/URL/`@`/`:`, 5–70 caracteres, sem palavras de ruído como "currículo"/"anotou"/"visualizar").

### 4.3 Classificação de cabeçalhos de seção
Uma tabela estática (~40 entradas) mapeia o texto normalizado do cabeçalho (sem acento, minúsculo)
para: um título de exibição, um "modo especial" de parsing (ver 4.5), e se é uma seção "filha" de
Orientações (recebe sufixo automático "(concluídas)"/"(em andamento)" conforme sob qual
subcabeçalho aparece). Cabeçalhos que servem só de delimitador, sem virar seção comprovável (ex.:
"Idiomas", "Áreas de atuação"), ficam numa lista de exclusão. A linha "Totais de produção" é um
marcador de PARADA — tudo depois dela (o resumo estatístico do Lattes) é ignorado.

Cada linha do corpo é testada contra essa tabela por **igualdade exata** OU por **prefixo** (só
quando o alias tem ≥12 caracteres, para evitar falsos positivos com palavras curtas). Lattes às
vezes quebra um cabeçalho em duas linhas físicas ("Projetos de" / "pesquisa") — se a linha atual
não bate sozinha, tenta juntar com a PRÓXIMA linha antes de desistir.

**Armadilha corrigida — parênteses desbalanceados**: o match por prefixo pode confundir uma
anotação de ENTRADA com um cabeçalho de verdade. Ex.: a anotação de cada item de "Organização de
eventos" termina em `"(Congresso, Organização de evento)"`; se isso quebra em duas linhas físicas,
sobra uma linha solta `"Organização de evento)"` que bate como prefixo do alias `"organizacao de
evento"` — disparando um corte de seção FALSO no meio da listagem. Regra de proteção: nunca tratar
uma linha como cabeçalho (nem por match direto, nem no reencaixe de 2 linhas) se ela tem mais `")"`
do que `"("` — isso indica que é o FECHO de um parêntese aberto antes, não um título. Cuidado com a
correção óbvia-mas-errada: bloquear qualquer linha que TERMINE em `")"` quebra cabeçalhos
legítimos como `"Trabalhos publicados em anais de eventos (completo)"` (que também termina em
`")"`, mas com parênteses BALANCEADOS). A regra certa é contar `(` vs `)`, não checar sufixo.

### 4.4 Padrões de "coluna achatada" e como cada um é resolvido
O texto de um PDF é extraído em ORDEM DE LEITURA aproximada, então colunas visuais viram texto
sequencial errado. Os padrões observados, e como cada parser os resolve:

- **Numeração em lista, inline**: `"1. Texto…\n2. Outro texto…"` → cada linha que começa com
  `\d{1,3}\.` inicia uma entrada nova (`isNumberStart` + `stripLeadingNumber`, limitado a 1–3
  dígitos para não confundir com um ano tipo `"2024."`).
- **Numeração DESTACADA/empilhada**: a coluna de números vem inteira, separada do conteúdo —
  `"1.\n2.\n3.\n"` seguido só depois pelo texto de todas as entradas. Detectado quando há ≥2
  números SEQUENCIAIS (`1`, depois `2`, depois `3`…) isolados em linhas próprias; nesse caso os
  números são removidos e a divisão cai para a "âncora de ano" (próximo item).
- **Sem numeração confiável — âncora de ano**: cada entrada termina numa frase que acaba em ponto
  final OU num ano (`"…, 2024."` ou `"…2024"`), EXCETO quando a próxima linha começa com `"("` —
  nesse caso o tipo/título entre parênteses ainda está por vir (`"… 2023."` seguido de
  `"(Congresso) Título."` deve ficar junto). Uma anotação final `"Citações: N | N"` é ignorada
  nesse teste (não impede o fechamento).
- **Metadados soltos sob uma entrada** ("Palavras-chave:", "Referências adicionais:", "Home
  page:", "Meio de divulgação:") NUNCA iniciam entrada nova — grudam na entrada ANTERIOR. Mesma
  regra para: URL quebrada em várias linhas (`http`/`www.`), uma URL entre colchetes sozinha numa
  linha (`"[http://…]"` — artefato de "Home page:" quebrado em 2 linhas), linhas começando em
  minúscula ou travessão (continuação de frase), e anotações curtas de portaria/edital.
- **Fragmento sem rótulo, em seções "de citação"** (Artigo/Livro-Capítulo/Trabalho em
  evento/Produção técnica/Mídia/Corpo editorial — tipos onde toda entrada real começa com
  "SOBRENOME, Iniciais.."): uma linha curta (≤160 caracteres) SEM vírgula nos primeiros 50
  caracteres E sem nenhum ano nela nunca é o início de uma entrada nova — é metadado sem rótulo
  (ex. um nome de veículo solto `"Cuadernos de Pesimismo (Ciudad de México)"`) ou a primeira linha
  de um resumo/abstract sem rótulo (`"Este artigo tem por objetivo…"`). Sem essa regra, esse texto
  vaza como prefixo do título da entrada SEGUINTE, corrompendo-a. Restrito a esses tipos porque em
  outros (ex. Eventos, cujas entradas começam com `"Conferencista no(a)…"`, sem vírgula perto do
  início) a mesma regra apagaria entradas legítimas.
- **Numeração + coluna de período juntas na MESMA linha**: em "Formação acadêmica" e bancas por
  nível, uma linha às vezes contém tanto o cluster de números quanto o marcador de nível/período —
  ver 4.6 e 4.7 para os dois casos concretos já resolvidos.
- **Bancas**: o delimitador mais confiável não é numeração nem ano — é a frase fixa
  `"Participação em banca de"` (aparece exatamente uma vez por banca, mesmo com achatamento
  extremo). Usada como âncora quando há numeração destacada.

### 4.5 Modos especiais de parsing por tipo de seção
Além do parser genérico (4.4), várias seções têm parsing dedicado porque seu layout é regular o
bastante para ser mais confiável que as heurísticas gerais:

- **Formação acadêmica/titulação** e **Pós-doutorado**: cada diploma começa com um NÍVEL
  reconhecível (`Doutorado|Mestrado( Profissional)?|Graduação|Especialização|Aperfeiçoamento|Curso
  Técnico|Ensino Fundamental|Ensino Médio|Livre-docência|Residência|Habilitação|Pós-Doutorado`,
  regex ancorada no início da linha após remover um prefixo de período `"AAAA - AAAA "`). Divide
  por esse nível; a instituição é a primeira linha do bloco que começa com
  "Universidade/Instituto/Faculdade/Fundação/Centro"; o ano vem de `"Ano de obtenção: AAAA"` ou,
  na falta, do último ano encontrado no texto do bloco. **Sem marcadores reconhecíveis, cai no
  parser genérico.**
- **Prêmios e títulos**: a coluna de anos vem achatada numa linha só no topo (`"2023 2017 2012
  <prêmio1>…"`) OU como linhas isoladas só com o ano. Delimitador entre prêmios: se o corpo tem
  linhas terminando em `"."`, usa o ponto; senão, quebra quando uma linha termina numa palavra
  Capitalizada (nome de instituição). Linhas vazadas de "Áreas de atuação"/"Idiomas" por
  achatamento de coluna adjacente (`"Grande área:"`, `"Compreende/Fala/Lê/Escreve"`,
  `"Periódico:"`) são filtradas antes.
- **Atuação profissional**: parser dedicado com máquina de estados (ver 4.8) — extrai vínculos
  institucionais, atividades administrativas e disciplinas ministradas, depois REAGRUPA tudo por
  instituição/categoria (ver 4.7 — feature pedida pelo usuário, com um bug de reordenação
  descoberto no caminho).
- **Projetos (pesquisa/extensão/desenvolvimento)**: âncora é a palavra `"Descrição:"` — o título
  do projeto é a linha de conteúdo IMEDIATAMENTE ANTES dela (juntando a linha de cima também, se o
  título ficou curto demais sozinho). Linhas de metadado (`"Situação:"`, `"Alunos envolvidos:"`,
  `"Financiador:"` etc., ou uma linha que é só uma lista de nomes com ≥2 `";"`) são puladas ao
  procurar o título. Sem nenhuma âncora "Descrição:" encontrada, verifica se o corpo parece
  conteúdo vazado de Atuação profissional (contém "disciplinas ministradas"/"vínculo:"/"conselhos,
  comissões") — se sim, descarta (não são projetos de verdade); senão cai no parser genérico.
- **Organização de eventos**: cada entrada termina em `"(Tipo, Organização de evento)"` — divide
  no terminador `"evento)"` (regex, case-insensitive) quando há ≥2 ocorrências; senão cai no
  parser genérico. O título extraído é o texto após a lista de autores (que termina em `".. "`) e
  antes da vírgula+ano.
- **Participação em eventos**: parser genérico, depois SEPARADO em duas seções — "Apresentação"
  (o titular teve papel ativo) e "Ouvinte" (só assistiu). Distinção: existe uma lista fixa de
  papéis de apresentação (`conferencista, apresentação, comunicação, moderador, mediador,
  palestrante, debatedor, expositor, avaliador, coordenador, organizador, relator, painelista,
  entrevistado, instrutor`) — se o texto da entrada (após remover numeração) começa com algum
  desses papéis, é Apresentação; senão (a entrada começa direto pelo NOME do evento), é Ouvinte.
- **Participação em bancas**: "trabalhos de conclusão" é subdividida por nível — ver 4.6.
  "Comissões julgadoras" (concurso público etc.) não tem esses marcadores, fica num bucket só. O
  título de cada banca é o nome do candidato, extraído do padrão `"…Participação em banca de
  <Nome>. <Título>, <ano>. (<Área>) <Instituição>."` via regex.

### 4.6 Bug real e corrigido — reordenação do extrator de PDF nos marcadores de nível de banca
O Lattes agrupa "Participação em banca de trabalhos de conclusão" com sub-cabeçalhos soltos no
meio do corpo: `"Mestrado"`, `"Doutorado"`, `"Exame de qualificação de mestrado"`, `"Exame de
qualificação de doutorado"`, `"Graduação"`. A subdivisão por nível funciona detectando essas linhas
EXATAS (após normalizar acento/caixa).

**Problema 1 — numeração colada no marcador ERRADO**: quando a coluna de numeração destacada
(`"1. 2. 3. 4. 5. 6."`) do nível ATUAL vem, no fluxo de extração, colada ao MARCADOR DO PRÓXIMO
nível (ex.: `"Mestrado"` aparece sozinho, e a linha seguinte já é `"1. 2. 3. 4. 5. 6. Doutorado"`,
sem nenhuma entrada real de Mestrado entre elas) — mas essas 6 entradas que vêm DEPOIS ainda são de
Mestrado, não de Doutorado. Sem tratar isso, as 6 entradas de mestrado e as 6 de doutorado ficam
todas misturadas sob um único rótulo errado.

**Correção — "troca de rótulo adiada"**: quando um marcador de nível chega colado a um cluster de
numeração (`numCount > 0`) logo após OUTRO marcador que ainda não recebeu nenhuma linha de
conteúdo, a troca de rótulo não acontece na hora — fica pendente. A partir daí, cada linha que
CONTÉM o início de uma entrada real (a frase `"Participação em banca de"`) é contada; só quando a
(N+1)-ésima entrada real começa (não a N-ésima — importante: cortar na N-ésima cortaria no meio do
texto que ainda resta da última entrada do nível anterior) é que o rótulo pendente entra em vigor.

**Problema 2 — a frase-âncora quebra entre duas linhas físicas**: `"…Participação em banca"` numa
linha e `"de Fulano…"` na próxima. Uma checagem linha-a-linha perde essas ocorrências e a contagem
do problema 1 fica errada. Correção: a busca pela frase é feita no texto de TODAS as linhas UNIDAS
por espaço (não linha a linha), e cada posição de match é depois mapeada de volta para o índice da
linha original (usando os deslocamentos de caractere acumulados) — só então a linha correspondente
é marcada como "início de entrada real" para fins de contagem.

**Problema 3 (armadilha de implementação)**: contar quantos números tem um cluster tipo `"1. 2. 3.
4. 5. 6. "` fazendo `.split(separator: ".")` conta ERRADO — o espaço sobrando depois do último
ponto vira um elemento a mais na lista, inflando a contagem em 1. A contagem certa é literalmente
contar os caracteres `"."` na substring do cluster.

Resultado verificado contra a tabela "Totais de produção" do próprio PDF: bate exato (Mestrado 6,
Doutorado 6, Qualificação de Doutorado 1, Graduação 21, + Qualificação de Mestrado 8 — essa última
não aparece nos totais oficiais do Lattes, mas soma certo com o total plano anterior).

### 4.7 Feature — reorganização de "Atuação profissional" por vínculo, e outro bug de reordenação
Pedido do usuário: separar por VÍNCULO (instituição — ex. duas universidades diferentes) e, dentro
de cada vínculo, em três grupos: "Vínculo institucional" (mudanças de cargo/nível), "Atividades
administrativas" (comissões, conselhos, cargos de direção) e "Disciplinas ministradas". Ordenado
da instituição/entrada mais recente para a mais antiga (um vínculo/atividade "em aberto" — sem ano
final, `"Atual"` — conta como o mais recente possível).

Implementação em duas etapas: (1) `parseAtuacao` extrai a lista PLANA de entradas, cada uma já
com `kind` = uma das três categorias e `venue` = nome da instituição vigente no momento em que a
linha foi processada (rastreado por uma máquina de estados simples: toda linha reconhecida como
nome de instituição — contém "universidade"/"instituto"/"faculdade"/"fundação", sem ser uma
continuação tipo "Regime:"/"Portaria"/"Lotado" — atualiza a instituição corrente até a PRÓXIMA
linha de instituição aparecer). (2) `groupAtuacaoPorVinculo` agrupa essa lista plana por
instituição+categoria, ordena, e reindexa `order` dentro de cada grupo final.

**Bug descoberto pela própria feature** (só ficou visível DEPOIS de agrupar por instituição — antes
tudo ficava numa lista só, então pequenas variações no texto do nome da instituição não apareciam
como problema): o extrator de PDF reordena o valor de `"Regime: <Integral/Parcial/Dedicação
exclusiva>"` e cola esse valor no FIM do nome da instituição do vínculo SEGUINTE — às vezes com
espaço (`"Universidade Federal do Espírito Santo Integral"`), às vezes sem
(`"Universidade Federal do Espírito SantoDedicação exclusiva"`). Isso fazia a MESMA instituição
virar 2–3 grupos diferentes.

Correção em duas camadas (defesa dupla, porque uma sozinha não bastava): (1) ao capturar o nome da
instituição, remove um sufixo de regime colado no fim (regex `"\s*(Integral|Parcial|Horista|
Dedicação\s*exclusiva)\s*$"`); (2) mesmo depois de limpo, a mesma instituição ainda pode aparecer
ORA com a sigla no fim (`"… - UFES"`), ORA sem (porque a linha contaminada nunca tinha a sigla) —
então o AGRUPAMENTO usa uma CHAVE normalizada que também remove um sufixo `"- SIGLA"` (2–10 letras
maiúsculas) antes de comparar, e escolhe como RÓTULO de exibição a variante mais longa/completa
encontrada no grupo (normalmente a que tem a sigla). Lição geral: ao agrupar por um campo de texto
extraído de PDF, nunca comparar a string bruta — normalizar removendo sufixos/artefatos conhecidos
E preferir a variante mais informativa para exibição.

### 4.8 Deduplicação entre seções repetidas
O Lattes às vezes lista o MESMO conteúdo em dois lugares (ex.: entrevistas aparecem tanto em
"Entrevistas, mesas redondas…" quanto dentro de "Educação e Popularização de C&T", com layout
diferente — achatado). Regra: quando duas seções brutas têm o MESMO título final, seus corpos são
parseados SEPARADAMENTE (nunca concatenados antes de parsear — layouts diferentes concatenados
contaminam a detecção de modo) e depois FUNDIDOS com deduplicação: uma entrada nova é descartada se
seu texto normalizado é igual a uma já existente, OU se o PREFIXO longo (≥60 caracteres) de uma
aparece dentro do texto da outra E ALÉM DISSO o título de uma (≥15 caracteres) também aparece na
outra (prefixo sozinho não basta — entradas distintas legitimamente repetem a mesma lista de
autores no início). Depois disso, uma segunda passada remove versões TRUNCADAS: uma entrada que
termina exatamente no ano (`"…2021."`) e é prefixo de outra mais longa do MESMO ano é removida
(evita duplicar uma entrada cujo texto foi cortado pela paginação numa ocorrência e veio completo
na outra).

## 5. Algoritmo de matching comprovante ↔ entrada (`SimilarityMatcher` + `CertificateIndexer`)

Objetivo: dado o texto extraído de um arquivo (PDF/imagem, nativo ou via OCR) e a lista de
entradas do currículo, encontrar qual entrada esse arquivo comprova, com um score 0.0–1.0.

### 5.1 Sinais usados, em ordem de prioridade
1. **Identificadores exatos** (quase-certeza, prioridade sobre qualquer análise de texto):
   - **Portaria nº+ano**: extrai pares `"núm/ano"` de ambos os lados via regex
     (`portaria\s*(?:nº?°?o?\.?\s*)?(\d{2,6})` + busca de um ano nos ~50 caracteres seguintes).
     Mesmo número + mesmo ano → **0.99**. Mesmo número mas um dos lados sem ano → **0.95** (não dá
     pra confirmar, mas é bom sinal). Mesmo número, anos DIFERENTES em ambos → **0** (é outra
     portaria, mesmo número reutilizado em anos diferentes é comum).
   - **Edital** (`"nº/ano"`, regex similar): match exato → **0.99**.
   - **DOI** (`10\.\d{4,}/…`): match exato → **1.0**.
   - Um "portão" (gate) impede um documento SEM nenhum identificador de publicação (ISSN/ISBN/DOI
     no texto) de ser vinculado a um Artigo ou Livro/Capítulo — evita falsos positivos por
     coincidência de palavras do título. Outro gate: um documento que claramente é uma portaria
     (contém a palavra ou um par portaria válido) nunca é sugerido para Orientação/Formação (tipos
     que não usam portaria).
2. **Score textual** (quando não há identificador): combinação de dois sinais —
   - **Cobertura do TÍTULO ponderada por IDF**: tokeniza o título da entrada (só palavras "com
     significado": ≥4 letras, fora de uma lista de stopwords português/inglês + termos
     administrativos genéricos tipo "carga horária"/"certificado"), calcula que fração do PESO
     TOTAL (soma do IDF de cada palavra) está presente no texto do certificado. IDF é calculado
     sobre TODOS os títulos das entradas do perfil (`buildIDF`): `log(N / (1 + docFreq)) + 0.5` —
     palavras raras ("Schopenhauer") pesam muito mais que comuns ("filosofia", "universidade").
   - **Maior FRASE contígua** do título presente literalmente no certificado (usa TODAS as
     palavras, inclusive preposições, já que o certificado costuma repetir o título ao pé da
     letra) — fração do comprimento da maior frase encontrada (máx. 10 palavras) sobre o mínimo
     entre isso e o total de palavras do título.
   - Título com ≤2 palavras "significativas": só a frase contígua conta (palavras soltas genéricas
     tipo "Doutorado em Filosofia" não devem casar por coincidência).
   - Menos de 2 palavras casadas E frase <60%: score de título zerado (proteção contra falso
     positivo por 1 palavra comum).
   - **Sinal de local/evento** (venue): mesma lógica de cobertura ponderada, aplicada ao campo
     `venue` (revista/instituição/evento) em vez do título — importante para EVENTOS, onde o
     certificado geralmente cita o NOME DO EVENTO, não o título do trabalho apresentado. Se o
     score de título é baixo (<0.4) mas o venue cobre ≥60% com ≥2 palavras casadas, o score final
     vira `venueCov * 0.8`. Se ambos título (≥0.3) e venue (≥0.5) batem, reforça o score de título
     em +0.1.
   - **Reforço por ISSN**: se o score de texto já é >0.2 e o ISSN do certificado bate com o da
     entrada, soma +0.15 (o ISSN identifica o PERIÓDICO, não o artigo específico — por isso só
     reforça, não decide sozinho).
3. **Ajuste por proximidade de ANO** (aplicado depois do score bruto): extrai anos plausíveis
   (1990–2035) do nome do arquivo (preferencial — mais confiável que o texto OCR) ou do texto.
   Para entradas com PERÍODO (`endYear` presente ou "Atual"/aberto): se algum ano do certificado
   cai dentro do intervalo `[year, endYear ou ano atual]`, reforça +0.06; se a distância mínima até
   o intervalo é ≥3 anos, penaliza ×0.80. Para entradas de ano único: mesmo ano → +0.06; distância
   ≥3 → ×0.80; ±1/±2 → neutro.
4. **Bônus pela PASTA onde o arquivo está** (só quando não é identificador exato): o caminho
   relativo da pasta é mapeado para um conjunto de `kind`s prováveis por palavras-chave (ex. pasta
   contendo "banca" → tipo "Banca"; "evento"/"congresso"/"palestra" → vários tipos de evento;
   "orienta"/"monitoria" → "Orientação"; "parecer"/"tecnic" → "Produção técnica"; etc.) — se o
   `kind` da entrada bate com algum inferido da pasta, soma **+0.20**. Sem esse bônus, score máximo
   por texto é limitado a 0.97 (só identificadores exatos chegam a 1.0).

### 5.2 Atribuição em lote (evita empilhar tudo numa entrada genérica)
Depois de pontuar TODOS os arquivos contra TODAS as entradas, uma passada global reequilibra: se o
top-1 e o top-2 de um arquivo estão muito próximos (diferença ≤0.08) e a entrada do top-1 já está
"bem coberta" (≥2 arquivos já escolheram ela) enquanto a do top-2 ainda não tem nenhum, prefere o
top-2 — evita que uma entrada genérica (ex. "Vínculo institucional") absorva arquivos que na
verdade servem entradas mais específicas com score quase igual.

### 5.3 Limiares de confiança
- `≥0.90` → "sugestão confiável" (contada, pode ser auto-aceita em lote com um clique).
- `0.35–0.90` → "palpite" (mostrado para confirmação manual rápida, não auto-aceito).
- `<0.35` ou sem nenhum match → arquivo fica "em limbo" (sem sugestão).

### 5.4 Extração de texto do arquivo (parte NÃO portável — Apple-specific)
PDF: tenta a camada de texto nativa primeiro (rápido); se o total extraído tem menos de 20
caracteres "úteis" (é digitalizado — sem camada de texto), renderiza as páginas como imagem (até
4 páginas, escala 3× limitada a 4000px por lado) e roda OCR. Imagem: sempre OCR direto. No app
original o OCR é `Vision` (`VNRecognizeTextRequest`, idiomas pt-BR + en-US, nível "accurate", com
correção de linguagem ligada) — no port, o equivalente mais próximo é **Tesseract** (suporta
português) ou um serviço de OCR na nuvem.

### 5.5 Aprendizado de rejeições
Toda vez que o usuário troca a sugestão de um arquivo por outra entrada, ou marca "ignorar", o par
`"nomeArquivoSemExtensao||hashKeyDaEntradaRecusada"` é gravado em `rejectedLinks` do perfil — em
buscas futuras, esse par nunca mais é sugerido (gate no passo 1 do matching).

### 5.6 Revisão de comprovantes órfãos ("em limbo")
Um botão dedicado roda a MESMA lógica de matching (`rankedMatches`) só para os certificados sem
`entry`, reaproveitando o `extractedText` já salvo no banco (não relê o arquivo do disco nem refaz
OCR) — útil depois de um "Atualizar Lattes" que não conseguiu re-vincular tudo por hash.

### 5.7 Re-vinculação após "Atualizar Lattes" (`applyUpdate`)
Ao importar um Lattes atualizado, TODAS as seções/entradas antigas são apagadas e recriadas do
zero a partir do novo parse (reflete qualquer melhoria no parser e qualquer mudança real no
currículo). Para não perder os vínculos já confirmados: (1) antes de apagar, mapeia cada
certificado vinculado pelo `hashKey` da sua entrada antiga; (2) ao criar cada entrada nova, se o
`hashKey` dela bate com algum da tabela, re-vincula os certificados automaticamente — **e
IMPORTANTE: recalcula `certificateStatus` da entrada nova a partir dos certificados re-vinculados**
(bug real do app original: esse recálculo tinha sido esquecido, então TODA entrada nova nascia
com status "pendente" mesmo com o certificado certo re-vinculado — parecia que "todos os vínculos
tinham sumido", quando na verdade só o campo de status armazenado não tinha sido atualizado). (3)
Para certificados cujo hash NÃO bateu em nenhuma entrada nova (o algoritmo de extração de
título/ano pode mudar entre versões do parser, invalidando o hash mesmo para o mesmo certificado
real) — fallback: roda o MESMO matching por similaridade (5.1) usando o texto já extraído do
certificado contra as entradas novas; acima de 0.90, re-vincula automaticamente; abaixo disso,
fica em limbo (recuperável depois via 5.6).

## 6. Geração do relatório final (`PDFReportGenerator`)

Monta um único PDF A4 retrato com esta estrutura de "páginas" (internamente uma lista de "slabs" —
cada slab é ou uma página externa copiada de outro PDF, ou uma página desenhada pelo app):

1. **Currículo Lattes completo** (opcional, todas as páginas do PDF original copiadas).
2. Para cada seção selecionada, na ordem: uma **página divisória** (fundo colorido, título grande
   centralizado — NÃO recebe número impresso mesmo que a numeração esteja ligada, mas ainda conta
   para a paginação), depois para cada entrada com pelo menos 1 certificado CONFIRMADO: uma
   **página de cabeçalho da entrada** (título, autores, badge Qualis se for artigo) seguida de
   TODAS as páginas dos certificados confirmados dessa entrada, na ordem definida pelo usuário
   (PDFs copiados página a página; imagens desenhadas centralizadas, redimensionadas para caber).
3. **Sumário** (opcional) — construído por ÚLTIMO porque precisa saber quantas páginas ele mesmo
   vai ocupar antes de poder numerar os itens do corpo (calcula quantas linhas cabem por página —
   30 na primeira, que tem o cabeçalho "Sumário", 36 nas seguintes — para saber quantas páginas o
   sumário precisa; só então soma esse total ao índice de cada item do corpo para saber o número
   de página final de cada entrada). Vem ANTES do corpo no arquivo final.
4. **Numeração de página** (opcional) — canto superior direito, número branco numa caixa preta
   arredondada (garante legibilidade em qualquer fundo). Contagem sempre em ordem (1, 2, 3…)
   incluindo páginas sem número impresso (a contagem não pula, só a IMPRESSÃO do número é
   condicional).

Config expõe: quais seções incluir (vazio = todas), filtro de período (ano inicial/final —
compara com `entry.year`, ignora entradas sem ano), incluir o Lattes completo (bool), gerar sumário
(bool), numerar páginas (bool). A classificação Qualis de cada artigo é pré-calculada ANTES de
gerar (mapa `entryID → estrato`), porque no app original o `QualisService` só pode ser consultado
na thread principal.

**Para o port**: a ESTRUTURA (lista de slabs, ordem, cálculo de paginação do sumário, contagem vs.
impressão do número) é 100% reaproveitável como lógica. O DESENHO (texto, cores, posicionamento,
cópia de páginas de outro PDF) precisa ser refeito na biblioteca de PDF escolhida — todas as libs
sugeridas (QuestPDF, iText, ReportLab) suportam desenhar texto/formas E importar páginas de outro
PDF como uma unidade.

## 7. Classificação Qualis (`QualisService`)

Três tabelas TSV (uma por quadriênio CAPES: 2016-2019, 2017-2020, 2021-2024), cada linha
`ISSN\tTítulo\tÁrea\tEstrato`, empacotadas comprimidas em `.tsv.gz` dentro do app (recursos
embutidos). Ao trocar a "área de avaliação" selecionada (ex. "Filosofia"), reconstrói em background
um índice por quadriênio: mapa ISSN→estrato, mapa título-normalizado→estrato, e uma lista para
fuzzy match (tokens do título, ≥3 letras).

Classificação de um artigo (`journal`, `issn`, `year`): escolhe o quadriênio pelo ano
(`>=2021→2021-2024`, `>=2017→2017-2020`, senão `2016-2019`). Tenta, em ordem: (1) ISSN exato; (2)
título exato normalizado (sem acento, maiúsculo, só alfanumérico); (2b) resolve por um mapa
GLOBAL título→ISSN (todas as áreas/quadriênios) para o caso do periódico ter mudado de nome entre
avaliações; (3) fuzzy — interseção de tokens ≥2 E cobertura ≥80% em relação ao título mais curto
(periódico ou consulta, o que for menor).

**Único pedaço não-portável**: a descompressão gzip usa o framework `Compression` da Apple
(`compression_decode_buffer`, formato `COMPRESSION_ZLIB` = raw DEFLATE) com um parser manual de
cabeçalho gzip (pula campos opcionais FEXTRA/FNAME/FCOMMENT/FHCRC, lê o tamanho descomprimido dos
últimos 4 bytes). Em qualquer stack moderna isso é uma chamada de biblioteca padrão (`gzip` em
Python, `System.IO.Compression.GZipStream` em .NET, `zlib`/`pako` em Node) — não precisa reescrever
esse parser manual, só usar a lib nativa da plataforma.

## 8. Formato de exportação/backup (`ProfileArchiver`)

Um único arquivo `.zip` contendo:
- `manifest.json` — todo o grafo de dados (perfil → seções → entradas → certificados, mais os
  certificados "em limbo") serializado como JSON (`Codable`/qualquer serializador padrão), com
  datas em ISO 8601. Ver o `struct ProfileArchiveData` no Apêndice G para o schema exato (campo a
  campo, 1:1 com o modelo de dados do §2, mais um campo `bundledRelativePath` opcional por
  certificado — presente só quando os arquivos foram embutidos no pacote).
- `curriculo.pdf` — cópia do PDF original do Lattes (sempre incluído, é pequeno e essencial —
  sem ele, "incluir o Lattes completo" no relatório final não tem o que copiar).
- `files/` — opcional (usuário escolhe na hora de exportar): cópia de cada arquivo de certificado,
  renomeado `"<uuid-do-certificado>_<nomeOriginal>"` (evita colisão entre arquivos de mesmo nome).

**Import**: descompacta para uma pasta temporária, localiza o `manifest.json` (pode estar na raiz
ou um nível abaixo, dependendo da ferramenta de zip usada), decodifica, recria todo o grafo com
IDs NOVOS (não reaproveita os UUIDs originais). Se o nome do perfil já existe no destino, sufixa
automaticamente `" (2)"`, `" (3)"`… Se havia arquivos embutidos, copia cada um para
`<pastaPadrãoDoUsuário>/<NomeDoPerfil>/Comprovantes Importados/`; se NÃO foram embutidos, os
certificados importados mantêm o `filePath` ORIGINAL (só funcionam no destino se esse caminho
existir lá — ex. uma pasta sincronizada via nuvem).

**No app original**, compactar/descompactar usa o utilitário de linha de comando `ditto`
(sempre presente no macOS) via `Process`, para não adicionar uma dependência externa de zip. No
port, usar a lib de zip nativa da stack (`System.IO.Compression.ZipFile`, `zipfile` do Python,
`adm-zip`/`archiver` no Node) — o FORMATO (JSON + PDF + pasta `files/`) é o que importa manter,
não a ferramenta usada para compactar.

## 9. Sugestão de stack para o port Windows

Sem prescrever — a decisão é de quem for construir —, mas alguns pontos objetivos para pesar:

| Necessidade | Boas opções .NET | Boas opções Python | Boas opções Node/Electron |
|---|---|---|---|
| UI desktop nativa | WinUI 3, WPF, Avalonia (cross-platform) | — (Python não é forte em UI desktop nativa) | Electron (web tech, mais familiar se o dev já sabe JS) |
| Ler texto de PDF | PdfPig, iText | pdfminer.six, PyMuPDF (fitz) | pdf.js, pdf-parse |
| Gerar PDF | QuestPDF, PdfSharp, iText | ReportLab, fpdf2 | pdf-lib, pdfkit (Node) |
| OCR | Tesseract via `Tesseract` NuGet | pytesseract | tesseract.js |
| Banco local | SQLite + EF Core, ou SQLite direto | SQLite + SQLAlchemy | SQLite + better-sqlite3 |
| Zip | `System.IO.Compression` (built-in) | `zipfile` (built-in) | `adm-zip`/`archiver` |

Dado que o app já é 100% desktop com acesso a arquivo local, OCR offline e manipulação de PDF —
**.NET (WinUI 3 ou Avalonia) + PdfPig + QuestPDF + Tesseract** é provavelmente o caminho mais direto
(ecossistema maduro para essas 4 necessidades ao mesmo tempo, sem depender de serviços na nuvem).
Electron é uma alternativa razoável se o desenvolvedor já tem mais fluência em JavaScript/web do
que em C#.

## 10. Apêndices — código-fonte completo dos arquivos 100% portáveis

Os arquivos abaixo usam SÓ `Foundation` (mais `Compression` no caso do Qualis, isolado numa única
função `gunzip`) — nenhuma dependência de UI/PDFKit/Vision. Podem ser usados como referência
literal para transliterar a lógica regex/algoritmo para a linguagem escolhida; os comentários em
português já documentam o PORQUÊ de cada decisão não-óbvia.

- **Apêndice A** — Modelos de dados (`LattesProfile.swift`, `LattesSection.swift`,
  `LattesEntry.swift`, `Certificate.swift`) — ~230 linhas.
- **Apêndice B** — `SimilarityMatcher.swift` (scoring de matching) — 227 linhas.
- **Apêndice C** — `LattesPDFParser.swift` (parsing do currículo) — 1.434 linhas.
- **Apêndice D** — `CertificateIndexer.swift` (orquestração do matching + extração/OCR — as
  funções de extração de texto/OCR são Apple-specific e marcadas como tal; o resto é portável) —
  543 linhas.
- **Apêndice E** — `QualisService.swift` (classificação CAPES) — 218 linhas.
- **Apêndice F** — `PDFReportGenerator.swift` (estrutura do relatório final — o desenho usa
  CoreGraphics/CoreText, não-portável, mas a lógica de composição é) — ~300 linhas.
- **Apêndice G** — `ProfileArchiver.swift` (formato de export/import) — 281 linhas.

### Apêndice A — Modelos de dados

```swift
// LattesProfile.swift
import Foundation
import SwiftData

@Model
final class LattesProfile {
    @Attribute(.unique) var id: UUID
    var name: String
    var pdfPath: String
    var importDate: Date
    var lastUpdated: Date
    var savePath: String
    var rawText: String

    /// Aprendizado: vínculos recusados pelo usuário ("nomeArquivo||hashEntrada").
    /// Em novos escaneamentos, essas combinações são evitadas.
    var rejectedLinks: [String] = []

    @Relationship(deleteRule: .cascade, inverse: \LattesSection.profile)
    var sections: [LattesSection]

    // Todos os certificados do perfil (assigned + limbo)
    @Relationship(deleteRule: .cascade, inverse: \Certificate.profile)
    var certificates: [Certificate]

    init(name: String, pdfPath: String, savePath: String) {
        self.id = UUID()
        self.name = name
        self.pdfPath = pdfPath
        self.savePath = savePath
        self.rawText = ""
        self.importDate = Date()
        self.lastUpdated = Date()
        self.sections = []
        self.certificates = []
    }

    var sortedSections: [LattesSection] {
        sections.sorted { $0.order < $1.order }
    }

    var totalEntries: Int {
        sections.reduce(0) { $0 + $1.entries.count }
    }

    var confirmedCount: Int {
        sections.reduce(0) { acc, s in
            acc + s.entries.filter { $0.certificateStatus == .confirmed }.count
        }
    }

    var suggestedCount: Int {
        sections.reduce(0) { acc, s in
            acc + s.entries.filter { $0.certificateStatus == .suggested }.count
        }
    }

    var pendingCount: Int {
        sections.reduce(0) { acc, s in
            acc + s.entries.filter { $0.certificateStatus == .none }.count
        }
    }

    var limboCertificates: [Certificate] {
        certificates.filter { $0.entry == nil && !$0.isRejected }
    }
}
```

```swift
// LattesSection.swift
import Foundation
import SwiftData

@Model
final class LattesSection {
    @Attribute(.unique) var id: UUID
    var title: String
    var order: Int

    var profile: LattesProfile?

    @Relationship(deleteRule: .cascade, inverse: \LattesEntry.section)
    var entries: [LattesEntry]

    init(title: String, order: Int) {
        self.id = UUID()
        self.title = title
        self.order = order
        self.entries = []
    }

    var sortedEntries: [LattesEntry] {
        entries.sorted { $0.order < $1.order }
    }

    var confirmedCount: Int { entries.filter { $0.certificateStatus == .confirmed }.count }
    var suggestedCount: Int { entries.filter { $0.certificateStatus == .suggested }.count }
    var pendingCount: Int   { entries.filter { $0.certificateStatus == .none }.count }
}
```

```swift
// LattesEntry.swift
import Foundation
import SwiftData

enum EntryStatus: String, Codable {
    case none      = "none"
    case suggested = "suggested"
    case confirmed = "confirmed"
}

@Model
final class LattesEntry {
    @Attribute(.unique) var id: UUID
    var rawText: String
    var title: String
    var kind: String = ""
    var year: Int
    var authors: String
    var venue: String
    var doi: String
    var isbn: String
    var portaria: String = ""
    var issn: String = ""
    var edital: String = ""
    var endYear: Int = 0        // ano final do período (vínculos/atividades); 0 = aberto/sem
    var hashKey: String
    var order: Int
    var certificateStatus: EntryStatus

    var section: LattesSection?

    // Nullify (não cascade): ao deletar entry, certificados voltam ao limbo
    @Relationship(inverse: \Certificate.entry)
    var certificates: [Certificate]

    init(rawText: String, title: String, kind: String = "", year: Int = 0,
         authors: String = "", venue: String = "", order: Int = 0) {
        self.id = UUID()
        self.rawText = rawText
        self.title = title
        self.kind = kind
        self.year = year
        self.authors = authors
        self.venue = venue
        self.doi = ""
        self.isbn = ""
        self.order = order
        self.certificateStatus = .none
        self.certificates = []
        self.hashKey = Self.makeHash(year: year, title: title)
    }

    private static func makeHash(year: Int, title: String) -> String {
        let clean = title.lowercased()
            .trimmingCharacters(in: .whitespacesAndNewlines)
            .prefix(60)
        return "\(year)_\(clean)"
    }

    var yearString: String { year > 0 ? String(year) : "" }

    /// Todos os certificados da entrada, na ordem definida pelo usuário.
    var sortedCertificates: [Certificate] {
        certificates.sorted {
            $0.order != $1.order ? $0.order < $1.order : $0.importDate < $1.importDate
        }
    }

    var confirmedCertificates: [Certificate] {
        sortedCertificates.filter { $0.isConfirmed }
    }

    /// Próxima posição livre (para anexar um novo comprovante ao final).
    func nextCertificateOrder() -> Int {
        (certificates.map { $0.order }.max() ?? -1) + 1
    }

    /// Título descritivo que identifica a entrada na interface e nos relatórios.
    /// Ex.: "Artigo — Título — Revista (2024)" ou "Banca — Fulano (2023)".
    var displayTitle: String {
        let core = (title.isEmpty ? rawText : title)
            .trimmingCharacters(in: .whitespacesAndNewlines)
            .replacingOccurrences(of: "\n", with: " ")
        let shortCore = core.count > 150 ? String(core.prefix(150)) + "…" : core

        var parts: [String] = []
        if !kind.isEmpty { parts.append(kind) }
        if !shortCore.isEmpty { parts.append(shortCore) }
        if !venue.isEmpty,
           !shortCore.localizedCaseInsensitiveContains(venue),
           venue.count < 80 {
            parts.append(venue)
        }
        var result = parts.joined(separator: " — ")
        if year > 0 { result += " (\(year))" }
        return result
    }
}
```

```swift
// Certificate.swift
import Foundation
import SwiftData

@Model
final class Certificate {
    @Attribute(.unique) var id: UUID
    var filePath: String
    var fileName: String
    var fileExtension: String
    var extractedText: String
    var confidence: Double   // 0.0–1.0; 0 = adicionado manualmente
    var isConfirmed: Bool
    var isRejected: Bool
    var importDate: Date
    var order: Int = 0        // ordem dentro da entrada (0 = padrão; reordenável)

    var profile: LattesProfile?
    var entry: LattesEntry?

    init(filePath: String) {
        let url = URL(fileURLWithPath: filePath)
        self.id = UUID()
        self.filePath = filePath
        self.fileName = url.lastPathComponent
        self.fileExtension = url.pathExtension.lowercased()
        self.extractedText = ""
        self.confidence = 0.0
        self.isConfirmed = false
        self.isRejected = false
        self.importDate = Date()
    }

    var isPDF: Bool   { fileExtension == "pdf" }
    var isImage: Bool { ["jpg", "jpeg", "png", "tiff", "tif", "heic"].contains(fileExtension) }

    var fileURL: URL { URL(fileURLWithPath: filePath) }

    var exists: Bool { FileManager.default.fileExists(atPath: filePath) }

    var fileNameNoExt: String { (fileName as NSString).deletingPathExtension }
}
```

### Apêndice B — `SimilarityMatcher.swift`

```swift
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
```

### Apêndice C — `LattesPDFParser.swift` (1 de 4)

```swift
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
```

### Apêndice C — `LattesPDFParser.swift` (2 de 4)

```swift
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
```

### Apêndice C — `LattesPDFParser.swift` (3 de 4)

```swift
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
```

### Apêndice C — `LattesPDFParser.swift` (4 de 4 — final)

```swift
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
```

### Apêndice D — `CertificateIndexer.swift` (1 de 2 — orquestração + extração/OCR, ⚠️ Apple-specific)

> As funções `extractText`, `extractTextFromPDF`, `ocrImage` e `renderPageToCGImage` usam
> `PDFKit`/`Vision`/`AppKit` — **não portáveis**. No port, substituir por: extração de texto de PDF
> nativo (PdfPig/pdfminer/pdf.js) com fallback para OCR (Tesseract) quando o texto extraído tem
> menos de ~20 caracteres úteis. A ORQUESTRAÇÃO ao redor (rodar em background, progresso, log) e
> TODO o resto do arquivo (Parte 2 — scoring/ranking) são portáveis.

```swift
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

    // MARK: - Extração de texto (nonisolated, roda fora do main) — ⚠️ Apple-specific abaixo

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
```

### Apêndice D — `CertificateIndexer.swift` (2 de 2 — scoring/ranking, 100% portável)

```swift
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
```

### Apêndice E — `QualisService.swift` (⚠️ só a função `gunzip` no final é Apple-specific)

```swift
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

    // MARK: - gunzip (Compression / raw DEFLATE) — ⚠️ Apple-specific; trocar pela lib gzip nativa da stack

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
```

### Apêndice F — `PDFReportGenerator.swift` (⚠️ desenho via CoreGraphics/CoreText — a composição de páginas/sumário é portável, o desenho não)

```swift
import Foundation
import PDFKit
import AppKit
import CoreText

/// Gera o PDF final em A4 retrato:
///   1. Sumário com a página de cada item comprobatório (opcional)
///   2. Currículo Lattes completo
///   3. Comprovantes na ordem do Lattes, com página de legenda (divisória) por seção
/// Páginas numeradas (a partir de 1) no canto superior direito, com o número em
/// branco dentro de uma caixa preta para ficar sempre visível — também opcional.
struct PDFReportGenerator {

    struct ReportConfig {
        var profile: LattesProfile
        var selectedSectionTitles: Set<String>   // vazio = todas
        var startYear: Int?
        var endYear: Int?
        var includeLattes: Bool = true
        var qualisByEntry: [UUID: String] = [:]  // id da entrada → "A1" etc.
        var includeTOC: Bool = true
        var numberPages: Bool = true
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

        // 3 — Sumário (opcional; quando presente, precisa do nº de páginas que ele
        // próprio ocupa para numerar os itens do corpo corretamente)
        let tocSlabs: [Slab] = config.includeTOC
            ? buildTOCSlabs(toc, tocPageCount: tocPageCount(for: toc.count))
            : []

        // 4 — Montagem final com numeração (opcional)
        let all = tocSlabs + body
        return render(all, numberPages: config.numberPages)
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

    private static func render(_ slabs: [Slab], numberPages: Bool) -> Data? {
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
            // O número só é impresso quando a opção está ligada E a página permite
            // (divisórias de seção nunca mostram número, mesmo com a opção ligada).
            if numberPages, slab.showsNumber {
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
```

### Apêndice G — `ProfileArchiver.swift` (formato 100% portável; `zip`/`unzip` são as 2 únicas funções Apple-specific, no final)

```swift
import Foundation
import SwiftData

/// Exporta/importa um perfil completo (currículo + seções + entradas + comprovantes)
/// como um único arquivo .zip — para backup ou para abrir em outro computador.
/// Usa `ditto` (sempre presente no macOS) em vez de uma dependência externa de zip.
enum ProfileArchiver {

    enum ArchiveError: LocalizedError {
        case zipFailed
        case unzipFailed
        case manifestNotFound

        var errorDescription: String? {
            switch self {
            case .zipFailed: return "Não foi possível compactar o arquivo exportado."
            case .unzipFailed: return "Não foi possível abrir o arquivo — verifique se é um .zip exportado por este app."
            case .manifestNotFound: return "Arquivo inválido: não contém um currículo exportado por este app."
            }
        }
    }

    // MARK: - Modelo serializável

    private struct ProfileArchiveData: Codable {
        var formatVersion: Int = 1
        var name: String
        var pdfPath: String
        var importDate: Date
        var lastUpdated: Date
        var savePath: String
        var rawText: String
        var rejectedLinks: [String]
        var sections: [SectionData]
        var limboCertificates: [CertificateData]   // sem entrada vinculada
        var includesFiles: Bool
    }

    private struct SectionData: Codable {
        var title: String
        var order: Int
        var entries: [EntryData]
    }

    private struct EntryData: Codable {
        var rawText: String
        var title: String
        var kind: String
        var year: Int
        var authors: String
        var venue: String
        var doi: String
        var isbn: String
        var portaria: String
        var issn: String
        var edital: String
        var endYear: Int
        var order: Int
        var certificateStatus: EntryStatus
        var certificates: [CertificateData]
    }

    private struct CertificateData: Codable {
        var originalFilePath: String
        var bundledRelativePath: String?   // presente só quando includesFiles == true
        var extractedText: String
        var confidence: Double
        var isConfirmed: Bool
        var isRejected: Bool
        var importDate: Date
        var order: Int
    }

    // MARK: - Exportação

    /// Monta o pacote num arquivo .zip temporário e retorna sua URL — o chamador
    /// decide onde salvá-lo (ex.: via NSSavePanel) e deve removê-lo depois.
    static func export(profile: LattesProfile, includeFiles: Bool) throws -> URL {
        let fm = FileManager.default
        let workDir = fm.temporaryDirectory.appendingPathComponent("LattesExport_\(UUID().uuidString)")
        let filesDir = workDir.appendingPathComponent("files")
        try fm.createDirectory(at: filesDir, withIntermediateDirectories: true)

        func archiveCert(_ cert: Certificate) -> CertificateData {
            var bundledRel: String? = nil
            if includeFiles, cert.exists {
                let destName = "\(cert.id.uuidString)_\(cert.fileName)"
                let dest = filesDir.appendingPathComponent(destName)
                if (try? fm.copyItem(at: cert.fileURL, to: dest)) != nil {
                    bundledRel = "files/\(destName)"
                }
            }
            return CertificateData(
                originalFilePath: cert.filePath, bundledRelativePath: bundledRel,
                extractedText: cert.extractedText, confidence: cert.confidence,
                isConfirmed: cert.isConfirmed, isRejected: cert.isRejected,
                importDate: cert.importDate, order: cert.order)
        }

        let sections = profile.sortedSections.map { section in
            SectionData(title: section.title, order: section.order, entries: section.sortedEntries.map { entry in
                EntryData(
                    rawText: entry.rawText, title: entry.title, kind: entry.kind, year: entry.year,
                    authors: entry.authors, venue: entry.venue, doi: entry.doi, isbn: entry.isbn,
                    portaria: entry.portaria, issn: entry.issn, edital: entry.edital, endYear: entry.endYear,
                    order: entry.order, certificateStatus: entry.certificateStatus,
                    certificates: entry.sortedCertificates.map(archiveCert))
            })
        }
        let limbo = profile.limboCertificates.map(archiveCert)

        let archive = ProfileArchiveData(
            name: profile.name, pdfPath: profile.pdfPath, importDate: profile.importDate,
            lastUpdated: profile.lastUpdated, savePath: profile.savePath, rawText: profile.rawText,
            rejectedLinks: profile.rejectedLinks, sections: sections, limboCertificates: limbo,
            includesFiles: includeFiles)

        let encoder = JSONEncoder()
        encoder.dateEncodingStrategy = .iso8601
        encoder.outputFormatting = [.prettyPrinted, .sortedKeys]
        try encoder.encode(archive).write(to: workDir.appendingPathComponent("manifest.json"))

        // O PDF original do Lattes é sempre incluído (pequeno e essencial — sem ele
        // o relatório final não consegue embutir o "Currículo Lattes completo").
        if fm.fileExists(atPath: profile.pdfPath) {
            try? fm.copyItem(at: URL(fileURLWithPath: profile.pdfPath),
                             to: workDir.appendingPathComponent("curriculo.pdf"))
        }

        let safeName = profile.name.isEmpty ? "Curriculo" : profile.name
            .replacingOccurrences(of: "/", with: "-")
        let zipDest = fm.temporaryDirectory
            .appendingPathComponent("\(safeName)_export_\(UUID().uuidString).zip")
        try zip(folder: workDir, to: zipDest)
        try? fm.removeItem(at: workDir)
        return zipDest
    }

    // MARK: - Importação

    @discardableResult
    static func importProfile(from zipURL: URL, modelContext: ModelContext) throws -> LattesProfile {
        let fm = FileManager.default
        let workDir = fm.temporaryDirectory.appendingPathComponent("LattesImport_\(UUID().uuidString)")
        try fm.createDirectory(at: workDir, withIntermediateDirectories: true)
        defer { try? fm.removeItem(at: workDir) }
        try unzip(zipURL, to: workDir)

        guard let manifestURL = findManifest(in: workDir) else { throw ArchiveError.manifestNotFound }
        let root = manifestURL.deletingLastPathComponent()
        let decoder = JSONDecoder()
        decoder.dateDecodingStrategy = .iso8601
        let archive = try decoder.decode(ProfileArchiveData.self, from: Data(contentsOf: manifestURL))

        let name = uniqueName(archive.name, modelContext: modelContext)
        let destBase = defaultSavePath(for: name)
        try? fm.createDirectory(at: destBase, withIntermediateDirectories: true)

        var pdfPath = archive.pdfPath
        let bundledPDF = root.appendingPathComponent("curriculo.pdf")
        if fm.fileExists(atPath: bundledPDF.path) {
            let dest = destBase.appendingPathComponent("Currículo Lattes.pdf")
            try? fm.removeItem(at: dest)
            try? fm.copyItem(at: bundledPDF, to: dest)
            pdfPath = dest.path
        }

        let profile = LattesProfile(name: name, pdfPath: pdfPath, savePath: destBase.path)
        profile.importDate = archive.importDate
        profile.lastUpdated = archive.lastUpdated
        profile.rawText = archive.rawText
        profile.rejectedLinks = archive.rejectedLinks
        modelContext.insert(profile)

        let certsDestDir = destBase.appendingPathComponent("Comprovantes Importados")
        var certsDirCreated = false
        func materialize(_ c: CertificateData) -> Certificate {
            var finalPath = c.originalFilePath
            if let rel = c.bundledRelativePath {
                let src = root.appendingPathComponent(rel)
                if fm.fileExists(atPath: src.path) {
                    if !certsDirCreated {
                        try? fm.createDirectory(at: certsDestDir, withIntermediateDirectories: true)
                        certsDirCreated = true
                    }
                    let dest = certsDestDir.appendingPathComponent(src.lastPathComponent)
                    try? fm.removeItem(at: dest)
                    if (try? fm.copyItem(at: src, to: dest)) != nil {
                        finalPath = dest.path
                    }
                }
            }
            let cert = Certificate(filePath: finalPath)
            cert.extractedText = c.extractedText
            cert.confidence = c.confidence
            cert.isConfirmed = c.isConfirmed
            cert.isRejected = c.isRejected
            cert.importDate = c.importDate
            cert.order = c.order
            cert.profile = profile
            modelContext.insert(cert)
            return cert
        }

        for s in archive.sections {
            let section = LattesSection(title: s.title, order: s.order)
            section.profile = profile
            modelContext.insert(section)
            for e in s.entries {
                let entry = LattesEntry(
                    rawText: e.rawText, title: e.title, kind: e.kind, year: e.year,
                    authors: e.authors, venue: e.venue, order: e.order)
                entry.doi = e.doi; entry.isbn = e.isbn; entry.portaria = e.portaria
                entry.issn = e.issn; entry.edital = e.edital; entry.endYear = e.endYear
                entry.certificateStatus = e.certificateStatus
                entry.section = section
                modelContext.insert(entry)
                for c in e.certificates {
                    materialize(c).entry = entry
                }
            }
        }
        for c in archive.limboCertificates {
            _ = materialize(c)   // sem entrada -> fica em limbo, igual ao original
        }

        try modelContext.save()
        return profile
    }

    // MARK: - Helpers

    private static func defaultSavePath(for name: String) -> URL {
        let base = FileManager.default.urls(for: .documentDirectory, in: .userDomainMask).first?
            .appendingPathComponent("ComprovantesLattes")
            ?? URL(fileURLWithPath: NSString(string: "~/Documents/ComprovantesLattes").expandingTildeInPath)
        return base.appendingPathComponent(name)
    }

    /// Evita colidir com um perfil já existente com o mesmo nome (ex.: reimportar
    /// um backup do mesmo currículo).
    private static func uniqueName(_ base: String, modelContext: ModelContext) -> String {
        let existing = (try? modelContext.fetch(FetchDescriptor<LattesProfile>()))?
            .map(\.name) ?? []
        guard existing.contains(base) else { return base }
        var n = 2
        while existing.contains("\(base) (\(n))") { n += 1 }
        return "\(base) (\(n))"
    }

    private static func findManifest(in root: URL) -> URL? {
        let fm = FileManager.default
        let direct = root.appendingPathComponent("manifest.json")
        if fm.fileExists(atPath: direct.path) { return direct }
        // ditto costuma preservar a pasta de origem como raiz do zip — procura 1 nível abaixo.
        guard let items = try? fm.contentsOfDirectory(at: root, includingPropertiesForKeys: nil) else { return nil }
        for item in items {
            let candidate = item.appendingPathComponent("manifest.json")
            if fm.fileExists(atPath: candidate.path) { return candidate }
        }
        return nil
    }

    private static func zip(folder: URL, to destination: URL) throws {
        let proc = Process()
        proc.executableURL = URL(fileURLWithPath: "/usr/bin/ditto")
        proc.arguments = ["-c", "-k", "--sequesterRsrc", folder.path, destination.path]
        try proc.run()
        proc.waitUntilExit()
        guard proc.terminationStatus == 0 else { throw ArchiveError.zipFailed }
    }

    private static func unzip(_ zipURL: URL, to destination: URL) throws {
        let proc = Process()
        proc.executableURL = URL(fileURLWithPath: "/usr/bin/ditto")
        proc.arguments = ["-x", "-k", zipURL.path, destination.path]
        try proc.run()
        proc.waitUntilExit()
        guard proc.terminationStatus == 0 else { throw ArchiveError.unzipFailed }
    }
}
```

---

*Fim do documento. Gerado em 2026-07-12 a partir do código-fonte real do app "Comprovação Fácil do
Lattes" (macOS). Qualquer dúvida sobre uma decisão específica do parser que não esteja clara na
prosa acima, o comentário no código-fonte correspondente (marcado inline nos apêndices) costuma
explicar o PORQUÊ, não só o COMO — vale ler antes de alterar o comportamento.*
