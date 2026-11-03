import SwiftUI
import XCTest
@testable import OneThingKit
@testable import OneThingUI
import OneThingWidgetUI
#if canImport(AppKit)
import AppKit
#endif

/// Renders the widgets (every size, light and dark, and empty) from the demo board to PNGs for visual review; CI
/// uploads them with the other SwiftUI snapshots (SNAPSHOT_DIR).
@MainActor
final class WidgetRenderTests: XCTestCase {
    private func png<V: View>(_ view: V, width: CGFloat, height: CGFloat, dark: Bool) -> Data? {
        let framed = view
            .padding(16)
            .frame(width: width, height: height)
            .background(dark ? Color(white: 0.11) : Color.white)
            .clipShape(RoundedRectangle(cornerRadius: 22, style: .continuous))
            .environment(\.colorScheme, dark ? .dark : .light)
            .environment(\.widgetLinks, false)
        let renderer = ImageRenderer(content: framed)
        renderer.scale = 2
        #if canImport(AppKit)
        guard let image = renderer.cgImage else { return nil }
        return NSBitmapImageRep(cgImage: image).representation(using: .png, properties: [:])
        #else
        return nil
        #endif
    }

    private func save(_ data: Data, _ name: String) throws {
        guard let dir = ProcessInfo.processInfo.environment["SNAPSHOT_DIR"] else { return }
        try FileManager.default.createDirectory(atPath: dir, withIntermediateDirectories: true)
        try data.write(to: URL(fileURLWithPath: dir).appendingPathComponent(name))
    }

    func testRenderWidgetSnapshots() throws {
        let now = Date()
        let board = TaskBoard()
        try DemoBoard.seed(board, now: now)
        let content = WidgetContent(snapshot: WidgetSnapshot.build(board, now: now, timeZone: .current), at: now)
        XCTAssertNotNil(content.view.focus, "the demo board has something to do now")
        let empty = WidgetContent(snapshot: WidgetSnapshot.build(TaskBoard(), now: now, timeZone: .current), at: now)
        let missing = WidgetContent(snapshot: nil, at: now)

        let sizes: [(String, WidgetSize, CGFloat, CGFloat)] = [("small", .small, 170, 170), ("medium", .medium, 364, 170)]
        for dark in [false, true] {
            let suffix = dark ? "dark" : "light"
            for (name, size, width, height) in sizes {
                let data = try XCTUnwrap(png(NowWidgetView(content: content, size: size) { _ in DoneLabel(compact: size == .small) }, width: width, height: height, dark: dark))
                XCTAssertGreaterThan(data.count, 5_000)
                try save(data, "widget-now-\(name)-\(suffix).png")
            }
            let today = try XCTUnwrap(png(TodayWidgetView(content: content) { _ in DoneLabel(compact: true) }, width: 364, height: 382, dark: dark))
            try save(today, "widget-today-large-\(suffix).png")
        }
        try save(try XCTUnwrap(png(NowWidgetView(content: empty, size: .small) { _ in DoneLabel() }, width: 170, height: 170, dark: false)), "widget-now-small-empty.png")
        try save(try XCTUnwrap(png(TodayWidgetView(content: missing) { _ in DoneLabel() }, width: 364, height: 382, dark: false)), "widget-today-large-no-snapshot.png")
        try save(try XCTUnwrap(png(NowWidgetView(content: content, size: .rectangular) { _ in DoneLabel() }, width: 172, height: 76, dark: true)), "widget-now-lock-screen.png")
    }
}
