import Foundation

/// A place a task can go in its details' Move…: on its own (a top-level task of the group), or under a task.
public struct MoveTarget: Equatable, Identifiable, Sendable {
    /// The task to go under (nil: on its own).
    public let parentId: UUID?
    public let title: String
    /// How deep it is in the tree (0: a top-level task), for indenting.
    public let depth: Int

    public var id: String { parentId?.uuidString ?? "own" }
}

/// Move… in a task's details (the web app's Move dialog, app.js moveDialog): pick a group, then on its own or under
/// any open task of that group, never itself or one of its own subtasks.
public enum MoveTargets {
    /// Where `item` can go in `groupId`, in tree order: "On its own" first. `query` narrows the tasks (in titles).
    public static func options(_ board: TaskBoard, moving item: WorkItem, groupId: UUID, query: String = "") -> [MoveTarget] {
        let inside = Set(item.selfAndDescendants.map(\.id))
        let q = query.trimmingCharacters(in: .whitespaces).lowercased()
        var result = [MoveTarget(parentId: nil, title: "On its own", depth: 0)]
        func walk(_ nodes: [WorkItem], _ depth: Int) {
            for node in nodes where !inside.contains(node.id) {
                if q.isEmpty || node.title.lowercased().contains(q) {
                    result.append(MoveTarget(parentId: node.id, title: node.title, depth: q.isEmpty ? depth : 0))
                }
                walk(node.children.filter { !$0.isDone }, depth + 1)
            }
        }
        walk(board.items.filter { $0.groupId == groupId && !$0.isDone && $0.archivedAt == nil }, 0)
        return result
    }

    /// The move picking `parentId` in `groupId` makes (nil: it's already there, nothing to do).
    public static func move(_ item: WorkItem, groupId: UUID, parentId: UUID?) -> BoardMove? {
        if let parentId {
            return parentId == item.parent?.id ? nil : .move(item.id, parentId: parentId, groupId: nil, index: nil)
        }
        let sameGroup = groupId == item.groupId
        return item.parent != nil || !sameGroup ? .move(item.id, parentId: nil, groupId: groupId, index: nil) : nil
    }

    /// Where it is now, as Move… starts: its parent, in its own group.
    public static func current(_ item: WorkItem) -> (groupId: UUID, parentId: UUID?) { (item.groupId, item.parent?.id) }
}
