import Foundation

public enum PomodoroPhase: String, Codable, Sendable {
    case idle, focus, shortBreak, longBreak
}

public enum PomodoroEventKind: String, Sendable {
    case focusCompleted, breakCompleted
}

public struct PomodoroEvent: Equatable, Sendable {
    public let kind: PomodoroEventKind
    public let itemId: UUID?
    public let at: Date
}

public struct PomodoroSettings: Codable, Equatable, Sendable {
    public var focusMinutes = 25
    public var shortBreakMinutes = 5
    public var longBreakMinutes = 15
    public var focusesBeforeLongBreak = 4

    public init(focusMinutes: Int = 25, shortBreakMinutes: Int = 5, longBreakMinutes: Int = 15, focusesBeforeLongBreak: Int = 4) {
        self.focusMinutes = max(1, focusMinutes)
        self.shortBreakMinutes = max(1, shortBreakMinutes)
        self.longBreakMinutes = max(1, longBreakMinutes)
        self.focusesBeforeLongBreak = max(1, focusesBeforeLongBreak)
    }

    public func duration(of phase: PomodoroPhase) -> TimeInterval {
        switch phase {
        case .shortBreak: return TimeInterval(shortBreakMinutes * 60)
        case .longBreak: return TimeInterval(longBreakMinutes * 60)
        default: return TimeInterval(focusMinutes * 60)
        }
    }
}

/// Pomodoro state machine. Breaks start automatically after focus; a new focus block needs an explicit start.
public struct PomodoroTimer: Equatable, Sendable {
    public var settings = PomodoroSettings()
    public private(set) var phase: PomodoroPhase = .idle
    public private(set) var endsAt: Date?
    public private(set) var pausedRemaining: TimeInterval?
    public private(set) var itemId: UUID?
    public private(set) var completedFocusCount = 0

    public init(settings: PomodoroSettings = PomodoroSettings()) {
        self.settings = settings
    }

    public var isRunning: Bool { endsAt != nil }

    public func remaining(_ now: Date) -> TimeInterval {
        if let endsAt { return max(0, endsAt.timeIntervalSince(now)) }
        return pausedRemaining ?? settings.duration(of: phase)
    }

    public mutating func startFocus(now: Date, itemId: UUID? = nil) {
        phase = .focus
        self.itemId = itemId
        pausedRemaining = nil
        endsAt = now.addingTimeInterval(settings.duration(of: .focus))
    }

    public mutating func pause(now: Date) {
        guard endsAt != nil, phase != .idle else { return }
        pausedRemaining = remaining(now)
        endsAt = nil
    }

    public mutating func resume(now: Date) {
        guard let left = pausedRemaining, phase != .idle else { return }
        endsAt = now.addingTimeInterval(left)
        pausedRemaining = nil
    }

    public mutating func reset() {
        phase = .idle
        endsAt = nil
        pausedRemaining = nil
        itemId = nil
        completedFocusCount = 0
    }

    public mutating func skip(now: Date) {
        if phase == .focus {
            beginBreak(.shortBreak, from: now)
        } else if phase != .idle {
            goIdle()
        }
    }

    /// Advances at most one transition; call repeatedly to catch up after sleep.
    public mutating func tick(now: Date) -> PomodoroEvent? {
        guard let end = endsAt, now >= end else { return nil }
        if phase == .focus {
            completedFocusCount += 1
            let item = itemId
            beginBreak(completedFocusCount % settings.focusesBeforeLongBreak == 0 ? .longBreak : .shortBreak, from: end)
            return PomodoroEvent(kind: .focusCompleted, itemId: item, at: end)
        }
        let item = itemId
        goIdle()
        return PomodoroEvent(kind: .breakCompleted, itemId: item, at: end)
    }

    mutating func detachItem() { itemId = nil }

    mutating func restore(phase: PomodoroPhase, endsAt: Date?, pausedRemaining: TimeInterval?, itemId: UUID?, completedFocusCount: Int) {
        self.phase = phase
        self.endsAt = phase == .idle ? nil : endsAt
        self.pausedRemaining = (phase == .idle || endsAt != nil) ? nil : pausedRemaining
        self.itemId = itemId
        self.completedFocusCount = max(0, completedFocusCount)
    }

    private mutating func beginBreak(_ phase: PomodoroPhase, from: Date) {
        self.phase = phase
        pausedRemaining = nil
        endsAt = from.addingTimeInterval(settings.duration(of: phase))
    }

    private mutating func goIdle() {
        phase = .idle
        endsAt = nil
        pausedRemaining = nil
    }
}
