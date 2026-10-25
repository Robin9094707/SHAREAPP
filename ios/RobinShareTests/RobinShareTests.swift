import XCTest
@testable import RobinShare
final class RobinShareTests: XCTestCase {
    func testServerURLRejectsUnsafeInputs() throws {
        XCTAssertThrowsError(try Safety.serverURL("http://example.com"))
        XCTAssertThrowsError(try Safety.serverURL("https://user:secret@example.com"))
        XCTAssertThrowsError(try Safety.serverURL("https://example.com/evil"))
        XCTAssertThrowsError(try Safety.serverURL("https://example.com?x=1"))
        XCTAssertEqual(try Safety.serverURL("https://share.rjuhas.eu").host,"share.rjuhas.eu")
    }
    func testDownloadStaysOnConfiguredServer() throws {
        XCTAssertThrowsError(try Safety.endpoint(base:"https://share.rjuhas.eu",path:"https://evil.example/api/test"))
        XCTAssertThrowsError(try Safety.endpoint(base:"https://share.rjuhas.eu",path:"//evil.example/api/test"))
        XCTAssertEqual(try Safety.endpoint(base:"https://share.rjuhas.eu",path:"/api/transfer/123/download?access=x").host,"share.rjuhas.eu")
    }
    func testReceivedFilenameCannotEscapeDocuments() {
        XCTAssertEqual(Safety.safeName("../../a.txt"), "a.txt")
        XCTAssertEqual(Safety.safeName("C:\\temp\\Foto.jpg"), "Foto.jpg")
        XCTAssertEqual(Safety.safeName("..."),"Datei")
    }
    func testLargeFilesDecodeWithout32BitOverflow() throws {
        let json = #"{"id":"x","filename":"video.mov","size":53687091200,"mime":"video/quicktime","sha256":"abc","expires_at":1800000000,"download_url":"/api/transfer/x/download?access=s","ack_url":"/api/transfer/x/ack?access=s"}"#
        let t = try JSONDecoder().decode(Transfer.self,from:Data(json.utf8))
        XCTAssertEqual(t.size,50*1024*1024*1024)
    }
}
