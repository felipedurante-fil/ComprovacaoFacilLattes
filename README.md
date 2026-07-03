# Comprovacao Facil do Lattes

Aplicativo para macOS que facilita a organizacao e comprovacao de curriculos Lattes para concursos e progressao no magisterio publico.

Importa o PDF do curriculo Lattes, extrai todas as secoes automaticamente, vincula comprovantes (certificados, declaracoes, publicacoes) a cada entrada e gera um relatorio PDF consolidado pronto para submissao.

## Funcionalidades

- **Importacao do Lattes**: Analisa o PDF exportado da Plataforma Lattes e extrai todas as secoes (formacao, atuacao, producoes, eventos, etc.)
- **Vinculacao de comprovantes**: Escaneia pastas de certificados e sugere vinculos automaticamente usando algoritmos de similaridade textual
- **Classificacao Qualis/CAPES**: Consulta integrada as tabelas Qualis dos quadrienios 2016-2019, 2017-2020 e 2021-2024
- **Visualizacao de PDFs**: Preview integrado dos comprovantes vinculados
- **Geracao de relatorio**: Exporta PDF final com todas as entradas e comprovantes organizados por secao e periodo
- **Multiplos perfis**: Gerencie varios curriculos simultaneamente com SwiftData

## Requisitos

- macOS 14 (Sonoma) ou superior
- Xcode 15 ou superior

## Como abrir

1. Abra o **Xcode**
2. Va em **File > Open...**
3. Navegue ate esta pasta e selecione o arquivo **`Package.swift`**
4. Xcode reconhece automaticamente como projeto Swift Package
5. Selecione **My Mac** como destino e pressione **Cmd+R**

## Fluxo de uso

1. **+** na sidebar para criar um novo perfil e selecionar o PDF do Lattes
2. O app analisa e exibe todas as secoes em acordeao
3. **Adicionar Pasta de Certificados** para escanear e sugerir vinculos automaticamente
4. Revise as sugestoes ou arraste arquivos manualmente sobre cada entrada
5. **Gerar Comprovantes** para configurar periodo, secoes e exportar o PDF final

## Estrutura

```
Sources/ComprovacaoFacilLattes/
├── Models/          — SwiftData: LattesProfile, LattesSection, LattesEntry, Certificate
├── Views/           — Interface de 3 colunas (sidebar + conteudo + preview)
├── Services/        — Parser do PDF Lattes, indexador de certificados, gerador de relatorio
├── QualisData/      — Tabelas Qualis/CAPES comprimidas (TSV.GZ)
└── Utils/           — Algoritmos de similaridade de texto
```

## Tecnologias

- Swift / SwiftUI
- SwiftData (persistencia)
- PDFKit (leitura e geracao de PDFs)
- Swift Package Manager
- Vision (OCR para indexacao de certificados)

## Licenca

Uso pessoal. Desenvolvido por Felipe Durante.
