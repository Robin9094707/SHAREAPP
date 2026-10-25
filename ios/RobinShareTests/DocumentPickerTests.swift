#if canImport(UIKit) && !SWIFT_PACKAGE
import XCTest
import UIKit
@testable import RobinShare
@MainActor final class DocumentPickerTests: XCTestCase {
    func testOpenHandsSelectionBackExactlyOnceWithoutFileIO() {
        let selection = [URL(fileURLWithPath: "/provider/Datei.pdf")]
        var received: [[URL]] = []
        let picker = DocumentPicker { received.append($0) }
        let coordinator = picker.makeCoordinator()
        let controller = UIDocumentPickerViewController(forOpeningContentTypes: [.data], asCopy: false)
        coordinator.documentPicker(controller, didPickDocumentsAt: selection)
        coordinator.documentPicker(controller, didPickDocumentsAt: selection)
        XCTAssertEqual(received, [selection])
    }
    func testCancelDoesNotQueueAnyFile() {
        var received: [[URL]] = []
        let coordinator = DocumentPicker { received.append($0) }.makeCoordinator()
        let controller = UIDocumentPickerViewController(forOpeningContentTypes: [.data], asCopy: false)
        coordinator.documentPickerWasCancelled(controller)
        XCTAssertEqual(received, [[]])
    }
}
#endif
