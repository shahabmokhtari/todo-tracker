#if canImport(SwiftUI) && DEBUG
// Debug-only: sample data and offscreen rendering for tests and previews; never shipped in release builds.
import SwiftUI
import TodoTrackerKit
#if canImport(AppKit)
import AppKit
#endif

/// Sample board used for previews and the CI snapshot test.
public enum DemoBoard {
    public static func seed(_ board: TaskBoard, now: Date) throws {
        // The same made-up story as the README's other screenshots (tests/e2e/screenshots.mjs).
        let release = try board.addTask(NewTask("Ship v2.4 release", priority: .high), now: now)
        let steps = try board.addSteps(parentId: release.id, titles: ["Ring 0: internal", "Ring 1: 5% of users", "Ring 2: everyone"], stepDelay: 86_400, now: now)
        try board.addNote(steps[0].id, text: "Ring 0 healthy: error rate 0.02%, p95 latency flat", actor: .agent("claude"), now: now)
        try board.complete(steps[0].id, now: now)
        let review = try board.addTask(NewTask("Reply to the security review", priority: .critical, deadline: now.addingTimeInterval(3 * 3600)), now: now)
        try board.addReminder(review.id, dueAt: now, message: "Review closes at 5 pm", now: now)
        try board.addNote(review.id, text: "Two findings left: token lifetime and the CSP header", now: now)
        try board.addTask(NewTask("Fix flaky checkout test", priority: .low, deadline: now.addingTimeInterval(-3600)), now: now)
        try board.addTask(NewTask("Review Maya's API design doc"), now: now)
        try board.addTask(NewTask("Prep 1:1 agenda"), now: now)
        try board.startFocus(review.id, now: now.addingTimeInterval(-6 * 60))
    }
}

#if os(macOS)
/// Renders the dashboard offscreen to PNG (used by the snapshot test; ScrollView content does not render offscreen).
@MainActor
public enum SnapshotRenderer {
    public static func png(model: BoardModel, dark: Bool) -> Data? {
        let view = DashboardView(model: model, scrollable: false)
            .frame(width: 420)
            .environment(\.colorScheme, dark ? .dark : .light)
        let renderer = ImageRenderer(content: view)
        renderer.scale = 2
        guard let image = renderer.cgImage else { return nil }
        return NSBitmapImageRep(cgImage: image).representation(using: .png, properties: [:])
    }
}
#endif
#endif
