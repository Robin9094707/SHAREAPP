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
        let size = (try url.resourceValues(forKeys: [.fileSizeKey])).fileSize ?? 0
        let config: ServerConfig = try await call("/api/config")
        guard Int64(size) <= config.max_bytes else { throw ShareError.message("Die Datei überschreitet das Serverlimit.") }
        var body: [String: Any] = ["filename": filename, "size": size, "mime": "application/octet-stream"]
        if let target { body["target_id"] = target }
        let upload: Upload = try await call("/api/transfers", method: "POST", body: body)
        let headers = ["Upload-Token": upload.upload_token]
        let handle = try FileHandle(forReadingFrom: url)
        defer { try? handle.close() }
        var offset: Int64 = 0
        do {
            while offset < size {
                try Task.checkCancellation()
                var success = false
                for attempt in 0..<4 {
                    do {
                        try handle.seek(toOffset: UInt64(offset))
                        let data = try handle.read(upToCount: min(config.chunk_bytes, 8*1024*1024)) ?? Data()
                        guard !data.isEmpty else { throw ShareError.message("Die Quelldatei wurde verändert.") }
                        var r = URLRequest(url: try Safety.endpoint(base: base, path: "/api/transfers/\(upload.id)/content"))
                        r.httpMethod = "PUT"
                        r.setValue(upload.upload_token, forHTTPHeaderField: "Upload-Token")
                        r.setValue(String(offset), forHTTPHeaderField: "Upload-Offset")
                        r.setValue("application/octet-stream", forHTTPHeaderField: "Content-Type")
                        if !token.isEmpty { r.setValue("Bearer \(token)", forHTTPHeaderField: "Authorization") }
                        let (result,response) = try await session.upload(for: r, from: data)
                        try check(result,response)
                        offset = try JSONDecoder().decode(Offset.self, from: result).offset
                        await progress(size == 0 ? 1 : Double(offset)/Double(size)); success = true; break
                    } catch {
                        if attempt == 3 || Task.isCancelled { throw error }
                        try await Task.sleep(for: .seconds(attempt+1))
                        let state: Offset = try await call("/api/transfers/\(upload.id)", headers: headers)
                        offset = state.offset
                    }
                }
                if !success { throw ShareError.message("Upload unterbrochen.") }
            }
            try Task.checkCancellation()
            let result: ShareResult = try await call("/api/transfers/\(upload.id)/complete", method: "POST", body: [:], headers: headers)
            await progress(1)
            return result
        } catch {
            let _: Empty? = try? await call("/api/transfers/\(upload.id)", method: "DELETE", headers: headers)
            throw error
        }
    }
}
struct Empty: Decodable { let ok: Bool }
