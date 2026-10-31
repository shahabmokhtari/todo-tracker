#if canImport(SwiftUI)
import SwiftUI
import TodoTrackerKit

/// The window's navigation and what's selected in it, shared by its views, the toolbar and the menu commands.
@MainActor
public final class ShellState: ObservableObject {
    public let model: BoardModel
    /// The view on screen.
    @Published public var view: SidebarItem = .today
    /// The task picked in the view (arrow keys, a click): what Mark done, Later and the other commands act on.
    @Published public var selectedTask: UUID?
    /// The task whose details are open (beside the view on a wide screen, as a sheet on a narrow one).
    @Published public var inspected: UUID?
    /// "New task" (⌘N): the quick add box is open.
    @Published public var quickAdd = false
    /// A task to delete, once you say so.
    @Published public var deleting: UUID?
    /// Bumped to put the keyboard in the search box (⌘F).
    @Published public var searchRequests = 0

    public init(model: BoardModel) { self.model = model }

    /// Selects a task and opens its details.
    public func open(_ id: UUID) {
        selectedTask = id
        inspected = id
    }

    /// The task the commands act on: the one picked, else the one open, else Today's focus.
    public var currentTask: UUID? {
        for id in [selectedTask, inspected] { if let id, model.board.find(id) != nil { return id } }
        return view == .today ? model.dashboard.focus?.item.id : nil
    }

    public func show(_ item: SidebarItem) {
        view = item
        if item == .search { searchRequests += 1 }
    }
}

struct ShellStateKey: EnvironmentKey {
    static let defaultValue: ShellState? = nil
}

extension EnvironmentValues {
    /// The window's shell (nil: a view shown on its own, like the dashboard rendered for a snapshot).
    var shellState: ShellState? {
        get { self[ShellStateKey.self] }
        set { self[ShellStateKey.self] = newValue }
    }
}

/// A task being dragged (to another column, group, place in the outline or in Do now): its id as text, marked so other
/// text dropped on the app isn't taken for a task.
enum TaskDrag {
    static let prefix = "todotracker-task:"
    static func payload(_ id: UUID) -> String { prefix + id.uuidString }
    static func id(_ text: String) -> UUID? {
        text.hasPrefix(prefix) ? UUID(uuidString: String(text.dropFirst(prefix.count))) : nil
    }
    static func ids(_ texts: [String]) -> [UUID] { texts.compactMap(id) }
}

/// What a right-click (or a long press) on a task offers: the same everywhere.
struct TaskContextMenu: View {
    @ObservedObject var model: BoardModel
    let id: UUID
    @Environment(\.shellState) private var shell
    @Environment(\.openWindow) private var openWindow
    @Environment(\.supportsMultipleWindows) private var multipleWindows

    var body: some View {
        if let item = model.board.find(id) {
            if item.isDone {
                Button { model.reopen(id) } label: { Label("Reopen", systemImage: "arrow.uturn.backward") }
            } else {
                Button { model.complete(id) } label: { Label("Mark done", systemImage: "checkmark") }
                Menu {
                    ForEach(model.snoozeChoices) { choice in
                        Button("\(choice.label)  ·  \(Snooze.describe(choice.at, now: Date(), timeZone: .current))") { model.snooze(id, until: choice.at) }
                    }
                } label: { Label("Later", systemImage: "clock") }
                let timing = model.runningTimer?.item.id == id
                Button { model.toggleTimer(id) } label: { Label(timing ? "Stop the timer" : "Start the timer", systemImage: timing ? "stop.circle" : "stopwatch") }
            }
            Divider()
            if let shell {
                Button { shell.open(id) } label: { Label("Open details", systemImage: "sidebar.right") }
            }
            if multipleWindows {
                Button { openWindow(id: TaskWindow.sceneId, value: id) } label: { Label("Open in New Window", systemImage: "macwindow.badge.plus") }
            }
            Menu {
                if item.parent == nil {
                    Section("Column") {
                        ForEach(BoardColumn.allCases) { column in
                            Button(column.name) { model.moveCard(id, to: column) }
                                .disabled(BoardColumns.column(of: item) == column)
                        }
                    }
                }
                Section("Group") {
                    ForEach(model.groups) { group in
                        Button(group.name) { model.move(id, toGroup: group.id) }
                            .disabled(item.parent == nil && item.groupId == group.id)
                    }
                }
            } label: { Label("Move to", systemImage: "arrow.right.square") }
            Button { Clipboard.copy(TaskLinks.forTask(id)) } label: { Label("Copy link", systemImage: "link") }
            if item.parent == nil && item.isDone {
                if item.archivedAt == nil {
                    Button { model.archive(id) } label: { Label("Archive", systemImage: "archivebox") }
                } else {
                    Button { model.unarchive(id) } label: { Label("Unarchive", systemImage: "tray.and.arrow.up") }
                }
            }
            Divider()
            Button(role: .destructive) {
                if let shell { shell.deleting = id } else { model.delete(id) }
            } label: { Label("Delete…", systemImage: "trash") }
        }
    }
}

/// A row or card that lights up under the pointer (iPad with a trackpad, Mac).
struct PointerHover: ViewModifier {
    var cornerRadius: CGFloat = 12
    @State private var hovering = false

    func body(content: Content) -> some View {
        content
            .background(RoundedRectangle(cornerRadius: cornerRadius, style: .continuous).fill(Color.primary.opacity(hovering ? 0.05 : 0)))
            .onHover { hovering = $0 }
            #if os(iOS)
            .contentShape(.hoverEffect, RoundedRectangle(cornerRadius: cornerRadius, style: .continuous))
            .hoverEffect(.highlight)
            #endif
    }
}

extension View {
    func pointerHover(cornerRadius: CGFloat = 12) -> some View { modifier(PointerHover(cornerRadius: cornerRadius)) }
}

/// A label's chip (its color from the board's list), and a tag's.
struct LabelChip: View {
    let name: String
    let color: String?

    var body: some View {
        Text(name)
            .font(.caption2.weight(.semibold))
            .padding(.horizontal, 7).padding(.vertical, 2)
            .foregroundStyle(Color(hex: color ?? "#64748b"))
            .background(Capsule().fill(Color(hex: color ?? "#64748b").opacity(0.15)))
    }
}

struct TagChip: View {
    let tag: String

    var body: some View {
        Text("#\(tag)").font(.caption2.weight(.medium)).foregroundStyle(.secondary)
    }
}

extension BoardModel {
    /// A label's color from the board's list.
    func labelColor(_ name: String) -> String? {
        board.labels.first { $0.name.caseInsensitiveCompare(name) == .orderedSame }?.color
    }

    func groupColor(_ item: WorkItem) -> String {
        board.groups.first { $0.id == item.groupId }?.color ?? "#6366f1"
    }
}

/// The quick add box (⌘N, or + in the toolbar): typed like the capture bar on Today ("tomorrow 9am !high").
struct QuickAddSheet: View {
    @ObservedObject var model: BoardModel
    @Environment(\.dismiss) private var dismiss
    @FocusState private var focused: Bool

    var body: some View {
        NavigationStack {
            Form {
                Section {
                    TextField("What's on your mind?", text: $model.quickText)
                        .focused($focused)
                        .onSubmit(add)
                } footer: {
                    Text("It goes to \(model.groups.first { $0.id == model.selectedGroupId }?.name ?? model.groups.first?.name ?? "your tasks"). Say when, like “call Sam tomorrow 9am”.")
                }
            }
            .navigationTitle("New task")
            #if os(iOS)
            .navigationBarTitleDisplayMode(.inline)
            #endif
            .toolbar {
                ToolbarItem(placement: .cancellationAction) { Button("Cancel") { dismiss() } }
                ToolbarItem(placement: .confirmationAction) {
                    Button("Add", action: add).disabled(model.quickText.trimmingCharacters(in: .whitespaces).isEmpty)
                }
            }
        }
        .onAppear { focused = true }
        #if os(macOS)
        .frame(minWidth: 380, minHeight: 180)
        #else
        .presentationDetents([.height(240), .medium])
        #endif
    }

    private func add() {
        guard !model.quickText.trimmingCharacters(in: .whitespaces).isEmpty else { return }
        model.capture()
        dismiss()
    }
}

/// A task on its own window ("Open in New Window").
public struct TaskWindow: View {
    public static let sceneId = "task"
    @ObservedObject var model: BoardModel
    let id: UUID?
    @Environment(\.dismiss) private var dismiss

    public init(model: BoardModel, id: UUID?) {
        self.model = model
        self.id = id
    }

    public var body: some View {
        if let id, model.board.find(id) != nil {
            NavigationStack {
                TaskDetailContent(model: model, itemId: id, close: {
                    model.saveDrafts(id)
                    dismiss()
                })
                .navigationDestination(for: UUID.self) { other in
                    TaskDetailContent(model: model, itemId: other, close: { dismiss() })
                }
            }
            .onDisappear { model.saveDrafts(id) }
        } else {
            ContentUnavailableView("This task isn’t here any more", systemImage: "questionmark.square.dashed",
                                   description: Text("It may have been deleted, or it’s in tasks this window can’t see."))
        }
    }
}
#endif
