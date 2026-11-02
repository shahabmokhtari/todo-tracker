#if canImport(SwiftUI)
import Foundation
import TodoTrackerKit
#if canImport(WidgetKit)
import WidgetKit
#endif

/// The home-screen widgets (Now and Today). Whatever the board is (on this device, the Mac's server, or the computer the
/// phone is connected to), the app writes what the widgets show for the next day into the shared App Group
/// (`WidgetSnapshot`) whenever it changes, and asks WidgetKit to show it again.
///
/// "Done" on a widget can't change the tasks from the widget (they're in the app's own storage, on the Mac's server, or
/// on a computer over the network): the widget keeps the request in the App Group, leaves the task out at once, and
/// opens the app, which makes the change the way its own Mark done does (the user did it: it's theirs on the timeline)
/// and removes the request. A request is applied once (see `WidgetDoneRequest.outcome`), and waits while the app is
/// still connecting.
extension BoardModel {
    /// Writes what the widgets show when the board changed, and every 15 minutes (the day ahead moves on).
    func updateWidgets(force: Bool = false) {
        guard let shelf = widgetShelf else { return }
        let current = Date()
        let key = "\(ObjectIdentifier(board))|\(board.activity.count)|\(board.activity.last?.at.timeIntervalSince1970 ?? 0)|\(board.nowOrder)|\(board.nowArrangedAt?.timeIntervalSince1970 ?? 0)"
        guard force || key != widgetKey || current.timeIntervalSince(widgetWrittenAt) >= 15 * 60 else { return }
        widgetKey = key
        widgetWrittenAt = current
        let snapshot = WidgetSnapshot.build(board, now: current, timeZone: .current)
        // The same lists as last time (only the clock moved): nothing to show again.
        if let last = widgetSnapshot, last.sameLists(as: snapshot), !force { return }
        do {
            try shelf.write(snapshot)
            widgetSnapshot = snapshot
            Self.reloadWidgets()
        } catch {
            // The widgets keep showing the last snapshot; the next change writes it again.
            widgetSnapshot = nil
        }
    }

    /// Marks done what was tapped Done on a widget: the same change as Mark done here (to this device's tasks, the
    /// Mac's server or the connected computer). Waits while the app is connecting; a task that can't be done here (it
    /// has open subtasks, or the change didn't go through) opens instead, with why.
    public func applyWidgetRequests() {
        guard let shelf = widgetShelf else { return }
        let pending = shelf.pendingDone()
        guard !pending.isEmpty else { return }
        // Opening the tasks folder, or the computer isn't answering yet: they wait (the widget keeps them out).
        if isConnecting || (isRemote && remote?.canChange != true) { return }
        // One change per task: two taps (or Now and Today both tapped) are one Done, and while a Done for a task is
        // still on its way the board here doesn't show it yet, so a repeating task would move on twice.
        var handled = widgetDoneSending
        for request in pending {
            // Removed before the change is made, so it's never made twice.
            shelf.remove(request)
            guard !handled.contains(request.task) else { continue }
            handled.insert(request.task)
            guard request.outcome(on: board) == .complete, let item = board.find(request.task) else { continue }
            if item.hasOpenChildren {
                openTaskId = item.id
                status = "“\(item.title)” has open subtasks: finish them here."
                continue
            }
            let id = item.id
            if server != nil {
                widgetDoneSending.insert(id)
                send(.complete(id), "Done 🎉", onFail: { [weak self] in self?.openTaskId = id },
                     onSettled: { [weak self] in self?.widgetDoneSending.remove(id) })
            } else if !mutate("Done 🎉", { try board.complete(id, now: $0) }) {
                openTaskId = id
            }
        }
        updateWidgets(force: true)
    }

    static func reloadWidgets() {
        #if canImport(WidgetKit)
        WidgetCenter.shared.reloadAllTimelines()
        #endif
    }
}
#endif
