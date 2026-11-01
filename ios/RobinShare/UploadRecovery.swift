import Foundation

/// The server keeps incomplete transfers for a limited time. A checkpoint
/// allows the next foreground session to resume at the authoritative offset.
struct UploadCheckpoint: Codable, Equatable {
    let base: String
    let file: String
    let filename: String
    let target: String?
    let size: Int64
    let id: String
    let token: String
    let created: Date
}

actor UploadRecovery {
    static let shared = UploadRecovery()
    private var entries: [UploadCheckpoint] = Vault.read("upload-checkpoints") ?? []

    func pending(base: String, file: URL, target: String?, size: Int64) -> UploadCheckpoint? {
        entries.first { $0.base == base && $0.file == file.path && $0.target == target && $0.size == size }
    }
    func record(_ checkpoint: UploadCheckpoint) {
        entries.removeAll { $0.base == checkpoint.base && $0.file == checkpoint.file && $0.target == checkpoint.target }
        entries.append(checkpoint)
        Vault.save(entries, "upload-checkpoints")
    }
    func finish(_ checkpoint: UploadCheckpoint) {
        entries.removeAll { $0.id == checkpoint.id && $0.base == checkpoint.base }
        Vault.save(entries, "upload-checkpoints")
    }
    func pendingCount() -> Int { entries.count }
}
