// swift-tools-version:5.9
import PackageDescription

let package = Package(
    name: "TodoTrackerKit",
    platforms: [.iOS(.v17), .macOS(.v14)],
    products: [
        .library(name: "TodoTrackerKit", targets: ["TodoTrackerKit"]),
        .library(name: "TodoTrackerUI", targets: ["TodoTrackerUI"]),
    ],
    targets: [
        // Platform-neutral port of TodoTracker.Core (same JSON schema and agenda rules).
        .target(name: "TodoTrackerKit"),
        // SwiftUI views and view model shared by the iOS and macOS apps.
        .target(name: "TodoTrackerUI", dependencies: ["TodoTrackerKit"]),
        .testTarget(name: "TodoTrackerKitTests", dependencies: ["TodoTrackerKit"]),
        .testTarget(name: "TodoTrackerUITests", dependencies: ["TodoTrackerUI", "TodoTrackerKit"]),
    ]
)
