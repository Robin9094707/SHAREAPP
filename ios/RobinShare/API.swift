import Foundation
import CryptoKit
import Security

final class TrustDelegate: NSObject, URLSessionDelegate, URLSessionTaskDelegate {
    let pin: String
    init(pin: String = "") { self.pin = pin.lowercased().replacingOccurrences(of: ":", with: "") }
    static func fingerprint(_ certificate: SecCertificate) -> String {
        SHA256.hash(data: SecCertificateCopyData(certificate) as Data).map { String(format: "%02x", $0) }.joined()
    }
    func urlSession(_ session: URLSession, didReceive challenge: URLAuthenticationChallenge, completionHandler: @escaping (URLSession.AuthChallengeDisposition, URLCredential?) -> Void) {
        guard challenge.protectionSpace.authenticationMethod == NSURLAuthenticationMethodServerTrust,
              let trust = challenge.protectionSpace.serverTrust else { completionHandler(.performDefaultHandling, nil); return }
        if pin.isEmpty { completionHandler(.performDefaultHandling, nil); return }
        guard let chain = SecTrustCopyCertificateChain(trust) as? [SecCertificate], let certificate = chain.first,
              Self.fingerprint(certificate) == pin else { completionHandler(.cancelAuthenticationChallenge, nil); return }
        completionHandler(.useCredential, URLCredential(trust: trust))
    }
    func urlSession(_ session: URLSession, task: URLSessionTask, willPerformHTTPRedirection response: HTTPURLResponse, newRequest request: URLRequest, completionHandler: @escaping (URLRequest?) -> Void) {
        guard let a = task.originalRequest?.url, let b = request.url, a.host == b.host, a.scheme == b.scheme, a.port == b.port else { completionHandler(nil); return }
        completionHandler(request)
    }
}
final class API {
    let base: String; let token: String; let pin: String
    private let trust: TrustDelegate
    let session: URLSession
    init(base: String, token: String = "", pin: String = "") {
        self.base = base; self.token = token; self.pin = pin
        trust = TrustDelegate(pin: pin)
        let c = URLSessionConfiguration.default
        c.timeoutIntervalForRequest = 120
        c.timeoutIntervalForResource = 24 * 3600
        c.waitsForConnectivity = true
        session = URLSession(configuration: c, delegate: trust, delegateQueue: nil)
    }
    func call<T: Decodable>(_ path: String, method: String = "GET", body: [String: Any]? = nil, headers: [String: String] = [:]) async throws -> T {
        var request = URLRequest(url: try Safety.endpoint(base: base, path: path))
        request.httpMethod = method
        if !token.isEmpty { request.setValue("Bearer \(token)", forHTTPHeaderField: "Authorization") }
        if let body { request.httpBody = try JSONSerialization.data(withJSONObject: body); request.setValue("application/json", forHTTPHeaderField: "Content-Type") }
        headers.forEach { request.setValue($1, forHTTPHeaderField: $0) }
        let (data, response) = try await session.data(for: request)
        try check(data, response)
        return try JSONDecoder().decode(T.self, from: data)
    }
    func check(_ data: Data, _ response: URLResponse) throws {
        guard let response = response as? HTTPURLResponse, (200..<300).contains(response.statusCode) else {
            let json = (try? JSONSerialization.jsonObject(with: data)) as? [String: Any]
            throw ShareError.message(json?["error"] as? String ?? "Der Server ist nicht erreichbar oder hat die Anfrage abgelehnt.")
        }
    }
    func upload(_ url: URL, filename: String, target: String?, progress: @escaping @MainActor (Double) -> Void) async throws -> ShareResult {
        let size = Int64((try url.resourceValues(forKeys: [.fileSizeKey])).fileSize ?? 0)
        let config: ServerConfig = try await call("/api/config")
        guard size <= config.max_bytes else { throw ShareError.message("Die Datei überschreitet das Serverlimit.") }

        let upload: Upload
        let checkpoint: UploadCheckpoint
        var offset: Int64
        if let entry = await UploadRecovery.shared.pending(base: base, file: url, target: target, size: size),
           let state: Offset = try? await call("/api/transfers/\(entry.id)", headers: ["Upload-Token": entry.token]),
           state.offset >= 0 && state.offset <= size {
            checkpoint = entry
            upload = Upload(id: entry.id, upload_token: entry.token, offset: state.offset, chunk_bytes: nil)
            offset = state.offset
        } else {
            var body: [String: Any] = ["filename": filename, "size": size, "mime": "application/octet-stream"]
            if let target { body["target_id"] = target }
            upload = try await call("/api/transfers", method: "POST", body: body)
            checkpoint = UploadCheckpoint(base: base, file: url.path, filename: filename,
                target: target, size: size, id: upload.id, token: upload.upload_token, created: Date())
            await UploadRecovery.shared.record(checkpoint)
            offset = upload.offset
        }

        let headers = ["Upload-Token": upload.upload_token]
        await progress(size == 0 ? 1 : Double(offset) / Double(size))
        let handle = try FileHandle(forReadingFrom: url)
        defer { try? handle.close() }
        while offset < size {
            try Task.checkCancellation()
            var delivered = false
            for attempt in 0..<6 {
                try Task.checkCancellation()
                do {
                    try handle.seek(toOffset: UInt64(offset))
                    let remaining = Int(min(Int64(min(config.chunk_bytes, 8 * 1024 * 1024)), size - offset))
                    let data = try handle.read(upToCount: remaining) ?? Data()
                    guard data.count == remaining else { throw ShareError.message("Die Quelldatei wurde verändert.") }
                    var request = URLRequest(url: try Safety.endpoint(base: base, path: "/api/transfers/\(upload.id)/content"))
                    request.httpMethod = "PUT"
                    request.setValue(upload.upload_token, forHTTPHeaderField: "Upload-Token")
                    request.setValue(String(offset), forHTTPHeaderField: "Upload-Offset")
                    request.setValue("application/octet-stream", forHTTPHeaderField: "Content-Type")
                    if !token.isEmpty { request.setValue("Bearer \(token)", forHTTPHeaderField: "Authorization") }

                    // Only file-backed URLSession tasks survive iOS app suspension.
                    let response = try await BackgroundChunks.shared.upload(request, body: data, pin: pin)
                    let next = try JSONDecoder().decode(Offset.self, from: response).offset
                    guard next > offset && next <= size else { throw ShareError.message("Ungültige Uploadposition vom Server.") }
                    offset = next
                    await progress(Double(offset) / Double(max(size, 1)))
                    delivered = true
                    break
                } catch {
                    if Task.isCancelled { throw CancellationError() }
                    if attempt == 5 { throw error }
                    try await Task.sleep(for: .seconds(min(2 << attempt, 20)))
                    if let state: Offset = try? await call("/api/transfers/\(upload.id)", headers: headers),
                       state.offset >= offset && state.offset <= size {
                        offset = state.offset
                        await progress(Double(offset) / Double(max(size, 1)))
                        if offset == size { delivered = true; break }
                    }
                }
            }
            if !delivered { throw ShareError.message("Übertragung unterbrochen. Die bisherige Position ist gesichert.") }
        }
        try Task.checkCancellation()
        let result: ShareResult = try await call("/api/transfers/\(upload.id)/complete", method: "POST", body: [:], headers: headers)
        await UploadRecovery.shared.finish(checkpoint)
        await progress(1)
        return result
    }
}
struct Empty: Decodable { let ok: Bool }
