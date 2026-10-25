import Foundation
import Combine
import UIKit

@MainActor final class AppModel: ObservableObject {
    @Published var server = UserDefaults.standard.string(forKey: "server") ?? "https://share.rjuhas.eu"
    @Published var name = UserDefaults.standard.string(forKey: "name") ?? UIDevice.current.name
    @Published var identity: Identity?
    @Published var state: DeviceState?
    @Published var localPeers: [LocalPeer] = Vault.read("local-peers") ?? []
    @Published var directInbox: [String: [Transfer]] = [:]
    @Published var selectedFiles: [URL] = []
    @Published var importing = false
    @Published var sending = false
    @Published var progress: Double = 0
    @Published var status = "Bereit zum Verbinden."
    @Published var latest: [ShareResult] = []
    @Published var found: Transfer?
    @Published var foundBase = ""
    @Published var foundPin = ""
    @Published var error: String?
    var sendTask: Task<Void, Never>?
    private var pollTask: Task<Void, Never>?
    private var connectedServer = ""
    var api: API { API(base: connectedServer.isEmpty ? server : connectedServer, token: identity?.token ?? "") }
    func connect() async {
        do {
            let base = try Safety.serverURL(server).absoluteString.trimmingCharacters(in: CharacterSet(charactersIn: "/"))
            let client = API(base: base)
            let _: ServerConfig = try await client.call("/api/config")
            server = base; connectedServer = base; identity = Vault.read("device-"+base)
            if identity == nil {
                let registered: Identity = try await client.call("/api/devices", method: "POST", body: ["name": name, "platform": "ios"])
                identity = registered; Vault.save(registered, "device-"+base)
            }
            UserDefaults.standard.set(server, forKey: "server"); UserDefaults.standard.set(name, forKey: "name")
            try await refresh(); status = "Mit deinem Server verbunden."
        } catch { self.error = error.localizedDescription; status = "Verbindung prüfen." }
    }
    func refresh() async throws {
        if identity != nil { let s: DeviceState = try await api.call("/api/devices/me"); state = s }
        for peer in localPeers {
            let client = API(base: peer.url, token: peer.token, pin: peer.fingerprint)
            do { let j: LocalInbox = try await client.call("/api/local/inbox"); directInbox[peer.id] = j.inbox }
            catch { directInbox[peer.id] = [] }
        }
    }
    func startPolling() {
        guard pollTask == nil else { return }
        pollTask = Task { [weak self] in
            while !Task.isCancelled {
                guard let self else { return }
                try? await self.refresh()
                try? await Task.sleep(for: .seconds(10))
            }
        }
    }
    func stopPolling() { pollTask?.cancel(); pollTask = nil }
    func pair(_ code: String) async {
        do { let _: Empty = try await api.call("/api/devices/pair", method: "POST", body: ["code": code]); try await refresh(); status = "Gerät gekoppelt." }
        catch { self.error = error.localizedDescription }
    }
    func unpair(_ device: Device) async {
        do { let _: Empty = try await api.call("/api/devices/\(device.id)", method: "DELETE"); try await refresh() }
        catch { self.error = error.localizedDescription }
    }
    func pairLocal(_ peer: LocalPeer, code: String) async {
        do {
            _ = try Safety.serverURL(peer.url)
            let pin = peer.fingerprint.replacingOccurrences(of: ":", with: "").replacingOccurrences(of: " ", with: "").lowercased()
            guard pin.count == 64, pin.allSatisfy({ $0.isHexDigit }) else { throw ShareError.message("Bitte den vollständigen SHA-256-Fingerabdruck vom PC eingeben oder ein erkanntes Gerät verwenden.") }
            let client = API(base: peer.url, pin: pin)
            let result: LocalToken = try await client.call("/api/local/pair", method: "POST", body: ["code": code, "name": name])
            var saved = LocalPeer(name: peer.name, url: peer.url, fingerprint: pin); saved.token = result.token
            localPeers.removeAll { $0.fingerprint == saved.fingerprint }; localPeers.append(saved)
            Vault.save(localPeers, "local-peers"); status = "PC lokal gekoppelt."
            try await refresh()
        } catch { self.error = error.localizedDescription }
    }
    func importFiles(_ urls: [URL]) {
        guard !sending, !importing, !urls.isEmpty else { return }
        importing = true
        status = "Dateien werden vorbereitet …"
        let folder = FileManager.default.urls(for: .applicationSupportDirectory, in: .userDomainMask)[0].appendingPathComponent("Uploads")
        Task {
            defer { importing = false }
            var failures: [String] = []
            var added = 0
            for url in urls {
                status = "Übernehme \(url.lastPathComponent) …"
                do {
                    let destination = try await FileStaging.stage(url, in: folder)
                    selectedFiles.append(destination); added += 1
                } catch { failures.append("\(url.lastPathComponent): \(error.localizedDescription)") }
            }
            status = added == 1 ? "Eine Datei ist zum Senden bereit." : "\(added) Dateien sind zum Senden bereit."
            if !failures.isEmpty { error = "Diese Dateien konnten nicht übernommen werden:\n" + failures.joined(separator: "\n") }
        }
    }
    func removeFile(_ url: URL) { try? FileManager.default.removeItem(at: url); selectedFiles.removeAll { $0 == url } }
    func send(target: String) {
        guard !sending, !importing, !selectedFiles.isEmpty else { return }
        let local = localPeers.first { "local:"+$0.id == target }
        let client = local.map { API(base: $0.url, token: $0.token, pin: $0.fingerprint) } ?? api
        let deviceID = target.isEmpty || local != nil ? nil : target
        sending = true; progress = 0; latest = []
        sendTask = Task {
            let backgroundID = UIApplication.shared.beginBackgroundTask(withName: "Datei senden") { [weak self] in self?.sendTask?.cancel() }
            defer { self.sending = false; if backgroundID != .invalid { UIApplication.shared.endBackgroundTask(backgroundID) } }
            do {
                for file in selectedFiles {
                    status = "Sende \(displayName(file)) …"
                    let result = try await client.upload(file, filename: displayName(file), target: deviceID) { self.progress = $0 }
                    latest.append(result); removeFile(file)
                }
                status = local == nil ? "Deine Dateien sind bereit." : "Anfrage an den PC gesendet. Bitte dort annehmen."
            } catch { self.error = error.localizedDescription; status = "Upload angehalten. Datei kann erneut gesendet werden." }
        }
    }
    func displayName(_ url: URL) -> String { String(url.lastPathComponent.dropFirst(37)) }
    func lookup(_ code: String) async {
        do {
            guard code.count == 6, code.allSatisfy({ $0.isNumber }) else { throw ShareError.message("Bitte sechs Ziffern eingeben.") }
            let result: Lookup = try await api.call("/api/lookup/pin/\(code)")
            found = result.transfer; foundBase = api.base; foundPin = ""
        } catch { self.error = error.localizedDescription }
    }
}
struct LocalToken: Decodable { let token: String }
struct LocalInbox: Decodable { let inbox: [Transfer] }
