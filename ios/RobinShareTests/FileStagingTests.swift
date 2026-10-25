import XCTest
#if SWIFT_PACKAGE
@testable import ShareCore
#else
@testable import RobinShare
#endif
final class FileStagingTests: XCTestCase {
    func testImportCopiesRealContentAndPreservesSource() async throws {
        let root = FileManager.default.temporaryDirectory.appendingPathComponent(UUID().uuidString)
        defer { try? FileManager.default.removeItem(at: root) }
        try FileManager.default.createDirectory(at: root, withIntermediateDirectories: true)
        let source = root.appendingPathComponent("Foto ä.txt")
        let content = Data(repeating: 0x5a, count: 2 * 1024 * 1024)
        try content.write(to: source)
        let destination = try await FileStaging.stage(source, in: root.appendingPathComponent("Uploads"))
        XCTAssertEqual(try Data(contentsOf: destination), content)
        XCTAssertEqual(try Data(contentsOf: source), content)
        XCTAssertTrue(destination.lastPathComponent.hasSuffix("-Foto ä.txt"))
    }
    func testSameFilenameDoesNotOverwritePreviousImport() async throws {
        let root = FileManager.default.temporaryDirectory.appendingPathComponent(UUID().uuidString)
        defer { try? FileManager.default.removeItem(at: root) }
        try FileManager.default.createDirectory(at: root, withIntermediateDirectories: true)
        let source = root.appendingPathComponent("Datei.bin"); try Data([1,2,3]).write(to: source)
        let first = try await FileStaging.stage(source, in: root.appendingPathComponent("Uploads"))
        let second = try await FileStaging.stage(source, in: root.appendingPathComponent("Uploads"))
        XCTAssertNotEqual(first, second)
        XCTAssertEqual(try Data(contentsOf: first), try Data(contentsOf: second))
    }
    @MainActor func testImportCalledFromUIActorDoesNotBlockOrReadOnMainThread() async throws {
        let root = FileManager.default.temporaryDirectory.appendingPathComponent(UUID().uuidString)
        defer { try? FileManager.default.removeItem(at: root) }
        try FileManager.default.createDirectory(at: root, withIntermediateDirectories: true)
        let source = root.appendingPathComponent("UI.txt"); try Data("UI bleibt frei".utf8).write(to: source)
        let destination = try await FileStaging.stage(source, in: root.appendingPathComponent("Uploads"))
        XCTAssertTrue(Thread.isMainThread)
        XCTAssertEqual(try String(contentsOf: destination, encoding: .utf8), "UI bleibt frei")
    }
    func testDirectoryFailsClearlyWithoutLeavingAQueuedFile() async throws {
        let root = FileManager.default.temporaryDirectory.appendingPathComponent(UUID().uuidString)
        defer { try? FileManager.default.removeItem(at: root) }
        let source = root.appendingPathComponent("Ordner"); let staging = root.appendingPathComponent("Uploads")
        try FileManager.default.createDirectory(at: source, withIntermediateDirectories: true)
        do { _ = try await FileStaging.stage(source, in: staging); XCTFail("Ordner dürfen nicht als Dateien übernommen werden") }
        catch { XCTAssertTrue(error.localizedDescription.contains("Datei")) }
        XCTAssertEqual(try FileManager.default.contentsOfDirectory(atPath: staging.path), [])
    }
}
