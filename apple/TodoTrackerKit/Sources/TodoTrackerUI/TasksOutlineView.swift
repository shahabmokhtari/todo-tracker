#if canImport(SwiftUI)
import SwiftUI
import TodoTrackerKit

/// A row of the outline: a task, how deep it is, and whether its subtasks are shown.
struct OutlineRow: Identifiable {
    let item: WorkItem
    let depth: Int
    let hasChildren: Bool
    let expanded: Bool
    var id: UUID { item.id }

    /// Rows shown, depth first: closed tasks hide their subtasks; finished ones are hidden unless `showDone`
    /// (outlinemodel.js flatten).
    static func flatten(_ roots: [WorkItem], expanded: Set<UUID>, showDone: Bool) -> [OutlineRow] {
        var rows: [OutlineRow] = []
        func walk(_ items: [WorkItem], _ depth: Int) {
            for item in items where showDone || !item.isDone {
                let open = expanded.contains(item.id)
                rows.append(OutlineRow(item: item, depth: depth, hasChildren: item.children.contains { showDone || !$0.isDone }, expanded: open))
                if open { walk(item.children, depth + 1) }
            }
        }
        walk(roots, 0)
        return rows
    }
}

/// Where a new task is being typed: right after a task (as its sibling), first under one, or at the top.
struct AddPlace: Equatable {
    var after: UUID?
    var parent: UUID?
}

/// Tasks (the web's outline.js): every task of the group as an outline. Return renames the selected task, and Return
/// while typing adds the next one below; Tab and Shift-Tab nest and un-nest; Option-up and down reorder; Space finishes;
/// drag a row before, after or into another (or use its Move menu).
struct TasksOutlineView: View {
    @ObservedObject var model: BoardModel
    @Environment(\.shellState) private var shell
    @AppStorage("tt.outline.expanded") private var expandedText = ""
    @AppStorage("tt.outline.showDone") private var showDone = false
    @State private var editing: UUID?
    @State private var titleDraft = ""
    @State private var adding: AddPlace?
    @State private var newTitle = ""
    @FocusState private var field: Field?
    @Environment(\.horizontalSizeClass) private var sizeClass

    enum Field: Hashable { case title(UUID), new }

    private var expanded: Set<UUID> {
        Set(expandedText.split(separator: ",").compactMap { UUID(uuidString: String($0)) })
    }

    private func setExpanded(_ id: UUID, _ open: Bool) {
        var set = expanded
        if open { set.insert(id) } else { set.remove(id) }
        // Kept short (the newest few hundred), like the web app's.
        expandedText = set.map(\.uuidString).suffix(500).joined(separator: ",")
    }

    private var selection: Binding<UUID?> {
        Binding(get: { shell?.selectedTask }, set: { shell?.selectedTask = $0 })
    }

    var body: some View {
        let rows = OutlineRow.flatten(model.matchingRoots, expanded: expanded, showDone: showDone)
        List(selection: selection) {
            if !model.searchText.isEmpty { SearchFilterBar(model: model).listRowSeparator(.hidden) }
            if adding == AddPlace() { newRow(depth: 0) }
            if rows.isEmpty && adding == nil {
                Text("No tasks here yet. Press ⌘N or + to add one.").foregroundStyle(.secondary)
            }
            ForEach(Array(rows.enumerated()), id: \.element.id) { index, row in
                rowView(row)
                    .tag(row.id)
                if adding?.parent == row.id && adding?.after == nil { newRow(depth: row.depth + 1) }
                if let spot = newRowSpot(rows), spot.index == index { newRow(depth: spot.depth) }
            }
        }
        .listStyle(.plain)
        .environment(\.defaultMinListRowHeight, 34)
        .onKeyPress(phases: .down) { press in key(press) }
        .toolbar {
            ToolbarItemGroup(placement: .secondaryAction) {
                Toggle(isOn: $showDone) { Label("Show finished", systemImage: "checkmark.circle") }
                Button { expandAll() } label: { Label("Expand all", systemImage: "arrow.down.right.and.arrow.up.left") }
                Button { expandedText = "" } label: { Label("Collapse all", systemImage: "arrow.up.left.and.arrow.down.right") }
            }
            ToolbarItem(placement: .primaryAction) {
                Button { startAdding(AddPlace(after: selection.wrappedValue, parent: selection.wrappedValue.flatMap { model.board.find($0)?.parent?.id })) } label: {
                    Label("New task here", systemImage: "text.badge.plus")
                }
                .help("A new task below the selected one (Return while typing adds the next)")
            }
        }
    }

    // MARK: Rows

    private func rowView(_ row: OutlineRow) -> some View {
        let item = row.item
        let timing = model.runningTimer?.item.id == item.id
        return HStack(spacing: 6) {
            Button { setExpanded(item.id, !row.expanded) } label: {
                Image(systemName: "chevron.right")
                    .font(.caption.weight(.bold))
                    .rotationEffect(.degrees(row.expanded ? 90 : 0))
                    .frame(width: 16)
                    .opacity(row.hasChildren ? 1 : 0)
            }
            .buttonStyle(.borderless)
            .disabled(!row.hasChildren)
            .accessibilityLabel(row.expanded ? "Hide subtasks of \(item.title)" : "Show subtasks of \(item.title)")
            Button { model.toggleDone(item.id) } label: {
                Image(systemName: item.isDone ? "checkmark.circle.fill" : "circle")
                    .foregroundStyle(item.isDone ? Color.green : Color(hex: item.priority.hexColor))
            }
            .buttonStyle(.borderless)
            .accessibilityLabel(item.isDone ? "Reopen \(item.title)" : "Complete \(item.title)")
            if sizeClass == .compact {
                // A narrow screen: the title gets the row, what it says about the task goes below it.
                VStack(alignment: .leading, spacing: 3) {
                    title(item)
                    if !item.labels.isEmpty || !item.tags.isEmpty || item.repeatRule != nil || item.deadline != nil || !item.children.isEmpty || !item.notes.isEmpty {
                        HStack(spacing: 6) {
                            chips(item)
                            facts(item, timing: timing)
                        }
                    }
                }
                Spacer(minLength: 4)
            } else {
                title(item)
                chips(item)
                Spacer(minLength: 8)
                facts(item, timing: timing)
            }
            Menu {
                Button { model.toggleTimer(item.id) } label: { Label(timing ? "Stop the timer" : "Start the timer", systemImage: timing ? "stop.circle" : "stopwatch") }
                    .disabled(item.isDone)
                Button {
                    setExpanded(item.id, true)
                    startAdding(AddPlace(after: nil, parent: item.id))
                } label: { Label("Add a subtask", systemImage: "plus") }
                Button { shell?.open(item.id) } label: { Label("Open details", systemImage: "sidebar.right") }
                Divider()
                Button { model.step(item.id, by: -1, showDone: showDone) } label: { Label("Up", systemImage: "arrow.up") }
                Button { model.step(item.id, by: 1, showDone: showDone) } label: { Label("Down", systemImage: "arrow.down") }
                Button { indent(item.id) } label: { Label("Into the task above", systemImage: "increase.indent") }
                    .disabled(!model.canIndent(item.id, showDone: showDone))
                Button { model.outdent(item.id) } label: { Label("Out one level", systemImage: "decrease.indent") }
                    .disabled(!model.canOutdent(item.id))
            } label: { Image(systemName: "ellipsis.circle") }
                .menuStyle(.button)
                .buttonStyle(.borderless)
                .menuIndicator(.hidden)
                .fixedSize()
                .foregroundStyle(.secondary)
                .accessibilityLabel("Actions for \(item.title)")
        }
        .padding(.leading, CGFloat(row.depth) * 22)
        .contentShape(Rectangle())
        .contextMenu { TaskContextMenu(model: model, id: item.id) }
        .draggable(TaskDrag.payload(item.id)) { Text(item.title).padding(6) }
        // A drop on the left half goes after it (a sibling); on the right, inside it.
        .dropDestination(for: String.self) { texts, point in
            let ids = TaskDrag.ids(texts)
            for id in ids {
                let zone: OutlineMoves.Zone = point.x > 160 + CGFloat(row.depth) * 22 ? .inside : .after
                if model.drop(id, on: item.id, zone), zone == .inside { setExpanded(item.id, true) }
            }
            return !ids.isEmpty
        }
    }

    @ViewBuilder private func title(_ item: WorkItem) -> some View {
        if editing == item.id {
            TextField("Title", text: $titleDraft)
                .textFieldStyle(.plain)
                .focused($field, equals: .title(item.id))
                .onSubmit {
                    // Return: saved, and the next task is typed below it.
                    finishRename(item.id)
                    startAdding(AddPlace(after: item.id, parent: item.parent?.id))
                }
                .onKeyPress(.escape) {
                    editing = nil
                    return .handled
                }
                .onKeyPress(.tab, phases: .down) { press in nest(item.id, out: press.modifiers.contains(.shift)) }
        } else {
            Text(Presentation.titleWithIcon(item))
                .strikethrough(item.isDone)
                .foregroundStyle(item.isDone ? .secondary : .primary)
                .lineLimit(2)
                // A click on the selected task's title renames it (like the web's title box).
                .onTapGesture {
                    if selection.wrappedValue == item.id { startRename(item) } else { selection.wrappedValue = item.id }
                }
                .onTapGesture(count: 2) { shell?.open(item.id) }
        }
    }

    @ViewBuilder private func chips(_ item: WorkItem) -> some View {
        if item.repeatRule != nil { Image(systemName: "repeat").font(.caption).foregroundStyle(.secondary) }
        ForEach(item.labels.prefix(3), id: \.self) { LabelChip(name: $0, color: model.labelColor($0)) }
        ForEach(item.tags.prefix(2), id: \.self) { TagChip(tag: $0) }
    }

    @ViewBuilder private func facts(_ item: WorkItem, timing: Bool) -> some View {
        HStack(spacing: 8) {
            if let due = item.deadline, !item.isDone {
                Label(RelativeTime.format(due, now: model.now), systemImage: "calendar")
                    .foregroundStyle(due < model.now ? Color(hex: "#ef4444") : .secondary)
            }
            if !item.children.isEmpty {
                let all = item.selfAndDescendants.dropFirst()
                Text("\(all.filter(\.isDone).count)/\(all.count)").foregroundStyle(.secondary)
            }
            let spent = item.timeSpent(model.now)
            if spent >= 60 || timing {
                Label(Presentation.duration(spent), systemImage: "timer").foregroundStyle(timing ? Theme.accent : .secondary)
            }
            if !item.notes.isEmpty { Label("\(item.notes.count)", systemImage: "note.text").foregroundStyle(.secondary) }
        }
        .font(.caption)
        .labelStyle(.titleAndIcon)
        .lineLimit(1)
        .fixedSize()
    }

    private func newRow(depth: Int) -> some View {
        HStack(spacing: 6) {
            Color.clear.frame(width: 16, height: 1)
            Image(systemName: "circle").foregroundStyle(.tertiary)
            TextField("New task", text: $newTitle)
                .textFieldStyle(.plain)
                .focused($field, equals: .new)
                .onSubmit { addTyped() }
                .onKeyPress(.escape) {
                    adding = nil
                    return .handled
                }
        }
        .padding(.leading, CGFloat(depth) * 22)
        .onAppear { field = .new }
    }

    /// Where the new task's box goes when it's typed after a task: after that task and everything shown under it.
    private func newRowSpot(_ rows: [OutlineRow]) -> (index: Int, depth: Int)? {
        guard let after = adding?.after, let at = rows.firstIndex(where: { $0.id == after }) else { return nil }
        var end = at + 1
        while end < rows.count && rows[end].depth > rows[at].depth { end += 1 }
        return (end - 1, rows[at].depth)
    }

    // MARK: Actions

    private func startRename(_ item: WorkItem) {
        titleDraft = item.title
        editing = item.id
        field = .title(item.id)
    }

    private func finishRename(_ id: UUID) {
        model.rename(id, to: titleDraft)
        editing = nil
    }

    private func startAdding(_ place: AddPlace) {
        if let editing { finishRename(editing) }
        newTitle = ""
        adding = place
        field = .new
    }

    /// Return in the new task's box: added in place, and the box moves below it for the next one (empty: done adding).
    private func addTyped() {
        guard let place = adding else { return }
        let title = newTitle
        guard !title.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty else {
            adding = nil
            return
        }
        newTitle = ""
        if let parent = place.parent { setExpanded(parent, true) }
        model.addTask(title, parentId: place.parent, after: place.after, stage: place.parent == nil ? .next : nil) { id in
            guard let id else { return }
            adding = AddPlace(after: id, parent: place.parent)
            selection.wrappedValue = id
            field = .new
        }
    }

    private func indent(_ id: UUID) {
        if let target = OutlineMoves.indent(model.board, id, showDone: showDone)?.first, case let .move(_, parent?, _, _) = target {
            setExpanded(parent, true)
        }
        model.indent(id, showDone: showDone)
    }

    /// Tab nests under the task above, Shift-Tab un-nests; when it can't, Tab moves on as anywhere else.
    private func nest(_ id: UUID, out: Bool) -> KeyPress.Result {
        if out ? model.canOutdent(id) : model.canIndent(id, showDone: showDone) {
            if out { model.outdent(id) } else { indent(id) }
            return .handled
        }
        return .ignored
    }

    private func expandAll() {
        var set = expanded
        for item in model.matchingRoots.flatMap(\.selfAndDescendants) where !item.children.isEmpty { set.insert(item.id) }
        expandedText = set.map(\.uuidString).suffix(500).joined(separator: ",")
    }

    /// The keys on the selected row (not while typing): Return renames, Space finishes, Tab nests,
    /// Option-arrows reorder, Left and Right close and open.
    private func key(_ press: KeyPress) -> KeyPress.Result {
        guard editing == nil, field != .new, let id = selection.wrappedValue, let item = model.board.find(id) else { return .ignored }
        switch press.key {
        case .return where press.modifiers.contains(.command):
            // ⌘Return is Mark Done (the menu's); ⌘I opens the details.
            return .ignored
        case .return:
            startRename(item)
        case .space:
            model.toggleDone(id)
        case .tab:
            return nest(id, out: press.modifiers.contains(.shift))
        case .upArrow where press.modifiers.contains(.option):
            model.step(id, by: -1, showDone: showDone)
        case .downArrow where press.modifiers.contains(.option):
            model.step(id, by: 1, showDone: showDone)
        case .rightArrow:
            if !item.children.isEmpty { setExpanded(id, true) } else { return .ignored }
        case .leftArrow:
            if expanded.contains(id) { setExpanded(id, false) } else if let parent = item.parent { selection.wrappedValue = parent.id } else { return .ignored }
        default:
            return .ignored
        }
        return .handled
    }
}
#endif
