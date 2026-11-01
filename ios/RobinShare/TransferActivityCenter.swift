import Foundation
import ActivityKit

/// Live Activities display progress; they do not grant unlimited runtime.
@MainActor final class TransferActivityCenter {
    static let shared = TransferActivityCenter()
    private var lastProgress: [String: Double] = [:]

    func start(id: String, filename: String, direction: String) {
        guard ActivityAuthorizationInfo().areActivitiesEnabled else { return }
        guard activity(id) == nil else { return }
        do {
            let attributes = TransferActivityAttributes(transferID: id, filename: filename, direction: direction)
            _ = try Activity.request(attributes: attributes,
                content: ActivityContent(state: .init(fraction: 0, message: direction), staleDate: Date().addingTimeInterval(300)),
                pushType: nil)
        } catch {
            // Live Activities are optional; transfers must never depend on them.
        }
    }
    func update(id: String, fraction: Double) {
        guard let activity = activity(id) else { return }
        let value = max(0, min(1, fraction))
        if abs((lastProgress[id] ?? -1) - value) < 0.025 && value < 1 { return }
        lastProgress[id] = value
        Task {
            await activity.update(ActivityContent(
                state: .init(fraction: value, message: activity.attributes.direction),
                staleDate: Date().addingTimeInterval(300)))
        }
    }
    func finish(id: String, message: String = "Abgeschlossen") {
        lastProgress.removeValue(forKey: id)
        guard let activity = activity(id) else { return }
        Task {
            await activity.end(ActivityContent(
                state: .init(fraction: message == "Abgeschlossen" ? 1 : 0, message: message),
                staleDate: nil),
                dismissalPolicy: .after(Date().addingTimeInterval(90)))
        }
    }
    private func activity(_ id: String) -> Activity<TransferActivityAttributes>? {
        Activity<TransferActivityAttributes>.activities.first { $0.attributes.transferID == id }
    }
}
