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
        .modelContainer(for: [
            LattesProfile.self,
            LattesSection.self,
            LattesEntry.self,
            Certificate.self
        ])
        .commands {
            CommandGroup(replacing: .newItem) { }
        }
    }
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
