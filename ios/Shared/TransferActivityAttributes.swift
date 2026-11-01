import ActivityKit

struct TransferActivityAttributes: ActivityAttributes {
    struct ContentState: Codable, Hashable {
        var fraction: Double
        var message: String
    }
    let transferID: String
    let filename: String
    let direction: String
}
