import Foundation
import SwiftUI
import UIKit

/// Uses Apple's native share sheet. AirDrop interoperability with supported
/// Android Quick Share devices is provided by the OS, not by a private API.
struct SystemShareSheet: UIViewControllerRepresentable {
    let files: [URL]

    func makeUIViewController(context: Context) -> UIActivityViewController {
        let controller = UIActivityViewController(activityItems: files, applicationActivities: nil)
        // Use a real attached view as the iPad popover anchor.
        controller.popoverPresentationController?.sourceView = controller.view
        controller.popoverPresentationController?.sourceRect = CGRect(x: controller.view.bounds.midX,
            y: controller.view.bounds.midY, width: 1, height: 1)
        return controller
    }
    func updateUIViewController(_ controller: UIActivityViewController, context: Context) {}
}

/// Our imported files are named "<UUID>-<original>". We create same-volume
/// hard links with clean names so recipients receive the original filenames.
/// Links avoid copying gigabytes of data and do not alter the originals.
enum NativeShareFiles {
    static func prepare(_ files: [URL]) async throws -> [URL] {
        try await withCheckedThrowingContinuation { continuation in
            DispatchQueue.global(qos: .userInitiated).async {
                do {
                    let base = FileManager.default.urls(for: .cachesDirectory, in: .userDomainMask)[0]
                        .appendingPathComponent("NativeShare", isDirectory: true)
                    try FileManager.default.createDirectory(at: base, withIntermediateDirectories: true)
                    var result: [URL] = []
                    for source in files {
                        let filename = source.lastPathComponent
                        let original = filename.count > 37 && filename[filename.index(filename.startIndex, offsetBy: 36)] == "-"
                            ? String(filename.dropFirst(37)) : filename
                        let targetDirectory = base.appendingPathComponent(UUID().uuidString, isDirectory: true)
                        try FileManager.default.createDirectory(at: targetDirectory, withIntermediateDirectories: true)
                        let target = targetDirectory.appendingPathComponent(Safety.safeName(original))
                        do { try FileManager.default.linkItem(at: source, to: target) }
                        catch { try FileManager.default.copyItem(at: source, to: target) }
                        result.append(target)
                    }
                    continuation.resume(returning: result)
                } catch { continuation.resume(throwing: error) }
            }
        }
    }

    static func clearOldCopies() {
        let folder = FileManager.default.urls(for: .cachesDirectory, in: .userDomainMask)[0]
            .appendingPathComponent("NativeShare", isDirectory: true)
        guard let folders = try? FileManager.default.contentsOfDirectory(at: folder,
                includingPropertiesForKeys: [.contentModificationDateKey]) else { return }
        for entry in folders {
            guard let date = try? entry.resourceValues(forKeys: [.contentModificationDateKey]).contentModificationDate,
                  date < Date().addingTimeInterval(-86400) else { continue }
            try? FileManager.default.removeItem(at: entry)
        }
    }
}
