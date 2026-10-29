import Foundation
import TodoTrackerKit

/// Presentation helpers shared by the SwiftUI views. Same wording and tones as the web dashboard (format.js)
/// and the Windows sidebar, so every platform reads the same.
public struct Chip: Hashable, Sendable {
    public enum Tone: String, Sendable { case step, info, warn, danger, muted, energy }

    public let text: String
    public let tone: Tone

    public init(_ text: String, _ tone: Tone) {
        self.text = text
        self.tone = tone
    }
}

/// A note as a card shows it.
public struct NoteLine: Identifiable, Equatable, Sendable {
    public let id: UUID
    public let text: String
    public let meta: String
}

/// Why a task waits, at the top of its details (the web app's waiting banner).
public struct WaitingLine {
    public let text: String
    /// Its own snooze or wait: Do now brings it back.
    public let canBringBack: Bool
    /// The nearest parent whose snooze or wait also keeps it waiting (open that one to change it).
    public let heldBy: WorkItem?
}

/// A subtask in the details' tree, `depth` levels under the task shown (0: its own subtasks).
public struct SubtaskRow: Identifiable {
    public var id: UUID { item.id }
    public let item: WorkItem
    public let depth: Int
}

public enum Presentation {
    /// Why a waiting task waits ("Waits for “Sign-off” and snoozed until Fri 9:00"), or nil when it doesn't.
    public static func waiting(_ item: WorkItem, now: Date, timeZone: TimeZone = .current) -> WaitingLine? {
        guard !item.isDone, Agenda.state(of: item, now: now) == .waiting else { return nil }
        let lineage = [item] + item.ancestors
        let waitsFor = lineage.lazy.compactMap(\.waitingFor).first
        let wake = lineage.compactMap(\.nextActionAt).filter { $0 > now }.max()
        guard waitsFor != nil || wake != nil else { return nil }
        let why = [waitsFor.map { "waits for “\($0.title)”" }, wake.map { "snoozed until \(Snooze.describe($0, now: now, timeZone: timeZone))" }]
            .compactMap { $0 }.joined(separator: " and ")
        let own = item.afterId != nil || (item.nextActionAt.map { $0 > now } ?? false)
        let heldBy = item.ancestors.first { ($0.nextActionAt.map { $0 > now } ?? false) || $0.waitingFor != nil }
        let text: String
        if !own, let heldBy {
            text = "Its parent “\(heldBy.title)” \(why)"
        } else {
            text = why.prefix(1).uppercased() + why.dropFirst() + (heldBy.map { "; its parent “\($0.title)” waits too" } ?? "")
        }
        return WaitingLine(text: text, canBringBack: own, heldBy: heldBy)
    }

    /// A task's subtasks at every depth, in order.
    public static func subtaskRows(_ item: WorkItem) -> [SubtaskRow] {
        func rows(_ children: [WorkItem], _ depth: Int) -> [SubtaskRow] {
            children.flatMap { [SubtaskRow(item: $0, depth: depth)] + rows($0.children, depth + 1) }
        }
        return rows(item.children, 0)
    }

    /// Hours offered between steps (the one set now among them, when it's another).
    public static func stepDelayChoices(current: TimeInterval?) -> [Double] {
        var hours: [Double] = [0, 1, 2, 4, 8, 12, 24, 48, 72, 168]
        if let current, current > 0, !hours.contains(current / 3600) { hours.append(current / 3600) }
        return hours.sorted()
    }

    /// "No wait", "1 hour", "1.5 hours", "2 days".
    public static func hoursLabel(_ hours: Double) -> String {
        if hours <= 0 { return "No wait" }
        if hours >= 48, hours.truncatingRemainder(dividingBy: 24) == 0 { return "\(Int(hours / 24)) days" }
        if hours == 1 { return "1 hour" }
        return hours == hours.rounded() ? "\(Int(hours)) hours" : String(format: "%.1f hours", hours)
    }

    /// A task's title with its icon (an emoji, set in the other apps) in front.
    public static func titleWithIcon(_ item: WorkItem) -> String {
        let title = item.repeatRule == nil ? item.title : "\(item.title) 🔁"
        guard let icon = item.icon, !icon.isEmpty else { return title }
        return "\(icon) \(title)"
    }

    /// `notes`: a "2 notes" chip (cards that show their notes themselves leave it out).
    public static func chips(for entry: AgendaEntry, now: Date, notes showNotes: Bool = true) -> [Chip] {
        var chips: [Chip] = []
        if let step = entry.stepLabel { chips.append(Chip(step, .step)) }
        if entry.state == .waiting, let wake = entry.wakeAt { chips.append(Chip("back \(RelativeTime.format(wake, now: now))", .info)) }
        if let other = entry.waitingFor { chips.append(Chip("after “\(other.title)”", .info)) }
        if let deadline = entry.item.deadline {
            chips.append(entry.isOverdue
                ? Chip("overdue \(RelativeTime.format(deadline, now: now))", .danger)
                : Chip("due \(RelativeTime.format(deadline, now: now))", .warn))
        }
        if let energy = entry.item.energy { chips.append(Chip(String(repeating: "⚡", count: energy), .energy)) }
        let notes = entry.item.notes.count
        if showNotes && notes > 0 { chips.append(Chip(notes == 1 ? "1 note" : "\(notes) notes", .muted)) }
        return chips
    }

    /// A card's notes, newest first, with who wrote each and when ("You · 2h ago").
    public static func notes(_ item: WorkItem, now: Date) -> [NoteLine] {
        item.notes.sorted { $0.at > $1.at }.map { NoteLine(id: $0.id, text: $0.text, meta: "\($0.author.displayName) · \(RelativeTime.format($0.at, now: now))") }
    }

    /// "2 notes ▾" to show them, "Hide notes ▴" when they're shown (the same words as the Windows sidebar and the web).
    public static func notesToggle(count: Int, open: Bool) -> String {
        open ? "Hide notes ▴" : count == 1 ? "1 note ▾" : "\(count) notes ▾"
    }

    /// Today's energy ("⚡ 4.5 of 10 planned today."), once something is planned today (the same rule in every app).
    public static func energyLine(_ board: TaskBoard, now: Date, timeZone: TimeZone = .current) -> String? {
        let planned = Energy.planned(on: now, in: timeZone, board: board)
        guard planned > 0 else { return nil }
        return Energy.line(planned: planned, perDay: board.dailyEnergy)
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

    /// A running timer's clock: "4:07", "1:02:09" (same as the web app).
    public static func clock(_ seconds: TimeInterval) -> String {
        let s = max(0, Int(seconds))
        let (h, m, sec) = (s / 3600, (s % 3600) / 60, s % 60)
        return h > 0 ? "\(h):" + String(format: "%02d:%02d", m, sec) : "\(m):" + String(format: "%02d", sec)
    }

    /// Time spent, as people read it: "45 s", "25 min", "1 h 05 min", "12 h" (same as the web app's duration).
    public static func duration(_ seconds: TimeInterval) -> String {
        let s = max(0, Int(seconds.rounded()))
        if s < 60 { return "\(s) s" }
        let minutes = Int((Double(s) / 60).rounded())
        if minutes < 60 { return "\(minutes) min" }
        let (h, m) = (minutes / 60, minutes % 60)
        return m == 0 ? "\(h) h" : "\(h) h " + String(format: "%02d", m) + " min"
    }
}
