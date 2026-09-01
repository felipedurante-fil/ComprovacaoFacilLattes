import SwiftUI
import SwiftData
import AppKit

@main
struct ComprovacaoFacilLattesApp: App {
    @NSApplicationDelegateAdaptor(AppDelegate.self) private var appDelegate

    var body: some Scene {
        WindowGroup {
            ContentView()
                .frame(minWidth: 1100, minHeight: 680)
        }
        .modelContainer(Self.sharedModelContainer)
        .commands {
            CommandGroup(replacing: .newItem) { }
        }
    }

    /// Banco de dados PRÓPRIO e isolado do app. `.modelContainer(for:)` sem
    /// configuração explícita usa por padrão o arquivo genérico
    /// "~/Library/Application Support/default.store" — COMPARTILHADO por
    /// qualquer outro processo não-sandboxed nesta máquina que também não
    /// configure um local próprio (ex.: outro app SwiftData, um protótipo em
    /// teste). Foi exatamente isso que apagou os dados do usuário em
    /// 2026-09-01: outro processo recriou esse arquivo genérico do zero,
    /// substituindo o currículo salvo. Um caminho próprio e nomeado elimina
    /// essa colisão de vez.
    static let sharedModelContainer: ModelContainer = {
        let schema = Schema([
            LattesProfile.self,
            LattesSection.self,
            LattesEntry.self,
            Certificate.self,
        ])
        let appSupport = FileManager.default
            .urls(for: .applicationSupportDirectory, in: .userDomainMask)[0]
            .appendingPathComponent("ComprovacaoFacilLattes", isDirectory: true)
        try? FileManager.default.createDirectory(at: appSupport, withIntermediateDirectories: true)
        let storeURL = appSupport.appendingPathComponent("ComprovacaoFacilLattes.store")
        let config = ModelConfiguration(schema: schema, url: storeURL)
        do {
            return try ModelContainer(for: schema, configurations: [config])
        } catch {
            fatalError("Não foi possível abrir o banco de dados do app: \(error)")
        }
    }()
}

final class AppDelegate: NSObject, NSApplicationDelegate {
    func applicationWillFinishLaunching(_ notification: Notification) {
        // Apps SPM rodam sem .app bundle adequado e ficam em policy `.accessory`,
        // o que bloqueia eventos de teclado em janelas secundárias.
        NSApp.setActivationPolicy(.regular)

        // Sem .app bundle, o ícone do Dock não vem do Info.plist — definimos manualmente.
        if let url = Bundle.module.url(forResource: "AppIcon", withExtension: "png"),
           let image = NSImage(contentsOf: url) {
            NSApp.applicationIconImage = image
        }
    }

    func applicationDidFinishLaunching(_ notification: Notification) {
        NSApp.activate(ignoringOtherApps: true)
        // Carrega as tabelas Qualis (CAPES) em segundo plano
        QualisService.shared.start()
        // Abre quase maximizado, respeitando menu bar e dock
        DispatchQueue.main.async {
            guard let window = NSApp.windows.first else { return }
            let screen = window.screen ?? NSScreen.main
            if let frame = screen?.visibleFrame {
                window.setFrame(frame, display: true, animate: false)
            }
        }
    }

    func applicationShouldTerminateAfterLastWindowClosed(_ sender: NSApplication) -> Bool {
        true
    }
}
