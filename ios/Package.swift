// swift-tools-version: 5.10
import PackageDescription
let package = Package(
    name: "RobinShareCore",
    platforms: [.macOS(.v13)],
    products: [.library(name: "ShareCore", targets: ["ShareCore"])],
    targets: [
        .target(name: "ShareCore", path: "RobinShare", exclude: ["API.swift", "AppModel.swift", "Downloads.swift", "Keychain.swift", "Nearby.swift", "RobinShareApp.swift", "Assets.xcassets"], sources: ["Models.swift"]),
        .testTarget(name: "ShareCoreTests", dependencies: ["ShareCore"], path: "RobinShareTests")
    ]
)
