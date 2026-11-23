#if canImport(SwiftUI)
import Foundation
import OneThingKit

/// Labels in Settings (rename, recolor, remove) and a task's look and place in its details (tags, labels, icon, color,
/// Move…): on the board on this device, or through the server on a Mac. The same rules everywhere
/// (tests/fixtures/labels.json, the web's Move dialog).
extension BoardModel {
    /// The same name, whatever the case (as the cores compare labels and tags).
    static func same(_ a: String, _ b: String) -> Bool { a.caseInsensitiveCompare(b) == .orderedSame }

    /// The board's labels, in the order they were made.
    public var labels: [LabelDefinition] { board.labels }

    /// How many tasks have the label (Settings says it before removing it).
    public func taskCount(withLabel name: String) -> Int {
        board.allItems.filter { $0.labels.contains { Self.same($0, name) } }.count
    }

    /// Renames a label: every task with it gets the new name.
    public func renameLabel(_ name: String, to newName: String) {
        guard let label = board.findLabel(name) else { return }
        let clean = newName.trimmingCharacters(in: .whitespaces)
        guard !clean.isEmpty, clean != label.name else { return }
        let color = label.color
        change("Renamed", server: .updateLabel(label.name, name: clean, color: color)) { try board.updateLabel(name, newName: clean, color: color, now: $0) }
    }

    /// A label's color (tasks without a color of their own show it).
    public func recolorLabel(_ name: String, to color: String) {
        guard let label = board.findLabel(name), label.color.lowercased() != color.lowercased() else { return }
        let current = label.name
        change("\(current): new color", server: .updateLabel(current, name: current, color: color)) { try board.updateLabel(current, newName: current, color: color, now: $0) }
    }

    /// Takes a label off every task and out of the list (the tasks stay).
    public func removeLabel(_ name: String) {
        guard let label = board.findLabel(name) else { return }
        let current = label.name
        change("Removed the label “\(current)”", server: .deleteLabel(current)) { try board.deleteLabel(current, now: $0) }
    }

    /// A task's tags, as given (cleaned; each once).
    public func setTags(_ id: UUID, _ tags: [String]) {
        change(tags.isEmpty ? "No tags" : "Tags saved", server: .setTags(id, tags)) { try board.setTags(id, tags, now: $0) }
    }

    /// Adds tags typed in a box ("#deep work, errands" or one per word): what's there stays.
    public func addTags(_ id: UUID, typed: String) {
        guard let item = board.find(id) else { return }
        let new = typed.split(whereSeparator: { $0 == "," || $0 == "\n" }).map { $0.trimmingCharacters(in: .whitespaces) }.filter { !$0.isEmpty }
        guard !new.isEmpty else { return }
        setTags(id, item.tags + new)
    }

    public func removeTag(_ id: UUID, _ tag: String) {
        guard let item = board.find(id) else { return }
        setTags(id, item.tags.filter { !Self.same($0, tag) })
    }

    /// A label typed under "New label": on the task (a new name makes a new label, with the next color). One it has
    /// already (any case) stays on.
    public func addLabel(_ id: UUID, _ name: String) {
        guard let item = board.find(id), let next = TaskBoard.labels(item.labels, adding: name) else { return }
        change("Label on", server: .setLabels(id, next)) { try board.setLabels(id, next, now: $0) }
    }

    /// A task's labels: on or off one (a new name makes a new label, with the next color).
    public func toggleLabel(_ id: UUID, _ name: String) {
        guard let item = board.find(id) else { return }
        let has = item.labels.contains { Self.same($0, name) }
        let next = has ? item.labels.filter { !Self.same($0, name) } : item.labels + [name.trimmingCharacters(in: .whitespaces)]
        change(has ? "Label off" : "Label on", server: .setLabels(id, next)) { try board.setLabels(id, next, now: $0) }
    }

    /// An emoji before the title (nil or blank: none).
    public func setIcon(_ id: UUID, _ icon: String?) {
        let clean = icon?.trimmingCharacters(in: .whitespacesAndNewlines)
        change(clean?.isEmpty == false ? "Icon set" : "No icon", server: .setIcon(id, clean)) { try board.setIcon(id, clean, now: $0) }
    }

    /// The task's own color, #rrggbb (nil: none; it shows its first label's).
    public func setColor(_ id: UUID, _ color: String?) {
        change(color == nil ? "No color of its own" : "Color set", server: .setColor(id, color)) { try board.setColor(id, color, now: $0) }
    }

    /// Where Move… can put a task in a group: on its own, or under any open task there (not itself or its subtasks).
    public func moveTargets(_ id: UUID, groupId: UUID, query: String = "") -> [MoveTarget] {
        guard let item = board.find(id) else { return [] }
        return MoveTargets.options(board, moving: item, groupId: groupId, query: query)
    }

    /// Move…: on its own in `groupId`, or under `parentId`.
    public func move(_ id: UUID, groupId: UUID, under parentId: UUID?) {
        guard let item = board.find(id), let move = MoveTargets.move(item, groupId: groupId, parentId: parentId) else { return }
        let sameGroup = groupId == item.groupId
        let name = board.groups.first { $0.id == groupId }?.name ?? "the group"
        change(sameGroup || parentId != nil ? "Moved" : "Moved to \(name)", server: .boardMove(move)) { try board.apply(move, now: $0) }
    }
}
#endif
