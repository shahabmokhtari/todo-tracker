#if canImport(SwiftUI)
import SwiftUI
import OneThingKit

/// What the Tasks tree remembers on this device (never in the tasks): the rows open, the groups closed, the Show choice
/// and the task zoomed into. The keys are the web app's (docs/design/tasks-tree.md); the view reads them through
/// @AppStorage, so a change made here (Show in tree) is drawn at once.
public struct OutlineMemory {
    public static let expandedKey = "onething.outline.expanded"
    public static let closedGroupsKey = "onething.outline.closedGroups"
    public static let showKey = "onething.outline.show"
    public static let zoomKey = "onething.outline.zoom"
    /// The old "Show finished" box: read once to pick the first Show choice, and left in place.
    public static let showDoneKey = "onething.outline.showDone"

    let defaults: UserDefaults

    public init(defaults: UserDefaults = .standard) { self.defaults = defaults }

    /// The open tasks (kept to the newest few hundred, in the order they were opened).
    public var expanded: Set<UUID> {
        get { Self.ids(defaults.string(forKey: Self.expandedKey)) }
        nonmutating set { defaults.set(Self.text(newValue, after: defaults.string(forKey: Self.expandedKey)), forKey: Self.expandedKey) }
    }

    public var closedGroups: Set<UUID> {
        get { Self.ids(defaults.string(forKey: Self.closedGroupsKey)) }
        nonmutating set { defaults.set(Self.text(newValue, after: defaults.string(forKey: Self.closedGroupsKey)), forKey: Self.closedGroupsKey) }
    }

    /// Opens or closes rows and groups, keeping only tasks and groups still on `board` (one that's gone is forgotten).
    public func set(expanded: Set<UUID>, closedGroups: Set<UUID>, on board: TaskBoard) {
        defaults.set(Self.text(expanded, after: defaults.string(forKey: Self.expandedKey), keeping: Self.tasks(on: board)), forKey: Self.expandedKey)
        defaults.set(Self.text(closedGroups, after: defaults.string(forKey: Self.closedGroupsKey), keeping: Self.groups(on: board)), forKey: Self.closedGroupsKey)
    }

    public var show: OutlineShow {
        get { OutlineShow.initial(saved: defaults.string(forKey: Self.showKey), showDone: defaults.bool(forKey: Self.showDoneKey)) }
        nonmutating set { defaults.set(newValue.rawValue, forKey: Self.showKey) }
    }

    public var zoom: UUID? {
        get { defaults.string(forKey: Self.zoomKey).flatMap(UUID.init(uuidString:)) }
        nonmutating set { defaults.set(newValue?.uuidString ?? "", forKey: Self.zoomKey) }
    }

    /// Once: someone who had "Show finished" on starts at Everything (the old value stays where it was).
    public func migrate() {
        let saved = defaults.string(forKey: Self.showKey) ?? ""
        if OutlineShow(rawValue: saved) == nil { show = OutlineShow.initial(saved: nil, showDone: defaults.bool(forKey: Self.showDoneKey)) }
    }

    /// How many open rows are remembered (the newest).
    static let keep = 500

    static func ids(_ text: String?) -> Set<UUID> { Set(order(text)) }

    /// The ids as kept, oldest first.
    static func order(_ text: String?) -> [UUID] {
        var seen = Set<UUID>()
        return (text ?? "").split(separator: ",").compactMap { UUID(uuidString: String($0)) }.reversed().filter { seen.insert($0).inserted }.reversed()
    }

    /// The ids in the order they were added, the newest `keep` of them, those `keeping` says still exist.
    static func text(_ order: [UUID], keeping: (UUID) -> Bool = { _ in true }) -> String {
        var seen = Set<UUID>()
        let newestLast = order.reversed().filter { seen.insert($0).inserted }.reversed().filter(keeping)
        return newestLast.suffix(keep).map(\.uuidString).joined(separator: ",")
    }

    /// A set of ids, in the order the ones already kept (`after`) were added, new ones after them.
    static func text(_ ids: Set<UUID>, after previous: String?, keeping: (UUID) -> Bool = { _ in true }) -> String {
        let before = order(previous).filter(ids.contains)
        let added = ids.subtracting(before).sorted { $0.uuidString < $1.uuidString }
        return text(before + added, keeping: keeping)
    }

    /// Whether a task is still there (anything goes while nothing is loaded yet).
    static func tasks(on board: TaskBoard) -> (UUID) -> Bool {
        board.items.isEmpty ? { _ in true } : { board.find($0) != nil }
    }

    static func groups(on board: TaskBoard) -> (UUID) -> Bool {
        let ids = Set(board.groups.map(\.id))
        return board.items.isEmpty ? { _ in true } : { ids.contains($0) }
    }
}

/// "Show in tree": the task Tasks brings into view, and the Show choice to use until another is picked (nil: the one
/// picked already shows it).
public struct TreeReveal: Equatable {
    public let id: UUID
    public let show: OutlineShow?
}

extension ShellState {
    /// "Show in tree" (a task's details): Tasks, with the task's group and parents open and its row picked and in view.
    /// A zoom that doesn't hold it is left; a group picked that isn't its own changes to its own; a Show choice that hides
    /// it gives way for now (not saved: Everything for a finished task, else To do).
    public func showInTree(_ id: UUID, memory: OutlineMemory = OutlineMemory()) {
        guard let item = model.board.find(id), !item.isArchived else { return }
        // The details' sheet going away (a phone) unpicks the row: not this time.
        keepSelection = id
        closeDetails()
        if let picked = model.selectedGroupId, picked != item.groupId { model.select(group: item.groupId) }
        if let zoom = memory.zoom, !item.ancestors.contains(where: { $0.id == zoom }) { memory.zoom = nil }
        memory.set(expanded: memory.expanded.union(item.ancestors.map(\.id)), closedGroups: memory.closedGroups.subtracting([item.groupId]), on: model.board)
        // A search that leaves it out goes (and says so); one that keeps it stays.
        if !model.searchText.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty,
           !Outline.kept(model.board, OutlineOptions(groupId: model.selectedGroupId, show: .all, query: model.searchText), now: model.now)(item) {
            model.searchText = ""
            model.status = "Cleared the search to show the task."
        }
        let show = memory.show
        let shown = Outline.kept(model.board, OutlineOptions(groupId: model.selectedGroupId, show: show), now: model.now)(item)
        view = .tasks
        selectedTask = id
        treeReveal = TreeReveal(id: id, show: shown ? nil : item.isDone ? .all : .todo)
    }
}

/// "Show in tree" in a task's details (nothing for an archived task, or a view shown on its own).
struct ShowInTreeButton: View {
    let item: WorkItem
    @Environment(\.shellState) private var shell

    var body: some View {
        if let shell, !item.isArchived {
            Button { shell.showInTree(item.id) } label: { Label("Show in tree", systemImage: "list.bullet.indent") }
                .accessibilityIdentifier("detail-show-in-tree")
        }
    }
}
#endif
