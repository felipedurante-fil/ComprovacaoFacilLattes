# Comprovação Fácil do Lattes — versão Windows

Port da versão macOS (Swift/SwiftUI/SwiftData) para .NET 10 + Avalonia UI, gerado a partir do
[`WINDOWS_PORT_LOGIC.md`](WINDOWS_PORT_LOGIC.md). Ver esse arquivo para a especificação completa
(modelo de dados, algoritmos, armadilhas conhecidas).

## Como abrir/rodar em desenvolvimento

Requer o [.NET SDK 10](https://dotnet.microsoft.com/download) instalado.

```bash
dotnet run --project src/ComprovacaoFacilLattes.App
```

Isso roda o app nativamente na plataforma atual (Windows, macOS ou Linux — Avalonia é
cross-platform). Para rodar os testes:

```bash
dotnet test
```

## Como gerar o executável Windows

A partir de qualquer plataforma com o .NET SDK (inclusive macOS/Linux, via cross-publish):

```bash
dotnet publish src/ComprovacaoFacilLattes.App -c Release -r win-x64 --self-contained true
```

O resultado fica em `src/ComprovacaoFacilLattes.App/bin/Release/net10.0/win-x64/publish/` — uma
pasta autocontida (~260MB: inclui o runtime do .NET, os binários nativos do Tesseract/OCR e do
PDFium/rasterização de PDF, e os dados de idioma pt/en do OCR). **O usuário final não precisa
instalar nada** (nem .NET, nem Tesseract) — é só copiar a pasta inteira e rodar
`ComprovacaoFacilLattes.App.exe`.

**Importante**: este executável nunca foi rodado numa máquina Windows de verdade (esta sessão
rodou inteiramente em macOS). O build, os 49 testes automatizados e a UI rodando nativamente como
app Mac foram todos verificados aqui — mas a validação final do `.exe` em si precisa ser feita
numa máquina Windows real. Se algo não funcionar, o candidato mais provável é algum caminho de
arquivo específico do Windows (`\` vs `/`) que passou despercebido, ou uma diferença de
comportamento do PDFium/Tesseract nos binários win-x64 vs os usados aqui no Mac para
desenvolvimento.

## Estrutura

```
src/
  ComprovacaoFacilLattes.Core/            lógica pura, sem UI (modelos, parser, matching, Qualis,
                                           planejamento de relatório, backup) — testável sem
                                           nenhuma dependência de plataforma
  ComprovacaoFacilLattes.Infrastructure/  integrações não-portáveis: leitura/OCR de PDF (PdfPig +
                                           PDFtoImage + Tesseract), desenho do relatório (PdfSharp)
  ComprovacaoFacilLattes.App/             Avalonia UI (MVVM) + camada de serviços que liga tudo
                                           ao banco SQLite
  ComprovacaoFacilLattes.Tests/           xUnit — 49 testes, incluindo verificação ponta-a-ponta
                                           contra o parser Swift real (compilado à parte) e OCR
                                           real (Tesseract lendo uma imagem sintética)
```

## O que ainda falta / pontos em aberto

- Combinação de vínculo múltiplo ("combo": um arquivo comprova mais de uma entrada de uma vez) —
  o app original suporta; o port ainda não tem UI para isso (o *matching* já calcula os
  candidatos combo, só falta a tela).
- Drag & drop de arquivos direto numa linha de entrada (o port usa botões "Escolher arquivo…").
- Reordenar comprovantes é por setas (↑/↓) em vez de arrastar — mais simples de implementar em
  Avalonia e o próprio documento de especificação já cita "arrastar/setas" como equivalentes.
- Ícone do app e instalador (`.msi`/`.exe` de instalação) não foram gerados — a pasta publicada é
  "portátil" (roda direto, sem instalar).
