import Foundation

public struct AgendaEntry: Identifiable {
    public var id: UUID { item.id }
    public let item: WorkItem
    public let state: ItemState
    public let effectivePriority: Priority
    public let needsAttention: Bool
    public let dueReminder: Reminder?
    public let wakeAt: Date?
    public let isOverdue: Bool
    public let breadcrumb: [String]

    /// "Step 2 of 10" for steps of a sequential parent.
    public var stepLabel: String? {
        guard let parent = item.parent, parent.sequential, let index = parent.children.firstIndex(where: { $0 === item }) else { return nil }
        return "Step \(index + 1) of \(parent.children.count)"
    }
}

public struct OverviewEntry: Identifiable {
    public var id: UUID { item.id }
    public let item: WorkItem
    public let doneLeaves: Int
    public let totalLeaves: Int
    public let actionableCount: Int
    public let waitingCount: Int
    public let nextWakeAt: Date?
    public let topPriority: Priority
}

public struct RecentNote: Identifiable {
    public var id: UUID { note.id }
    public let item: WorkItem
    public let note: Note
}

public struct GroupCount: Equatable {
    public let now: Int
    public let waiting: Int
    public let attention: Int
}

public struct Dashboard {
    public let focus: AgendaEntry?
    public let now: [AgendaEntry]
    public let waiting: [AgendaEntry]
    public let overview: [OverviewEntry]
    public let recentNotes: [RecentNote]
    public let groupCounts: [UUID: GroupCount]
}

/// Time-aware projection of the board. Must match TodoTracker.Core.Agenda (see shared scenarios).
public enum Agenda {
    public static func state(of item: WorkItem, now: Date) -> ItemState {
        if item.isDone { return .done }
        if isLocked(item) { return .locked }
        if isAfter(item.nextActionAt, now) || item.ancestors.contains(where: { isAfter($0.nextActionAt, now) }) { return .waiting }
        return item.hasOpenChildren ? .container : .actionable
    }

    public static func effectivePriority(_ item: WorkItem) -> Priority {
        (item.ancestors.map(\.priority) + [item.priority]).max() ?? item.priority
    }

    public static func build(_ board: TaskBoard, now: Date, recentNoteCount: Int = 5, groupId: UUID? = nil) -> Dashboard {
        let openItems = board.allItems.filter { !$0.isDone }.enumerated().map { (offset: $0.offset, entry: describe($0.element, now: now)) }
        let nowAll = openItems.filter { $0.entry.state == .actionable || ($0.entry.needsAttention && ($0.entry.state == .waiting || $0.entry.state == .container)) }
        let waitingAll = openItems.filter {
            $0.entry.state == .waiting && !$0.entry.needsAttention && isAfter($0.entry.item.nextActionAt, now)
                && !$0.entry.item.ancestors.contains(where: { isAfter($0.nextActionAt, now) })
        }

        var counts: [UUID: GroupCount] = [:]
        for g in board.groups {
            counts[g.id] = GroupCount(
                now: nowAll.filter { $0.entry.item.groupId == g.id }.count,
                waiting: waitingAll.filter { $0.entry.item.groupId == g.id }.count,
                attention: nowAll.filter { $0.entry.item.groupId == g.id && $0.entry.needsAttention }.count)
        }

        func inScope(_ item: WorkItem) -> Bool { groupId == nil || item.groupId == groupId }

        let nowList = nowAll.filter { inScope($0.entry.item) }.sorted { a, b in
            let x = a.entry, y = b.entry
            if x.needsAttention != y.needsAttention { return x.needsAttention }
            if x.effectivePriority != y.effectivePriority { return x.effectivePriority > y.effectivePriority }
            if x.isOverdue != y.isOverdue { return x.isOverdue }
            let dx = x.item.deadline ?? .distantFuture, dy = y.item.deadline ?? .distantFuture
            if dx != dy { return dx < dy }
            if x.item.createdAt != y.item.createdAt { return x.item.createdAt < y.item.createdAt }
            return a.offset < b.offset
        }.map(\.entry)

        let waitingList = waitingAll.filter { inScope($0.entry.item) }.sorted { a, b in
            let x = a.entry, y = b.entry
            let wx = x.wakeAt ?? .distantFuture, wy = y.wakeAt ?? .distantFuture
            if wx != wy { return wx < wy }
            if x.effectivePriority != y.effectivePriority { return x.effectivePriority > y.effectivePriority }
            if x.item.createdAt != y.item.createdAt { return x.item.createdAt < y.item.createdAt }
            return a.offset < b.offset
        }.map(\.entry)

        let overview = board.items.filter { !$0.isDone && inScope($0) }.map { summarize($0, now: nowList, waiting: waitingList) }

        let notes: [RecentNote] = recentNoteCount <= 0 ? [] : Array(
            board.allItems.filter(inScope)
                .flatMap { item in item.notes.map { RecentNote(item: item, note: $0) } }
                .enumerated()
                .sorted { $0.element.note.at != $1.element.note.at ? $0.element.note.at > $1.element.note.at : $0.offset < $1.offset }
                .map(\.element)
                .prefix(recentNoteCount))

        return Dashboard(focus: nowList.first, now: nowList, waiting: waitingList, overview: overview, recentNotes: notes, groupCounts: counts)
    }

    private static func isAfter(_ date: Date?, _ now: Date) -> Bool {
        guard let date else { return false }
        return date > now
    }

    private static func describe(_ item: WorkItem, now: Date) -> AgendaEntry {
        let state = state(of: item, now: now)
        let due = (state == .locked || state == .done) ? nil : item.reminders.filter { $0.isDue(now) }.min { $0.dueAt < $1.dueAt }
        let wake: Date? = state == .waiting ? (item.ancestors.compactMap(\.nextActionAt) + [item.nextActionAt].compactMap { $0 }).filter { $0 > now }.max() : nil
        return AgendaEntry(
            item: item,
            state: state,
            effectivePriority: effectivePriority(item),
            needsAttention: due != nil,
            dueReminder: due,
            wakeAt: wake,
            isOverdue: item.deadline.map { $0 < now } ?? false,
            breadcrumb: item.ancestors.reversed().map(\.title))
    }

    private static func isLocked(_ item: WorkItem) -> Bool {
        var current: WorkItem? = item
        while let c = current {
            if TaskBoard.blockingStep(c) != nil { return true }
            current = c.parent
        }
        return false
    }

    private static func summarize(_ root: WorkItem, now: [AgendaEntry], waiting: [AgendaEntry]) -> OverviewEntry {
        let subtree = root.selfAndDescendants
        let ids = Set(subtree.map(\.id))
        let leaves = subtree.filter { $0.children.isEmpty }
        let waitingHere = waiting.filter { ids.contains($0.item.id) }
        return OverviewEntry(
            item: root,
            doneLeaves: leaves.filter(\.isDone).count,
            totalLeaves: leaves.count,
            actionableCount: now.filter { ids.contains($0.item.id) }.count,
            waitingCount: waitingHere.count,
            nextWakeAt: waitingHere.compactMap(\.wakeAt).min(),
            topPriority: subtree.filter { !$0.isDone }.map(effectivePriority).max() ?? root.priority)
    }
}
