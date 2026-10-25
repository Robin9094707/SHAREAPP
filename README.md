# Robins Share Suite

Eine deutsche Suite für Dateifreigaben: ein eigenständiger Server mit Weboberfläche, eine native SwiftUI-App für iPhone/iPad und ein natives Windows-11-Programm mit WPF Fluent. Der bestehende Webport **5055** bleibt erhalten.

## Die drei Dateien

Die [Releases](https://github.com/Robin9094707/SHAREAPP/releases) enthalten nach erfolgreicher CI:

| Datei | Verwendung |
| --- | --- |
| `RobinsShare-Server.py` | Python 3.11 oder neuer installieren, Datei starten. Keine zusätzlichen Python-Pakete. |
| `RobinsShare-iOS-unsigniert.ipa` | Native Geräte-App für iOS/iPadOS 26 oder neuer. Vor Installation mit eigener Apple-Signatur signieren. |
| `RobinsShare-Windows-Setup.exe` | Installationsprogramm für Windows 11 x64, einschließlich .NET-Laufzeit. |

**Die unsignierte IPA ist kein unmittelbar installierbares App-Store-/TestFlight-Paket.** Es sind keine Apple-Zertifikate im Repository. Eine gültige Signierung und bei Ad-hoc-Installationen ein zum Gerät passendes Provisioningprofil sind erforderlich. Der optionale Workflow `Signierte iOS-App` kann mit den unten beschriebenen Repository-Secrets eine signierte IPA exportieren.

Windows ist eine native WPF-App mit der integrierten Fluent-Systemdarstellung aus .NET 10, kein WebView und kein Electron. Die EXE hat ohne eigenes Windows-Code-Signing-Zertifikat keine Herausgebersignatur.

## Server starten

```bash
python3 RobinsShare-Server.py
```

Unter Windows gegebenenfalls `py RobinsShare-Server.py`. Danach lokal `http://localhost:5055` öffnen. Für die öffentliche Nutzung den vorhandenen HTTPS-Reverse-Proxy auf diesen Port weiterleiten. Der Server richtet keine Domain oder TLS-Zertifikate auf deinem Host ein. Die nativen Apps akzeptieren für den Webserver ausschließlich HTTPS.

```bash
python3 RobinsShare-Server.py --host 0.0.0.0 --port 5055 --public-url https://share.rjuhas.eu
```

| Variable | Standard |
| --- | --- |
| `ROBINS_PORT` | `5055` (auch alter Alias `ROBBINS_DATEISHARE_PORT`) |
| `ROBINS_HOST` | `0.0.0.0` |
| `ROBINS_PUBLIC_URL` | `https://share.rjuhas.eu` |
| `ROBINS_DATA_DIR` | `robins_dateishare_daten` neben der gestarteten Datei |
| `ROBINS_MAX_MB` | `102400` = 100 GiB je Datei |
| `ROBINS_QUOTA_MB` | `512000` = 500 GiB reservierte Gesamtkapazität |
| `ROBINS_TTL_SECONDS` | `3600` = eine Stunde ab letzter Uploadaktivität |

Die tatsächliche Dateigröße hängt zusätzlich von freiem Speicher und Reverse-Proxy-Limits ab. Beim Proxy Request-Größen von mindestens 8 MiB und angemessene Timeouts erlauben; Request-Buffering für `/api/` möglichst deaktivieren. Öffentliches HTTPS endet am Reverse-Proxy. Freigaben sind bewusst ohne Konto möglich; bei großem öffentlichen Traffic zusätzlich Proxy-Ratenlimits und eine passende Speicherquote setzen.

SQLite und Dateiinhalte bleiben auf deinem eigenen Server. HTTP-Transport ist hinter einem HTTPS-Proxy verschlüsselt; Serverfreigaben sind **nicht Ende-zu-Ende verschlüsselt**. Codes und Links sind Zugriffsschlüssel. PIN-Abfragen und Kopplungsversuche werden limitiert. Geräte-Token authentifizieren Posteingänge. Webinhalte kommen ohne CDNs, Analyseanbieter oder externe Schriftdateien aus.

### Bestehenden Server ersetzen

Alten Prozess stoppen, neue Einzeldatei starten, Proxy auf Port 5055 beibehalten. Die alte Datei und deren Daten vorher sichern. Die neue Version verwendet `shares.sqlite3` in ihrem Datenverzeichnis. Bereits laufende Freigaben des angehängten alten Flask-Skripts werden nicht übernommen; dessen `transfers.sqlite3` und Altdaten werden nicht verändert. Die alte WebRTC-Signalisierung wird durch authentifizierte Gerätekopplung und die lokalen nativen Transfers ersetzt.

## Teilen mit Website und Apps

1. Datei(en) auswählen, hochladen, Code oder Link weitergeben.
2. Auf einem anderen Gerät Code eingeben und Datei annehmen.
3. Für dauerhafte Kopplung auf beiden Geräten denselben Server einstellen. Unter **Geräte** den Kopplungscode des Zielgeräts auf dem anderen Gerät eingeben.
4. Danach beim Senden ein gekoppeltes Gerät auswählen. Es erhält die Datei im Posteingang und entscheidet über Annahme oder Ablehnung.

Jede Datei bekommt einen eigenen Code. Der Browser lädt beim Speichern über den nativen Downloadmanager; nach Prüfung kann die Serverkopie mit einer Bestätigung entfernt werden. Native Apps prüfen Größe und SHA-256 vor dem Speichern und quittieren erst danach. Spätestens mit Ablauf der Freigabe entfernt der Server die Kopie. Wer einen gültigen Code besitzt, hat Zugriff auf die Datei und kann die Serverkopie über die dazugehörige Freigabeberechtigung löschen.

Uploads erfolgen in Blöcken bis 8 MiB mit begrenztem Speicherbedarf. Unterbrechungen werden innerhalb des aktiven Uploads mit Offset-Abfrage wiederholt. Ein kompletter Prozess-/Browser-Neustart setzt einen Upload nicht automatisch fort. Windows-Downloads können vorhandene `.partial`-Dateien über HTTP-Range weiterladen. iOS nutzt für Downloads eine Background-URLSession; die Kontrolle über deren Fortführung hat iOS.

## iPhone / iPad

- iOS 26+, echte native Liquid-Glass-Komponenten und Systemnavigation.
- Konfigurierbarer HTTPS-Server, Codefreigaben und Posteingang.
- Empfangene Dateien unter **Dateien → Auf meinem iPhone → Robins Share → Empfangen**.
- Teilen gespeicherter Dateien über das iOS-Teilen-Menü.
- Dateien über die Dateien-Auswahl importieren; fürs zuverlässige Senden offen lassen. Große Quelldateien werden zuerst in die App kopiert, dafür wird zusätzlicher freier Speicher benötigt.
- Downloads im Hintergrund, Größen-/SHA-256-Prüfung vor Ablage.
- Bonjour im lokalen Netz; optionale Bluetooth-LE-Suche für kompatible Windows-PCs.
- Geräteberechtigungen in der iOS-App im Schlüsselbund, keine Tokens in URLs der Einstellungen.

Eine iOS-Share-Extension für das systemweite Senden **aus anderen Apps** ist in dieser Version nicht enthalten. Empfang und Import in der geöffneten App sind implementiert. iOS lässt keine beliebig permanent laufende Empfangs-App wie einen Desktopdienst zu: Die App muss für neue Direktanfragen geöffnet sein; angenommene Downloads verwenden die Hintergrundschnittstelle.

### Signierte IPA mit GitHub Actions

In den Repository-Secrets einrichten:

- `IOS_CERTIFICATE_P12_BASE64`: Base64-codiertes Apple-Distribution-Zertifikat mit privatem Schlüssel.
- `IOS_CERTIFICATE_PASSWORD`: Passwort dieses P12.
- `IOS_PROVISIONING_PROFILE_BASE64`: Base64-codiertes Ad-hoc-Provisioningprofil für `eu.rjuhas.robinshare`, inklusive Zielgeräte.
- `IOS_TEAM_ID`: Apple-Developer-Team-ID.

Dann **Actions → Signierte iOS-App → Run workflow** ausführen. Zertifikate werden nur im temporären Runner-Keychain installiert und danach entfernt. Der Standardworkflow benötigt diese Secrets nicht und kennzeichnet seine IPA ausdrücklich als unsigniert.

## Windows

- Installierbare, eigenständige EXE mit nativer Fluent-Systemdarstellung.
- Schließen minimiert in den Tray; Rechtsklick aufs Tray-Symbol → **Beenden** beendet den Prozess.
- Optionaler Autostart ohne offene Oberfläche; Polling alle zehn Sekunden statt einer Dauerschleife.
- Optionales Dateikontextmenü **Mit Robins Share an iPhone senden** unter Windows 11 gegebenenfalls in **Weitere Optionen anzeigen**. Der Explorer übergibt die Datei an die bereits laufende Instanz.
- Windows-Integration in **Einstellungen** aktivieren. Sie gilt nur für das aktuelle Benutzerkonto und benötigt keine Administratorrechte.
- Downloadordner: `Downloads\Robins Share`.
- Einstellungen und Token mit Windows-DPAPI für das aktuelle Benutzerkonto geschützt.

### Direkt vom iPhone zum PC – ohne Serverkopie

1. Windows-Programm starten. **Geräte → Direkt im WLAN oder Hotspot** öffnen.
2. Auf dem iPhone **Geräte → Im WLAN suchen** oder **Mit Bluetooth erkennen** wählen. Falls die Erkennung blockiert ist, PC-Adresse manuell eingeben.
3. Den vollständigen SHA-256-Fingerabdruck mit der PC-Anzeige vergleichen und den lokalen PC-Kopplungscode eingeben. Die Kopplung erfolgt mit gepinntem TLS-Zertifikat; kein globales Abschalten der Zertifikatsprüfung.
4. Beim Senden das Gerät mit **Direkt im WLAN** wählen.
5. iPhone → PC: Übertragung landet zunächst in einer temporären PC-Ablage. Am PC **Annehmen** zum endgültigen Speichern wählen, **Ablehnen** löscht sie.
6. PC → iPhone: Am PC **Jetzt senden**, auf dem iPhone **Empfangen → Annehmen**. Die App lädt direkt vom PC. Die Originaldatei am PC wird niemals gelöscht.

Der PC lauscht lokal auf **TCP 5057**, Bonjour auf **UDP 5353**. In Windows Firewall ausschließlich im privaten Netzwerk freigeben. Falls die automatische Windows-Abfrage nicht genügt, enthält der Installationsordner das optionale Skript `Lokale-Verbindung.ps1`; es benötigt Administratorrechte und beschränkt eingehende Verbindungen auf das lokale Subnetz. Ohne Firewallfreigabe bleibt die Serverübertragung nutzbar.

Die direkte Verbindung ist TLS-verschlüsselt, mit bei der Kopplung verglichenem Zertifikatsfingerabdruck. Lokale Dateien bleiben maximal eine Stunde in der Zwischenablage. Ausstehende lokale Sendungen überleben einen Programmneustart nicht. Nach einem Netzwerkwechsel kann die manuell gespeicherte PC-Adresse neu gekoppelt werden müssen.

## Was Bluetooth / WLAN Direct hier bedeutet

**Bluetooth LE dient zur Geräteerkennung und übermittelt die lokale Adresse sowie den Zertifikatsfingerabdruck. Dateiinhalte werden nicht per Bluetooth übertragen.** BLE-Advertising benötigt einen PC-Adapter und Treiber mit Peripheral-/GATT-Server-Unterstützung. Ohne diese Fähigkeit funktionieren Bonjour und manuelle Adresse weiterhin.

Die direkte Dateiübertragung benötigt eine erreichbare Netzwerkverbindung. Ohne gemeinsames WLAN den Windows-Hotspot öffnen und das iPhone damit verbinden. Automatisches Wi-Fi Direct, Wi-Fi Aware zwischen iPhone und beliebigen Windows-Adaptern sowie eine reine Bluetooth-Dateiübertragung sind **nicht implementiert**. Die Suite bezeichnet diese Möglichkeiten nicht fälschlich als verfügbar.

## Entwicklung und kleine Tests

```bash
python -m unittest discover -s tests -v
python scripts/check_web.py
python scripts/package_server.py
```

```bash
# macOS mit Xcode 26 und XcodeGen
cd ios
xcodegen generate
open RobinShare.xcodeproj
```

```powershell
# Windows mit .NET-10-SDK
dotnet build windows/RobinShare/RobinShare.csproj -c Release
# Native kleine Tests ohne Fenster:
windows/RobinShare/bin/Release/net10.0-windows10.0.19041.0/win-x64/RobinsShare.exe --self-test
dotnet publish windows/RobinShare/RobinShare.csproj -c Release -o dist/windows
```

Die CI führt API-/Integritätstests auf Linux, Swift-Tests im iOS-Simulator und kleine native Windows-Tests aus. Anschließend archiviert sie die echte iOS-Geräte-App, erzeugt das Windows-Setup und veröffentlicht die drei Dateien mit Prüfsummen. Grün bedeutet erfolgreicher Build und bestandene automatisierte Tests, keine praktische Geräteprüfung mit einem echten iPhone und Bluetooth-Adapter. Hardware-, Firewall- und Installationstests sind ergänzend erforderlich.

## Architektur

- `server/robinshare.py`: ausschließlich Python-Standardbibliothek, Streaming-HTTP, SQLite, explizite Downloadquittung.
- `web/index.html`: deutsche responsive Oberfläche, keine externen Abhängigkeiten.
- `ios/`: SwiftUI, CoreBluetooth, Bonjour, CryptoKit, Keychain und Background-URLSession.
- `windows/`: .NET 10, WPF Fluent, Kestrel/TLS, GATT und mDNS.
- `.github/workflows/`: plattformnative Builds und Releases.

Das Projekt ist unabhängig von Blip. Dessen Direktübertragungen waren eine Funktionsreferenz; es werden keine Blip-Dienste oder proprietären Protokolle verwendet.
