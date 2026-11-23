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

    /// The open tasks (kept to the newest few hundred).
    public var expanded: Set<UUID> {
        get { Self.ids(defaults.string(forKey: Self.expandedKey)) }
        nonmutating set { defaults.set(Self.text(newValue), forKey: Self.expandedKey) }
    }

    public var closedGroups: Set<UUID> {
        get { Self.ids(defaults.string(forKey: Self.closedGroupsKey)) }
        nonmutating set { defaults.set(Self.text(newValue), forKey: Self.closedGroupsKey) }
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

    static func ids(_ text: String?) -> Set<UUID> {
        Set((text ?? "").split(separator: ",").compactMap { UUID(uuidString: String($0)) })
    }

    static func text(_ ids: Set<UUID>) -> String { ids.map(\.uuidString).sorted().suffix(500).joined(separator: ",") }
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
        closeDetails()
        if let picked = model.selectedGroupId, picked != item.groupId { model.select(group: item.groupId) }
        if let zoom = memory.zoom, !item.ancestors.contains(where: { $0.id == zoom }) { memory.zoom = nil }
        memory.expanded = memory.expanded.union(item.ancestors.map(\.id))
        memory.closedGroups = memory.closedGroups.subtracting([item.groupId])
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
