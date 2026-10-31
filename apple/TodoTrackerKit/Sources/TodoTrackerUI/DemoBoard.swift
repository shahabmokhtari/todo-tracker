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

    /// The whole app on a made-up board, kept in memory only (nothing is read or saved): for screenshots, started
    /// with TT_DEMO=1 in a debug build. Cards in every column, subtasks, labels and tags, done and archived tasks.
    @MainActor
    public static func model(now: Date = Date()) -> BoardModel {
        let board = TaskBoard()
        let work = board.groups[0].id, home = board.groups[1].id
        try? seed(board, now: now)
        func add(_ title: String, group: UUID = work, stage: Stage, priority: Priority = .normal, parent: UUID? = nil, due: TimeInterval? = nil) -> UUID {
            (try? board.addTask(NewTask(title, parentId: parent, groupId: parent == nil ? group : nil, priority: priority,
                                        deadline: due.map { now.addingTimeInterval($0) }, stage: stage), now: now.addingTimeInterval(-86_400 * 3)))?.id ?? UUID()
        }
        for item in board.items where item.parent == nil { try? board.setStage(item.id, .next, now: now) }
        if let release = board.items.first { _ = try? board.startTimer(release.id, now: now.addingTimeInterval(-40 * 60)) }
        _ = add("Draft the Q3 roadmap", stage: .inbox, priority: .high, due: 5 * 86_400)
        _ = add("Try the new CI cache", stage: .inbox, priority: .low)
        let onboarding = add("Onboarding guide for new hires", stage: .doing)
        for step in ["Outline", "First draft", "Review with Sam"] { _ = add(step, stage: .next, parent: onboarding) }
        let groceries = add("Groceries for the weekend", group: home, stage: .next)
        _ = add("Book the dentist", group: home, stage: .inbox, due: 2 * 86_400)
        _ = add("Plan the summer trip", group: home, stage: .next, priority: .high)
        for (title, daysAgo) in [("Renew the passport", 0.0), ("Send the invoice", 1.0), ("Fix the login redirect", 1.0), ("Pay the electricity bill", 3.0)] {
            let id = add(title, group: title.contains("bill") || title.contains("passport") ? home : work, stage: .next)
            try? board.complete(id, now: now.addingTimeInterval(-daysAgo * 86_400 - 3600))
        }
        let old = add("Clean up old branches", stage: .next)
        try? board.complete(old, now: now.addingTimeInterval(-20 * 86_400))
        try? board.archive(old, now: now.addingTimeInterval(-19 * 86_400))
        if let outline = board.find(onboarding)?.children.first { try? board.complete(outline.id, now: now.addingTimeInterval(-7200)) }
        try? board.complete(groceries, now: now.addingTimeInterval(-600))
        try? board.reopen(groceries, now: now.addingTimeInterval(-300))
        // Labels and tags are kept in the board's file: added there.
        guard var json = (try? BoardCodec.encode(board)).flatMap({ try? JSONSerialization.jsonObject(with: $0) as? [String: Any] }) else { return BoardModel(store: nil, fallback: board) }
        json["labels"] = [["name": "urgent", "color": "#ef4444"], ["name": "writing", "color": "#8b5cf6"], ["name": "errand", "color": "#10b981"]]
        let extras: [String: (labels: [String], tags: [String])] = [
            "Reply to the security review": (["urgent"], ["security"]), "Draft the Q3 roadmap": (["writing"], ["planning"]),
            "Onboarding guide for new hires": (["writing"], ["docs", "team"]), "Groceries for the weekend": (["errand"], []),
            "Ship v2.4 release": ([], ["release"]), "Book the dentist": (["errand"], ["health"]),
        ]
        func tag(_ items: [[String: Any]]) -> [[String: Any]] {
            items.map { item in
                var item = item
                if let title = item["title"] as? String, let extra = extras[title] {
                    if !extra.labels.isEmpty { item["labels"] = extra.labels }
                    if !extra.tags.isEmpty { item["tags"] = extra.tags }
                }
                if let children = item["children"] as? [[String: Any]] { item["children"] = tag(children) }
                return item
            }
        }
        json["items"] = tag(json["items"] as? [[String: Any]] ?? [])
        let tagged = (try? JSONSerialization.data(withJSONObject: json)).flatMap { try? BoardCodec.decode($0) } ?? board
        return BoardModel(store: nil, fallback: tagged)
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
