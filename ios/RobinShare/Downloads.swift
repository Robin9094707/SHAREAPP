import Foundation
import Combine
import CryptoKit
import UIKit

final class Downloads: NSObject, ObservableObject, URLSessionDownloadDelegate {
    static let shared = Downloads()
    @Published var files: [SavedFile] = []
    @Published var progress: [String: Double] = [:]
    @Published var message: String = ""
    var completionHandler: (() -> Void)?
    lazy var session: URLSession = {
        let c = URLSessionConfiguration.background(withIdentifier: "eu.rjuhas.robinshare.downloads")
        c.sessionSendsLaunchEvents = true
        c.isDiscretionary = false
        c.waitsForConnectivity = true
        return URLSession(configuration: c, delegate: self, delegateQueue: nil)
    }()
    static var directory: URL {
        let url = FileManager.default.urls(for: .documentDirectory, in: .userDomainMask)[0].appendingPathComponent("Empfangen", isDirectory: true)
        try? FileManager.default.createDirectory(at: url, withIntermediateDirectories: true)
        return url
    }
    func reload() {
        let urls = (try? FileManager.default.contentsOfDirectory(at: Self.directory, includingPropertiesForKeys: [.fileSizeKey,.contentModificationDateKey])) ?? []
        files = urls.filter { !$0.lastPathComponent.hasPrefix(".") }.map { SavedFile(url: $0, size: Int64((try? $0.resourceValues(forKeys: [.fileSizeKey]).fileSize) ?? 0)) }.sorted { $0.url.lastPathComponent.localizedStandardCompare($1.url.lastPathComponent) == .orderedAscending }
    }
    func start(_ transfer: Transfer, api: API) throws {
        if progress[transfer.id] != nil { return }
        var r = URLRequest(url: try Safety.endpoint(base: api.base, path: transfer.download_url))
        if !api.token.isEmpty { r.setValue("Bearer \(api.token)", forHTTPHeaderField: "Authorization") }
        let task = session.downloadTask(with: r)
        let context = DownloadContext(transfer: transfer, base: api.base, pin: api.pin, token: api.token)
        task.taskDescription = String(data: try JSONEncoder().encode(context), encoding: .utf8)
        progress[transfer.id] = 0
        task.resume()
    }
    func context(_ task: URLSessionTask) -> DownloadContext? {
        task.taskDescription.flatMap { $0.data(using: .utf8) }.flatMap { try? JSONDecoder().decode(DownloadContext.self, from: $0) }
    }
    func urlSession(_ session: URLSession, downloadTask: URLSessionDownloadTask, didWriteData bytesWritten: Int64, totalBytesWritten: Int64, totalBytesExpectedToWrite: Int64) {
        guard let c = context(downloadTask) else { return }
        DispatchQueue.main.async { self.progress[c.transfer.id] = Double(totalBytesWritten)/Double(max(1,c.transfer.size)) }
    }
    func urlSession(_ session: URLSession, downloadTask: URLSessionDownloadTask, didFinishDownloadingTo location: URL) {
        guard let c = context(downloadTask) else { return }
        do {
            guard let response = downloadTask.response as? HTTPURLResponse, response.statusCode == 200 else { throw ShareError.message("Die Datei ist nicht mehr verfügbar.") }
            let file = try FileHandle(forReadingFrom: location)
            defer { try? file.close() }
            var hash = SHA256(); var count: Int64 = 0
            while let chunk = try file.read(upToCount: 1024*1024), !chunk.isEmpty { hash.update(data: chunk); count += Int64(chunk.count) }
            let digest = hash.finalize().map { String(format: "%02x", $0) }.joined()
            guard count == c.transfer.size, digest == c.transfer.sha256 else { throw ShareError.message("Integritätsprüfung fehlgeschlagen. Die Serverdatei bleibt erhalten.") }
            let name = Safety.safeName(c.transfer.filename)
            var destination = Self.directory.appendingPathComponent(name)
            if FileManager.default.fileExists(atPath: destination.path) { destination = Self.directory.appendingPathComponent(UUID().uuidString.prefix(8)+"-"+name) }
            try FileManager.default.moveItem(at: location, to: destination)
            DispatchQueue.main.async { self.message = "\(name) wurde sicher gespeichert."; self.reload() }
            Task {
                let _: Empty? = try? await API(base: c.base, token: c.token, pin: c.pin).call(c.transfer.ack_url, method: "POST", body: [:])
            }
        } catch { DispatchQueue.main.async { self.message = error.localizedDescription } }
        DispatchQueue.main.async { self.progress.removeValue(forKey: c.transfer.id) }
    }
    func urlSession(_ session: URLSession, task: URLSessionTask, didCompleteWithError error: Error?) {
        guard let error, let c = context(task) else { return }
        DispatchQueue.main.async { self.progress.removeValue(forKey: c.transfer.id); self.message = "Download unterbrochen: \(error.localizedDescription). Bitte erneut laden." }
    }
    func urlSessionDidFinishEvents(forBackgroundURLSession session: URLSession) {
        DispatchQueue.main.async { self.completionHandler?(); self.completionHandler = nil }
    }
    func urlSession(_ session: URLSession, task: URLSessionTask, didReceive challenge: URLAuthenticationChallenge, completionHandler: @escaping (URLSession.AuthChallengeDisposition, URLCredential?) -> Void) {
        TrustDelegate(pin: context(task)?.pin ?? "").urlSession(session, didReceive: challenge, completionHandler: completionHandler)
    }
    func urlSession(_ session: URLSession, task: URLSessionTask, willPerformHTTPRedirection response: HTTPURLResponse, newRequest request: URLRequest, completionHandler: @escaping (URLRequest?) -> Void) {
        guard request.url?.host == task.originalRequest?.url?.host, request.url?.scheme == task.originalRequest?.url?.scheme, request.url?.port == task.originalRequest?.url?.port else { completionHandler(nil); return }
        completionHandler(request)
    }
}
final class AppDelegate: NSObject, UIApplicationDelegate {
    func application(_ application: UIApplication, handleEventsForBackgroundURLSession identifier: String, completionHandler: @escaping () -> Void) {
        Downloads.shared.completionHandler = completionHandler
        _ = Downloads.shared.session
    }
}
