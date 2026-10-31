#if canImport(SwiftUI)
import Foundation
import TodoTrackerKit

/// What the Board, Tasks and Done views show and do. Every change works the same way as the others: made on the board
/// on this device (and saved), or sent to the server in use (the Mac's own, or the computer this iPhone or iPad uses).
extension BoardModel {
    // MARK: What the views show

    /// Top-level tasks of the group on screen (all groups when none), in tree order; archived ones are put away (Done ›
    /// Archive shows them).
    public var visibleRoots: [WorkItem] {
        board.items.filter { $0.archivedAt == nil && (selectedGroupId == nil || $0.groupId == selectedGroupId) }
    }

    /// Top-level tasks the search finds (itself or one of its subtasks matches), or all of them when not searching.
    public var matchingRoots: [WorkItem] {
        let search = TaskSearch(searchText)
        guard !search.isEmpty else { return visibleRoots }
        let found = Set(search.apply(board).map { $0.root.id })
        return visibleRoots.filter { found.contains($0.id) }
    }

    /// The Board's columns for the group on screen (and the search).
    public var boardColumns: [BoardColumn: [WorkItem]] { BoardColumns.of(matchingRoots, now: now) }

    /// Finished top-level tasks (not archived), newest first.
    public var finishedRoots: [WorkItem] {
        matchingRoots.filter(\.isDone).sorted { $0.completedAt! > $1.completedAt! }
    }

    /// Archived top-level tasks, newest first.
    public var archivedRoots: [WorkItem] {
        let search = TaskSearch(searchText)
        let found = search.isEmpty ? nil : Set(search.apply(board).map { $0.root.id })
        return board.items.filter { $0.archivedAt != nil && (selectedGroupId == nil || $0.groupId == selectedGroupId) && (found?.contains($0.id) ?? true) }
            .sorted { ($0.archivedAt ?? .distantPast) > ($1.archivedAt ?? .distantPast) }
    }

    /// Open top-level tasks in a group (for the sidebar's counts): every open card, like the web's.
    public func openCount(in groupId: UUID) -> Int {
        board.items.filter { $0.groupId == groupId && !$0.isDone }.count
    }

    // MARK: Moves

    /// Makes a move, worked out by `build` from the board as it is when its turn comes. Here it's made at once (all or
    /// nothing); on a server, each move waits for the one before (sent, and the board read back), so two quick presses
    /// never send the same change twice.
    func perform(_ build: @escaping () -> PlannedMove?) {
        if waitWhileConnecting() { return }
        guard server != nil else {
            guard let planned = build(), !planned.moves.isEmpty else { return }
            mutate(planned.message) { at in for move in planned.moves { try board.apply(move, now: at) } }
            return
        }
        queuedMoves.append(build)
        nextMove()
    }

    private func nextMove() {
        guard !moveInFlight, !queuedMoves.isEmpty else { return }
        let build = queuedMoves.removeFirst()
        guard server != nil, let planned = build(), !planned.moves.isEmpty else {
            nextMove()
            return
        }
        moveInFlight = true
        sendInOrder(planned.moves.map { ServerAction.boardMove($0) }, planned.message, onSettled: { [weak self] in
            self?.moveInFlight = false
            self?.nextMove()
        })
    }

    /// Sends changes one after the other (each waits for the one before; one that fails stops the rest). `onSettled`:
    /// all sent (or one failed), and the board read back.
    func sendInOrder(_ actions: [ServerAction], _ message: String?, onDone: ((Data) -> Void)? = nil, onSettled: (() -> Void)? = nil) {
        guard let first = actions.first else {
            onSettled?()
            return
        }
        let rest = Array(actions.dropFirst())
        var ok = false
        send(first, rest.isEmpty ? message : nil, onDone: { data in
            ok = true
            if rest.isEmpty { onDone?(data) }
        }, onSettled: { [weak self] in
            if ok, !rest.isEmpty, let self { self.sendInOrder(rest, message, onDone: onDone, onSettled: onSettled) } else { onSettled?() }
        })
    }

    /// A card to a column (Done finishes it; out of Done opens it again), before `before` (a card there; nil: its place
    /// in the shared order, or last within its own column). Only top-level tasks are cards.
    public func moveCard(_ id: UUID, to column: BoardColumn, before: UUID? = nil) {
        guard let item = board.find(id) else { return }
        guard item.parent == nil else {
            status = "Only top-level tasks are cards; move its task instead."
            return
        }
        perform { [weak self] in
            guard let self, let card = self.board.find(id), card.parent == nil else { return nil }
            let columns = self.boardColumns
            let from = BoardColumns.column(of: card)
            var message: String? = column == .done ? "Done 🎉" : from == column ? nil : "Moved to \(column.name)"
            if column == .doing, from != .doing, (columns[.doing]?.count ?? 0) + 1 > BoardColumns.wipLimit {
                message = BoardColumns.overLimitMessage
            }
            return PlannedMove(BoardColumns.drop(card, to: column, before: before, columns: columns), message)
        }
    }

    /// Option-arrows on a card: left and right change column, up and down move it within the column.
    public func moveCard(_ id: UUID, _ direction: BoardColumns.Direction) {
        guard board.find(id)?.parent == nil else { return }
        perform { [weak self] in
            guard let self, let card = self.board.find(id), card.parent == nil else { return nil }
            let columns = self.boardColumns
            guard let move = BoardColumns.keyboardMove(card, direction, columns: columns) else { return nil }
            return PlannedMove(BoardColumns.drop(card, to: move.column, before: move.before, columns: columns), move.column == .done ? "Done 🎉" : nil)
        }
    }

    /// Tab: under the task above it on screen.
    public func indent(_ id: UUID, showDone: Bool = true) {
        perform { [weak self] in
            guard let self else { return nil }
            return OutlineMoves.indent(self.board, id, showDone: showDone, roots: self.matchingRoots).map { PlannedMove($0, nil) }
        }
    }

    /// Shift-Tab: out one level, right after its parent.
    public func outdent(_ id: UUID) {
        perform { [weak self] in
            guard let self else { return nil }
            return OutlineMoves.outdent(self.board, id).map { PlannedMove($0, nil) }
        }
    }

    public func canIndent(_ id: UUID, showDone: Bool = true) -> Bool {
        OutlineMoves.indent(board, id, showDone: showDone, roots: matchingRoots) != nil
    }

    public func canOutdent(_ id: UUID) -> Bool { OutlineMoves.outdent(board, id) != nil }

    /// Option-up or down: past the task above or below on screen.
    public func step(_ id: UUID, by delta: Int, showDone: Bool = true) {
        perform { [weak self] in
            guard let self else { return nil }
            return OutlineMoves.step(self.board, id, by: delta, showDone: showDone, roots: self.matchingRoots).map { PlannedMove($0, nil) }
        }
    }

    /// A task dropped before, after or into another. False when it can't go there (into itself).
    @discardableResult
    public func drop(_ dragged: UUID, on target: UUID, _ zone: OutlineMoves.Zone) -> Bool {
        guard OutlineMoves.drop(board, dragged, on: target, zone) != nil else {
            if dragged != target { status = "A task can’t go inside itself." }
            return false
        }
        perform { [weak self] in
            guard let self else { return nil }
            return OutlineMoves.drop(self.board, dragged, on: target, zone).map { PlannedMove($0, "Moved") }
        }
        return true
    }

    /// A task dropped on a group in the sidebar: it moves there (a subtask becomes a task of its own, last).
    public func move(_ id: UUID, toGroup groupId: UUID) {
        let name = groups.first { $0.id == groupId }?.name ?? "the group"
        perform { [weak self] in
            guard let self else { return nil }
            return OutlineMoves.toGroup(self.board, id, groupId: groupId).map { PlannedMove($0, "Moved to \(name)") }
        }
    }

    /// Do now in a new order (a task dragged before another, or to the end).
    public func arrangeNow(moving id: UUID, before other: UUID?) {
        var ids = dashboard.now.map(\.item.id)
        guard let from = ids.firstIndex(of: id), id != other else { return }
        ids.remove(at: from)
        ids.insert(id, at: other.flatMap { ids.firstIndex(of: $0) } ?? ids.count)
        guard ids != dashboard.now.map(\.item.id) else { return }
        if waitWhileConnecting() { return }
        if server != nil {
            send(.arrangeNow(ids), nil)
        } else {
            mutate(nil) { try board.arrangeNow(ids, now: $0) }
        }
    }

    // MARK: New tasks

    /// Adds a task: under `parentId` (first, or right after `after`), or at the top level of the group on screen (in
    /// `stage`, right after `after` when given). `done` gets its id (nil: it wasn't added).
    public func addTask(_ title: String, parentId: UUID? = nil, after: UUID? = nil, stage: Stage? = nil, done: ((UUID?) -> Void)? = nil) {
        let clean = title.trimmingCharacters(in: .whitespacesAndNewlines)
        guard !clean.isEmpty else {
            done?(nil)
            return
        }
        if waitWhileConnecting() {
            done?(nil)
            return
        }
        let groupId = parentId == nil ? (after.flatMap { board.find($0)?.groupId } ?? selectedGroupId) : nil
        let first = parentId.flatMap { board.find($0)?.children.first?.id }
        // Only a sibling can be the task it goes after.
        let after = after.flatMap { board.find($0)?.parent?.id == parentId ? $0 : nil }
        if server != nil {
            send(.addTask(title: clean, parentId: parentId, groupId: groupId, stage: stage), "Added", onDone: { [weak self] data in
                guard let self, let object = try? JSONSerialization.jsonObject(with: data) as? [String: Any],
                      let text = object["id"] as? String, let id = UUID(uuidString: text) else {
                    done?(nil)
                    return
                }
                // In place: right after the task it was typed under, or first under its parent.
                if let after {
                    self.send(.boardMove(.reorderAfter(id, after: after)), nil)
                } else if let first {
                    self.send(.boardMove(.reorder(id, before: first)), nil)
                }
                done?(id)
            }, onFail: { done?(nil) })
            return
        }
        var created: UUID?
        mutate("Added") { at in
            let item = try board.addTask(NewTask(clean, parentId: parentId, groupId: groupId, stage: stage), now: at)
            if let after { try board.reorderAfter(item.id, after: after) } else if let first { try board.reorder(item.id, before: first) }
            created = item.id
        }
        done?(created)
    }

    /// Renames a task (blank: nothing changes).
    public func rename(_ id: UUID, to title: String) {
        let clean = title.trimmingCharacters(in: .whitespacesAndNewlines)
        guard !clean.isEmpty, board.find(id)?.title != clean else { return }
        edit(id, TaskChanges(title: clean), message: nil)
    }

    /// Done or open again (the checkbox in Tasks, Space on a selected task).
    public func toggleDone(_ id: UUID) {
        guard let item = board.find(id) else { return }
        if item.isDone { reopen(id) } else { complete(id) }
    }
}

/// A move worked out from the board: its changes, and what to say once it's made.
struct PlannedMove {
    let moves: [BoardMove]
    let message: String?

    init(_ moves: [BoardMove], _ message: String?) {
        self.moves = moves
        self.message = message
    }
}

/// What a card says about its task, most urgent first: overdue or due, waiting, progress, time (boardmodel.js cardFacts).
public enum CardFacts {
    public static func chips(_ item: WorkItem, now: Date) -> [Chip] {
        var chips: [Chip] = []
        if let due = item.deadline, !item.isDone {
            let when = RelativeTime.format(due, now: now)
            if due < now {
                chips.append(Chip("Overdue · \(when)", .danger))
            } else {
                chips.append(Chip("Due \(when)", due.timeIntervalSince(now) < 2 * 86_400 ? .warn : .muted))
            }
        }
        if !item.isDone, let wake = item.nextActionAt, wake > now { chips.append(Chip("Waiting", .info)) }
        let leaves = item.selfAndDescendants.dropFirst()
        if !leaves.isEmpty {
            let done = leaves.filter(\.isDone).count
            chips.append(Chip("\(done)/\(leaves.count)", done == leaves.count ? .step : .muted))
        }
        let spent = item.timeSpent(now)
        let timing = item.selfAndDescendants.contains { $0.timeEntries.contains(where: \.isRunning) }
        if spent >= 60 || timing { chips.append(Chip("⏱ \(Presentation.duration(spent))", timing ? .step : .muted)) }
        return chips
    }
}
#endif
