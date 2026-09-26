#if canImport(SwiftUI)
import SwiftUI
import TodoTrackerKit
#if canImport(AppKit)
import AppKit
#endif

/// Sample board used for previews and the CI snapshot test.
public enum DemoBoard {
    public static func seed(_ board: TaskBoard, now: Date) throws {
        let x = try board.addTask(NewTask("Roll out feature X", priority: .high), now: now)
        let a = try board.addTask(NewTask("Feature A", parentId: x.id), now: now)
        let b = try board.addTask(NewTask("Feature B", parentId: x.id), now: now)
        try board.addSteps(parentId: a.id, titles: ["A ring 0", "A ring 1", "A ring 2"], stepDelay: 86_400, now: now)
        let bSteps = try board.addSteps(parentId: b.id, titles: ["B ring 0", "B ring 1", "B ring 2"], stepDelay: 86_400, now: now)
        try board.addNote(bSteps[0].id, text: "B ring 0 deployed, metrics green", actor: .agent("copilot"), now: now)
        try board.complete(bSteps[0].id, now: now)
        let review = try board.addTask(NewTask("Reply to design review", priority: .critical, deadline: now.addingTimeInterval(3 * 3600)), now: now)
        try board.addReminder(review.id, dueAt: now, message: "Review closes today", now: now)
        try board.addNote(review.id, text: "Left 3 comments, waiting on Alex", now: now)
        try board.addTask(NewTask("Update onboarding doc", priority: .low, deadline: now.addingTimeInterval(-3600)), now: now)
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
