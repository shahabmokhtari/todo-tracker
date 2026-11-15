// swift-tools-version:5.9
import PackageDescription

let package = Package(
    name: "OneThingKit",
    // tvOS: OneThingKit and OneThingTV (the Apple TV app, apple/TV); OneThingUI's views are for iOS and macOS only.
    platforms: [.iOS(.v17), .macOS(.v14), .tvOS(.v17)],
    products: [
        .library(name: "OneThingKit", targets: ["OneThingKit"]),
        .library(name: "OneThingUI", targets: ["OneThingUI"]),
        .library(name: "OneThingWidgetUI", targets: ["OneThingWidgetUI"]),
        .library(name: "OneThingTV", targets: ["OneThingTV"]),
    ],
    dependencies: [
        // YAML frontmatter values in task files (the node API only; see Vault/Frontmatter.swift).
        .package(url: "https://github.com/jpsim/Yams.git", from: "5.1.0"),
    ],
    targets: [
        // Platform-neutral port of OneThing.Core (same JSON schema and agenda rules).
        .target(name: "OneThingKit", dependencies: [.product(name: "Yams", package: "Yams")]),
        // SwiftUI views and view model shared by the iOS and macOS apps.
        .target(name: "OneThingUI", dependencies: ["OneThingKit"]),
        // The home-screen widgets' views (Now and Today), used by the widget extensions (apple/Widgets).
        .target(name: "OneThingWidgetUI", dependencies: ["OneThingKit"]),
        // What the Apple TV app shows and does (its views are in apple/TV): the Now screen, the Board, Done, the remote,
        // and its cache (tvOS keeps no lasting storage).
        .target(name: "OneThingTV", dependencies: ["OneThingKit"]),
        .testTarget(name: "OneThingKitTests", dependencies: ["OneThingKit"]),
        .testTarget(name: "OneThingUITests", dependencies: ["OneThingUI", "OneThingKit", "OneThingWidgetUI"]),
        .testTarget(name: "OneThingTVTests", dependencies: ["OneThingTV", "OneThingKit"]),
    ]
)
