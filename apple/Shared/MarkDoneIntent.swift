import AppIntents
import Foundation
import OneThingKit
#if canImport(WidgetKit)
import WidgetKit
#endif

/// Done on a widget (built into the app and the widget extension). The widget can't reach the tasks (they're in the
/// app's own storage, or on the Mac's server), so the request is kept in the App Group, the
/// widgets leave the task out at once, and the app opens and makes the change as its own Mark done does
/// (`BoardModel.applyWidgetRequests`): once, as the user, in whatever mode it's in.
struct MarkDoneIntent: AppIntent {
    static var title: LocalizedStringResource = "Mark done"
    static var description = IntentDescription("Marks the task done.")
    static var openAppWhenRun = true
    static var isDiscoverable = false

    @Parameter(title: "Task")
    var task: String

    init() {}

    init(task: UUID) {
        self.task = task.uuidString.lowercased()
    }

    @MainActor
    func perform() async throws -> some IntentResult {
        if let id = UUID(uuidString: task) { _ = try? WidgetShelf.shared?.requestDone(id) }
        #if canImport(WidgetKit)
        WidgetCenter.shared.reloadAllTimelines()
        #endif
        // In the app's process: made now (else when the app comes forward).
        NotificationCenter.default.post(name: WidgetShelf.doneRequested, object: nil)
        return .result()
    }
}
