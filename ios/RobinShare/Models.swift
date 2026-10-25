import Foundation

struct Identity: Codable { let id: String; let token: String }
struct Device: Codable, Identifiable { let id: String; let name: String; let platform: String }
struct Transfer: Codable, Identifiable, Hashable {
    let id: String
    let filename: String
    let size: Int64
    let mime: String
    let sha256: String
    let expires_at: Int64
    let download_url: String
    let ack_url: String
}
struct DeviceState: Decodable { let pair_code: String; let devices: [Device]; let inbox: [Transfer] }
struct Upload: Decodable { let id: String; let upload_token: String; let offset: Int64; let chunk_bytes: Int? }
struct ShareResult: Decodable { let pin: String; let link: String; let transfer: Transfer }
struct Lookup: Decodable { let transfer: Transfer }
struct Offset: Decodable { let offset: Int64 }
struct ServerConfig: Decodable { let name: String; let chunk_bytes: Int; let max_bytes: Int64 }
struct LocalPeer: Codable, Identifiable, Hashable {
    var id: String { url }
    let name: String
    let url: String
    let fingerprint: String
    var token: String = ""
}
struct SavedFile: Identifiable {
    var id: String { url.path }
    let url: URL
    let size: Int64
}
struct DownloadContext: Codable { let transfer: Transfer; let base: String; let pin: String; let token: String }

enum ShareError: LocalizedError {
    case message(String)
    var errorDescription: String? { if case .message(let text) = self { return text }; return nil }
}
enum Safety {
    static func serverURL(_ text: String) throws -> URL {
        guard let url = URL(string: text.trimmingCharacters(in: .whitespacesAndNewlines)),
              url.scheme == "https", let host = url.host, !host.isEmpty,
              url.user == nil, url.password == nil, url.query == nil, url.fragment == nil,
              url.path == "" || url.path == "/" else {
            throw ShareError.message("Bitte eine vollständige HTTPS-Serveradresse ohne Pfad eingeben.")
        }
        return url
    }
    static func safeName(_ name: String) -> String {
        let leaf = name.replacingOccurrences(of: "\\", with: "/").components(separatedBy: "/").last ?? "Datei"
        let clean = leaf.unicodeScalars.filter { !CharacterSet.controlCharacters.contains($0) && $0 != ":" }
        let value = String(String.UnicodeScalarView(clean)).trimmingCharacters(in: CharacterSet(charactersIn: ". "))
        return value.isEmpty ? "Datei" : String(value.prefix(180))
    }
    static func endpoint(base: String, path: String) throws -> URL {
        let root = try serverURL(base)
        guard path.hasPrefix("/api/"), let resolved = URL(string: path, relativeTo: root)?.absoluteURL,
              resolved.host == root.host, resolved.port == root.port, resolved.scheme == root.scheme else {
            throw ShareError.message("Ungültige Downloadadresse.")
        }
        return resolved
    }
}
