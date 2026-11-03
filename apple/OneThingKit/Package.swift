// swift-tools-version:5.9
import PackageDescription

let package = Package(
    name: "OneThingKit",
    platforms: [.iOS(.v17), .macOS(.v14)],
    products: [
        .library(name: "OneThingKit", targets: ["OneThingKit"]),
        .library(name: "OneThingUI", targets: ["OneThingUI"]),
        .library(name: "OneThingWidgetUI", targets: ["OneThingWidgetUI"]),
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
        .testTarget(name: "OneThingKitTests", dependencies: ["OneThingKit"]),
        .testTarget(name: "OneThingUITests", dependencies: ["OneThingUI", "OneThingKit", "OneThingWidgetUI"]),
    ]
)
