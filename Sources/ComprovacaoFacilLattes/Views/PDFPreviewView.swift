import SwiftUI
import PDFKit
import AppKit

struct PDFPreviewView: View {
    let entry: LattesEntry
    @State private var selectedCert: Certificate?

    var body: some View {
        VStack(spacing: 0) {
            // Cabeçalho com info da entrada
            entryHeader

            Divider()

            if entry.certificates.isEmpty {
                emptyState
            } else {
                HStack(spacing: 0) {
                    // Lista lateral de comprovantes (na ordem definida)
                    if entry.certificates.count > 1 {
                        certList
                        Divider()
                    }

                    // Preview principal — recriado quando a seleção muda
                    if let cert = selectedCert ?? entry.sortedCertificates.first {
                        FilePreview(cert: cert)
                            .id(cert.id)
                    }
                }
            }
        }
        .onAppear {
            selectedCert = entry.confirmedCertificates.first ?? entry.sortedCertificates.first
        }
        .onChange(of: entry.id) {
            selectedCert = entry.confirmedCertificates.first ?? entry.sortedCertificates.first
        }
    }

    private var entryHeader: some View {
        VStack(alignment: .leading, spacing: 4) {
            Text(entry.displayTitle.isEmpty ? "Entrada sem título" : entry.displayTitle)
                .font(.headline)
                .lineLimit(3)
            if !entry.authors.isEmpty {
                Text(entry.authors)
                    .font(.caption)
                    .foregroundStyle(.secondary)
                    .lineLimit(1)
            }
        }
        .frame(maxWidth: .infinity, alignment: .leading)
        .padding(12)
        .background(Color(nsColor: .windowBackgroundColor))
    }

    private var certList: some View {
        ScrollView {
            VStack(spacing: 0) {
                ForEach(Array(entry.sortedCertificates.enumerated()), id: \.element.id) { idx, cert in
                    Button {
                        selectedCert = cert
                    } label: {
                        HStack(spacing: 6) {
                            Text("\(idx + 1).")
                                .font(.caption2).monospacedDigit().foregroundStyle(.secondary)
                            Image(systemName: cert.isPDF ? "doc.fill" : "photo.fill")
                                .foregroundStyle(cert.isPDF ? .red : .blue)
                                .font(.system(size: 13))
                            Text(cert.fileName)
                                .font(.system(size: 12))
                                .lineLimit(2)
                                .foregroundStyle(.primary)
                            Spacer()
                            if cert.isConfirmed {
                                Image(systemName: "checkmark.circle.fill")
                                    .foregroundStyle(.green).font(.caption)
                            }
                        }
                        .padding(.vertical, 7).padding(.horizontal, 8)
                        .frame(maxWidth: .infinity, alignment: .leading)
                        .background(
                            (selectedCert?.id == cert.id)
                                ? Color.accentColor.opacity(0.20) : Color.clear
                        )
                        .contentShape(Rectangle())
                    }
                    .buttonStyle(.plain)
                    Divider()
                }
            }
        }
        .frame(width: 200)
        .background(Color(nsColor: .controlBackgroundColor))
    }

    private var emptyState: some View {
        VStack(spacing: 12) {
            Image(systemName: "paperclip.badge.ellipsis")
                .font(.system(size: 40))
                .foregroundStyle(.secondary)
            Text("Nenhum comprovante vinculado")
                .foregroundStyle(.secondary)
            Text("Arraste um arquivo para esta entrada ou use o menu \u{22EF}")
                .font(.caption)
                .foregroundStyle(.secondary)
                .multilineTextAlignment(.center)
                .padding(.horizontal, 20)
        }
        .frame(maxWidth: .infinity, maxHeight: .infinity)
    }
}

// MARK: - FilePreview: PDF ou Imagem

struct FilePreview: View {
    let cert: Certificate

    var body: some View {
        if !cert.exists {
            VStack(spacing: 12) {
                Image(systemName: "exclamationmark.triangle")
                    .font(.system(size: 36))
                    .foregroundStyle(.orange)
                Text("Arquivo não encontrado")
                    .foregroundStyle(.secondary)
                Text(cert.filePath)
                    .font(.caption2)
                    .foregroundStyle(.secondary)
                    .lineLimit(3)
                    .multilineTextAlignment(.center)
                    .padding(.horizontal)
            }
            .frame(maxWidth: .infinity, maxHeight: .infinity)
        } else if cert.isPDF {
            PDFKitView(url: cert.fileURL)
        } else if cert.isImage {
            ImagePreviewView(url: cert.fileURL)
        } else {
            VStack(spacing: 12) {
                Image(systemName: "doc")
                    .font(.system(size: 36))
                    .foregroundStyle(.secondary)
                Text(cert.fileName)
                Button("Abrir no Finder") {
                    NSWorkspace.shared.open(cert.fileURL)
                }
                .buttonStyle(.bordered)
            }
            .frame(maxWidth: .infinity, maxHeight: .infinity)
        }
    }
}

// MARK: - NSViewRepresentable wrappers

struct PDFKitView: NSViewRepresentable {
    let url: URL

    func makeNSView(context: Context) -> PDFView {
        let view = PDFView()
        view.autoScales = true
        view.displayMode = .singlePageContinuous
        view.displaysPageBreaks = true
        view.document = PDFDocument(url: url)
        return view
    }

    func updateNSView(_ nsView: PDFView, context: Context) {
        if nsView.document?.documentURL != url {
            nsView.document = PDFDocument(url: url)
        }
    }
}

struct ImagePreviewView: NSViewRepresentable {
    let url: URL

    func makeNSView(context: Context) -> NSScrollView {
        let scrollView = NSScrollView()
        let imageView = NSImageView()
        imageView.image = NSImage(contentsOf: url)
        imageView.imageScaling = .scaleProportionallyUpOrDown
        scrollView.documentView = imageView
        scrollView.hasVerticalScroller = true
        scrollView.hasHorizontalScroller = true
        return scrollView
    }

    func updateNSView(_ nsView: NSScrollView, context: Context) {
        if let imageView = nsView.documentView as? NSImageView {
            imageView.image = NSImage(contentsOf: url)
        }
    }
}
