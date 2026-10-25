import Foundation
import Combine
import CoreBluetooth

final class Nearby: NSObject, ObservableObject, NetServiceBrowserDelegate, NetServiceDelegate, CBCentralManagerDelegate, CBPeripheralDelegate {
    static let serviceID = CBUUID(string: "9EE79C20-ED71-4F54-ACF6-050550550001")
    static let endpointID = CBUUID(string: "9EE79C20-ED71-4F54-ACF6-050550550002")
    @Published var peers: [LocalPeer] = []
    @Published var bluetoothState = "Bluetooth-Erkennung ist ausgeschaltet."
    private var browser: NetServiceBrowser?
    private var services: [NetService] = []
    private var central: CBCentralManager?
    private var peripherals: [UUID: CBPeripheral] = [:]
    private var scanTimer: Timer?
    func startNetwork() {
        guard browser == nil else { return }
        let browser = NetServiceBrowser(); browser.delegate = self; self.browser = browser
        browser.searchForServices(ofType: "_robinshare._tcp.", inDomain: "local.")
    }
    func scanBluetooth() {
        if central == nil { central = CBCentralManager(delegate: self, queue: .main) }
        else { centralManagerDidUpdateState(central!) }
    }
    func stop() { browser?.stop(); browser = nil; central?.stopScan(); scanTimer?.invalidate(); peripherals.removeAll(); services.removeAll() }
    func netServiceBrowser(_ browser: NetServiceBrowser, didFind service: NetService, moreComing: Bool) {
        services.append(service); service.delegate = self; service.resolve(withTimeout: 8)
    }
    func netServiceDidResolveAddress(_ sender: NetService) {
        guard let host = sender.hostName, let data = sender.txtRecordData() else { return }
        let txt = NetService.dictionary(fromTXTRecord: data)
        guard let pin = txt["fp"].flatMap({ String(data: $0, encoding: .utf8) }), pin.count == 64 else { return }
        add(LocalPeer(name: sender.name, url: "https://\(host.trimmingCharacters(in: CharacterSet(charactersIn: "."))):\(sender.port)", fingerprint: pin))
    }
    func add(_ peer: LocalPeer) {
        guard (try? Safety.serverURL(peer.url)) != nil, peer.fingerprint.count == 64 else { return }
        if !peers.contains(where: { $0.fingerprint == peer.fingerprint }) { peers.append(peer) }
    }
    func centralManagerDidUpdateState(_ central: CBCentralManager) {
        guard central.state == .poweredOn else { bluetoothState = "Bluetooth ist nicht verfügbar oder nicht freigegeben."; return }
        bluetoothState = "Suche 20 Sekunden nach Robins-Share-PCs …"
        central.scanForPeripherals(withServices: [Self.serviceID], options: [CBCentralManagerScanOptionAllowDuplicatesKey: false])
        scanTimer?.invalidate()
        scanTimer = Timer.scheduledTimer(withTimeInterval: 20, repeats: false) { [weak self] _ in
            self?.central?.stopScan(); self?.bluetoothState = "Suche beendet. Dateien werden über WLAN oder Hotspot übertragen."
        }
    }
    func centralManager(_ central: CBCentralManager, didDiscover peripheral: CBPeripheral, advertisementData: [String: Any], rssi RSSI: NSNumber) {
        guard peripherals[peripheral.identifier] == nil else { return }
        peripherals[peripheral.identifier] = peripheral; peripheral.delegate = self; central.connect(peripheral)
    }
    func centralManager(_ central: CBCentralManager, didConnect peripheral: CBPeripheral) { peripheral.discoverServices([Self.serviceID]) }
    func peripheral(_ peripheral: CBPeripheral, didDiscoverServices error: Error?) {
        for service in peripheral.services ?? [] { peripheral.discoverCharacteristics([Self.endpointID], for: service) }
    }
    func peripheral(_ peripheral: CBPeripheral, didDiscoverCharacteristicsFor service: CBService, error: Error?) {
        if let characteristic = service.characteristics?.first { peripheral.readValue(for: characteristic) }
    }
    func peripheral(_ peripheral: CBPeripheral, didUpdateValueFor characteristic: CBCharacteristic, error: Error?) {
        defer { central?.cancelPeripheralConnection(peripheral) }
        guard let data = characteristic.value, let peer = try? JSONDecoder().decode(LocalPeer.self, from: data) else { return }
        add(peer)
    }
}
