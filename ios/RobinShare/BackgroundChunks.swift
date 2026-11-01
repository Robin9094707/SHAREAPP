import Foundation
import Security

/// Background URLSession accepts file-backed HTTP(S) uploads. Every chunk is
/// staged on disk, so iOS can complete the active request after suspension.
/// The upload journal reconciles server offsets after process restarts.
final class BackgroundChunks: NSObject, URLSessionDataDelegate {
    static let shared = BackgroundChunks()
    var completionHandler: (() -> Void)?
    private struct Context: Codable { let file: String; let pin: String }
    private var continuations: [Int: CheckedContinuation<Data, Error>] = [:]
    private var buffers: [Int: Data] = [:]
    lazy var session: URLSession = {
        let configuration = URLSessionConfiguration.background(withIdentifier: "eu.rjuhas.robinshare.uploads")
        configuration.sessionSendsLaunchEvents = true
        configuration.isDiscretionary = false
        configuration.waitsForConnectivity = true
        configuration.timeoutIntervalForResource = 24 * 3600
        return URLSession(configuration: configuration, delegate: self, delegateQueue: .main)
    }()
    private override init() { super.init() }

    func upload(_ request: URLRequest, body: Data, pin: String) async throws -> Data {
        let folder = FileManager.default.urls(for: .applicationSupportDirectory, in: .userDomainMask)[0]
            .appendingPathComponent("UploadChunks", isDirectory: true)
        try FileManager.default.createDirectory(at: folder, withIntermediateDirectories: true)
        let file = folder.appendingPathComponent(UUID().uuidString)
        try body.write(to: file, options: .atomic)
        return try await withCheckedThrowingContinuation { continuation in
            DispatchQueue.main.async {
                let task = self.session.uploadTask(with: request, fromFile: file)
                let context = Context(file: file.path, pin: pin)
                task.taskDescription = (try? JSONEncoder().encode(context)).flatMap { String(data: $0, encoding: .utf8) }
                self.continuations[task.taskIdentifier] = continuation
                task.resume()
            }
        }
    }
    private func context(_ task: URLSessionTask) -> Context? {
        task.taskDescription?.data(using: .utf8).flatMap { try? JSONDecoder().decode(Context.self, from: $0) }
    }
    func urlSession(_ session: URLSession, dataTask: URLSessionDataTask, didReceive data: Data) {
        buffers[dataTask.taskIdentifier, default: Data()].append(data)
    }
    func urlSession(_ session: URLSession, task: URLSessionTask, didCompleteWithError error: Error?) {
        if let context = context(task) { try? FileManager.default.removeItem(atPath: context.file) }
        let data = buffers.removeValue(forKey: task.taskIdentifier) ?? Data()
        guard let continuation = continuations.removeValue(forKey: task.taskIdentifier) else { return }
        if let error { continuation.resume(throwing: error); return }
        guard let response = task.response as? HTTPURLResponse, (200..<300).contains(response.statusCode) else {
            let json = (try? JSONSerialization.jsonObject(with: data)) as? [String: Any]
            continuation.resume(throwing: ShareError.message(json?["error"] as? String ?? "Der Upload wurde vom Server abgelehnt."))
            return
        }
        continuation.resume(returning: data)
    }
    func urlSession(_ session: URLSession, task: URLSessionTask, didReceive challenge: URLAuthenticationChallenge, completionHandler: @escaping (URLSession.AuthChallengeDisposition, URLCredential?) -> Void) {
        TrustDelegate(pin: context(task)?.pin ?? "").urlSession(session, didReceive: challenge, completionHandler: completionHandler)
    }
    func urlSession(_ session: URLSession, task: URLSessionTask, willPerformHTTPRedirection response: HTTPURLResponse, newRequest request: URLRequest, completionHandler: @escaping (URLRequest?) -> Void) {
        guard let a = task.originalRequest?.url, let b = request.url,
              a.host == b.host, a.port == b.port, a.scheme == b.scheme else { completionHandler(nil); return }
        completionHandler(request)
    }
    func urlSessionDidFinishEvents(forBackgroundURLSession session: URLSession) {
        completionHandler?()
        completionHandler = nil
    }
}
