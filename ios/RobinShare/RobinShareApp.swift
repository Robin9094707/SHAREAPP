import SwiftUI
import UniformTypeIdentifiers
import QuickLook

@main struct RobinShareApp: App {
    @UIApplicationDelegateAdaptor(AppDelegate.self) var delegate
    @StateObject private var model = AppModel()
    @StateObject private var nearby = Nearby()
    @StateObject private var downloads = Downloads.shared
    @Environment(\.scenePhase) private var phase
    var body: some Scene {
        WindowGroup {
            ContentView().environmentObject(model).environmentObject(nearby).environmentObject(downloads)
                .task {
                    if ProcessInfo.processInfo.environment["XCTestConfigurationFilePath"] != nil { return }
                    downloads.reload()
                    NativeShareFiles.clearOldCopies()
                    _ = downloads.session
                    _ = BackgroundChunks.shared.session
                    downloads.reconnectTasks()
                    nearby.startNetwork()
                    nearby.scanBluetooth()
                    await model.connect()
                    model.startPolling()
                }
                .onChange(of: phase) { _, value in
                    if value == .active {
                        model.startPolling()
                        downloads.reload()
                        downloads.reconnectTasks()
                        nearby.startNetwork()
                    }
                    else { model.stopPolling(); nearby.stop() }
                }
        }
    }
}
struct ContentView: View {
    @EnvironmentObject var model: AppModel
    @EnvironmentObject var nearby: Nearby
    @EnvironmentObject var downloads: Downloads
    @State private var importer = false
    @State private var pendingImports: [URL] = []
    @State private var target = ""
    @State private var code = ""
    @State private var pairing = ""
    @State private var localURL = ""
    @State private var localPin = ""
    @State private var localCode = ""
    @State private var candidate: LocalPeer?
    @State private var verifyLocal = false
    @State private var tab = 0
    @State private var pendingRemoval: Device?
    @State private var sharingNatively = false
    @State private var preparingNativeShare = false
    @State private var nativeShareFiles: [URL] = []
    @State private var previewFile: URL?
    @State private var fileSearch = ""
    var body: some View {
        TabView(selection: $tab) {
            Tab("Teilen", systemImage: "arrow.up.arrow.down", value: 0) { sendView }
            Tab("Empfangen", systemImage: "tray.and.arrow.down", value: 1) { receiveView }
            Tab("Geräte", systemImage: "laptopcomputer.and.iphone", value: 2) { devicesView }
            Tab("Dateien", systemImage: "folder", value: 3) { filesView }
        }.tint(.indigo)
        .sheet(isPresented: $sharingNatively) {
            SystemShareSheet(files: nativeShareFiles)
                .ignoresSafeArea()
        }
        .quickLookPreview($previewFile)
        .sheet(isPresented: $importer, onDismiss: {
            let selection = pendingImports; pendingImports = []
            model.importFiles(selection)
        }) {
            DocumentPicker { urls in
                pendingImports = urls
                importer = false
            }
        }
        .alert("Hinweis", isPresented: Binding(get: { model.error != nil && !importer && pendingImports.isEmpty && !model.importing }, set: { if !$0 { model.error = nil } })) { Button("OK") { model.error = nil } } message: { Text(model.error ?? "") }
        .alert("PC-Fingerabdruck prüfen", isPresented: $verifyLocal) {
            Button("Abbrechen", role: .cancel) {}
            Button("Stimmt überein · Koppeln") { if let candidate { Task { await model.pairLocal(candidate, code: localCode) } } }
        } message: { Text("Vergleiche diesen SHA-256-Fingerabdruck mit der Anzeige im Windows-Programm:\n\n\(candidate?.fingerprint ?? "")\n\nNur bestätigen, wenn beide Werte übereinstimmen.") }
        .confirmationDialog("Kopplung entfernen?", isPresented: Binding(get: { pendingRemoval != nil }, set: { if !$0 { pendingRemoval = nil } }), titleVisibility: .visible) {
            Button("Kopplung entfernen", role: .destructive) { if let device = pendingRemoval { Task { await model.unpair(device); pendingRemoval = nil } } }
        }
    }
    var background: some View {
        LinearGradient(colors: [Color.indigo.opacity(0.12), Color(.systemGroupedBackground), Color.cyan.opacity(0.08)], startPoint: .topLeading, endPoint: .bottomTrailing).ignoresSafeArea()
    }
    func header(_ title: String, subtitle: String, symbol: String) -> some View {
        VStack(alignment: .leading, spacing: 14) {
            Image(systemName: symbol).font(.system(size: 32, weight: .light)).foregroundStyle(.indigo).padding(18).glassEffect(.regular, in: .rect(cornerRadius: 22))
            Text(title).font(.system(.largeTitle, design: .rounded, weight: .bold))
            Text(subtitle).foregroundStyle(.secondary)
        }.frame(maxWidth: .infinity, alignment: .leading).padding(.vertical, 15)
    }
    var sendView: some View {
        NavigationStack {
            ScrollView {
                VStack(alignment: .leading, spacing: 22) {
                    header("SHAREAPP 3.0", subtitle: "Eine Datei. Alle Möglichkeiten. Ohne Umwege.", symbol: "square.and.arrow.up")
                    Button { importer = true } label: {
                        VStack(spacing: 14) { Image(systemName: "plus.circle").font(.largeTitle); Text("Dateien auswählen").font(.headline); Text("Fotos, Videos, Dokumente und mehr").font(.caption).foregroundStyle(.secondary) }.frame(maxWidth: .infinity).padding(.vertical, 34)
                    }.buttonStyle(.plain).glassEffect(.regular.interactive(), in: .rect(cornerRadius: 28)).disabled(model.sending || model.importing)
                    ForEach(model.selectedFiles, id: \.self) { file in
                        HStack { Image(systemName: "doc").foregroundStyle(.indigo); Text(model.displayName(file)).lineLimit(1); Spacer(); Button { model.removeFile(file) } label: { Image(systemName: "xmark.circle.fill").foregroundStyle(.secondary) }.disabled(model.sending || model.importing).accessibilityLabel("Datei entfernen") }.padding(.vertical, 5)
                    }
                    HStack(spacing: 12) {
                        Button {
                            preparingNativeShare = true
                            Task {
                                do {
                                    nativeShareFiles = try await NativeShareFiles.prepare(model.selectedFiles)
                                    sharingNatively = !nativeShareFiles.isEmpty
                                } catch {
                                    model.error = "Native Freigabe fehlgeschlagen: " + error.localizedDescription
                                }
                                preparingNativeShare = false
                            }
                        } label: {
                            Label("AirDrop / Quick Share", systemImage: "airplayvideo")
                                .frame(maxWidth: .infinity)
                        }
                        .buttonStyle(.glassProminent)
                        .disabled(model.selectedFiles.isEmpty || model.sending || model.importing || preparingNativeShare)
                    }
                    if preparingNativeShare {
                        ProgressView("Native Freigabe wird vorbereitet …")
                    }
                    Text("Für kompatible Android-Geräte wird Quick Share über Apples AirDrop-Freigabe unterstützt. Der Empfänger braucht keine SHAREAPP-App. Für andere Geräte erstelle einen Server-Link.")
                        .font(.caption).foregroundStyle(.secondary)
                    Picker("Senden an", selection: $target) {
                        Text("Code oder Link erstellen").tag("")
                        ForEach(model.state?.devices ?? []) { Text($0.name+" · Server").tag($0.id) }
                        ForEach(model.localPeers) { Text($0.name+" · Direkt im WLAN").tag("local:"+$0.id) }
                    }.pickerStyle(.menu).disabled(model.sending || model.importing)
                    if model.importing { ProgressView("Datei wird vorbereitet …") }
                    if model.sending { ProgressView(value: model.progress); Button("Abbrechen", role: .destructive) { model.sendTask?.cancel() } }
                    Button { model.send(target: target) } label: { Label("Jetzt senden", systemImage: "arrow.up").frame(maxWidth: .infinity).padding(8) }.buttonStyle(.glassProminent).disabled(model.selectedFiles.isEmpty || model.sending || model.importing)
                    Text(model.status).font(.footnote).foregroundStyle(.secondary)
                    Text("Im Hintergrund übernimmt iOS laufende Dateiblöcke. Unterbrochene Uploads lassen sich mit „Jetzt senden“ fortsetzen.")
                        .font(.caption2).foregroundStyle(.tertiary)
                    ForEach(model.latest, id: \.transfer.id) { result in
                        VStack(alignment: .leading, spacing: 10) {
                            Text(result.transfer.filename).font(.headline)
                            Text(result.pin).font(.system(size: 34, weight: .semibold, design: .monospaced)).textSelection(.enabled)
                            HStack {
                                Button("Code kopieren", systemImage: "doc.on.doc") { UIPasteboard.general.string = result.pin }
                                if let url = URL(string: result.link) { ShareLink(item: url) }
                            }.buttonStyle(.glass)
                            if let url = URL(string: result.link), url.scheme == "https" {
                                ShareQRCard(link: url)
                            }
                        }.padding(20).frame(maxWidth: .infinity, alignment: .leading).glassEffect(.regular, in: .rect(cornerRadius: 22))
                    }
                }.padding(24)
            }.background(background).navigationTitle("Robins Share").navigationBarTitleDisplayMode(.inline)
        }
    }
    var receiveView: some View {
        NavigationStack {
            ScrollView {
                VStack(alignment: .leading, spacing: 22) {
                    header("Für dich.", subtitle: "Ein Code verbindet deine Geräte.", symbol: "tray.and.arrow.down")
                    TextField("000000", text: $code).keyboardType(.numberPad).textContentType(.oneTimeCode).font(.system(size: 38, weight: .medium, design: .monospaced)).multilineTextAlignment(.center).padding(24).glassEffect(.regular, in: .rect(cornerRadius: 23)).onChange(of: code) { _, value in code = String(value.filter(\.isNumber).prefix(6)) }
                    Button("Datei finden", systemImage: "magnifyingglass") { Task { await model.lookup(code) } }.buttonStyle(.glassProminent).disabled(code.count != 6)
                    if let found = model.found { transferRow(found, api: API(base: model.foundBase, pin: model.foundPin)) }
                    Text("Posteingang").font(.title2.bold()).padding(.top)
                    if (model.state?.inbox ?? []).isEmpty && model.directInbox.values.allSatisfy(\.isEmpty) { ContentUnavailableView("Noch keine Sendungen", systemImage: "tray", description: Text("Dateien von deinen gekoppelten Geräten erscheinen hier.")) }
                    ForEach(model.state?.inbox ?? []) { transferRow($0, api: model.api) }
                    ForEach(model.localPeers) { peer in
                        ForEach(model.directInbox[peer.id] ?? []) { transferRow($0, api: API(base: peer.url, token: peer.token, pin: peer.fingerprint)) }
                    }
                    if !downloads.message.isEmpty { Text(downloads.message).font(.footnote).foregroundStyle(.secondary) }
                }.padding(24)
            }.background(background).navigationTitle("Empfangen").navigationBarTitleDisplayMode(.inline).refreshable { try? await model.refresh() }
        }
    }
    func transferRow(_ transfer: Transfer, api: API) -> some View {
        VStack(alignment: .leading, spacing: 13) {
            Label(transfer.filename, systemImage: "doc").font(.headline).lineLimit(2)
            Text(ByteCountFormatter.string(fromByteCount: transfer.size, countStyle: .file)).foregroundStyle(.secondary)
            if let progress = downloads.progress[transfer.id] { ProgressView(value: progress) }
            else {
                HStack {
                    Button("Annehmen", systemImage: "arrow.down.to.line") { do { try downloads.start(transfer, api: api) } catch { model.error = error.localizedDescription } }.buttonStyle(.glassProminent)
                    Button("Ablehnen", role: .destructive) { Task { do { let _: Empty = try await api.call(transfer.ack_url, method: "POST", body: [:]); if model.found?.id == transfer.id { model.found = nil }; try await model.refresh() } catch { model.error = error.localizedDescription } } }.buttonStyle(.glass)
                }
            }
        }.padding(19).frame(maxWidth: .infinity, alignment: .leading).glassEffect(.regular, in: .rect(cornerRadius: 22))
    }
    var devicesView: some View {
        NavigationStack {
            Form {
                Section("Dein Webserver") {
                    TextField("https://share.rjuhas.eu", text: $model.server).keyboardType(.URL).textInputAutocapitalization(.never).autocorrectionDisabled()
                    TextField("Gerätename für neue Kopplung", text: $model.name)
                    Button("Server verbinden", systemImage: "network") { Task { await model.connect() } }
                    Text(model.status).font(.caption).foregroundStyle(.secondary)
                }
                Section("Dieses iPhone koppeln") {
                    Text(model.state?.pair_code ?? "Zuerst verbinden").font(.system(.title, design: .monospaced)).textSelection(.enabled)
                    Text("Diesen Code im Browser oder am PC eingeben. Er ist zehn Minuten gültig und wird nach Verwendung erneuert.").font(.caption).foregroundStyle(.secondary)
                    TextField("Code des anderen Geräts", text: $pairing).keyboardType(.numberPad)
                    Button("Gerät koppeln") { Task { await model.pair(pairing); pairing = "" } }.disabled(pairing.count != 6)
                }
                Section("Gekoppelt über deinen Server") {
                    ForEach(model.state?.devices ?? []) { device in
                        HStack { Label(device.name, systemImage: device.platform == "windows" ? "desktopcomputer" : "iphone"); Spacer(); Button(role: .destructive) { pendingRemoval = device } label: { Image(systemName: "link.badge.plus") }.accessibilityLabel("Kopplung entfernen") }
                    }
                }
                Section("Direkt mit deinem Windows-PC") {
                    Text("Beide Geräte brauchen dasselbe Netzwerk oder einen gemeinsamen Hotspot. Vergleiche vor dem Koppeln den Fingerabdruck am PC.").font(.caption).foregroundStyle(.secondary)
                    Button("Im WLAN suchen", systemImage: "wifi") { nearby.startNetwork() }
                    Button("Mit Bluetooth erkennen", systemImage: "antenna.radiowaves.left.and.right") { nearby.scanBluetooth() }
                    Text(nearby.bluetoothState).font(.caption).foregroundStyle(.secondary)
                    ForEach(nearby.peers) { peer in
                        Button(peer.name) { localURL = peer.url; localPin = peer.fingerprint; candidate = peer }
                    }
                    TextField("https://PC-IP:5057", text: $localURL).keyboardType(.URL).textInputAutocapitalization(.never).autocorrectionDisabled()
                    TextField("SHA-256-Fingerabdruck vom PC", text: $localPin, axis: .vertical).textInputAutocapitalization(.never).autocorrectionDisabled().font(.caption.monospaced())
                    TextField("Lokaler Kopplungscode vom PC", text: $localCode).keyboardType(.numberPad)
                    Button("Lokal koppeln") {
                        candidate = LocalPeer(name: candidate?.name ?? "Windows-PC", url: localURL, fingerprint: localPin)
                        verifyLocal = true
                    }.disabled(localCode.count != 6 || localURL.isEmpty || localPin.isEmpty)
                    ForEach(model.localPeers) { peer in
                        HStack { Label(peer.name, systemImage: "wifi"); Spacer(); Button("Entfernen", role: .destructive) { model.localPeers.removeAll { $0.id == peer.id }; Vault.save(model.localPeers, "local-peers") } }
                    }
                }
                Section { Text("Bluetooth erkennt kompatible PCs. Für Dateiinhalte wird die schnelle Netzwerkverbindung genutzt. Windows kann einen mobilen Hotspot bereitstellen; automatische WLAN-Direct-Übertragung zwischen Windows und iPhone ist hier nicht implementiert.").font(.footnote).foregroundStyle(.secondary) }
            }.navigationTitle("Deine Geräte")
        }
    }
    var filesView: some View {
        NavigationStack {
            List {
                Section {
                    Label("Vorschau, Suchen, AirDrop und Quick Share direkt aus deinen Downloads. Alle Dateitypen bleiben im Original erhalten.", systemImage: "folder")
                        .font(.footnote).foregroundStyle(.secondary)
                }
                ForEach(downloads.files.filter {
                    fileSearch.isEmpty || $0.url.lastPathComponent.localizedCaseInsensitiveContains(fileSearch)
                }) { file in
                    HStack(spacing: 12) {
                        Button {
                            previewFile = file.url
                        } label: {
                            Label {
                                VStack(alignment: .leading, spacing: 4) {
                                    Text(file.url.lastPathComponent).lineLimit(2)
                                    Text(ByteCountFormatter.string(fromByteCount: file.size, countStyle: .file))
                                        .font(.caption).foregroundStyle(.secondary)
                                }
                            } icon: {
                                Image(systemName: "doc.text.magnifyingglass")
                            }
                        }.buttonStyle(.plain)
                        Spacer(minLength: 0)
                        ShareLink(item: file.url) {
                            Image(systemName: "square.and.arrow.up")
                        }.buttonStyle(.glass).accessibilityLabel("Datei mit AirDrop oder anderen Apps teilen")
                    }
                    .swipeActions(edge: .trailing) {
                        Button("Löschen", role: .destructive) {
                            try? FileManager.default.removeItem(at: file.url)
                            downloads.reload()
                        }
                    }
                }
                if downloads.files.isEmpty {
                    ContentUnavailableView("Noch keine Downloads", systemImage: "folder", description: Text("Angenommene Dateien werden hier gespeichert und können systemweit geteilt werden."))
                }
            }
            .searchable(text: $fileSearch, prompt: "Dateien durchsuchen")
            .navigationTitle("Deine Dateien")
            .refreshable { downloads.reload() }
            .toolbar {
                ToolbarItem(placement: .topBarTrailing) {
                    Button("Aktualisieren", systemImage: "arrow.clockwise") { downloads.reload() }
                }
            }
        }
    }
}
