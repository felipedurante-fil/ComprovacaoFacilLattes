# Comprovação Fácil do Lattes — Como abrir no Xcode

## Pré-requisito
- macOS 14 (Sonoma) ou superior
- Xcode 15 ou superior

## Abrir o projeto

1. Abra o **Xcode**
2. Vá em **File → Open…**
3. Navegue até esta pasta (`ComprovacaoFacilLattes/`)
4. Selecione o arquivo **`Package.swift`** e clique em **Open**
5. Xcode reconhece automaticamente como projeto Swift Package

## Rodar o app

1. No seletor de schema (barra superior), selecione **`ComprovacaoFacilLattes`**
2. Selecione **My Mac** como destino
3. Clique em ▶ ou pressione **⌘R**

## Fluxo de uso

1. **+** na sidebar → selecione o PDF exportado do Lattes
2. O app analisa e exibe todas as seções em acordeão
3. **Adicionar Pasta de Certificados** → escaneia e sugere vínculos automaticamente
4. Revise as sugestões (✔ / ✖) ou arraste arquivos manualmente sobre cada entrada
5. **Gerar Comprovantes** → configura período e seções → salva PDF final

## Estrutura de arquivos

```
Sources/ComprovacaoFacilLattes/
├── Models/          — SwiftData: LattesProfile, LattesSection, LattesEntry, Certificate
├── Views/           — Interface (3 colunas + sheets)
├── Services/        — Parser do PDF, indexador OCR, gerador de relatório
└── Utils/           — Algoritmos de similaridade de texto
```

## Banco de dados

SwiftData armazena os dados em:
`~/Library/Application Support/ComprovacaoFacilLattes/`

Os certificados ficam no caminho configurado na criação do perfil
(padrão: `~/Documents/ComprovantesLattes/<Nome>/`)
