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
