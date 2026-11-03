import Foundation

/// A break due now: when it's over, whether it's the long one, and its tip.
public struct BreakPrompt: Equatable, Sendable {
    public let until: Date
    public let isLong: Bool
    /// "Break's over": the break ran out (`until` is when); Start next focus / Not now instead of the clock.
    public var isOver = false

    public init(until: Date, isLong: Bool, isOver: Bool = false) {
        self.until = until
        self.isLong = isLong
        self.isOver = isOver
    }
    public var tip: String { isOver ? "Ready for the next one? One small step is enough." : Breaks.tip(for: until) }

    public var title: String { isOver ? "Break’s over" : isLong ? "Time for a longer break" : "Time for a break" }
}

/// The full-screen break after a focus session: the same rules as the web app and Windows
/// (tests/fixtures/breaks.json checks all three).
public enum Breaks {
    /// Same tips, in the same order, as the web app.
    public static let tips = [
        "Stand up and stretch for a minute.",
        "Look at something far away: rest your eyes.",
        "Drink a glass of water.",
        "Take five slow breaths.",
        "Walk around the room.",
        "Roll your shoulders and unclench your jaw.",
        "Step outside or open a window.",
    ]

    /// Clicks and keys this soon after the screen appears are typing still going on, not an answer.
    public static let settleTime: TimeInterval = 0.8

    /// The break due at `now`: during a running break, and the moment a running focus session ends.
    public static func due(phase: PomodoroPhase, running: Bool, endsAt: Date?, completedFocusCount: Int, settings: PomodoroSettings, now: Date) -> BreakPrompt? {
        guard running, let end = endsAt else { return nil }
        switch phase {
        case .shortBreak, .longBreak:
            return end > now ? BreakPrompt(until: end, isLong: phase == .longBreak) : nil
        case .focus where end <= now:
            let isLong = (completedFocusCount + 1) % max(1, settings.focusesBeforeLongBreak) == 0
            let until = end.addingTimeInterval(settings.duration(of: isLong ? .longBreak : .shortBreak))
            return until > now ? BreakPrompt(until: until, isLong: isLong) : nil
        default:
            return nil
        }
    }

    /// How long "Break's over" keeps asking after a break ran out (later, the timer's own Start does).
    public static let overFor: TimeInterval = 15 * 60

    /// Whether to ask "Break's over": the break this app saw (`seen`, its end) ran out by itself, not long ago, and it
    /// wasn't answered with Not now. The timer says when a break ran out (never for a skipped or paused one); until it
    /// has moved on, a running break at its end counts too. Same rule as the web app and Windows.
    public static func over(phase: PomodoroPhase, running: Bool, endsAt: Date?, breakEndedAt: Date?, now: Date, seen: Date?, dismissedOver: Date?) -> Bool {
        guard let seen, seen != dismissedOver, now >= seen, now.timeIntervalSince(seen) < overFor else { return false }
        if phase == .idle { return breakEndedAt == seen }
        guard running, let end = endsAt else { return false } // paused
        // The break itself, run out; or the session before it (both ran out before the timer moved on). Not a new session.
        return phase == .focus ? end < seen : end == seen
    }

    public static func due(_ timer: PomodoroTimer, now: Date) -> BreakPrompt? {
        due(phase: timer.phase, running: timer.isRunning, endsAt: timer.endsAt, completedFocusCount: timer.completedFocusCount, settings: timer.settings, now: now)
    }

    /// The web app's tipFor: a hash of the break's end in milliseconds.
    public static func tip(for until: Date) -> String {
        let ms = Int64((until.timeIntervalSince1970 * 1000).rounded())
        var hash: Int32 = 7
        for c in String(ms).utf8 {
            hash = hash &* 31 &+ Int32(c)
        }
        return tips[Int(abs(Int64(hash)) % Int64(tips.count))]
    }

    /// "4:59": minutes and seconds left.
    public static func clock(until: Date, now: Date) -> String {
        let seconds = max(0, Int((until.timeIntervalSince(now)).rounded(.up)))
        return "\(seconds / 60):" + String(format: "%02d", seconds % 60)
    }
}
