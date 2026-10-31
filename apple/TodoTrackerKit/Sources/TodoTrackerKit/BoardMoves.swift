import Foundation

// The Board (columns Inbox, Next, Doing, Done) and the Tasks outline: moving cards and tasks. Ports of
// TodoTracker.Core's SetStage, Move, Reorder and ReorderAfter, and of the web app's boardmodel.js and outlinemodel.js
// (what a drop, Tab or Shift+Tab turns into). tests/fixtures/board-moves.json holds the cases every app runs.

/// One change a move is made of: made on the board here, or sent to the server (ServerAction.boardMove).
public enum BoardMove: Equatable, Sendable {
    case complete(UUID)
    /// A card to a column (a finished card opens again).
    case stage(UUID, Stage)
    /// Under `parentId` (nil: the top level of `groupId`, or of its own group), at `index` (nil: last).
    case move(UUID, parentId: UUID?, groupId: UUID?, index: Int?)
    /// Among its siblings, before `before` (nil: last).
    case reorder(UUID, before: UUID?)
    /// Right after a sibling.
    case reorderAfter(UUID, after: UUID)
}

extension TaskBoard {
    /// Moves a card to another column. A finished card moved back is open again. Only top-level tasks are cards.
    public func setStage(_ id: UUID, _ stage: Stage, actor: Actor = .user, now: Date) throws {
        let item = try get(id)
        if item.parent != nil { throw BoardError.conflict("Only top-level tasks are cards; subtasks move with their task.") }
        if item.isDone { try reopen(id, actor: actor, now: now) }
        guard item.stage != stage else { return }
        item.stage = stage
        log(now, id, "moved", "Moved \"\(item.title)\" to \(stage.name)", actor)
    }

    /// Moves a task under `parentId` (or to the top level, in `groupId` or its current group) at `index` (nil: last).
    /// Subtasks move with it. A subtask that becomes a task of its own is a card in Next.
    public func move(_ id: UUID, parentId: UUID?, index: Int? = nil, groupId: UUID? = nil, actor: Actor = .user, now: Date) throws {
        let item = try get(id)
        let parent = try parentId.map { try get($0) }
        if let parent, parent === item || parent.ancestors.contains(where: { $0 === item }) {
            throw BoardError.conflict("\"\(item.title)\" can't move into itself or one of its subtasks.")
        }
        let group = parent == nil ? try self.group(groupId ?? item.groupId) : nil
        let wasRoot = item.parent == nil
        detach(item)
        var siblings = parent?.children ?? items
        siblings.insert(item, at: min(max(index ?? siblings.count, 0), siblings.count))
        if let parent { parent.children = siblings } else { items = siblings }
        item.parent = parent
        if let group { item.ownGroupId = group.id }
        if parent == nil && !wasRoot {
            item.stage = .next
        } else if parent != nil {
            item.archivedAt = nil
        }
        log(now, id, "moved", parent.map { "Moved \"\(item.title)\" under \"\($0.title)\"" } ?? "Moved \"\(item.title)\" to \(group!.name)", actor)
    }

    /// Moves a task among its siblings, before `before` (nil: to the end). Its parent stays.
    public func reorder(_ id: UUID, before: UUID?) throws {
        let item = try get(id)
        if before == id { return }
        let target = try before.map { try get($0) }
        var siblings = item.parent?.children ?? items
        if let target, !siblings.contains(where: { $0 === target }) {
            throw BoardError.invalid("\"\(target.title)\" isn't next to \"\(item.title)\"; use move to put a task somewhere else.")
        }
        siblings.removeAll { $0 === item }
        let at = target.flatMap { t in siblings.firstIndex { $0 === t } } ?? siblings.count
        siblings.insert(item, at: at)
        setSiblings(of: item, siblings)
    }

    /// Puts a task right after another one next to it.
    public func reorderAfter(_ id: UUID, after: UUID) throws {
        let item = try get(id)
        let target = try get(after)
        if after == id { return }
        var siblings = item.parent?.children ?? items
        guard siblings.contains(where: { $0 === target }) else {
            throw BoardError.invalid("\"\(target.title)\" isn't next to \"\(item.title)\"; use move to put a task somewhere else.")
        }
        siblings.removeAll { $0 === item }
        siblings.insert(item, at: siblings.firstIndex { $0 === target }! + 1)
        setSiblings(of: item, siblings)
    }

    /// Makes one change of a move.
    public func apply(_ move: BoardMove, actor: Actor = .user, now: Date) throws {
        switch move {
        case let .complete(id): try complete(id, actor: actor, now: now)
        case let .stage(id, stage): try setStage(id, stage, actor: actor, now: now)
        case let .move(id, parentId, groupId, index): try self.move(id, parentId: parentId, index: index, groupId: groupId, actor: actor, now: now)
        case let .reorder(id, before): try reorder(id, before: before)
        case let .reorderAfter(id, after): try reorderAfter(id, after: after)
        }
    }

    private func detach(_ item: WorkItem) {
        if let parent = item.parent { parent.children.removeAll { $0 === item } } else { items.removeAll { $0 === item } }
    }

    private func setSiblings(of item: WorkItem, _ siblings: [WorkItem]) {
        if let parent = item.parent { parent.children = siblings } else { items = siblings }
    }
}

extension Stage {
    /// As the board says it ("Moved … to Doing").
    public var name: String { rawValue.prefix(1).uppercased() + rawValue.dropFirst() }
}

/// A column of the Board: three stages, and Done (finished cards).
public enum BoardColumn: String, CaseIterable, Identifiable, Sendable {
    case inbox, next, doing, done

    public var id: String { rawValue }
    public var name: String { rawValue.prefix(1).uppercased() + rawValue.dropFirst() }

    /// What it's for (the web's column hints).
    public var hint: String {
        switch self {
        case .inbox: return "Captured, not decided yet"
        case .next: return "Ready to pick up"
        case .doing: return "In progress (keep it to 3)"
        case .done: return "Finished recently"
        }
    }

    /// What an empty column says.
    public var emptyText: String {
        switch self {
        case .inbox: return "Nothing new. Type below to add one."
        case .next: return "Pick something from the inbox."
        case .doing: return "Start one thing."
        case .done: return "Finished cards land here."
        }
    }

    public var stage: Stage? { Stage(rawValue: rawValue) }
}

/// The Board's columns, how full Doing is, and what a drop or a keyboard move means (the web's boardmodel.js).
public enum BoardColumns {
    /// Doing is kept small on purpose: more than this at once splits attention.
    public static let wipLimit = 3
    /// How long finished cards stay on the board (then they're in Done).
    public static let doneDays = 14
    /// Said when Doing has more than three.
    public static let overLimitMessage = "More than 3 at once splits attention. Finish one, or move one back to Next."

    public enum Fill: Sendable { case ok, full, over }

    /// Cards per column, in tree order. Done shows cards finished in the last two weeks, newest first; archived cards
    /// are on none.
    public static func of(_ roots: [WorkItem], now: Date) -> [BoardColumn: [WorkItem]] {
        var columns: [BoardColumn: [WorkItem]] = [.inbox: [], .next: [], .doing: [], .done: []]
        let since = now.addingTimeInterval(-Double(doneDays) * 86_400)
        for root in roots where root.archivedAt == nil {
            if let done = root.completedAt {
                if done >= since { columns[.done, default: []].append(root) }
            } else {
                columns[BoardColumn(rawValue: root.stage.rawValue) ?? .next, default: []].append(root)
            }
        }
        columns[.done] = (columns[.done] ?? []).enumerated()
            .sorted { ($0.element.completedAt!, $1.offset) > ($1.element.completedAt!, $0.offset) }.map(\.element)
        return columns
    }

    /// The column a card is in.
    public static func column(of card: WorkItem) -> BoardColumn {
        card.isDone ? .done : BoardColumn(rawValue: card.stage.rawValue) ?? .next
    }

    public static func fill(_ count: Int, limit: Int = wipLimit) -> Fill {
        count > limit ? .over : count == limit ? .full : .ok
    }

    /// The changes that move `card` to `column`, before `before` (a card in that column; nil: no place). Without a
    /// place, a card from another column keeps its spot in the one shared order; within its column, no place means last.
    /// Moving to Done finishes it; out of Done opens it again (by setting its column).
    public static func drop(_ card: WorkItem, to column: BoardColumn, before: UUID?, columns: [BoardColumn: [WorkItem]]) -> [BoardMove] {
        let from = self.column(of: card)
        guard let stage = column.stage else { return from == .done ? [] : [.complete(card.id)] }
        var moves: [BoardMove] = []
        if from != column { moves.append(.stage(card.id, stage)) }
        let list = columns[column] ?? []
        let target = before.flatMap { b in list.contains { $0.id == b && $0.id != card.id } ? b : nil }
        let current = list.firstIndex { $0.id == card.id }
        let currentNext: UUID? = current.flatMap { $0 + 1 < list.count ? list[$0 + 1].id : nil }
        if (from == column && target != currentNext) || (from != column && target != nil) {
            moves.append(.reorder(card.id, before: target))
        }
        return moves
    }

    public enum Direction: Sendable { case left, right, up, down }

    /// Where a keyboard move lands (Option-arrows): left/right changes column, up/down moves within it.
    public static func keyboardMove(_ card: WorkItem, _ direction: Direction, columns: [BoardColumn: [WorkItem]]) -> (column: BoardColumn, before: UUID?)? {
        let order = BoardColumn.allCases
        let from = column(of: card)
        let list = columns[from] ?? []
        let index = list.firstIndex { $0.id == card.id } ?? -1
        switch direction {
        case .left, .right:
            let to = order.firstIndex(of: from)! + (direction == .left ? -1 : 1)
            return order.indices.contains(to) ? (order[to], nil) : nil
        case .up:
            guard from != .done, index > 0 else { return nil }
            return (from, list[index - 1].id)
        case .down:
            guard from != .done, index >= 0, index < list.count - 1 else { return nil }
            return (from, index + 2 < list.count ? list[index + 2].id : nil)
        }
    }
}

/// Moves in the Tasks outline (the web's outlinemodel.js): Tab, Shift+Tab, Option-up/down, and drops.
public enum OutlineMoves {
    /// Where a drop lands on a row: before it, after it, or inside it.
    public enum Zone: Sendable { case before, inside, after }

    private static func siblings(_ board: TaskBoard, _ item: WorkItem) -> [WorkItem] { item.parent?.children ?? board.items }

    /// The siblings shown (finished ones hidden unless `showDone`; the task itself always counts).
    private static func shown(_ board: TaskBoard, _ item: WorkItem, showDone: Bool) -> [WorkItem] {
        siblings(board, item).filter { showDone || !$0.isDone || $0 === item }
    }

    /// Make the task a subtask of the one above it (its previous sibling shown), last. Nil when there's none.
    public static func indent(_ board: TaskBoard, _ id: UUID, showDone: Bool = true) -> [BoardMove]? {
        guard let item = board.find(id) else { return nil }
        let visible = shown(board, item, showDone: showDone)
        guard let i = visible.firstIndex(where: { $0 === item }), i > 0 else { return nil }
        return [.move(id, parentId: visible[i - 1].id, groupId: nil, index: nil)]
    }

    /// Move the task out one level: right after its parent. Nil at the top level.
    public static func outdent(_ board: TaskBoard, _ id: UUID) -> [BoardMove]? {
        guard let item = board.find(id), let parent = item.parent else { return nil }
        let up: BoardMove = parent.parent.map { .move(id, parentId: $0.id, groupId: nil, index: nil) } ?? .move(id, parentId: nil, groupId: parent.groupId, index: nil)
        return [up, .reorderAfter(id, after: parent.id)]
    }

    /// Up (-1) or down (1) among the siblings shown (finished ones may be hidden: they're stepped over).
    public static func step(_ board: TaskBoard, _ id: UUID, by delta: Int, showDone: Bool = true) -> [BoardMove]? {
        guard let item = board.find(id) else { return nil }
        let visible = shown(board, item, showDone: showDone)
        guard let i = visible.firstIndex(where: { $0 === item }), visible.indices.contains(i + delta) else { return nil }
        let neighbour = visible[i + delta].id
        return [delta < 0 ? .reorder(id, before: neighbour) : .reorderAfter(id, after: neighbour)]
    }

    /// Puts `dragged` before, after or inside `target`; nil when it can't go there (into itself or its own subtasks).
    public static func drop(_ board: TaskBoard, _ dragged: UUID, on target: UUID, _ zone: Zone) -> [BoardMove]? {
        guard dragged != target, let item = board.find(dragged), let onto = board.find(target),
              !onto.ancestors.contains(where: { $0 === item }) else { return nil }
        if zone == .inside { return [.move(dragged, parentId: target, groupId: nil, index: nil)] }
        let place: BoardMove = zone == .after ? .reorderAfter(dragged, after: target) : .reorder(dragged, before: target)
        if let parent = onto.parent {
            return parent === item.parent ? [place] : [.move(dragged, parentId: parent.id, groupId: nil, index: nil), place]
        }
        return item.parent != nil || item.groupId != onto.groupId ? [.move(dragged, parentId: nil, groupId: onto.groupId, index: nil), place] : [place]
    }

    /// A task to another group (from the sidebar): a top-level one changes group; a subtask becomes a task there, last.
    public static func toGroup(_ board: TaskBoard, _ id: UUID, groupId: UUID) -> [BoardMove]? {
        guard let item = board.find(id) else { return nil }
        if item.parent == nil && item.groupId == groupId { return nil }
        return [.move(id, parentId: nil, groupId: groupId, index: nil)]
    }
}
