import Foundation
import TodoTrackerKit

/// Presentation helpers shared by the SwiftUI views. Same wording and tones as the web dashboard (format.js)
/// and the Windows sidebar, so every platform reads the same.
public struct Chip: Hashable, Sendable {
    public enum Tone: String, Sendable { case step, info, warn, danger, muted }

    public let text: String
    public let tone: Tone

    public init(_ text: String, _ tone: Tone) {
        self.text = text
        self.tone = tone
    }
}

public enum Presentation {
    public static func chips(for entry: AgendaEntry, now: Date) -> [Chip] {
        var chips: [Chip] = []
        if let step = entry.stepLabel { chips.append(Chip(step, .step)) }
        if entry.state == .waiting, let wake = entry.wakeAt { chips.append(Chip("back \(RelativeTime.format(wake, now: now))", .info)) }
        if let deadline = entry.item.deadline {
            chips.append(entry.isOverdue
                ? Chip("overdue \(RelativeTime.format(deadline, now: now))", .danger)
                : Chip("due \(RelativeTime.format(deadline, now: now))", .warn))
        }
        let notes = entry.item.notes.count
        if notes > 0 { chips.append(Chip(notes == 1 ? "1 note" : "\(notes) notes", .muted)) }
        return chips
    }

    public static func summary(_ dashboard: Dashboard) -> String {
        let now = dashboard.now.count
        let reminders = dashboard.now.filter(\.needsAttention).count
        let waiting = dashboard.waiting.count
        if now == 0 && waiting == 0 { return "All clear. Nothing needs you right now." }
        var parts = [now > 0 ? "\(now) thing\(now > 1 ? "s" : "") to do now" : "Nothing to do right now"]
        if reminders > 0 { parts.append("\(reminders) reminder\(reminders > 1 ? "s" : "")") }
        if waiting > 0 { parts.append("\(waiting) waiting") }
        return parts.joined(separator: " · ") + "."
    }

    public static func greeting(_ date: Date, calendar: Calendar = .current) -> String {
        let hour = calendar.component(.hour, from: date)
        if hour < 4 { return "Still up?" }
        if hour < 12 { return "Good morning" }
        if hour < 18 { return "Good afternoon" }
        return "Good evening"
    }

    /// Elapsed share (0...1) of the current focus-timer phase; 0 when idle.
    public static func pomodoroFraction(_ timer: PomodoroTimer, now: Date) -> Double {
        guard timer.phase != .idle else { return 0 }
        let total = timer.settings.duration(of: timer.phase)
        guard total > 0 else { return 0 }
        return min(1, max(0, 1 - timer.remaining(now) / total))
    }

    /// Workstreams are tasks with subtasks; single tasks already appear in Do now / Waiting.
    public static func workstreams(_ dashboard: Dashboard) -> [OverviewEntry] {
        dashboard.overview.filter { !$0.item.children.isEmpty }
    }
}
