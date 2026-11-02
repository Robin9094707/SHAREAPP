#if !SWIFT_PACKAGE
import XCTest
@testable import RobinShare

final class NativeShareTests: XCTestCase {
    func testOriginalFilenamePreservedForSystemShare() async throws {
        let directory = FileManager.default.temporaryDirectory.appendingPathComponent(UUID().uuidString, isDirectory: true)
        try FileManager.default.createDirectory(at: directory, withIntermediateDirectories: true)
        defer { try? FileManager.default.removeItem(at: directory) }

        let name = "Datei mit Übung 🥰.txt"
        let staged = directory.appendingPathComponent(UUID().uuidString + "-" + name)
        try Data("Test".utf8).write(to: staged)
        let prepared = try await NativeShareFiles.prepare([staged])
        XCTAssertEqual(prepared.count, 1)
        XCTAssertEqual(prepared.first?.lastPathComponent, name)
        XCTAssertEqual(try Data(contentsOf: prepared[0]), Data("Test".utf8))
        XCTAssertTrue(FileManager.default.fileExists(atPath: staged.path))
    }

    func testMultipleFilesHaveIndependentShareURLs() async throws {
        let directory = FileManager.default.temporaryDirectory.appendingPathComponent(UUID().uuidString, isDirectory: true)
        try FileManager.default.createDirectory(at: directory, withIntermediateDirectories: true)
        defer { try? FileManager.default.removeItem(at: directory) }

        let staged = (0..<2).map { i in
            directory.appendingPathComponent(UUID().uuidString + "-same.txt")
        }
        for (index, file) in staged.enumerated() {
            try Data("Hello \(index)".utf8).write(to: file)
        }
        let prepared = try await NativeShareFiles.prepare(staged)
        XCTAssertEqual(prepared.map(\.lastPathComponent), ["same.txt", "same.txt"])
        XCTAssertNotEqual(prepared[0].path, prepared[1].path)
    }
}
#endif
