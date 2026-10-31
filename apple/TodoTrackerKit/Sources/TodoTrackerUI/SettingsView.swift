#if canImport(SwiftUI)
import SwiftUI
import TodoTrackerKit

/// Settings: the computer a phone uses, appearance, the energy you have in a day, and the focus timer. The same settings as the web app's
/// Appearance and Energy sections; the focus timer's lengths are kept with the board.
public struct SettingsView: View {
    @ObservedObject var model: BoardModel
    /// In the window beside the sidebar, or the Mac's Settings window (no Close button, no navigation of its own).
    var embedded = false
    @AppStorage(AppTheme.storageKey) private var theme = AppTheme.system.rawValue
    @Environment(\.dismiss) private var dismiss

    public init(model: BoardModel, embedded: Bool = false) {
        self.model = model
        self.embedded = embedded
    }

    public var body: some View {
        if embedded {
            form
                #if os(macOS)
                .formStyle(.grouped)
                .frame(minWidth: 420, minHeight: 420)
                #endif
        } else {
            NavigationStack {
                form.toolbar { ToolbarItem(placement: .confirmationAction) { Button("Close") { dismiss() } } }
            }
            #if os(macOS)
            .frame(minWidth: 420, minHeight: 520)
            #endif
        }
    }

    private var form: some View {
        Form {
            // iPhone and iPad: one of three — a computer's tasks, syncing on its own, or only on this device (the Mac
            // has its own server).
            if model.phone != nil {
                Section {
                    Text(model.modeDescription).font(.footnote).accessibilityIdentifier("settings-mode")
                } header: {
                    Text("Your tasks")
                }
                ComputerSection(model: model)
                if let sync = model.sync { SyncSection(model: model, sync: sync) }
            }

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

            focusSection

            Section {
                Toggle("Full-screen breaks", isOn: $model.fullScreenBreaks)
            } footer: {
                Text("When a focus session ends, a reminder to rest covers the screen (you can skip it).")
            }
        }
        .navigationTitle("Settings")
        #if os(iOS)
        .navigationBarTitleDisplayMode(.inline)
        #endif
    }

    private var energyChoices: [Int] {
        Array(Set([4, 6, 8, 10, 12, 14, 16, 20, model.board.dailyEnergy])).sorted()
    }

    @ViewBuilder private var focusSection: some View {
        let settings = model.board.pomodoro.settings
        Section {
            if model.usesServer {
                LabeledContent("Focus", value: "\(settings.focusMinutes) min")
                LabeledContent("Short break", value: "\(settings.shortBreakMinutes) min")
                LabeledContent("Long break", value: "\(settings.longBreakMinutes) min")
                LabeledContent("Long break after", value: "\(settings.focusesBeforeLongBreak) sessions")
            } else {
                Stepper("Focus: \(settings.focusMinutes) min", value: binding(\.focusMinutes), in: 5...120, step: 5)
                    .accessibilityIdentifier("settings-focus")
                Stepper("Short break: \(settings.shortBreakMinutes) min", value: binding(\.shortBreakMinutes), in: 1...30)
                Stepper("Long break: \(settings.longBreakMinutes) min", value: binding(\.longBreakMinutes), in: 5...60, step: 5)
                Stepper("Long break after \(settings.focusesBeforeLongBreak) sessions", value: binding(\.focusesBeforeLongBreak), in: 2...8)
            }
        } header: {
            Text("Focus timer")
        } footer: {
            Text(model.usesServer ? "These come from your tasks folder." : "A new length counts from the next session.")
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
