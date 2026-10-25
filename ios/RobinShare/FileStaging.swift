import Foundation

/// Coordinates provider/iCloud reads away from the UI thread. The source is never moved.
enum FileStaging {
    static func stage(_ source: URL, in directory: URL) async throws -> URL {
        try await withCheckedThrowingContinuation { continuation in
            DispatchQueue.global(qos: .userInitiated).async {
                do { continuation.resume(returning: try copyCoordinated(source, in: directory)) }
                catch { continuation.resume(throwing: error) }
            }
        }
    }
    private static func copyCoordinated(_ source: URL, in directory: URL) throws -> URL {
        precondition(!Thread.isMainThread, "File-provider reads must not block the document picker.")
        let access = source.startAccessingSecurityScopedResource()
        defer { if access { source.stopAccessingSecurityScopedResource() } }
        let manager = FileManager.default
        try manager.createDirectory(at: directory, withIntermediateDirectories: true)
        let destination = directory.appendingPathComponent(UUID().uuidString + "-" + Safety.safeName(source.lastPathComponent))
        var coordinationError: NSError?
        var operationError: Error?
        var copied = false
        NSFileCoordinator().coordinate(readingItemAt: source, options: .withoutChanges, error: &coordinationError) { readableURL in
            do {
                let values = try readableURL.resourceValues(forKeys: [.isRegularFileKey])
                guard values.isRegularFile == true else { throw ShareError.message("Bitte eine Datei auswählen. Ordner werden hier noch nicht unterstützt.") }
                try manager.copyItem(at: readableURL, to: destination)
                copied = true
            } catch { operationError = error }
        }
        if let error = operationError ?? coordinationError {
            try? manager.removeItem(at: destination)
            throw error
        }
        guard copied else { throw ShareError.message("Die ausgewählte Datei konnte nicht übernommen werden. Bitte ihre Verfügbarkeit in der Dateien-App prüfen.") }
        return destination
    }
}
