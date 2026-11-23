#if canImport(SwiftUI)
import SwiftUI
import OneThingKit

/// Where a new task is being typed: right after a task (as its sibling), first under one, first in a group, or at the top.
struct AddPlace: Equatable {
    var after: UUID?
    var parent: UUID?
    var group: UUID?
}

/// Tasks (the web's outline.js; the rules are OneThingKit's `Outline`). Under All groups the top level is the groups;
/// a parent shows how far along it is; ⤢ (or Option-Return) zooms into a branch, with a path back (Back, or Escape on a
/// keyboard, goes up one level). Return renames the selected task, and Return while typing adds the next one below; Tab
/// and Shift-Tab nest and un-nest; Option-up and down reorder; Space finishes; drag a row before, after or into another,
/// or onto a group (or use its ⋯ menu).
struct TasksOutlineView: View {
    @ObservedObject var model: BoardModel
    @Environment(\.shellState) private var shell
    @AppStorage(OutlineMemory.expandedKey) private var expandedText = ""
    @AppStorage(OutlineMemory.closedGroupsKey) private var closedText = ""
    @AppStorage(OutlineMemory.showKey) private var showText = ""
    @AppStorage(OutlineMemory.zoomKey) private var zoomText = ""
    @AppStorage(OutlineMemory.showDoneKey) private var oldShowDone = false
    /// A Show choice for now (Show in tree, for a task the one picked hides), until another is picked.
    @State private var showForNow: OutlineShow?
    @State private var editing: UUID?
    @State private var titleDraft = ""
    @State private var adding: AddPlace?
    @State private var newTitle = ""
    /// The row Show in tree brought into view, highlighted for a moment.
    @State private var highlighted: UUID?
    @FocusState private var field: Field?
    @Environment(\.horizontalSizeClass) private var sizeClass

    enum Field: Hashable { case title(UUID), new }

    /// What's drawn: the options with the zoom that can be shown, the rows and the path.
    private struct Drawn {
        let options: OutlineOptions
        let rows: [OutlineRow]
        let path: [OutlinePathPart]
        /// Under All groups and not zoomed: the groups are the top level.
        let grouped: Bool
        /// The zoom to keep (what's remembered): the one asked unless the task is really gone.
        let remembered: UUID?
        var zoom: UUID? { options.zoomId }
        func row(_ id: UUID) -> OutlineRow? { rows.first { $0.id == id } }
    }

    private var compact: Bool { sizeClass == .compact }
    private var step: CGFloat { compact ? 18 : 22 }
    private var expanded: Set<UUID> { OutlineMemory.ids(expandedText) }
    private var closedGroups: Set<UUID> { OutlineMemory.ids(closedText) }
    private var show: OutlineShow { showForNow ?? OutlineShow.initial(saved: showText, showDone: oldShowDone) }

    private var drawn: Drawn {
        var options = OutlineOptions(groupId: model.selectedGroupId, zoomId: UUID(uuidString: zoomText), show: show,
                                     query: model.searchText, expanded: expanded, closedGroups: closedGroups)
        let remembered = Outline.rememberedZoom(model.board, options)
        options.zoomId = Outline.resolveZoom(model.board, options, now: model.now)
        return Drawn(options: options, rows: Outline.rows(model.board, options, now: model.now), path: Outline.path(model.board, options, now: model.now),
                     grouped: options.groupId == nil && options.zoomId == nil, remembered: remembered)
    }

    /// Opened last is kept longest; tasks that are gone are forgotten.
    private func setExpanded(_ id: UUID, _ open: Bool) {
        let order = OutlineMemory.order(expandedText).filter { $0 != id } + (open ? [id] : [])
        expandedText = OutlineMemory.text(order, keeping: OutlineMemory.tasks(on: model.board))
    }

    private func setExpanded(_ ids: Set<UUID>) {
        expandedText = OutlineMemory.text(ids, after: expandedText, keeping: OutlineMemory.tasks(on: model.board))
    }

    private func setGroupOpen(_ id: UUID, _ open: Bool) {
        let order = OutlineMemory.order(closedText).filter { $0 != id } + (open ? [] : [id])
        closedText = OutlineMemory.text(order, keeping: OutlineMemory.groups(on: model.board))
    }

    private var selection: Binding<UUID?> {
        Binding(get: { shell?.selectedTask }, set: { shell?.selectedTask = $0 })
    }

    var body: some View {
        let now = drawn
        ScrollViewReader { proxy in
            List(selection: selection) {
                if !model.searchText.isEmpty { SearchFilterBar(model: model).listRowSeparator(.hidden) }
                if editorAtTop(now) { newRow(level: 0) }
                if now.grouped {
                    groupSections(now)
                } else {
                    taskRows(now.rows, now, base: 0)
                }
                if now.rows.isEmpty && adding == nil { emptyText(now).listRowSeparator(.hidden) }
            }
            .listStyle(.plain)
            .contentMargins(.top, 4, for: .scrollContent)
            .environment(\.defaultMinListRowHeight, 34)
            .safeAreaInset(edge: .top, spacing: 0) { header(now) }
            .onKeyPress(phases: .down) { press in key(press, now) }
            .onChange(of: ZoomCheck(asked: zoomText, remembered: now.remembered), initial: true) { _, check in fixZoom(check) }
            .modifier(RevealWatcher(shell: shell) { reveal($0, proxy) })
            .onAppear { OutlineMemory().migrate() }
            .toolbar {
                ToolbarItem(placement: .primaryAction) {
                    Button { newTask(now) } label: { Label("New task here", systemImage: "text.badge.plus") }
                        .help(now.zoom == nil ? "A new task below the selected one (Return while typing adds the next)" : "A new task inside the task you're looking at")
                }
            }
        }
    }

    // MARK: The parts

    private func editorAtTop(_ now: Drawn) -> Bool {
        guard let adding, adding.after == nil else { return false }
        if let zoom = now.zoom, adding.parent == zoom { return true }
        guard adding.parent == nil else { return false }
        // First in a group whose row isn't shown (a Show choice or a search leaves it out): at the top all the same.
        guard let group = adding.group, now.grouped else { return true }
        return !now.rows.contains { $0.kind == .group && $0.id == group }
    }

    /// Under All groups: a section per group, its row pinned at the top while its tasks scroll under it.
    @ViewBuilder private func groupSections(_ now: Drawn) -> some View {
        ForEach(Self.sections(now.rows), id: \.group.id) { section in
            Section {
                if section.group.expanded, let adding, adding.group == section.group.id, adding.after == nil, adding.parent == nil { newRow(level: 0) }
                taskRows(section.tasks, now, base: 1)
            } header: {
                groupRow(section.group)
            }
        }
    }

    /// The rows cut into groups (each group's row with the task rows under it).
    static func sections(_ rows: [OutlineRow]) -> [(group: OutlineRow, tasks: [OutlineRow])] {
        var sections: [(group: OutlineRow, tasks: [OutlineRow])] = []
        for row in rows {
            if row.kind == .group { sections.append((row, [])) } else if !sections.isEmpty { sections[sections.count - 1].tasks.append(row) }
        }
        return sections
    }

    @ViewBuilder private func taskRows(_ rows: [OutlineRow], _ now: Drawn, base: Int) -> some View {
        ForEach(Array(rows.enumerated()), id: \.element.id) { index, row in
            if let item = row.item {
                rowView(row, item, level: row.depth - base, now)
                    .tag(row.id)
                if adding?.parent == row.id && adding?.after == nil && row.id != now.zoom { newRow(level: row.depth - base + 1) }
                if let spot = newRowSpot(rows), spot.index == index { newRow(level: spot.depth - base) }
            }
        }
    }

    @ViewBuilder private func emptyText(_ now: Drawn) -> some View {
        Group {
            if now.options.searching {
                Text("Nothing here matches the filter.")
            } else if now.zoom != nil {
                // Show narrows a zoom: what it leaves out, and how to see it.
                Text("\(Self.emptyHere(show)) \(compact ? "Tap Back to go up." : "Press Esc to go up.")")
            } else if show == .now {
                Text("Nothing can be done right now.")
            } else if show == .waiting {
                Text("Nothing is waiting.")
            } else {
                Text("No tasks here yet. Press ⌘N or + to add one.")
            }
        }
        .foregroundStyle(.secondary)
    }

    /// A zoom with nothing under it the Show choice keeps.
    static func emptyHere(_ show: OutlineShow) -> String {
        switch show {
        case .todo: return "Nothing left to do here. Show › Everything shows what’s done."
        case .now: return "Nothing here can be done right now. Show › To do shows the rest."
        case .waiting: return "Nothing here is waiting. Show › To do shows the rest."
        case .all: return "Nothing under this task."
        }
    }

    // MARK: Above the tree: Show, Open to, and the path while zoomed

    private func header(_ now: Drawn) -> some View {
        VStack(alignment: .leading, spacing: 0) {
            controls(now)
            if !now.path.isEmpty { pathBar(now) }
            Divider()
        }
        .background(.bar)
    }

    private var showChoice: Binding<OutlineShow> {
        Binding(get: { show }, set: {
            showText = $0.rawValue
            showForNow = nil
        })
    }

    private func controls(_ now: Drawn) -> some View {
        let levels = OutlineLevel.allCases.filter { $0 != .groups || now.grouped }
        return HStack(spacing: 10) {
            HStack(spacing: 2) {
                Text("Show").foregroundStyle(.secondary)
                Picker("Show", selection: showChoice) {
                    ForEach(OutlineShow.allCases) { Text($0.label).tag($0) }
                }
                .pickerStyle(.menu)
                .labelsHidden()
                .fixedSize()
                .accessibilityLabel("Show")
                .accessibilityIdentifier("outline-show")
            }
            if compact {
                Menu {
                    ForEach(levels) { level in Button(level.label) { openTo(level, now) } }
                } label: {
                    Label("Open to", systemImage: "list.bullet.indent").labelStyle(.titleAndIcon)
                }
                .fixedSize()
                .accessibilityIdentifier("outline-open-to")
            } else {
                HStack(spacing: 4) {
                    Text("Open to").foregroundStyle(.secondary)
                    ForEach(levels) { level in
                        Button(level.label) { openTo(level, now) }
                            .buttonStyle(.bordered)
                            .controlSize(.small)
                            .help("Open the tree to \(level.label.lowercased())")
                    }
                }
                .accessibilityElement(children: .contain)
                .accessibilityLabel("Open to")
                .accessibilityIdentifier("outline-open-to")
            }
            Spacer(minLength: 0)
        }
        .font(.subheadline)
        .padding(.horizontal, 16)
        .padding(.vertical, 6)
    }

    /// All groups › group › each parent › the task zoomed into (with its progress; it opens its details).
    private func pathBar(_ now: Drawn) -> some View {
        HStack(spacing: 8) {
            if compact {
                Button { back(now) } label: { Label("Back", systemImage: "chevron.backward").labelStyle(.iconOnly) }
                    .buttonStyle(.bordered)
                    .buttonBorderShape(.circle)
                    .controlSize(.small)
                    .accessibilityLabel("Back out one level")
            }
            // One line: the parts before the last one give way (shortened) to the task you're in.
            HStack(spacing: 4) {
                ForEach(Array(now.path.enumerated()), id: \.offset) { index, part in
                    if index > 0 { Image(systemName: "chevron.right").font(.caption2).foregroundStyle(.tertiary) }
                    crumb(part, last: index == now.path.count - 1, now)
                        .layoutPriority(index == now.path.count - 1 ? 1 : 0)
                }
            }
            .padding(.vertical, 2)
            Spacer(minLength: 0)
        }
        .font(.subheadline)
        .padding(.horizontal, 16)
        .padding(.bottom, 6)
        .accessibilityElement(children: .contain)
        .accessibilityLabel("Where you are")
        .accessibilityIdentifier("outline-path")
    }

    @ViewBuilder private func crumb(_ part: OutlinePathPart, last: Bool, _ now: Drawn) -> some View {
        if last, let id = part.id {
            Button { shell?.open(id) } label: {
                HStack(spacing: 6) {
                    Text(part.title).fontWeight(.semibold).foregroundStyle(Color.primary).lineLimit(1).truncationMode(.middle)
                    if part.total > 0 { OutlineProgress(done: part.done, total: part.total) }
                }
            }
            .buttonStyle(.plain)
            .accessibilityLabel("\(part.title): open its details")
        } else {
            Button {
                switch part.kind {
                case .all:
                    zoom(to: nil)
                    if model.selectedGroupId != nil { model.select(group: nil) }
                case .group:
                    zoom(to: nil)
                    if let id = part.id, model.selectedGroupId != id { model.select(group: id) }
                case .task:
                    zoom(to: part.id)
                }
            } label: {
                if part.kind == .all {
                    // A phone has Back beside it: the icon is enough there.
                    if compact {
                        Image(systemName: "square.stack.3d.up")
                    } else {
                        Label(part.title, systemImage: "square.stack.3d.up").labelStyle(.titleAndIcon).lineLimit(1)
                    }
                } else {
                    Text(part.title).lineLimit(1).truncationMode(.tail)
                }
            }
            .buttonStyle(.borderless)
            // A group's name is short: it stays whole; the parents in between give way.
            .fixedSize(horizontal: part.kind != .task, vertical: false)
            .accessibilityLabel(part.kind == .all ? "All groups: leave the zoom" : part.title)
        }
    }

    // MARK: Rows

    private func groupRow(_ row: OutlineRow) -> some View {
        let name = row.title
        return HStack(spacing: 8) {
            chevron(open: row.expanded, shown: true) { setGroupOpen(row.id, !row.expanded) }
                .accessibilityLabel(row.expanded ? "Close \(name)" : "Open \(name)")
            Circle().fill(Color(hex: row.group?.color ?? "#6366f1")).frame(width: 9, height: 9)
            // Colors, not .primary/.secondary: a section header dims what's in it.
            Text(name).font(.subheadline.weight(.semibold)).foregroundStyle(Color.primary).lineLimit(1)
            Spacer(minLength: 8)
            Text(row.left > 0 ? "\(row.left) to do" : row.total > 0 ? "All done" : "No tasks yet")
                .font(.caption).foregroundStyle(Color.secondary).fixedSize()
            if row.total > 0 { OutlineProgress(done: row.done, total: row.total) }
            Button {
                setGroupOpen(row.id, true)
                startAdding(AddPlace(group: row.id))
            } label: { Image(systemName: "plus").font(.caption.weight(.semibold)) }
                .buttonStyle(.borderless)
                .foregroundStyle(Color.secondary)
                .accessibilityLabel("Add a task to \(name)")
        }
        .padding(.vertical, 4)
        .contentShape(Rectangle())
        .onTapGesture { setGroupOpen(row.id, !row.expanded) }
        // A task dropped on a group goes to the top of it.
        .dropDestination(for: String.self) { texts, _ in
            let ids = TaskDrag.ids(texts)
            for id in ids { model.moveToTop(id, ofGroup: row.id) }
            if !ids.isEmpty { setGroupOpen(row.id, true) }
            return !ids.isEmpty
        }
        .accessibilityElement(children: .contain)
        .accessibilityLabel("\(name), \(row.left) to do")
        .accessibilityIdentifier("outline-group")
    }

    private func chevron(open: Bool, shown: Bool, _ toggle: @escaping () -> Void) -> some View {
        Button(action: toggle) {
            Image(systemName: "chevron.right")
                .font(.caption.weight(.bold))
                .foregroundStyle(.secondary)
                .rotationEffect(.degrees(open ? 90 : 0))
                .frame(width: 16)
                .contentShape(Rectangle())
                .opacity(shown ? 1 : 0)
        }
        .buttonStyle(.borderless)
        .disabled(!shown)
    }

    private func rowView(_ row: OutlineRow, _ item: WorkItem, level: Int, _ now: Drawn) -> some View {
        let timing = model.runningTimer?.item.id == item.id
        return HStack(alignment: .firstTextBaseline, spacing: 6) {
            chevron(open: row.expanded, shown: row.hasChildren) { setExpanded(item.id, !row.expanded) }
                .accessibilityLabel(row.expanded ? "Hide subtasks of \(item.title)" : "Show subtasks of \(item.title)")
            Button { if let shell { shell.toggleDone(item.id) } else { model.toggleDone(item.id) } } label: {
                Image(systemName: item.isDone ? "checkmark.circle.fill" : "circle")
                    .foregroundStyle(item.isDone ? Color.green : Color(hex: item.priority.hexColor))
            }
            .buttonStyle(.borderless)
            .accessibilityLabel(item.isDone ? "Reopen \(item.title)" : "Complete \(item.title)")
            if compact {
                // A narrow screen: the title gets the row, what it says about the task goes below it.
                VStack(alignment: .leading, spacing: 3) {
                    title(row, item)
                    if !item.labels.isEmpty || !item.tags.isEmpty || item.repeatRule != nil || item.deadline != nil || row.total > 0 || !item.notes.isEmpty {
                        HStack(spacing: 6) {
                            chips(item)
                            facts(item, timing: timing)
                            if row.total > 0 { OutlineProgress(done: row.done, total: row.total) }
                        }
                    }
                }
                Spacer(minLength: 4)
            } else {
                title(row, item)
                chips(item)
                Spacer(minLength: 8)
                facts(item, timing: timing)
                if row.total > 0 { OutlineProgress(done: row.done, total: row.total) }
            }
            if !item.children.isEmpty {
                Button { zoom(to: item.id) } label: { Image(systemName: "arrow.up.left.and.arrow.down.right").font(.caption) }
                    .buttonStyle(.borderless)
                    .foregroundStyle(.secondary)
                    .help("Zoom in: show only this")
                    .accessibilityLabel("Zoom into \(item.title)")
                    .accessibilityIdentifier("outline-zoom")
            }
            menu(row, item, timing: timing)
        }
        .padding(.leading, CGFloat(level) * step)
        .padding(.vertical, 6)
        .background(alignment: .leading) { guides(level) }
        // Show in tree: the row lights up for a moment.
        .background(highlighted == item.id ? Theme.accent.opacity(0.18) : Color.clear, in: RoundedRectangle(cornerRadius: 6))
        .contentShape(Rectangle())
        .listRowInsets(EdgeInsets(top: 0, leading: compact ? 12 : 10, bottom: 0, trailing: compact ? 12 : 10))
        .taskRowActions(model, id: item.id) { shell?.open(item.id) }
        .draggable(TaskDrag.payload(item.id)) { Text(item.title).padding(6) }
        // A drop on the left half goes after it (a sibling); on the right, inside it.
        .dropDestination(for: String.self) { texts, point in
            let ids = TaskDrag.ids(texts)
            for id in ids {
                let zone: OutlineMoves.Zone = point.x > 160 + CGFloat(level) * step ? .inside : .after
                if model.drop(id, on: item.id, zone), zone == .inside { setExpanded(item.id, true) }
            }
            return !ids.isEmpty
        }
    }

    /// Thin lines down each level of indent.
    private func guides(_ level: Int) -> some View {
        HStack(spacing: 0) {
            ForEach(0..<max(level, 0), id: \.self) { _ in
                Rectangle()
                    .fill(Color.secondary.opacity(0.22))
                    .frame(width: 1)
                    .padding(.leading, 7.5)
                    .frame(width: step, alignment: .leading)
            }
        }
        .accessibilityHidden(true)
    }

    private func menu(_ row: OutlineRow, _ item: WorkItem, timing: Bool) -> some View {
        Menu {
            Button { model.toggleTimer(item.id) } label: { Label(timing ? "Stop the timer" : "Start the timer", systemImage: timing ? "stop.circle" : "stopwatch") }
                .disabled(item.isDone)
            Button {
                setExpanded(item.id, true)
                startAdding(AddPlace(after: nil, parent: item.id))
            } label: { Label("Add a subtask", systemImage: "plus") }
            Button { shell?.open(item.id) } label: { Label("Open details", systemImage: "sidebar.right") }
            if !item.children.isEmpty {
                Button { zoom(to: item.id) } label: { Label("Zoom in", systemImage: "arrow.up.left.and.arrow.down.right") }
            }
            Divider()
            Button { model.step(item.id, by: -1, show: show) } label: { Label("Up", systemImage: "arrow.up") }
            Button { model.step(item.id, by: 1, show: show) } label: { Label("Down", systemImage: "arrow.down") }
            Button { indent(item.id) } label: { Label("Into the task above", systemImage: "increase.indent") }
                .disabled(!model.canIndent(item.id, show: show))
            Button { model.outdent(item.id) } label: { Label("Out one level", systemImage: "decrease.indent") }
                .disabled(!row.canOutdent || !model.canOutdent(item.id))
        } label: { Image(systemName: "ellipsis.circle") }
            .menuStyle(.button)
            .buttonStyle(.borderless)
            .menuIndicator(.hidden)
            .fixedSize()
            .foregroundStyle(.secondary)
            .accessibilityLabel("Actions for \(item.title)")
    }

    @ViewBuilder private func title(_ row: OutlineRow, _ item: WorkItem) -> some View {
        if editing == item.id {
            // Renaming shows and saves the whole title.
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
                .onKeyPress(.tab, phases: .down) { press in nest(row, out: press.modifiers.contains(.shift)) }
        } else {
            Text(Self.titleWithIcon(row.title, item))
                .strikethrough(item.isDone)
                .foregroundStyle(item.isDone ? .secondary : .primary)
                .lineLimit(2)
                .help(row.title == row.fullTitle ? "" : row.fullTitle)
                // A click on the selected task's title renames it (like the web's title box).
                .onTapGesture {
                    if selection.wrappedValue == item.id { startRename(item) } else { selection.wrappedValue = item.id }
                }
                .onTapGesture(count: 2) { shell?.open(item.id) }
        }
    }

    /// The title as shown (without the parent's name it repeats), with the task's icon and its repeat mark.
    static func titleWithIcon(_ shown: String, _ item: WorkItem) -> String {
        let title = item.repeatRule == nil ? shown : "\(shown) 🔁"
        guard let icon = item.icon, !icon.isEmpty else { return title }
        return "\(icon) \(title)"
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

    private func newRow(level: Int) -> some View {
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
        .padding(.leading, CGFloat(level) * step)
        .onAppear { field = .new }
    }

    /// Where the new task's box goes when it's typed after a task: after that task and everything shown under it.
    private func newRowSpot(_ rows: [OutlineRow]) -> (index: Int, depth: Int)? {
        guard let after = adding?.after, let at = rows.firstIndex(where: { $0.id == after }) else { return nil }
        var end = at + 1
        while end < rows.count && rows[end].depth > rows[at].depth { end += 1 }
        return (end - 1, rows[at].depth)
    }

    // MARK: Zoom

    /// Into a task's branch (nil: out to the whole tree). A title being renamed is saved first, as leaving the box does.
    private func zoom(to id: UUID?) {
        if let editing { finishRename(editing) }
        adding = nil
        zoomText = id?.uuidString ?? ""
    }

    /// Out of the zoom to the whole tree (the group picked stays), with the task zoomed into open on the way and picked.
    private func zoomOut(_ now: Drawn) {
        guard let zoom = now.zoom else { return }
        if let item = model.board.find(zoom) {
            setExpanded(expanded.union(item.ancestors.map(\.id)))
            setGroupOpen(item.groupId, true)
        }
        self.zoom(to: nil)
        selection.wrappedValue = zoom
    }

    /// Back (a phone) and Escape: up one level, to the zoomed task's parent (the task it came from picked); from the
    /// first level out to the tree.
    private func back(_ now: Drawn) {
        if let up = Outline.zoomUp(model.board, now.options, now: model.now) {
            selection.wrappedValue = now.zoom
            zoom(to: up)
        } else {
            zoomOut(now)
        }
    }

    /// The zoom asked and the one to keep: when they differ, the task is really gone (deleted, archived, moved out of the
    /// group picked, without subtasks) and the zoom goes for good. A search or a Show choice only narrows for now.
    private struct ZoomCheck: Equatable {
        let asked: String
        let remembered: UUID?
    }

    private func fixZoom(_ check: ZoomCheck) {
        guard !model.board.items.isEmpty, check.remembered != UUID(uuidString: check.asked) else { return }
        zoomText = check.remembered?.uuidString ?? ""
    }

    // MARK: Actions

    private func openTo(_ level: OutlineLevel, _ now: Drawn) {
        let open = Outline.openTo(model.board, now.options, level: level, now: model.now)
        setExpanded(open.expanded)
        closedText = OutlineMemory.text(open.closedGroups, after: closedText, keeping: OutlineMemory.groups(on: model.board))
    }

    /// Show in tree: the Show choice it needs for now, and the row brought into view, picked and lit up for a moment. A
    /// title being renamed is saved first.
    private func reveal(_ request: TreeReveal, _ proxy: ScrollViewProxy) {
        if let choice = request.show { showForNow = choice }
        if let editing { finishRename(editing) }
        adding = nil
        DispatchQueue.main.async {
            shell?.treeReveal = nil
            selection.wrappedValue = request.id
            highlighted = request.id
            withAnimation { proxy.scrollTo(request.id, anchor: .center) }
            DispatchQueue.main.asyncAfter(deadline: .now() + 2.4) {
                if highlighted == request.id { withAnimation { highlighted = nil } }
            }
        }
    }

    /// New task: below the selected task when it's shown; else inside the zoom, else first in the group picked (all
    /// groups: the first group).
    private func newTask(_ now: Drawn) {
        if let id = selection.wrappedValue, let row = now.row(id), let item = row.item {
            startAdding(AddPlace(after: id, parent: item.parent?.id))
            return
        }
        let place = Outline.newTaskPlace(model.board, now.options, now: model.now)
        if let parent = place.parentId {
            startAdding(AddPlace(parent: parent))
        } else {
            setGroupOpen(place.groupId, true)
            startAdding(AddPlace(group: place.groupId))
        }
    }

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
        if let parent = place.parent, parent != UUID(uuidString: zoomText) { setExpanded(parent, true) }
        model.addTask(title, parentId: place.parent, after: place.after, group: place.group, stage: place.parent == nil ? .next : nil) { id in
            guard let id else { return }
            adding = AddPlace(after: id, parent: place.parent)
            selection.wrappedValue = id
            field = .new
        }
    }

    private func indent(_ id: UUID) {
        if let target = OutlineMoves.indent(model.board, id, options: drawn.options, now: model.now)?.first, case let .move(_, parent?, _, _) = target {
            setExpanded(parent, true)
        }
        model.indent(id, show: show)
    }

    /// Tab nests under the task above, Shift-Tab un-nests (not out of the zoom); when it can't, Tab moves on as anywhere
    /// else.
    private func nest(_ row: OutlineRow, out: Bool) -> KeyPress.Result {
        let id = row.id
        if out ? row.canOutdent && model.canOutdent(id) : model.canIndent(id, show: show) {
            if out { model.outdent(id) } else { indent(id) }
            return .handled
        }
        return .ignored
    }

    /// The keys on the selected row (not while typing): Return renames, Option-Return zooms in, Space finishes, Tab
    /// nests, Option-arrows reorder, Left and Right close and open; Escape goes up one level of the zoom.
    private func key(_ press: KeyPress, _ now: Drawn) -> KeyPress.Result {
        guard editing == nil, field != .new else { return .ignored }
        if press.key == .escape {
            guard now.zoom != nil else { return .ignored }
            back(now)
            return .handled
        }
        guard let id = selection.wrappedValue, let item = model.board.find(id), let row = now.row(id) else { return .ignored }
        switch press.key {
        case .return where press.modifiers.contains(.command):
            // ⌘Return is Mark Done (the menu's); ⌘I opens the details.
            return .ignored
        case .return where press.modifiers.contains(.option):
            guard !item.children.isEmpty else { return .ignored }
            zoom(to: id)
        case .return:
            startRename(item)
        case .space:
            if let shell { shell.toggleDone(id) } else { model.toggleDone(id) }
        case .tab:
            return nest(row, out: press.modifiers.contains(.shift))
        case .upArrow where press.modifiers.contains(.option):
            model.step(id, by: -1, show: show)
        case .downArrow where press.modifiers.contains(.option):
            model.step(id, by: 1, show: show)
        case .rightArrow:
            if row.hasChildren && !row.expanded { setExpanded(id, true) } else { return .ignored }
        case .leftArrow:
            if row.expanded {
                setExpanded(id, false)
            } else if let parent = item.parent, now.row(parent.id) != nil {
                selection.wrappedValue = parent.id
            } else {
                return .ignored
            }
        default:
            return .ignored
        }
        return .handled
    }
}

/// Show in tree: watches the window for a row to bring into view (a publisher made in `body` would be subscribed to
/// again on every drawing).
private struct RevealWatcher: ViewModifier {
    let shell: ShellState?
    let reveal: (TreeReveal) -> Void

    func body(content: Content) -> some View {
        if let shell { content.modifier(Watching(shell: shell, reveal: reveal)) } else { content }
    }

    private struct Watching: ViewModifier {
        @ObservedObject var shell: ShellState
        let reveal: (TreeReveal) -> Void

        func body(content: Content) -> some View {
            content.onChange(of: shell.treeReveal, initial: true) { _, request in if let request { reveal(request) } }
        }
    }
}

/// done/total and a thin bar: the leaves under a task (or a group's tasks) that are done.
struct OutlineProgress: View {
    let done: Int
    let total: Int

    var body: some View {
        HStack(spacing: 6) {
            Capsule()
                .fill(Color.secondary.opacity(0.22))
                .frame(width: 40, height: 3)
                .overlay(alignment: .leading) {
                    Capsule()
                        .fill(Theme.accent)
                        .frame(width: total > 0 ? 40 * CGFloat(done) / CGFloat(total) : 0, height: 3)
                }
            Text("\(done)/\(total)")
                .font(.caption.monospacedDigit())
                .foregroundStyle(Color.secondary)
        }
        .fixedSize()
        .accessibilityElement(children: .ignore)
        .accessibilityLabel("\(done) of \(total) done")
    }
}
#endif
