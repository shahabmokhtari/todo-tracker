#if canImport(SwiftUI)
import SwiftUI
import TodoTrackerKit

/// The views, in the web app's order (shell.js VIEWS): ⌘1 to ⌘6 go to them. Search and Settings are at the end of the
/// sidebar.
public enum SidebarItem: String, CaseIterable, Identifiable, Hashable, Sendable {
    case today, board, tasks, done, reports, plan
    case search, settings

    public var id: String { rawValue }

    /// The views (not Search and Settings).
    public static let views: [SidebarItem] = [.today, .board, .tasks, .done, .reports, .plan]

    public var name: String {
        switch self {
        case .today: return "Today"
        case .board: return "Board"
        case .tasks: return "Tasks"
        case .done: return "Done"
        case .reports: return "Reports"
        case .plan: return "Plan"
        case .search: return "Search"
        case .settings: return "Settings"
        }
    }

    public var symbol: String {
        switch self {
        case .today: return "sun.max"
        case .board: return "rectangle.split.3x1"
        case .tasks: return "list.bullet.indent"
        case .done: return "checkmark.circle"
        case .reports: return "chart.pie"
        case .plan: return "calendar"
        case .search: return "magnifyingglass"
        case .settings: return "gearshape"
        }
    }

    /// ⌘1…⌘6.
    public var shortcut: KeyEquivalent? {
        SidebarItem.views.firstIndex(of: self).map { KeyEquivalent(Character(String($0 + 1))) }
    }
}

/// The app's window: a sidebar (views, groups with their counts, Search, Settings) beside the view and, on a wide
/// screen, the task's details. On a narrow one (iPhone, an iPad in Slide Over or a narrow Split View) the views are tabs
/// and the details a sheet.
public struct AppShell: View {
    @ObservedObject var model: BoardModel
    @StateObject private var shell: ShellState
    @Environment(\.horizontalSizeClass) private var sizeClass
    @State private var columns = NavigationSplitViewVisibility.automatic
    @State private var addingGroup = false
    @State private var newGroupName = ""
    @State private var renamingGroup: TaskGroup?
    @State private var groupName = ""
    @State private var deletingGroup: TaskGroup?

    public init(model: BoardModel, start: SidebarItem? = nil) {
        self.model = model
        let state = ShellState(model: model)
        if let start { state.view = start }
        _shell = StateObject(wrappedValue: state)
    }

    public var body: some View {
        Group {
            if sizeClass == .compact {
                tabs
            } else {
                split
            }
        }
        .environment(\.shellState, shell)
        .focusedSceneObject(shell)
        // A todotracker://task/<id> link (also when it started the app): the task's details.
        .onAppear { openLinkedTask() }
        .onChange(of: model.openTaskId) { _, _ in openLinkedTask() }
        .sheet(isPresented: $shell.quickAdd) { QuickAddSheet(model: model) }
        .confirmationDialog(deleteQuestion, isPresented: Binding(get: { shell.deleting != nil }, set: { if !$0 { shell.deleting = nil } }), titleVisibility: .visible) {
            Button("Delete", role: .destructive) {
                if let id = shell.deleting {
                    if shell.inspected == id { shell.inspected = nil }
                    if shell.selectedTask == id { shell.selectedTask = nil }
                    model.delete(id)
                }
                shell.deleting = nil
            }
        }
        .alert("New group", isPresented: $addingGroup) {
            TextField("Name", text: $newGroupName)
            Button("Add") {
                model.addGroup(newGroupName)
                newGroupName = ""
            }
            .disabled(newGroupName.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty)
            Button("Cancel", role: .cancel) { newGroupName = "" }
        }
        .alert("Rename group", isPresented: Binding(get: { renamingGroup != nil }, set: { if !$0 { renamingGroup = nil } })) {
            TextField("Name", text: $groupName)
            Button("Save") {
                if let group = renamingGroup { model.renameGroup(group.id, to: groupName) }
                renamingGroup = nil
            }
            .disabled(groupName.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty)
            Button("Cancel", role: .cancel) { renamingGroup = nil }
        }
        // Deleting a group never deletes its tasks: they move to the group picked here.
        .confirmationDialog(deletingGroup.map { "Delete the group “\($0.name)”? Its tasks move to the group you pick." } ?? "",
                            isPresented: Binding(get: { deletingGroup != nil }, set: { if !$0 { deletingGroup = nil } }),
                            titleVisibility: .visible) {
            if let group = deletingGroup {
                ForEach(model.groups.filter { $0.id != group.id }) { target in
                    Button("Move its tasks to \(target.name)", role: .destructive) {
                        model.deleteGroup(group.id, moveTo: target.id)
                        deletingGroup = nil
                    }
                }
            }
            Button("Cancel", role: .cancel) { deletingGroup = nil }
        }
        #if os(iOS)
        // The Mac covers every screen with its own windows (see the app); here the break covers the app.
        .fullScreenCover(isPresented: Binding(get: { model.breakPrompt != nil }, set: { if !$0 { model.takeBreak() } })) {
            BreakView(model: model)
        }
        .onChange(of: model.breakPrompt != nil) { _, showing in
            if showing {
                shell.inspected = nil
                shell.quickAdd = false
            }
        }
        #endif
    }

    private var deleteQuestion: String {
        guard let id = shell.deleting, let item = model.board.find(id) else { return "" }
        let subtasks = item.selfAndDescendants.count - 1
        return subtasks > 0 ? "Delete “\(item.title)” and its \(subtasks) subtask\(subtasks == 1 ? "" : "s")?" : "Delete “\(item.title)”?"
    }

    private func openLinkedTask() {
        guard let id = model.openTaskId else { return }
        model.openTaskId = nil
        shell.open(id)
    }

    // MARK: Wide: sidebar, view, details

    private var split: some View {
        NavigationSplitView(columnVisibility: $columns) {
            sidebar
                .navigationTitle("Todo Tracker")
                #if os(macOS)
                .navigationSplitViewColumnWidth(min: 200, ideal: 230, max: 320)
                #endif
        } detail: {
            GeometryReader { geo in
                HStack(spacing: 0) {
                    NavigationStack {
                        destination(shell.view)
                            .navigationTitle(shell.view.name)
                            #if os(iOS)
                            .navigationBarTitleDisplayMode(.inline)
                            #endif
                            .toolbar { primaryActions }
                    }
                    // The task's details beside the view: a column of their own (with their own bar), as wide as the
                    // window allows.
                    if let id = shell.inspected {
                        Divider().ignoresSafeArea()
                        TaskDetailView(model: model, itemId: id, sized: false)
                            .id(id)
                            .frame(width: min(520, max(320, geo.size.width * 0.4)))
                            .transition(.move(edge: .trailing))
                    }
                }
                .animation(.snappy(duration: 0.25), value: shell.inspected)
            }
        }
    }

    private var inspectorShown: Binding<Bool> {
        Binding(get: { shell.inspected != nil }, set: { shown in
            if !shown {
                // However the details go away, what was typed is saved.
                model.saveAllDrafts()
                shell.inspected = nil
            }
        })
    }

    private var sidebar: some View {
        List(selection: Binding(get: { Optional(shell.view) }, set: { if let item = $0 { shell.show(item) } })) {
            Section {
                ForEach(SidebarItem.views) { item in sidebarRow(item) }
            }
            Section("Groups") {
                groupRow(nil)
                ForEach(model.groups) { group in groupRow(group) }
                Button { addingGroup = true } label: { Label("New group", systemImage: "plus") }
                    .buttonStyle(.borderless)
                    .foregroundStyle(.secondary)
            }
            Section {
                sidebarRow(.search)
                #if os(macOS)
                SettingsLink { Label("Settings", systemImage: SidebarItem.settings.symbol) }
                    .buttonStyle(.borderless)
                #else
                sidebarRow(.settings)
                #endif
            }
        }
        .listStyle(.sidebar)
    }

    private func sidebarRow(_ item: SidebarItem) -> some View {
        Label(item.name, systemImage: item.symbol)
            .badge(item == .today ? model.dashboard.now.count : 0)
            .tag(item)
            .accessibilityIdentifier("sidebar-\(item.rawValue)")
    }

    /// A group (nil: all of them): picks what every view shows; tasks dropped on it move there.
    private func groupRow(_ group: TaskGroup?) -> some View {
        let selected = model.selectedGroupId == group?.id
        return Button { model.select(group: group?.id) } label: {
            HStack(spacing: 10) {
                Circle().fill(group.map { Color(hex: $0.color ?? "#6366f1") } ?? Color.secondary.opacity(0.5)).frame(width: 9, height: 9)
                Text(group?.name ?? "All groups").lineLimit(1)
                Spacer()
                let count = model.count(for: group?.id)
                if count > 0 { Text("\(count)").font(.caption.weight(.semibold)).monospacedDigit().foregroundStyle(.secondary) }
            }
            .contentShape(Rectangle())
        }
        .buttonStyle(.plain)
        .fontWeight(selected ? .semibold : .regular)
        .listRowBackground(selected ? RoundedRectangle(cornerRadius: 8, style: .continuous).fill(Color.primary.opacity(0.07)).padding(.horizontal, 6) : nil)
        .accessibilityAddTraits(selected ? .isSelected : [])
        .contextMenu { if let group { groupActions(group) } }
        .dropDestination(for: String.self) { texts, _ in
            guard let group else { return false }
            let ids = TaskDrag.ids(texts)
            ids.forEach { model.move($0, toGroup: group.id) }
            return !ids.isEmpty
        }
    }

    /// Rename, recolor or delete a group (its tasks move to another one; none are deleted).
    @ViewBuilder private func groupActions(_ group: TaskGroup) -> some View {
        Button {
            groupName = group.name
            renamingGroup = group
        } label: { Label("Rename…", systemImage: "pencil") }
        Menu {
            ForEach(groupColors, id: \.hex) { color in
                Button { model.recolorGroup(group.id, to: color.hex) } label: {
                    if group.color?.lowercased() == color.hex { Label(color.name, systemImage: "checkmark") } else { Text(color.name) }
                }
            }
        } label: { Label("Color", systemImage: "paintpalette") }
        Button(role: .destructive) { deletingGroup = group } label: { Label("Delete group…", systemImage: "trash") }
            .disabled(model.groups.count < 2)
    }

    @ToolbarContentBuilder private var primaryActions: some ToolbarContent {
        ToolbarItemGroup(placement: .primaryAction) {
            Button { shell.quickAdd = true } label: { Label("New task", systemImage: "plus") }
                .help("New task (⌘N)")
            Button { shell.show(.search) } label: { Label("Search", systemImage: "magnifyingglass") }
                .help("Search (⌘F)")
            Button {
                if shell.inspected == nil { shell.inspected = shell.currentTask } else { inspectorShown.wrappedValue = false }
            } label: { Label("Details", systemImage: "sidebar.right") }
                .disabled(shell.inspected == nil && shell.currentTask == nil)
                .help("Show or hide the task’s details")
        }
    }

    // MARK: Narrow: tabs

    private var tabs: some View {
        TabView(selection: Binding(get: { SidebarItem.views.contains(shell.view) ? shell.view : .today }, set: { shell.view = $0 })) {
            ForEach(SidebarItem.views) { item in
                Group {
                    if item == .today {
                        // Today keeps its own header: group tabs, search and Settings.
                        destination(.today)
                    } else {
                        NavigationStack {
                            destination(item)
                                .navigationTitle(item.name)
                                #if os(iOS)
                                .navigationBarTitleDisplayMode(.inline)
                                #endif
                                .toolbar {
                                    ToolbarItem(placement: .navigation) { groupMenu }
                                    ToolbarItem(placement: .primaryAction) {
                                        Button { shell.quickAdd = true } label: { Label("New task", systemImage: "plus") }
                                    }
                                }
                        }
                    }
                }
                .tabItem { Label(item.name, systemImage: item.symbol) }
                .tag(item)
            }
        }
        .sheet(isPresented: Binding(get: { shell.inspected != nil }, set: { if !$0 { model.saveAllDrafts(); shell.inspected = nil } })) {
            if let id = shell.inspected { TaskDetailView(model: model, itemId: id) }
        }
    }

    /// The group shown, as a menu (narrow screens have no sidebar).
    private var groupMenu: some View {
        Menu {
            Picker("Group", selection: Binding(get: { model.selectedGroupId }, set: { model.select(group: $0) })) {
                Text("All groups").tag(UUID?.none)
                ForEach(model.groups) { Text($0.name).tag(Optional($0.id)) }
            }
        } label: {
            Label(model.groups.first { $0.id == model.selectedGroupId }?.name ?? "All groups", systemImage: "line.3.horizontal.decrease.circle")
                .labelStyle(.titleAndIcon)
        }
    }

    // MARK: The views

    @ViewBuilder private func destination(_ item: SidebarItem) -> some View {
        switch item {
        case .today: DashboardView(model: model, chrome: sizeClass == .compact)
        case .board: BoardView(model: model)
        case .tasks: TasksOutlineView(model: model)
        case .done: DoneView(model: model)
        case .search: SearchView(model: model)
        case .settings: SettingsView(model: model, embedded: true)
        // MARK: Plan and Reports plug in here: replace the placeholder line with the view.
        case .plan: ViewPlaceholder(item: .plan) // Plan: PlanView(model: model)
        case .reports: ViewPlaceholder(item: .reports) // Reports: ReportsView(model: model)
        }
    }
}

/// A view that isn't in this app yet: says where to find it meanwhile.
struct ViewPlaceholder: View {
    let item: SidebarItem

    var body: some View {
        ContentUnavailableView(item.name, systemImage: item.symbol,
                               description: Text("\(item.name) is on its way to this app. Meanwhile it’s in Todo Tracker on your computer."))
    }
}

/// Every task the search finds, done and archived ones too: each opens its details. The search also narrows the Board,
/// Tasks and Done.
struct SearchView: View {
    @ObservedObject var model: BoardModel
    @Environment(\.shellState) private var shell
    @FocusState private var focused: Bool

    var body: some View {
        List {
            Section {
                HStack {
                    Image(systemName: "magnifyingglass").foregroundStyle(.secondary)
                    TextField("Find in titles and details", text: $model.searchText)
                        .textFieldStyle(.plain)
                        .autocorrectionDisabled()
                        .focused($focused)
                        .accessibilityIdentifier("search-field")
                    if !model.searchText.isEmpty {
                        Button { model.searchText = "" } label: { Image(systemName: "xmark.circle.fill") }
                            .buttonStyle(.borderless).foregroundStyle(.secondary).accessibilityLabel("Clear the search")
                    }
                }
            } footer: {
                Text("Words, #tags, label:name, is:done, is:archived, is:doing… The Board, Tasks and Done show only what matches too.")
            }
            if !model.searchText.isEmpty {
                Section(model.searchResults.isEmpty ? "No tasks match your search." : "Found \(model.searchResults.count)") {
                    ForEach(model.searchResults.prefix(100)) { item in
                        Button { shell?.open(item.id) } label: {
                            HStack(spacing: 8) {
                                Image(systemName: item.isArchived ? "archivebox" : item.isDone ? "checkmark.circle.fill" : "circle")
                                    .foregroundStyle(item.isDone ? Color.green : .secondary)
                                VStack(alignment: .leading, spacing: 1) {
                                    Text(Presentation.titleWithIcon(item)).strikethrough(item.isDone).foregroundStyle(item.isDone ? .secondary : .primary)
                                    if item.parent != nil {
                                        Text(item.path.dropLast().joined(separator: " › ")).font(.caption).foregroundStyle(.secondary).lineLimit(1)
                                    }
                                }
                                Spacer(minLength: 0)
                            }
                            .contentShape(Rectangle())
                        }
                        .buttonStyle(.plain)
                        .contextMenu { TaskContextMenu(model: model, id: item.id) }
                    }
                }
            }
        }
        .onAppear { focused = true }
        .onChange(of: shell?.searchRequests) { _, _ in focused = true }
    }
}

/// "Filtered by your search": above a view the search narrows, with a way out.
struct SearchFilterBar: View {
    @ObservedObject var model: BoardModel

    var body: some View {
        if !model.searchText.isEmpty {
            HStack(spacing: 8) {
                Image(systemName: "line.3.horizontal.decrease.circle").foregroundStyle(Theme.accent)
                Text("Showing what matches “\(model.searchText)”").font(.subheadline).lineLimit(1)
                Spacer(minLength: 0)
                Button("Show all") { model.searchText = "" }.buttonStyle(.borderless)
            }
            .padding(.horizontal, 12).padding(.vertical, 6)
            .background(RoundedRectangle(cornerRadius: 10, style: .continuous).fill(Theme.accent.opacity(0.08)))
        }
    }
}

/// The menu bar's commands (and the iPad's keyboard shortcuts, listed when you hold ⌘): new task, search, the views,
/// and what to do with the selected task.
public struct TodoTrackerCommands: Commands {
    @FocusedObject private var shell: ShellState?

    public init() {}

    public var body: some Commands {
        CommandGroup(replacing: .newItem) {
            Button("New Task") { shell?.quickAdd = true }
                .keyboardShortcut("n")
                .disabled(shell == nil)
        }
        CommandGroup(after: .sidebar) {
            ForEach(SidebarItem.views) { item in
                Button(item.name) { shell?.show(item) }
                    .keyboardShortcut(item.shortcut ?? " ")
                    .disabled(shell == nil)
            }
            Button("Search") { shell?.show(.search) }
                .keyboardShortcut("f")
                .disabled(shell == nil)
            Divider()
        }
        CommandMenu("Task") {
            let id = shell?.currentTask
            let item = id.flatMap { shell?.model.board.find($0) }
            Button(item?.isDone == true ? "Reopen" : "Mark Done") { if let id { shell?.model.toggleDone(id) } }
                .keyboardShortcut(.return)
                .disabled(item == nil)
            Button("Later: In an Hour") {
                if let id, let model = shell?.model, let choice = model.snoozeChoices.first(where: { $0.id == "1h" }) { model.snooze(id, until: choice.at) }
            }
            .keyboardShortcut("l")
            .disabled(item == nil || item?.isDone == true)
            Button("Later: Tomorrow Morning") {
                if let id, let model = shell?.model, let choice = model.snoozeChoices.first(where: { $0.id == "tomorrow" }) { model.snooze(id, until: choice.at) }
            }
            .keyboardShortcut("l", modifiers: [.command, .shift])
            .disabled(item == nil || item?.isDone == true)
            Button("Show Details") { if let id { shell?.open(id) } }
                .keyboardShortcut("i")
                .disabled(item == nil)
        }
    }
}
#endif
