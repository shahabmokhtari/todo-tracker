import SwiftUI
import TodoTrackerKit
import TodoTrackerUI
#if os(macOS)
import AppKit
import Combine
#endif

@main
struct TodoTrackerApp: App {
    @StateObject private var model = BoardModel.live()
    #if os(macOS)
    @State private var breakWindows: BreakWindows?
    #endif

    var body: some Scene {
        #if os(macOS)
        WindowGroup("Todo Tracker") {
            dashboard.frame(minWidth: 320, idealWidth: 380, minHeight: 480)
                .onAppear { if breakWindows == nil { breakWindows = BreakWindows(model: model) } }
        }
        .defaultSize(width: 380, height: 820)
        .windowResizability(.contentMinSize)

        // Always-available glance from the menu bar: what to do now and quick capture.
        MenuBarExtra {
            MenuBarGlance(model: model)
        } label: {
            Label("\(model.dashboard.now.count)", systemImage: model.dashboard.now.contains(where: \.needsAttention) ? "bell.badge" : "checklist")
                // The menu bar item is always there (even with no window open): breaks start with it.
                .onAppear { if breakWindows == nil { breakWindows = BreakWindows(model: model) } }
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
    @State private var exported: String?
    @State private var exporting = false

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
            Divider()
            Toggle("Full-screen breaks", isOn: $model.fullScreenBreaks)
            HStack {
                Button(exporting ? "Copying…" : "Copy to Apple Notes") {
                    exporting = true
                    exported = nil
                    Task {
                        exported = await AppleNotesWriter.export(model)
                        exporting = false
                    }
                }
                .disabled(exporting)
                .help("Puts this group's tasks in a note in the \"\(AppleNotesExport.folder)\" folder (updated each time)")
                if let exported { Text(exported).font(.caption).foregroundStyle(.secondary) }
            }
        }
        .padding(12)
        .frame(width: 300)
    }
}

/// Writes the group on screen to Apple Notes with osascript (values passed as arguments, never in the script). Runs off
/// the main thread: the first time, Notes may start and macOS asks for permission.
@MainActor
enum AppleNotesWriter {
    static func export(_ model: BoardModel) async -> String {
        let group = model.groups.first { $0.id == model.selectedGroupId }
        let roots = model.board.items.filter { group == nil || $0.groupId == group?.id }
        let title = "Todo Tracker – \(group?.name ?? "All tasks")"
        let arguments = AppleNotesExport.arguments(title: title, body: AppleNotesExport.html(roots, title: title))
        return await Task.detached { run(arguments) }.value
    }

    nonisolated private static func run(_ arguments: [String]) -> String {
        let process = Process()
        process.executableURL = URL(fileURLWithPath: "/usr/bin/osascript")
        process.arguments = arguments
        let errors = Pipe()
        process.standardError = errors
        process.standardOutput = Pipe()
        do {
            try process.run()
        } catch {
            return "Couldn't run osascript"
        }
        let message = String(decoding: errors.fileHandleForReading.readDataToEndOfFile(), as: UTF8.self).trimmingCharacters(in: .whitespacesAndNewlines)
        process.waitUntilExit()
        if process.terminationStatus == 0 { return "Saved in Notes" }
        // -1743: not allowed to control Notes.
        return message.contains("-1743") ? "Allow it in System Settings › Privacy & Security › Automation" : (message.isEmpty ? "Notes didn't save it" : message)
    }
}

/// A borderless window can't take the keyboard by default; the break's does (Esc = "I'm taking it").
final class BreakWindow: NSWindow {
    override var canBecomeKey: Bool { true }
    override var canBecomeMain: Bool { true }
}

/// The full-screen break on every screen: translucent windows above everything (full-screen apps and every Space
/// included), shown and hidden with the model's break, rebuilt when screens are plugged in or out.
@MainActor
final class BreakWindows {
    private var windows: [NSWindow] = []
    private var cancellables = Set<AnyCancellable>()

    init(model: BoardModel) {
        // Any change (the clock, "I'm taking it", the setting): checked right after it lands.
        model.objectWillChange
            .receive(on: DispatchQueue.main)
            .sink { [weak self, weak model] _ in
                guard let self, let model else { return }
                self.sync(model)
            }
            .store(in: &cancellables)
        NotificationCenter.default.publisher(for: NSApplication.didChangeScreenParametersNotification)
            .receive(on: DispatchQueue.main)
            .sink { [weak self, weak model] _ in
                guard let self, let model else { return }
                self.close()
                self.sync(model)
            }
            .store(in: &cancellables)
    }

    private func sync(_ model: BoardModel) {
        if model.breakPrompt != nil {
            if windows.isEmpty { open(model) }
        } else if !windows.isEmpty {
            close()
        }
    }

    private func open(_ model: BoardModel) {
        for screen in NSScreen.screens {
            let window = BreakWindow(contentRect: screen.frame, styleMask: [.borderless], backing: .buffered, defer: false, screen: screen)
            window.level = .screenSaver
            window.isOpaque = false
            window.backgroundColor = .clear
            window.collectionBehavior = [.canJoinAllSpaces, .fullScreenAuxiliary, .stationary]
            window.isReleasedWhenClosed = false
            window.contentView = NSHostingView(rootView: BreakView(model: model))
            window.setFrame(screen.frame, display: true)
            window.orderFrontRegardless()
            windows.append(window)
        }
        NSApp.activate(ignoringOtherApps: true)
        windows.first?.makeKey()
    }

    private func close() {
        windows.forEach { $0.orderOut(nil) }
        windows.removeAll()
    }
}
#endif
