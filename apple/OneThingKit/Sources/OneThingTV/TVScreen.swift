import Foundation
import OneThingKit

/// When a task is due, as the TV says it ("Due in 3h", "Overdue · 2h ago"); `urgent` shows it in the warning color.
public struct TVWhen: Equatable, Sendable {
    public let text: String
    public let urgent: Bool

    public init(text: String, urgent: Bool) {
        self.text = text
        self.urgent = urgent
    }

    /// Nil for a task without a deadline (or one that's done).
    public static func of(_ item: WorkItem, now: Date) -> TVWhen? {
        guard let due = item.deadline, !item.isDone else { return nil }
        let when = RelativeTime.format(due, now: now)
        return due < now ? TVWhen(text: "Overdue · \(when)", urgent: true) : TVWhen(text: "Due \(when)", urgent: due.timeIntervalSince(now) < 3600)
    }
}

/// A task on a card (Up next, the Board): its title, group and when it's due.
public struct TVCard: Identifiable, Equatable, Sendable {
    public let id: UUID
    public let title: String
    /// The group's name and color (`#rrggbb`, nil: none set).
    public let group: String?
    public let groupColor: String?
    public let when: TVWhen?
    /// "1 of 3 done" for a task with steps.
    public let progress: String?
    /// The path to a subtask ("Ship v2.4 release"), nil for a top-level task.
    public let parentTitle: String?
    public let isDone: Bool

    init(_ item: WorkItem, board: TaskBoard, now: Date) {
        id = item.id
        title = TVCard.title(item)
        let group = board.groups.first { $0.id == item.groupId }
        self.group = group?.name
        groupColor = item.root.color ?? group?.color
        when = TVWhen.of(item, now: now)
        let leaves = Array(item.selfAndDescendants.dropFirst())
        progress = leaves.isEmpty ? nil : "\(leaves.filter(\.isDone).count) of \(leaves.count) done"
        parentTitle = item.parent?.title
        isDone = item.isDone
    }

    /// The title with its icon in front (an emoji set in the other apps).
    static func title(_ item: WorkItem) -> String {
        guard let icon = item.icon, !icon.isEmpty else { return item.title }
        return "\(icon) \(item.title)"
    }
}

/// The task to do now, as big as the TV can make it.
public struct TVHero: Identifiable, Equatable, Sendable {
    public var id: UUID { card.id }
    public let card: TVCard
    /// A reminder that's gone off ("Review closes at 5 pm"), nil when none.
    public let reminder: String?
    /// "Step 2 of 3" for a step of a task done in order.
    public let step: String?

    public var title: String { card.title }
    public var group: String? { card.group }
    public var groupColor: String? { card.groupColor }
    public var when: TVWhen? { card.when }
}

/// The focus timer (🍅), shown big while it runs or is paused.
public struct TVFocusClock: Equatable, Sendable {
    /// "Focus", "Short break", "Long break".
    public let label: String
    /// Time left, "24:59".
    public let time: String
    /// How much of this phase has gone (0...1).
    public let fraction: Double
    public let isPaused: Bool
    public let isBreak: Bool
    /// The task this session is for.
    public let taskTitle: String?
}

/// The time tracker (⏱) running on a task.
public struct TVTracker: Equatable, Sendable {
    public let title: String
    /// Time so far, "40:05" or "1:02:09".
    public let elapsed: String
}

/// Everything the Now screen shows: the one thing, the timers, Up next and what's done today.
public struct TVScreen: Equatable, Sendable {
    public let hero: TVHero?
    public let upNext: [TVCard]
    public let doneToday: Int
    /// What got done today, newest first.
    public let done: [TVCard]
    public var doneTitles: [String] { done.map(\.title) }
    public let cheer: String?
    public let focus: TVFocusClock?
    public let tracker: TVTracker?
    /// Shown when there's nothing to do now.
    public let emptyMessage: String
    /// "2 waiting" and the like, under an empty screen.
    public let waitingCount: Int

    public static let defaultUpNext = 5

    public static func build(_ board: TaskBoard, now: Date, timeZone: TimeZone = .current, upNextLimit: Int = defaultUpNext) -> TVScreen {
        let dashboard = Agenda.build(board, now: now)
        let hero = dashboard.focus.map { entry in
            TVHero(card: TVCard(entry.item, board: board, now: now),
                   reminder: entry.needsAttention ? entry.dueReminder?.message : nil,
                   step: entry.stepLabel)
        }
        let next = dashboard.now.filter { $0.item.id != hero?.id }.prefix(max(0, upNextLimit)).map { TVCard($0.item, board: board, now: now) }
        let done = Agenda.doneToday(board, now: now, timeZone: timeZone)
        return TVScreen(hero: hero, upNext: Array(next), doneToday: done.count, done: done.map { TVCard($0, board: board, now: now) },
                        cheer: Agenda.cheer(done.count), focus: focusClock(board, now: now), tracker: tracker(board, now: now),
                        emptyMessage: "Nothing needs you right now. Enjoy it.", waitingCount: dashboard.waiting.count)
    }

    static func focusClock(_ board: TaskBoard, now: Date) -> TVFocusClock? {
        let timer = board.pomodoro
        guard timer.phase != .idle, timer.isRunning || timer.pausedRemaining != nil else { return nil }
        let total = timer.settings.duration(of: timer.phase)
        let left = timer.remaining(now)
        let seconds = Int(left.rounded(.up))
        let label: String
        switch timer.phase {
        case .focus: label = "Focus"
        case .shortBreak: label = "Short break"
        case .longBreak: label = "Long break"
        case .idle: label = ""
        }
        return TVFocusClock(label: label, time: String(format: "%02d:%02d", seconds / 60, seconds % 60),
                            fraction: total > 0 ? min(1, max(0, 1 - left / total)) : 0,
                            isPaused: !timer.isRunning, isBreak: timer.phase != .focus,
                            taskTitle: timer.itemId.flatMap { board.find($0) }.map { TVCard.title($0) })
    }

    static func tracker(_ board: TaskBoard, now: Date) -> TVTracker? {
        guard let running = board.runningTimer(now) else { return nil }
        let s = max(0, Int(now.timeIntervalSince(running.entry.start)))
        let (h, m, sec) = (s / 3600, (s % 3600) / 60, s % 60)
        let clock = h > 0 ? "\(h):" + String(format: "%02d:%02d", m, sec) : "\(m):" + String(format: "%02d", sec)
        return TVTracker(title: TVCard.title(running.item), elapsed: clock)
    }
}

/// What the Siri Remote's Play/Pause does to the focus timer.
public enum TVRemoteAction: Equatable, Sendable {
    /// A focus session on this task (nil: on its own).
    case start(UUID?)
    case pause
    case resume
}

public enum TVRemote {
    /// Off: a session starts on the task shown. Running: it pauses. Paused: it goes on.
    public static func playPause(_ timer: PomodoroTimer, hero: UUID?) -> TVRemoteAction {
        if timer.isRunning { return .pause }
        if timer.phase != .idle, timer.pausedRemaining != nil { return .resume }
        return .start(hero)
    }
}

/// What the TV asks before marking a task done.
public struct TVDoneQuestion: Equatable, Sendable {
    public let title: String
    public let message: String
    public let button: String
}

public enum TVCompletion {
    /// A task with open subtasks says they're marked done with it (as on the iPhone); any other task, that it moves on.
    public static func question(_ item: WorkItem) -> TVDoneQuestion {
        let open = item.children.filter { !$0.isDone }.count
        guard open > 0 else {
            return TVDoneQuestion(title: "Mark “\(item.title)” done?", message: "It moves to Done, and the next thing comes up.", button: "Mark done")
        }
        let which = open == 1 ? "1 open subtask. It’s" : "\(open) open subtasks. They’re"
        return TVDoneQuestion(title: "Also mark the open subtasks as done?", message: "“\(item.title)” has \(which) marked done with it.", button: "Mark all done")
    }
}

/// A column of the Board on the TV (read only).
public struct TVColumn: Identifiable, Equatable, Sendable {
    public var id: String { title }
    public let title: String
    public let cards: [TVCard]
}

public enum TVBoard {
    /// Inbox, Next, Doing and Done (the last two weeks), every group together, in the order the other apps show them.
    public static func columns(_ board: TaskBoard, now: Date) -> [TVColumn] {
        let columns = BoardColumns.of(board.items, now: now)
        let order: [(BoardColumn, String)] = [(.inbox, "Inbox"), (.next, "Next"), (.doing, "Doing"), (.done, "Done")]
        return order.map { column, title in TVColumn(title: title, cards: (columns[column] ?? []).map { TVCard($0, board: board, now: now) }) }
    }
}

/// A task's details on the TV: what it is, its steps and its notes.
public struct TVDetails: Equatable, Sendable {
    public struct Step: Identifiable, Equatable, Sendable {
        public let id: UUID
        public let title: String
        public let isDone: Bool
        public let depth: Int
    }

    public struct NoteLine: Identifiable, Equatable, Sendable {
        public let id: UUID
        public let text: String
        /// Who wrote it and when ("You · 2h ago").
        public let meta: String
    }

    public let card: TVCard
    public let details: String?
    public let steps: [Step]
    public let notes: [NoteLine]
    public let reminders: [String]

    public init(_ item: WorkItem, board: TaskBoard, now: Date) {
        card = TVCard(item, board: board, now: now)
        let text = item.details?.trimmingCharacters(in: .whitespacesAndNewlines)
        details = text?.isEmpty == false ? text : nil
        func rows(_ children: [WorkItem], _ depth: Int) -> [Step] {
            children.flatMap { [Step(id: $0.id, title: TVCard.title($0), isDone: $0.isDone, depth: depth)] + rows($0.children, depth + 1) }
        }
        steps = rows(item.children, 0)
        notes = item.notes.sorted { $0.at > $1.at }
            .map { NoteLine(id: $0.id, text: $0.text, meta: "\($0.author.displayName) · \(RelativeTime.format($0.at, now: now))") }
        reminders = item.reminders.filter(\.isPending).sorted { $0.dueAt < $1.dueAt }
            .map { "\($0.message) · \(RelativeTime.format($0.dueAt, now: now))" }
    }
}

/// The party on screen when a task gets done.
public enum TVCelebration {
    /// Only when the count went up while the screen was showing (not at launch, not when a task was reopened).
    public static func shouldCelebrate(from previous: Int?, to current: Int) -> Bool {
        guard let previous else { return false }
        return current > previous
    }
}
