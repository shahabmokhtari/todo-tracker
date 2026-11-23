import Foundation

/// What the Tasks tree shows: unfinished tasks, what can be done now, what's waiting, or everything.
public enum OutlineShow: String, CaseIterable, Identifiable, Sendable {
    /// Every task that isn't finished (the default).
    case todo
    /// Tasks that can be done now, with their parents.
    case now
    /// Tasks that wait (snoozed, waiting for another task) or come after an unfinished step, with their parents.
    case waiting
    /// Finished tasks too.
    case all

    public var id: String { rawValue }

    /// The name in the Show menu.
    public var label: String {
        switch self {
        case .todo: return "To do"
        case .now: return "Can do now"
        case .waiting: return "Waiting"
        case .all: return "Everything"
        }
    }

    /// Parses todo, now, waiting or all (any case; "to do" and "everything" too); nil for anything else.
    public static func parse(_ text: String?) -> OutlineShow? {
        switch text?.trimmingCharacters(in: .whitespaces).lowercased() {
        case "todo", "to do": return .todo
        case "now": return .now
        case "waiting": return .waiting
        case "all", "everything": return .all
        default: return nil
        }
    }

    /// The first Show choice on this device: what was picked, else Everything for someone who had "Show finished" on.
    public static func initial(saved: String?, showDone: Bool) -> OutlineShow {
        if let saved, let choice = OutlineShow(rawValue: saved) { return choice }
        return showDone ? .all : .todo
    }
}

/// How deep "Open to" opens the tree.
public enum OutlineLevel: String, CaseIterable, Identifiable, Sendable {
    /// Only the group rows (under All groups).
    case groups
    /// The top-level tasks, closed.
    case tasks
    /// One level of subtasks.
    case subtasks
    /// Everything.
    case all

    public var id: String { rawValue }

    public var label: String {
        switch self {
        case .groups: return "Groups"
        case .tasks: return "Tasks"
        case .subtasks: return "Subtasks"
        case .all: return "All"
        }
    }
}

/// How the Tasks tree is looked at (per device, never saved in the tasks): the group picked (nil: all groups), the task
/// zoomed into, what's shown, the search text, the tasks that are open and the groups that are closed.
public struct OutlineOptions: Equatable, Sendable {
    public var groupId: UUID?
    public var zoomId: UUID?
    public var show: OutlineShow
    public var query: String?
    public var expanded: Set<UUID>
    public var closedGroups: Set<UUID>

    public init(groupId: UUID? = nil, zoomId: UUID? = nil, show: OutlineShow = .todo, query: String? = nil,
                expanded: Set<UUID> = [], closedGroups: Set<UUID> = []) {
        self.groupId = groupId
        self.zoomId = zoomId
        self.show = show
        self.query = query
        self.expanded = expanded
        self.closedGroups = closedGroups
    }

    /// There's search text: the tree is narrowed to it and shown open on the way to what matched.
    public var searching: Bool { !(query ?? "").trimmingCharacters(in: .whitespacesAndNewlines).isEmpty }
}

/// A row of the Tasks tree. `title` is what's shown (without the parent's name it repeats); `done`/`total` count the
/// leaves under it (0/0 for a task without subtasks; a group adds up its top-level tasks, one each for a task without
/// subtasks); `left` is `total - done`. `canOutdent`: it can move out one level here (not a top-level task, nor one at
/// the top of a zoom).
public struct OutlineRow: Identifiable {
    public enum Kind: String, Sendable { case group, task }

    public let kind: Kind
    public let id: UUID
    public let depth: Int
    public let title: String
    public let fullTitle: String
    public let hasChildren: Bool
    public let expanded: Bool
    public let done: Int
    public let total: Int
    public let left: Int
    public let state: ItemState?
    public let canOutdent: Bool
    /// The task (task rows).
    public let item: WorkItem?
    /// The group (group rows).
    public let group: TaskGroup?
}

/// A part of the path above a zoomed tree: All groups › group › each parent › the zoomed task.
public struct OutlinePathPart: Equatable {
    public enum Kind: String, Sendable {
        /// "All groups": the way out of the zoom.
        case all
        case group
        case task
    }

    public let kind: Kind
    public let id: UUID?
    public let title: String
    public let done: Int
    public let total: Int
}

/// What "Open to" leaves open: the tasks open and the groups closed.
public struct OutlineOpen: Equatable, Sendable {
    public let expanded: Set<UUID>
    public let closedGroups: Set<UUID>
}

/// The Tasks tree as rows: the same rules as the C# core (Outline.cs, also behind `ot tree`), the web app
/// (outlinemodel.js) and Kotlin, checked against tests/fixtures/outline.json. Only how the tree is shown; nothing in the
/// tasks changes.
public enum Outline {
    /// The first part of the path.
    public static let allGroupsTitle = "All groups"

    private static let separators = [":", " -", " –", " —"]

    /// The rows to draw, in order.
    public static func rows(_ board: TaskBoard, _ options: OutlineOptions, now: Date) -> [OutlineRow] {
        let shown = Shown(board, options, now)
        var rows: [OutlineRow] = []
        let zoom = resolveZoom(shown)

        func walk(_ items: [WorkItem], _ depth: Int, _ parentShown: String?) {
            for item in items where shown.kept(item) {
                let children = item.children.filter(shown.kept)
                let open = !children.isEmpty && (options.searching || options.expanded.contains(item.id))
                let (done, total) = leaves(item)
                let parent = item.parent
                let title = shownTitle(item.title, parentTitle: parent?.title, parentShownTitle: parentShown)
                rows.append(OutlineRow(kind: .task, id: item.id, depth: depth, title: title, fullTitle: item.title,
                                       hasChildren: !children.isEmpty, expanded: open, done: done, total: total, left: total - done,
                                       state: Agenda.state(of: item, now: now), canOutdent: parent != nil && parent !== zoom,
                                       item: item, group: nil))
                if open { walk(children, depth + 1, title) }
            }
        }

        if let zoom {
            walk(zoom.children, 0, chain(zoom).last?.shown)
            return rows
        }

        let roots = shown.roots()
        if let picked = options.groupId {
            walk(roots.filter { $0.groupId == picked }, 0, nil)
            return rows
        }

        // Empty groups stay (somewhere to drop into) unless a search or a narrowing choice is on.
        let hideEmpty = options.searching || options.show == .now || options.show == .waiting
        for group in board.groups {
            let tasks = roots.filter { $0.groupId == group.id }
            let hasChildren = tasks.contains(where: shown.kept)
            if !hasChildren && hideEmpty { continue }
            let (done, total) = groupLeaves(tasks)
            let open = options.searching || !options.closedGroups.contains(group.id)
            rows.append(OutlineRow(kind: .group, id: group.id, depth: 0, title: group.name, fullTitle: group.name,
                                   hasChildren: hasChildren, expanded: open, done: done, total: total, left: total - done,
                                   state: nil, canOutdent: false, item: nil, group: group))
            if open { walk(tasks, 1, nil) }
        }
        return rows
    }

    /// The path above the tree while zoomed (empty when not).
    public static func path(_ board: TaskBoard, _ options: OutlineOptions, now: Date) -> [OutlinePathPart] {
        guard let zoom = resolveZoom(Shown(board, options, now)) else { return [] }
        let group = board.groups.first { $0.id == zoom.groupId }
        var parts = [OutlinePathPart(kind: .all, id: nil, title: allGroupsTitle, done: 0, total: 0),
                     OutlinePathPart(kind: .group, id: zoom.groupId, title: group?.name ?? "", done: 0, total: 0)]
        for (item, shown) in chain(zoom) {
            let (done, total) = leaves(item)
            parts.append(OutlinePathPart(kind: .task, id: item.id, title: shown, done: done, total: total))
        }
        return parts
    }

    /// The zoom to use: the asked task when it's shown and has subtasks; else its nearest parent that is; else nil (the
    /// top). A task that's gone (deleted, archived, in another group than the one picked) has none.
    public static func resolveZoom(_ board: TaskBoard, _ options: OutlineOptions, now: Date) -> UUID? {
        resolveZoom(Shown(board, options, now))?.id
    }

    /// "Open to" a level: which tasks are open and which groups closed afterwards. It changes only what's in view (under
    /// the zoom, the group picked, or every group); tasks elsewhere stay as they were. Groups closes every group (under
    /// All groups; elsewhere it's the same as Tasks); the other levels open every group.
    public static func openTo(_ board: TaskBoard, _ options: OutlineOptions, level: OutlineLevel, now: Date) -> OutlineOpen {
        let shown = Shown(board, options, now)
        let zoom = resolveZoom(shown)
        let top = zoom.map { $0.children.filter(shown.inQuery) }
            ?? shown.roots().filter { options.groupId == nil || $0.groupId == options.groupId }
        var expanded = options.expanded
        func each(_ items: [WorkItem], _ depth: Int) {
            for item in items {
                let open = item.children.contains(where: shown.inQuery) && (level == .all || (level == .subtasks && depth == 0))
                if open { expanded.insert(item.id) } else { expanded.remove(item.id) }
                each(item.children.filter(shown.inQuery), depth + 1)
            }
        }
        each(top, 0)
        let closed = zoom != nil || options.groupId != nil
            ? options.closedGroups
            : level == .groups ? Set(board.groups.map(\.id)) : []
        return OutlineOpen(expanded: expanded, closedGroups: closed)
    }

    /// Where New task adds: inside the zoomed task, else at the top of the group picked (all groups: the first group).
    public static func newTaskPlace(_ board: TaskBoard, _ options: OutlineOptions, now: Date) -> (parentId: UUID?, groupId: UUID) {
        let zoom = resolveZoom(Shown(board, options, now))
        return (zoom?.id, zoom?.groupId ?? options.groupId ?? board.defaultGroupId)
    }

    /// Whether a task is in the tree with these options (the search and the Show choice; its parents count with it).
    public static func kept(_ board: TaskBoard, _ options: OutlineOptions, now: Date) -> (WorkItem) -> Bool {
        let shown = Shown(board, options, now)
        return { shown.kept($0) }
    }

    /// A subtask's title without its parent's name in front: when it starts with the parent's title (any case) followed
    /// by `:`, ` -`, ` –` or ` —`, the rest (leading spaces dropped), unless that's empty. When the parent's own title is
    /// shown shorter (`parentShownTitle`), that name counts too.
    public static func shownTitle(_ title: String, parentTitle: String?, parentShownTitle: String? = nil) -> String {
        let shown = trim(title, parentTitle)
        return shown == title && parentShownTitle != parentTitle ? trim(title, parentShownTitle) : shown
    }

    private static func trim(_ title: String, _ parentTitle: String?) -> String {
        guard let parentTitle, !parentTitle.isEmpty,
              let match = title.range(of: parentTitle, options: [.caseInsensitive, .anchored]) else { return title }
        let after = title[match.upperBound...]
        for separator in separators where after.hasPrefix(separator) {
            let rest = after.dropFirst(separator.count).drop { $0.isWhitespace }
            return rest.isEmpty ? title : String(rest)
        }
        return title
    }

    /// Finished leaves and all leaves under the task (not counting the task itself).
    public static func leaves(_ item: WorkItem) -> (done: Int, total: Int) {
        let leaves = item.selfAndDescendants.dropFirst().filter { $0.children.isEmpty }
        return (leaves.filter(\.isDone).count, leaves.count)
    }

    /// The task and its parents, outermost first, each with its title as shown under the one before.
    private static func chain(_ item: WorkItem) -> [(item: WorkItem, shown: String)] {
        var chain: [(item: WorkItem, shown: String)] = []
        for at in item.ancestors.reversed() + [item] {
            let shown = chain.last.map { shownTitle(at.title, parentTitle: $0.item.title, parentShownTitle: $0.shown) } ?? at.title
            chain.append((at, shown))
        }
        return chain
    }

    private static func groupLeaves(_ roots: [WorkItem]) -> (done: Int, total: Int) {
        var done = 0, total = 0
        for root in roots {
            let (d, t) = leaves(root)
            if t > 0 {
                done += d
                total += t
            } else {
                done += root.isDone ? 1 : 0
                total += 1
            }
        }
        return (done, total)
    }

    private static func resolveZoom(_ shown: Shown) -> WorkItem? {
        guard let id = shown.options.zoomId, let item = shown.board.find(id), !item.isArchived else { return nil }
        if let group = shown.options.groupId, item.groupId != group { return nil }
        var at: WorkItem? = item
        while let task = at {
            if !task.children.isEmpty && shown.kept(task) { return task }
            at = task.parent
        }
        return nil
    }

    /// Which tasks the search and the Show choice keep (each kept task's parents are kept too).
    private final class Shown {
        let board: TaskBoard
        let options: OutlineOptions
        private let now: Date
        private let query: Set<UUID>?
        private var memo: [UUID: Bool] = [:]

        init(_ board: TaskBoard, _ options: OutlineOptions, _ now: Date) {
            self.board = board
            self.options = options
            self.now = now
            if options.searching {
                // As /api/tree?q= does: the matches, their parents (for context) and their subtasks.
                var query = Set<UUID>()
                for match in TaskSearch(options.query).apply(board) {
                    query.formUnion(match.selfAndDescendants.map(\.id))
                    query.formUnion(match.ancestors.map(\.id))
                }
                self.query = query
            } else {
                query = nil
            }
        }

        /// Top-level tasks that aren't archived and that the search keeps.
        func roots() -> [WorkItem] { board.items.filter { $0.archivedAt == nil && inQuery($0) } }

        func inQuery(_ item: WorkItem) -> Bool { query?.contains(item.id) ?? true }

        func kept(_ item: WorkItem) -> Bool {
            if let known = memo[item.id] { return known }
            let kept = !item.isArchived && inQuery(item) && (itself(item) || item.children.contains(where: kept))
            memo[item.id] = kept
            return kept
        }

        private func itself(_ item: WorkItem) -> Bool {
            switch options.show {
            case .all: return true
            case .now: return Agenda.state(of: item, now: now) == .actionable
            case .waiting:
                let state = Agenda.state(of: item, now: now)
                return state == .waiting || state == .locked
            case .todo: return !item.isDone
            }
        }
    }
}
