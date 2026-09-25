import SwiftUI
import TodoTrackerKit
import TodoTrackerUI

@main
struct TodoTrackerApp: App {
    @StateObject private var model = BoardModel.live()

    var body: some Scene {
        #if os(macOS)
        WindowGroup("Todo Tracker") {
            dashboard.frame(minWidth: 320, idealWidth: 380, minHeight: 480)
        }
        .defaultSize(width: 380, height: 820)
        .windowResizability(.contentMinSize)

        // Always-available glance from the menu bar: what to do now and quick capture.
        MenuBarExtra {
            MenuBarGlance(model: model)
        } label: {
            Label("\(model.dashboard.now.count)", systemImage: model.dashboard.now.contains(where: \.needsAttention) ? "bell.badge" : "checklist")
        }
        .menuBarExtraStyle(.window)
        #else
        WindowGroup("Todo Tracker") {
            dashboard
        }
        #endif
    }

    private var dashboard: some View {
        DashboardView(model: model)
            .onAppear { BoardModel.requestNotificationPermission() }
    }
}
#if os(macOS)
struct MenuBarGlance: View {
    @ObservedObject var model: BoardModel

    var body: some View {
        VStack(alignment: .leading, spacing: 8) {
            Text("Do this now").font(.caption).foregroundStyle(.secondary)
            if let focus = model.dashboard.focus {
                Text(focus.item.title).font(.headline)
                HStack {
                    Button("Done") { model.complete(focus.item.id) }
                    Button("Later (1h)") { model.snooze(focus.item.id, minutes: 60) }
                    Button("Tomorrow") { model.snoozeUntilTomorrow(focus.item.id) }
                }
            } else {
                Text("Nothing is due ✨")
            }
            Divider()
            TextField("Add a task…", text: $model.quickText).onSubmit { model.capture() }
            Text("\(model.dashboard.now.count) now · \(model.dashboard.waiting.count) waiting · 🍅 \(model.pomodoroText)")
                .font(.caption).foregroundStyle(.secondary)
        }
        .padding(12)
        .frame(width: 300)
    }
}
#endif
