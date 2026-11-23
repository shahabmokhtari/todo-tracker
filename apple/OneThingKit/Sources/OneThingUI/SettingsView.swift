#if canImport(SwiftUI)
import SwiftUI
import OneThingKit
#if os(macOS)
import AppKit
#endif

/// Settings: how a phone syncs, appearance, the energy you have in a day, and the focus timer. The same settings as the web app's
/// Appearance and Energy sections; the focus timer's lengths are kept with the board.
public struct SettingsView: View {
    @ObservedObject var model: BoardModel
    /// In the window beside the sidebar, or the Mac's Settings window (no Close button, no navigation of its own).
    var embedded = false
    @AppStorage(AppTheme.storageKey) private var theme = AppTheme.system.rawValue
    @AppStorage(FeedbackShake.storageKey) private var shakeForFeedback = true
    @State private var sendingFeedback = false
    @Environment(\.dismiss) private var dismiss

    public init(model: BoardModel, embedded: Bool = false) {
        self.model = model
        self.embedded = embedded
    }

    public var body: some View {
        if embedded {
            // Section names as written ("Your tasks"), not in capitals.
            form
                .textCase(nil)
                #if os(macOS)
                .formStyle(.grouped)
                .frame(minWidth: 420, minHeight: 420)
                #endif
        } else {
            NavigationStack {
                form.textCase(nil).toolbar { ToolbarItem(placement: .confirmationAction) { Button("Close") { dismiss() } } }
            }
            #if os(macOS)
            .frame(minWidth: 420, minHeight: 520)
            #endif
        }
    }

    private var form: some View {
        Form {
            // iPhone and iPad: syncing on their own, or only on this device (the Mac has its own server).
            if model.phone != nil {
                Section {
                    Text(model.modeDescription).font(.footnote).accessibilityIdentifier("settings-mode")
                } header: {
                    Text("Your tasks")
                }
                if let sync = model.sync { SyncSection(model: model, sync: sync) }
            }

            #if os(macOS)
            if model.usesServer {
                tasksFolderSection
                windowSection
            }
            #endif

            Section {
                Picker("Appearance", selection: Binding(get: { theme }, set: { chosen in
                    theme = chosen
                    model.setTheme(chosen)
                })) {
                    ForEach(AppTheme.allCases) { Text($0.label).tag($0.rawValue) }
                }
                .pickerStyle(.segmented)
            } header: {
                Text("Appearance")
            } footer: {
                Text(model.usesServer ? "Every window follows it: this one, the browser and the sidebar." : "Light, dark, or as your device is.")
            }

            if model.isOn(ExtraSwitches.energy) {
            Section {
                Picker("Energy in a day", selection: Binding(get: { model.board.dailyEnergy }, set: { model.setDailyEnergy($0) })) {
                    ForEach(energyChoices, id: \.self) { Text("\($0) ⚡").tag($0) }
                }
                .accessibilityIdentifier("settings-energy")
            } header: {
                Text("Energy")
            } footer: {
                Text("How much you have in a day, in ⚡. Each task takes ⚡ to ⚡⚡⚡ an hour (set it in its details; not said counts as ⚡). Every app uses this number.")
            }
            }

            // The focus-timer extra switched off: its lengths and breaks go too.
            if model.isOn(ExtraSwitches.focusTimer) {
                focusSection

                Section {
                    Toggle("Full-screen breaks", isOn: $model.fullScreenBreaks)
                } footer: {
                    Text("When a focus session ends, a reminder to rest covers the screen (you can skip it).")
                }
            }

            if model.isOn(ExtraSwitches.feedback) {
            Section {
                Button("Send feedback…") { sendingFeedback = true }
                    .accessibilityIdentifier("settings-feedback")
                #if os(iOS)
                Toggle("Shake to send feedback", isOn: $shakeForFeedback)
                #endif
            } header: {
                Text("Feedback")
            } footer: {
                #if os(iOS)
                Text("Tell us what’s broken or what you’d like. Shake your iPhone or iPad to send a note from anywhere in the app.")
                #else
                Text("Tell us what’s broken or what you’d like (also in Help › Send Feedback…).")
                #endif
            }
            }
        }
        .sheet(isPresented: $sendingFeedback) { FeedbackSheet() }
        .navigationTitle("Settings")
        #if os(iOS)
        .navigationBarTitleDisplayMode(.inline)
        #endif
    }

    #if os(macOS)
    /// Where your tasks are (the web app's footer): the folder, Show in Finder, Open in Obsidian, and files that need
    /// fixing.
    @ViewBuilder private var tasksFolderSection: some View {
        Section {
            if let vault = model.vaultInfo {
                Text(vault.path)
                    .font(.callout.monospaced())
                    .textSelection(.enabled)
                    .accessibilityIdentifier("settings-tasks-folder")
                HStack {
                    Button("Show in Finder") {
                        NSWorkspace.shared.activateFileViewerSelecting([URL(fileURLWithPath: vault.path, isDirectory: true)])
                    }
                    if model.isOn(ExtraSwitches.obsidian), vault.obsidianInstalled, let url = vault.obsidianURL {
                        Button("Open in Obsidian") { NSWorkspace.shared.open(url) }
                    }
                }
                if let problem = vault.problemNotice {
                    Label(problem, systemImage: "exclamationmark.triangle.fill").foregroundStyle(Theme.warnText)
                }
            } else {
                Text("Opening your tasks folder…").foregroundStyle(.secondary)
            }
        } header: {
            Text("Your tasks")
        } footer: {
            Text("Saved as markdown files, synced with your other computers. You can open and edit them in any editor.")
        }
    }

    /// What the web app's Settings has beyond these, each opening its own panel in the One Thing window (signed in as
    /// you, inside this app).
    private var windowSection: some View {
        Section {
            ForEach(WebAppPanel.settingsRows.filter { $0.plugin.map(model.isOn) ?? true }) { panel in
                Button { OneThingWindow.shared.show(panel, model: model) } label: {
                    HStack(spacing: 10) {
                        Image(systemName: panel.symbol).frame(width: 20).foregroundStyle(Theme.accent)
                        VStack(alignment: .leading, spacing: 1) {
                            Text(panel.title).foregroundStyle(.primary)
                            Text(panel.subtitle).font(.caption).foregroundStyle(.secondary)
                        }
                        Spacer(minLength: 0)
                        Image(systemName: "arrow.up.forward.app").foregroundStyle(.tertiary)
                    }
                    .contentShape(Rectangle())
                }
                .buttonStyle(.plain)
                .accessibilityIdentifier("settings-\(panel.rawValue)")
            }
        } header: {
            Text("More settings")
        } footer: {
            Text("These open in the One Thing window, signed in as you (File › Open the Full App).")
        }
    }
    #endif

    private var energyChoices: [Int] {
        Array(Set([4, 6, 8, 10, 12, 14, 16, 20, model.board.dailyEnergy])).sorted()
    }

    @ViewBuilder private var focusSection: some View {
        let settings = model.board.pomodoro.settings
        Section {
            Stepper("Focus: \(settings.focusMinutes) min", value: binding(\.focusMinutes), in: 5...120, step: 5)
                .accessibilityIdentifier("settings-focus")
            Stepper("Short break: \(settings.shortBreakMinutes) min", value: binding(\.shortBreakMinutes), in: 1...30)
            Stepper("Long break: \(settings.longBreakMinutes) min", value: binding(\.longBreakMinutes), in: 5...60, step: 5)
            Stepper("Long break after \(settings.focusesBeforeLongBreak) sessions", value: binding(\.focusesBeforeLongBreak), in: 2...8)
        } header: {
            Text("Focus timer")
        } footer: {
            Text(model.usesServer ? "A new length counts from the next session. Kept with your tasks, so every app uses it." : "A new length counts from the next session.")
        }
    }

    private func binding(_ key: WritableKeyPath<PomodoroSettings, Int>) -> Binding<Int> {
        Binding(get: { model.board.pomodoro.settings[keyPath: key] }, set: { value in
            var settings = model.board.pomodoro.settings
            settings[keyPath: key] = value
            model.setPomodoroSettings(settings)
        })
    }
}
#endif
