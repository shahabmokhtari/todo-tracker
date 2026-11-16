#if canImport(SwiftUI)
import SwiftUI
import OneThingKit
#if canImport(AppKit)
import AppKit
#elseif canImport(UIKit)
import UIKit
#endif

extension Color {
    init(hex: String) {
        let value = UInt64(hex.replacingOccurrences(of: "#", with: ""), radix: 16) ?? 0x3B82F6
        self.init(red: Double((value >> 16) & 0xFF) / 255, green: Double((value >> 8) & 0xFF) / 255, blue: Double(value & 0xFF) / 255)
    }
}

/// Design tokens shared with the web dashboard and the Windows sidebar.
enum Theme {
    static let accent = Color(hex: "#6366f1")
    /// The app icon's gradient (wwwroot/img/logo.svg): indigo, violet, pink.
    static let brand = LinearGradient(colors: [Color(hex: "#4f46e5"), Color(hex: "#7c3aed"), Color(hex: "#db2777")], startPoint: .topLeading, endPoint: .bottomTrailing)
    static let attention = LinearGradient(colors: [Color(hex: "#f59e0b"), Color(hex: "#fb7185")], startPoint: .topLeading, endPoint: .bottomTrailing)
    static let calm = LinearGradient(colors: [Color(hex: "#10b981"), Color(hex: "#22d3ee")], startPoint: .topLeading, endPoint: .bottomTrailing)
    static let warnText = Color(hex: "#d97706")

    static func color(for tone: Chip.Tone) -> Color {
        switch tone {
        case .step: return accent
        case .info: return Color(hex: "#0ea5e9")
        case .warn: return warnText
        case .danger: return Color(hex: "#ef4444")
        case .muted: return .secondary
        case .energy: return Color(hex: "#ca8a04")
        }
    }
}

/// Wrapping row layout for chips and action buttons.
struct FlowLayout: Layout {
    var spacing: CGFloat = 6

    func sizeThatFits(proposal: ProposedViewSize, subviews: Subviews, cache: inout ()) -> CGSize {
        arrange(proposal: proposal, subviews: subviews).size
    }

    func placeSubviews(in bounds: CGRect, proposal: ProposedViewSize, subviews: Subviews, cache: inout ()) {
        let result = arrange(proposal: ProposedViewSize(width: bounds.width, height: bounds.height), subviews: subviews)
        for (index, point) in result.points.enumerated() {
            subviews[index].place(at: CGPoint(x: bounds.minX + point.x, y: bounds.minY + point.y), proposal: .unspecified)
        }
    }

    private func arrange(proposal: ProposedViewSize, subviews: Subviews) -> (size: CGSize, points: [CGPoint]) {
        let maxWidth = proposal.width ?? .infinity
        var points: [CGPoint] = []
        var x: CGFloat = 0, y: CGFloat = 0, rowHeight: CGFloat = 0, width: CGFloat = 0
        for subview in subviews {
            let size = subview.sizeThatFits(.unspecified)
            if x > 0 && x + size.width > maxWidth {
                x = 0
                y += rowHeight + spacing
                rowHeight = 0
            }
            points.append(CGPoint(x: x, y: y))
            x += size.width + spacing
            rowHeight = max(rowHeight, size.height)
            width = max(width, x - spacing)
        }
        return (CGSize(width: width, height: y + rowHeight), points)
    }
}

struct ChipView: View {
    let chip: Chip

    var body: some View {
        Text(chip.text)
            .font(.caption2.weight(.semibold))
            .foregroundStyle(Theme.color(for: chip.tone))
            .padding(.horizontal, chip.tone == .muted ? 0 : 8)
            .padding(.vertical, 2)
            .background(Capsule().fill(chip.tone == .muted ? Color.clear : Theme.color(for: chip.tone).opacity(0.14)))
    }
}

struct PriorityDot: View {
    let priority: Priority

    var body: some View {
        Circle()
            .fill(Color(hex: priority.hexColor))
            .frame(width: 9, height: 9)
            .padding(3)
            .background(Circle().fill(Color(hex: priority.hexColor).opacity(0.2)))
            .accessibilityLabel("\(priority.label) priority")
    }
}

/// Card surface used for sections.
struct Card<Content: View>: View {
    @ViewBuilder var content: Content

    var body: some View {
        content
            .padding(14)
            .frame(maxWidth: .infinity, alignment: .leading)
            .background(RoundedRectangle(cornerRadius: 18, style: .continuous).fill(.background.secondary))
            .overlay(RoundedRectangle(cornerRadius: 18, style: .continuous).strokeBorder(.quaternary, lineWidth: 1))
    }
}

/// ADHD-friendly dashboard shared by iOS and macOS: greeting, group tabs, quick capture, one focus card,
/// and collapsible Do now / Waiting / Workstreams / Recent notes sections.
public struct DashboardView: View {
    @ObservedObject var model: BoardModel
    var scrollable = true
    @State private var detailId: UUID?
    @State private var showWaiting = true
    @State private var showDone = true
    @State private var showNotes = false
    @State private var showNow = true
    @State private var showWorkstreams = true
    @State private var newGroupName = ""
    @State private var addingGroup = false
    @State private var renamingGroup: TaskGroup?
    @State private var groupName = ""
    @State private var deletingGroup: TaskGroup?
    @State private var searching = false
    @State private var showSettings = false
    /// The group tabs, search and Settings in the header (false: the window's sidebar has them).
    var chrome = true
    /// The window's shell: it shows a task's details, the break and the deep-linked task (nil: this view does).
    @Environment(\.shellState) private var shell
    /// The task dragged to a new place in Do now (for its drop line).
    @State private var dropTarget: UUID?

    public init(model: BoardModel, scrollable: Bool = true, chrome: Bool = true) {
        self.model = model
        self.scrollable = scrollable
        self.chrome = chrome
    }

    /// A task's details: beside the view (or a sheet) through the shell, else this view's own sheet.
    private func open(_ id: UUID) {
        if let shell { shell.open(id) } else { detailId = id }
    }

    public var body: some View {
        VStack(spacing: 12) {
            VStack(spacing: 12) {
                header
                if chrome { groupTabs }
                captureBar
                if searching || !model.searchText.isEmpty { searchBar }
                if let status = model.status {
                    Text(status).font(.caption).foregroundStyle(.secondary).frame(maxWidth: .infinity, alignment: .leading).padding(.horizontal, 4)
                }
            }
            .padding(.horizontal, Self.margin)
            .padding(.top, 14)
            if scrollable {
                GeometryReader { geo in
                    if Presentation.todayColumns(width: geo.size.width) == 2 {
                        // A wide screen (an iPad, a Mac): the one thing and Do now on the left; the rest beside them.
                        HStack(alignment: .top, spacing: 0) {
                            mainList
                            ScrollView { sideCards.padding(.top, Self.listTop).padding(.bottom, 16) }
                                .scrollIndicators(.hidden)
                                .frame(width: Presentation.todaySideWidth(width: geo.size.width))
                                .padding(.trailing, Self.margin)
                        }
                    } else {
                        singleList
                    }
                }
            } else {
                sections.padding(.horizontal, Self.margin)
            }
            // No sidebar (an iPhone shows the strip above its tabs), or the dashboard on its own: the timers here.
            if shell == nil { TimerStrip(model: model, style: .bar, showIdle: true).padding(.horizontal, Self.margin).padding(.bottom, 10) }
        }
        .background(backdrop)
        // However the details go away (Close, swipe down, Esc, a break covering them), what was typed is saved.
        .sheet(item: Binding(get: { detailId.map(IdentifiedID.init) }, set: { detailId = $0?.id }), onDismiss: { model.saveAllDrafts() }) { wrapped in
            TaskDetailView(model: model, itemId: wrapped.id)
        }
        // A onething://task/<id> link: that task's details (also when the link started the app).
        .onAppear {
            if shell == nil, let id = model.openTaskId {
                detailId = id
                model.openTaskId = nil
            }
        }
        .onChange(of: model.openTaskId) { _, id in
            guard shell == nil, let id else { return }
            detailId = id
            model.openTaskId = nil
        }
        #if os(iOS)
        // The Mac covers every screen with its own windows (see the app); here the break covers the app.
        // (In the app's window, the window covers itself: see AppShell.)
        .fullScreenCover(isPresented: Binding(get: { shell == nil && model.breakPrompt != nil }, set: { if !$0 { model.takeBreak() } })) {
            BreakView(model: model)
        }
        // Only one screen can be on top: an open task or prompt makes way for the break.
        .onChange(of: model.breakPrompt != nil) { _, showing in
            if showing {
                detailId = nil
                addingGroup = false
                renamingGroup = nil
                deletingGroup = nil
                showSettings = false
            }
        }
        #endif
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
        .sheet(isPresented: $showSettings) { SettingsView(model: model) }
    }

    private var searchBar: some View {
        HStack(spacing: 8) {
            Image(systemName: "magnifyingglass").foregroundStyle(.secondary)
            TextField("Find in titles and details", text: $model.searchText)
                .textFieldStyle(.plain)
                .autocorrectionDisabled()
                .accessibilityIdentifier("search-field")
            if !model.searchText.isEmpty {
                Button { model.searchText = "" } label: { Image(systemName: "xmark.circle.fill") }
                    .buttonStyle(.plain)
                    .foregroundStyle(.secondary)
                    .accessibilityLabel("Clear the search")
            }
        }
        .padding(.horizontal, 14).padding(.vertical, 8)
        .background(Capsule().fill(.background))
        .overlay(Capsule().strokeBorder(.quaternary, lineWidth: 1))
    }

    private var backdrop: some View {
        ZStack {
            Rectangle().fill(.background)
            RadialGradient(colors: [Theme.accent.opacity(0.16), .clear], center: .topLeading, startRadius: 0, endRadius: 420)
            RadialGradient(colors: [Color(hex: "#ec4899").opacity(0.10), .clear], center: .topTrailing, startRadius: 0, endRadius: 360)
        }
        .ignoresSafeArea()
    }

    private var header: some View {
        HStack(spacing: 12) {
            RoundedRectangle(cornerRadius: 9, style: .continuous)
                .fill(Theme.brand)
                .frame(width: 32, height: 32)
                .overlay(AppIconGlyph())
                .shadow(color: Theme.accent.opacity(0.35), radius: 6, y: 3)
            VStack(alignment: .leading, spacing: 1) {
                Text(Presentation.greeting(model.now)).font(.title3.weight(.bold))
                Text(Presentation.summary(model.dashboard)).font(.caption).foregroundStyle(.secondary).lineLimit(1)
                if let energy = Presentation.energyLine(model.board, now: model.now) {
                    Text(energy).font(.caption).foregroundStyle(energy.contains("too much") ? Color(hex: "#ef4444") : .secondary).lineLimit(1)
                }
            }
            Spacer()
            if chrome { headerButtons }
        }
    }

    private var headerButtons: some View {
        HStack(spacing: 12) {
            Button {
                withAnimation(.snappy(duration: 0.2)) {
                    if searching || !model.searchText.isEmpty {
                        searching = false
                        model.searchText = ""
                    } else {
                        searching = true
                    }
                }
            } label: { Image(systemName: "magnifyingglass") }
                .buttonStyle(.plain)
                .foregroundStyle(searching || !model.searchText.isEmpty ? Theme.accent : .secondary)
                .accessibilityLabel(searching || !model.searchText.isEmpty ? "Close the search" : "Search")
                .accessibilityIdentifier("search-button")
            Button { showSettings = true } label: { Image(systemName: "gearshape") }
                .buttonStyle(.plain)
                .foregroundStyle(.secondary)
                .accessibilityLabel("Settings")
                .help("Settings: appearance, energy, focus timer")
        }
    }

    /// The space beside Today's lists and its header, and above the first card.
    static let margin: CGFloat = 20
    static let listTop: CGFloat = 8

    /// The task picked in the lists (arrows move it, Space finishes it, Return opens it); picking one opens it.
    private var selection: Binding<UUID?> {
        Binding(get: { shell?.selectedTask }, set: { id in
            shell?.selectedTask = id
            if let id { open(id) }
        })
    }

    // MARK: Lists (swipe actions work in a List)

    /// A wide screen's first column: the one thing, then Do now.
    private var mainList: some View {
        List(selection: selection) {
            if !model.searchText.isEmpty { searchSection }
            heroSection
            doNowSection
        }
        .todayList()
        .modifier(ListKeys(model: model, open: open))
    }

    /// A narrow screen: everything in one column, the one thing first.
    private var singleList: some View {
        List(selection: selection) {
            if !model.searchText.isEmpty { searchSection }
            heroSection
            doNowSection
            comingUpSection
            doneSection
            workstreamsSection
            notesSection
        }
        .todayList()
        .modifier(ListKeys(model: model, open: open))
    }

    private var heroSection: some View {
        Section {
            focusCard
                .listRowInsets(EdgeInsets(top: 0, leading: 0, bottom: 4, trailing: 0))
                .listRowBackground(Color.clear)
                .listRowSeparator(.hidden)
        }
    }

    private var rest: [AgendaEntry] {
        model.dashboard.now.filter { $0.item.id != model.dashboard.focus?.item.id }
    }

    private var doNowSection: some View {
        listSection("Do now", count: model.dashboard.now.count, expanded: $showNow) {
            if rest.isEmpty {
                emptyText(model.searchText.isEmpty ? "Nothing else right now. Type above to add a task, or enjoy the calm." : "Nothing else here matches your search.")
                    .listRowSeparator(.hidden)
            }
            ForEach(rest) { entry in
                taskRow(entry)
                    // Drag a task above another to do it first.
                    .draggable(TaskDrag.payload(entry.item.id))
                    .dropDestination(for: String.self) { texts, _ in
                        let ids = TaskDrag.ids(texts)
                        ids.forEach { model.arrangeNow(moving: $0, before: entry.item.id) }
                        return !ids.isEmpty
                    } isTargeted: { over in
                        if over { dropTarget = entry.item.id } else if dropTarget == entry.item.id { dropTarget = nil }
                    }
                    .overlay(alignment: .top) {
                        if dropTarget == entry.item.id { Capsule().fill(Theme.accent).frame(height: 3).offset(y: -6) }
                    }
            }
        }
    }

    private var comingUpSection: some View {
        listSection("Coming up", count: model.dashboard.waiting.count, expanded: $showWaiting) {
            if model.dashboard.waiting.isEmpty { emptyText(comingUpEmpty).listRowSeparator(.hidden) }
            ForEach(model.dashboard.waiting) { entry in taskRow(entry) }
        }
    }

    private var doneSection: some View {
        listSection("Done today", count: model.doneToday.count, expanded: $showDone) {
            doneContent(inList: true)
        }
    }

    private var workstreamsSection: some View {
        let streams = Presentation.workstreams(model.dashboard)
        return listSection("Workstreams", count: streams.count, expanded: $showWorkstreams) {
            if streams.isEmpty { emptyText(workstreamsEmpty).listRowSeparator(.hidden) }
            ForEach(streams) { o in
                WorkstreamRow(entry: o, now: model.now).tag(o.item.id)
                    .contextMenu { TaskContextMenu(model: model, id: o.item.id) }
            }
        }
    }

    private var notesSection: some View {
        listSection("Recent notes", count: model.dashboard.recentNotes.count, expanded: $showNotes) {
            notesContent
        }
    }

    private var searchSection: some View {
        Section {
            if model.searchResults.isEmpty { emptyText("No tasks match your search. Try fewer words.") }
            ForEach(model.searchResults.prefix(50)) { item in searchRow(item) }
        } header: {
            sectionLabel("Found", count: model.searchResults.count)
        } footer: {
            if model.searchResults.count > 50 { Text("Showing 50 of \(model.searchResults.count): add a word to narrow it down.") }
        }
        .textCase(nil)
    }

    /// A row of Do now or Coming up: tap to open, swipe or right-click for the rest.
    private func taskRow(_ entry: AgendaEntry) -> some View {
        TaskRow(entry: entry, model: model, now: model.now)
            .tag(entry.item.id)
            .listRowBackground(entry.needsAttention ? Color(hex: "#f59e0b").opacity(0.12) : nil)
            .taskRowActions(model, id: entry.item.id, inlineNote: true) { open(entry.item.id) }
            .accessibilityHint("Opens it. Swipe right to finish it, left to put it off.")
    }

    /// A section of a list with its name and count; the name folds it away.
    private func listSection<Content: View>(_ title: String, count: Int, expanded: Binding<Bool>, @ViewBuilder content: () -> Content) -> some View {
        Section {
            if expanded.wrappedValue { content() }
        } header: {
            Button { withAnimation(.snappy(duration: 0.2)) { expanded.wrappedValue.toggle() } } label: {
                HStack(spacing: 6) {
                    sectionLabel(title, count: count)
                    Image(systemName: "chevron.down")
                        .font(.caption.weight(.semibold))
                        .foregroundStyle(.tertiary)
                        .rotationEffect(.degrees(expanded.wrappedValue ? 0 : -90))
                }
                .foregroundStyle(.primary)
                .padding(.bottom, 2)
                .contentShape(Rectangle())
            }
            .buttonStyle(.plain)
            .accessibilityHint(expanded.wrappedValue ? "Folds it away" : "Shows it")
        }
        .textCase(nil)
        .headerProminence(.increased)
    }

    // MARK: Cards (the wide screen's second column, and the snapshot)

    private var comingUpEmpty: String {
        model.searchText.isEmpty ? "Nothing is waiting. Put a task off with Later and it waits here until it's time." : "Nothing waiting matches your search."
    }

    private var workstreamsEmpty: String { "Give a task subtasks or steps and its progress shows here." }

    /// A task finished today: its title (and parent), and when.
    private func doneRow(_ item: WorkItem) -> some View {
        HStack(spacing: 10) {
            Image(systemName: "checkmark.circle.fill").foregroundStyle(.green)
            VStack(alignment: .leading, spacing: 1) {
                Text(item.title).strikethrough().foregroundStyle(.secondary).multilineTextAlignment(.leading)
                if let parent = item.parent {
                    Text(parent.title).font(.caption).foregroundStyle(.tertiary).lineLimit(1)
                }
            }
            Spacer(minLength: 4)
            if let at = Agenda.doneAt(model.board, item) {
                Text(at.formatted(date: .omitted, time: .shortened)).font(.caption).foregroundStyle(.secondary).monospacedDigit()
                    .accessibilityLabel("done at \(at.formatted(date: .omitted, time: .shortened))")
            }
        }
        .padding(.vertical, 3)
        .contentShape(Rectangle())
    }

    /// `inList`: rows of a list (picked to open, swiped); else rows of a card, each a button that opens it, with Reopen.
    @ViewBuilder private func doneContent(inList: Bool) -> some View {
        if let cheer = Agenda.cheer(model.doneToday.count) {
            Text("🎉 \(cheer)")
                .font(.subheadline.weight(.semibold))
                .foregroundStyle(.green)
                .padding(.vertical, 6).padding(.horizontal, 10)
                .frame(maxWidth: .infinity, alignment: .leading)
                .background(Color.green.opacity(0.12), in: RoundedRectangle(cornerRadius: 10))
                .listRowSeparator(.hidden)
        } else {
            emptyText(model.searchText.isEmpty ? "Nothing yet. Finish something and it shows up here." : "Nothing done today matches your search.")
                .listRowSeparator(.hidden)
        }
        ForEach(model.doneToday) { item in
            if inList {
                doneRow(item)
                    .tag(item.id)
                    .taskRowActions(model, id: item.id) { open(item.id) }
            } else {
                HStack(spacing: 8) {
                    Button { open(item.id) } label: { doneRow(item) }
                        .buttonStyle(.plain)
                        .accessibilityHint("Opens it")
                    Button("Reopen") { model.reopen(item.id) }
                        .buttonStyle(.plain).font(.caption).foregroundStyle(Theme.accent)
                        .accessibilityLabel("Reopen \(item.title)")
                }
                .pointerHover(cornerRadius: 8)
                .contextMenu { TaskContextMenu(model: model, id: item.id) }
            }
        }
        if model.archivedCount > 0 && model.searchText.isEmpty {
            // Put away, never gone: the search finds them (and their details have Unarchive).
            Button {
                searching = true
                model.searchText = "is:archived"
            } label: {
                Label("Archived (\(model.archivedCount))", systemImage: "archivebox").font(.caption.weight(.semibold))
                    .labelStyle(InlineLabelStyle())
            }
            .buttonStyle(.borderless)
            .foregroundStyle(Theme.accent)
            .padding(.top, 4)
        }
    }

    @ViewBuilder private var notesContent: some View {
        if model.dashboard.recentNotes.isEmpty { emptyText("Notes you and your agents add to tasks show here.").listRowSeparator(.hidden) }
        ForEach(model.dashboard.recentNotes) { n in
            // The note's task opens.
            Button { open(n.item.id) } label: {
                VStack(alignment: .leading, spacing: 3) {
                    Text(n.note.text).foregroundStyle(.primary).multilineTextAlignment(.leading)
                    HStack(spacing: 5) {
                        Avatar(actor: n.note.author)
                        Text("\(n.note.author.displayName) · \(n.item.title) · \(RelativeTime.format(n.note.at, now: model.now))")
                            .font(.caption).foregroundStyle(.secondary).lineLimit(1)
                    }
                }
                .frame(maxWidth: .infinity, alignment: .leading)
                .padding(.vertical, 5)
                .contentShape(Rectangle())
            }
            .buttonStyle(.borderless)
            .accessibilityHint("Opens \(n.item.title)")
        }
    }

    private func searchRow(_ item: WorkItem) -> some View {
        Button { open(item.id) } label: {
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
            .padding(.vertical, 4)
            .contentShape(Rectangle())
        }
        .buttonStyle(.borderless)
        .contextMenu { TaskContextMenu(model: model, id: item.id) }
        .accessibilityHint("Opens it")
    }

    /// A card that folds away: its name and count, then what's in it.
    private func foldingCard<Content: View>(_ title: String, count: Int, expanded: Binding<Bool>, @ViewBuilder content: () -> Content) -> some View {
        let inside = content()
        return Card {
            DisclosureGroup(isExpanded: expanded) {
                VStack(alignment: .leading, spacing: 2) { inside }.padding(.top, 6)
            } label: { sectionLabel(title, count: count) }
        }
    }

    /// The wide screen's second column: what moves (Workstreams), what's coming, what's done, and the latest notes.
    private var sideCards: some View {
        let streams = Presentation.workstreams(model.dashboard)
        return VStack(alignment: .leading, spacing: 12) {
            foldingCard("Workstreams", count: streams.count, expanded: $showWorkstreams) {
                if streams.isEmpty { emptyText(workstreamsEmpty) }
                ForEach(streams) { o in
                    Button { open(o.item.id) } label: { WorkstreamRow(entry: o, now: model.now) }
                        .buttonStyle(.plain)
                        .pointerHover()
                        .contextMenu { TaskContextMenu(model: model, id: o.item.id) }
                }
            }
            foldingCard("Coming up", count: model.dashboard.waiting.count, expanded: $showWaiting) {
                if model.dashboard.waiting.isEmpty { emptyText(comingUpEmpty) }
                ForEach(model.dashboard.waiting) { entry in
                    Button { open(entry.item.id) } label: { ComingUpRow(entry: entry, now: model.now, selected: shell?.selectedTask == entry.item.id) }
                        .buttonStyle(.plain)
                        .pointerHover(cornerRadius: 10)
                        .contextMenu { TaskContextMenu(model: model, id: entry.item.id) }
                }
            }
            foldingCard("Done today", count: model.doneToday.count, expanded: $showDone) {
                doneContent(inList: false)
            }
            foldingCard("Recent notes", count: model.dashboard.recentNotes.count, expanded: $showNotes) {
                notesContent
            }
        }
        .tint(.secondary)
    }

    /// Offscreen snapshots can't draw lists: the same content as cards.
    private var sections: some View {
        VStack(alignment: .leading, spacing: 12) {
            focusCard
            foldingCard("Do now", count: model.dashboard.now.count, expanded: $showNow) {
                if rest.isEmpty { emptyText("Nothing else right now.") }
                ForEach(rest) { entry in TaskRow(entry: entry, model: model, now: model.now) }
            }
            sideCards
        }
        .tint(.secondary)
    }

    private var groupTabs: some View {
        ScrollView(.horizontal, showsIndicators: false) {
            HStack(spacing: 2) {
                tab(nil, name: "All", color: nil)
                ForEach(model.groups) { g in tab(g.id, name: g.name, color: g.color ?? "#6366f1").contextMenu { groupActions(g) } }
                if let selected = model.groups.first(where: { $0.id == model.selectedGroupId }) {
                    Menu { groupActions(selected) } label: {
                        Image(systemName: "ellipsis").font(.caption.weight(.bold)).padding(.horizontal, 8).padding(.vertical, 6)
                    }
                    .menuStyle(.button)
                    .buttonStyle(.plain)
                    .menuIndicator(.hidden)
                    .fixedSize()
                    .foregroundStyle(.secondary)
                    .accessibilityLabel("Change the group \(selected.name)")
                }
                Button { addingGroup = true } label: {
                    Image(systemName: "plus").font(.caption.weight(.bold)).padding(.horizontal, 10).padding(.vertical, 6)
                }
                .buttonStyle(.plain)
                .foregroundStyle(.secondary)
                .accessibilityLabel("Add group")
            }
            .padding(3)
            .background(Capsule().fill(.quaternary.opacity(0.6)))
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

    private func tab(_ id: UUID?, name: String, color: String?) -> some View {
        let selected = model.selectedGroupId == id
        let count = model.count(for: id)
        return Button { withAnimation(.snappy(duration: 0.2)) { model.select(group: id) } } label: {
            HStack(spacing: 6) {
                if let color { Circle().fill(Color(hex: color)).frame(width: 7, height: 7) }
                Text(name).font(.subheadline.weight(.semibold))
                if count > 0 {
                    Text("\(count)").font(.caption2.weight(.bold)).monospacedDigit()
                        .padding(.horizontal, 6).padding(.vertical, 1)
                        .background(Capsule().fill(selected ? Theme.accent.opacity(0.15) : Color.secondary.opacity(0.15)))
                        .foregroundStyle(selected ? Theme.accent : .secondary)
                }
            }
            .padding(.horizontal, 12).padding(.vertical, 6)
            .foregroundStyle(selected ? .primary : .secondary)
            .background {
                if selected {
                    Capsule().fill(.background).shadow(color: .black.opacity(0.08), radius: 3, y: 1)
                }
            }
        }
        .buttonStyle(.plain)
        .accessibilityAddTraits(selected ? .isSelected : [])
    }

    private var captureBar: some View {
        HStack(spacing: 10) {
            Image(systemName: "plus").font(.body.weight(.semibold)).foregroundStyle(Theme.accent)
            if scrollable {
                TextField("What's on your mind?", text: $model.quickText)
                    .textFieldStyle(.plain)
                    .onSubmit { model.capture() }
            } else {
                // Offscreen snapshots can't render live text fields; show the placeholder instead.
                Text("What's on your mind?").foregroundStyle(.tertiary).frame(maxWidth: .infinity, alignment: .leading)
            }
            Button { model.capture() } label: {
                Text("Add").font(.subheadline.weight(.semibold)).foregroundStyle(.white)
                    .padding(.horizontal, 14).padding(.vertical, 7)
                    .background(Capsule().fill(Theme.brand))
            }
            .buttonStyle(.plain)
            .disabled(model.quickText.trimmingCharacters(in: .whitespaces).isEmpty)
        }
        .padding(.leading, 14).padding(.trailing, 5).padding(.vertical, 5)
        .background(Capsule().fill(.background))
        .overlay(Capsule().strokeBorder(.quaternary, lineWidth: 1))
        .shadow(color: .black.opacity(0.06), radius: 10, y: 4)
    }

    @ViewBuilder private var focusCard: some View {
        if let f = model.dashboard.focus {
            FocusCard(entry: f, model: model, now: model.now) { open(f.item.id) }
                .contextMenu { TaskContextMenu(model: model, id: f.item.id) }
                // A task dropped here is done first.
                .dropDestination(for: String.self) { texts, _ in
                    let ids = TaskDrag.ids(texts)
                    ids.forEach { model.arrangeNow(moving: $0, before: f.item.id) }
                    return !ids.isEmpty
                }
        } else {
            HStack(spacing: 14) {
                RoundedRectangle(cornerRadius: 14, style: .continuous).fill(Color(hex: "#10b981").opacity(0.14))
                    .frame(width: 48, height: 48)
                    .overlay(Image(systemName: "checkmark").font(.title3.weight(.bold)).foregroundStyle(Color(hex: "#10b981")))
                VStack(alignment: .leading, spacing: 3) {
                    Label("All clear", systemImage: "sparkles").font(.subheadline.weight(.semibold)).foregroundStyle(Color(hex: "#10b981"))
                    Text("Nothing to do right now. Enjoy the calm, or type above to add a task.").font(.headline)
                    if let next = model.dashboard.waiting.first, let wake = next.wakeAt {
                        Text("Next up: \(next.item.title) \(RelativeTime.format(wake, now: model.now))").font(.caption).foregroundStyle(.secondary)
                    }
                }
                Spacer(minLength: 0)
            }
            .padding(16)
            .background(RoundedRectangle(cornerRadius: 22, style: .continuous).fill(.background))
            .overlay(RoundedRectangle(cornerRadius: 22, style: .continuous).strokeBorder(Theme.calm, lineWidth: 1.5))
        }
    }

    private func sectionLabel(_ title: String, count: Int) -> some View {
        HStack {
            Text(title).font(.headline).foregroundStyle(.primary)
            Spacer()
            if count > 0 {
                Text("\(count)").font(.caption.weight(.bold)).monospacedDigit().foregroundStyle(.secondary)
                    .padding(.horizontal, 8).padding(.vertical, 1)
                    .background(Capsule().fill(.quaternary.opacity(0.7)))
            }
        }
    }

    private func emptyText(_ text: String) -> some View {
        Text(text).font(.subheadline).foregroundStyle(.tertiary).padding(.vertical, 6).frame(maxWidth: .infinity, alignment: .leading)
    }
}

struct IdentifiedID: Identifiable {
    let id: UUID
}

struct Avatar: View {
    let actor: Actor

    var body: some View {
        let letter = actor.kind == .user ? "Y" : String((actor.name ?? actor.kind.rawValue).prefix(1)).uppercased()
        Text(letter)
            .font(.system(size: 9, weight: .bold))
            .foregroundStyle(.white)
            .frame(width: 16, height: 16)
            .background(Circle().fill(actor.kind == .user ? Theme.brand : LinearGradient(colors: [Color(hex: "#0ea5e9"), Theme.accent], startPoint: .topLeading, endPoint: .bottomTrailing)))
    }
}

/// The hero "Do this now" card with a gradient hairline border (amber when a reminder rings).
struct FocusCard: View {
    let entry: AgendaEntry
    @ObservedObject var model: BoardModel
    let now: Date
    let open: () -> Void

    var body: some View {
        let priority = Color(hex: entry.effectivePriority.hexColor)
        VStack(alignment: .leading, spacing: 10) {
            HStack {
                Label(entry.needsAttention ? "Reminder" : "Do this now", systemImage: entry.needsAttention ? "alarm" : "scope")
                    .font(.subheadline.weight(.semibold))
                    .foregroundStyle(entry.needsAttention ? Theme.warnText : Theme.accent)
                Spacer()
                HStack(spacing: 5) {
                    Circle().fill(priority).frame(width: 6, height: 6)
                    Text(entry.effectivePriority.label).font(.caption2.weight(.bold))
                }
                .foregroundStyle(priority)
                .padding(.horizontal, 9).padding(.vertical, 3)
                .background(Capsule().fill(priority.opacity(0.14)))
            }
            Button(action: open) {
                Text(Presentation.titleWithIcon(entry.item)).font(.title2.weight(.bold)).multilineTextAlignment(.leading).foregroundStyle(.primary)
            }
            .buttonStyle(.plain)
            if !entry.breadcrumb.isEmpty {
                Text(entry.breadcrumb.joined(separator: " › ")).font(.caption).foregroundStyle(.secondary)
            }
            let chips = Presentation.chips(for: entry, now: now, notes: false)
            if !chips.isEmpty {
                FlowLayout(spacing: 5) { ForEach(chips, id: \.self) { ChipView(chip: $0) } }
            }
            if let reminder = entry.dueReminder {
                Label(reminder.message, systemImage: "alarm")
                    .font(.subheadline.weight(.semibold))
                    .foregroundStyle(Theme.warnText)
                    .padding(.horizontal, 12).padding(.vertical, 9)
                    .frame(maxWidth: .infinity, alignment: .leading)
                    .background(RoundedRectangle(cornerRadius: 12, style: .continuous).fill(Color(hex: "#f59e0b").opacity(0.14)))
            }
            // The task you're on: every note, newest first.
            CardNotes(item: entry.item, now: now)
            FlowLayout(spacing: 6) {
                if entry.state == .actionable {
                    Button { model.complete(entry.item.id) } label: {
                        // "Mark done", not "Done": a bare "Done" reads as "close this".
                        Label("Mark done", systemImage: "checkmark").font(.subheadline.weight(.semibold)).foregroundStyle(.white)
                            .padding(.horizontal, 14).padding(.vertical, 7)
                            .background(Capsule().fill(Theme.brand))
                    }
                    .buttonStyle(.plain)
                }
                SnoozeMenu(model: model, id: entry.item.id, style: .pill)
                PillButton(title: "Focus", systemImage: "play.fill") { model.startFocus(entry.item.id) }
                // While this task is timed, stopping it is one tap away; Note, Timer and Skip are in ⋯.
                if model.runningTimer?.item.id == entry.item.id {
                    PillButton(title: "Stop timer", systemImage: "stop.circle") { model.toggleTimer(entry.item.id) }
                }
                MoreMenu(model: model, entry: entry, pill: true, focusCard: true, openDetails: open)
            }
            if model.openNotes.contains(entry.item.id) {
                NoteField(model: model, id: entry.item.id)
            }
        }
        .padding(18)
        .background(
            ZStack(alignment: .topTrailing) {
                RoundedRectangle(cornerRadius: 22, style: .continuous).fill(.background)
                Circle().fill(priority.opacity(0.18)).frame(width: 220, height: 220).blur(radius: 40).offset(x: 70, y: -110)
            }
            .clipShape(RoundedRectangle(cornerRadius: 22, style: .continuous)))
        .overlay(RoundedRectangle(cornerRadius: 22, style: .continuous).strokeBorder(entry.needsAttention ? Theme.attention : Theme.brand, lineWidth: 1.5))
        .shadow(color: .black.opacity(0.12), radius: 18, y: 10)
        // In a list a label gets a list row's icon column and tint: here it stays a plain icon and title.
        .labelStyle(InlineLabelStyle())
    }
}

/// An icon and its title side by side, as they are outside a list.
struct InlineLabelStyle: LabelStyle {
    func makeBody(configuration: Configuration) -> some View {
        HStack(spacing: 6) {
            configuration.icon
            configuration.title
        }
    }
}

/// The rarer actions behind ⋯ (dismissing a reminder, copying a link), so the buttons stay few.
struct MoreMenu: View {
    @ObservedObject var model: BoardModel
    let entry: AgendaEntry
    var pill = false
    /// On the focus card: Note, the task timer and Skip are here instead of buttons of their own.
    var focusCard = false

    @Environment(\.openURL) var openURL
    /// Opens the task's details here (nil: not offered).
    var openDetails: (() -> Void)?

    var body: some View {
        Menu {
            if focusCard {
                Button { model.toggleNote(entry.item.id) } label: { Label("Add a note", systemImage: "note.text") }
                if model.runningTimer?.item.id != entry.item.id {
                    Button { model.toggleTimer(entry.item.id) } label: { Label("Start the timer", systemImage: "stopwatch") }
                }
                if model.dashboard.now.count > 1 {
                    Button { model.skip(entry.item.id) } label: { Label("Skip: do something else first", systemImage: "arrow.down") }
                }
                Divider()
            }
            if let openDetails {
                Button(action: openDetails) { Label("Open details", systemImage: "arrow.up.left.and.arrow.down.right") }
            }
            if model.canOpenInBrowser {
                // The full app in the browser, on this task (signed in for you).
                Button {
                    let id = entry.item.id.uuidString.lowercased()
                    Task { if let url = await model.launchURL(returnPath: "/#/task/\(id)") { openURL(url) } }
                } label: { Label("Open in the browser", systemImage: "safari") }
            }
            if let reminder = entry.dueReminder {
                Button { model.dismissReminder(entry.item.id, reminderId: reminder.id) } label: { Label("Dismiss reminder", systemImage: "bell.slash") }
            }
            Menu {
                ForEach(EnergyChoice.all) { choice in
                    Button { model.setEnergy(entry.item.id, level: choice.level) } label: {
                        if entry.item.energy == choice.level { Label(choice.label, systemImage: "checkmark") } else { Text(choice.label) }
                    }
                }
            } label: { Label("Energy per hour", systemImage: "bolt") }
            Button { Clipboard.copy(TaskLinks.forTask(entry.item.id)) } label: { Label("Copy link", systemImage: "link") }
        } label: {
            if pill {
                // In a Text the symbol takes the line's height, so the pill matches the labeled ones beside it.
                Text(Image(systemName: "ellipsis")).font(.subheadline.weight(.semibold))
                    .padding(.horizontal, 12).padding(.vertical, 6)
                    .background(Capsule().fill(.background))
                    .overlay(Capsule().strokeBorder(.quaternary, lineWidth: 1))
            } else {
                Image(systemName: "ellipsis")
            }
        }
        .menuStyle(.button)
        .buttonStyle(.plain)
        .menuIndicator(.hidden)
        .fixedSize()
        .accessibilityLabel("More")
        .help("More")
    }
}

/// A card's notes, newest first: the text, then who wrote it and when.
struct CardNotes: View {
    let item: WorkItem
    let now: Date

    var body: some View {
        let notes = Presentation.notes(item, now: now)
        if !notes.isEmpty {
            VStack(alignment: .leading, spacing: 8) {
                ForEach(notes) { note in
                    VStack(alignment: .leading, spacing: 1) {
                        Text(note.text).font(.subheadline).foregroundStyle(.primary).fixedSize(horizontal: false, vertical: true)
                        Text(note.meta).font(.caption2).foregroundStyle(.secondary)
                    }
                    .padding(.leading, 10)
                    .overlay(alignment: .leading) { Capsule().fill(Theme.accent.opacity(0.25)).frame(width: 3) }
                }
            }
            .accessibilityElement(children: .contain)
            .accessibilityLabel("Notes")
        }
    }
}

/// A task's energy per hour, as offered in ⋯ › Energy per hour.
struct EnergyChoice: Identifiable {
    let level: Int?
    let label: String
    /// The same in words, for a picker: its label draws ⚡ as a missing glyph on iPad.
    let words: String
    var id: String { label }

    static let all = [
        EnergyChoice(level: nil, label: "Not said (counts as ⚡)", words: "Not said (counts as light)"),
        EnergyChoice(level: 1, label: "⚡ light", words: "Light"),
        EnergyChoice(level: 2, label: "⚡⚡", words: "Medium"),
        EnergyChoice(level: 3, label: "⚡⚡⚡ draining", words: "Draining"),
    ]
}

/// Plain text on the clipboard (macOS and iOS).
enum Clipboard {
    static func copy(_ text: String) {
        #if canImport(AppKit)
        NSPasteboard.general.clearContents()
        NSPasteboard.general.setString(text, forType: .string)
        #elseif canImport(UIKit)
        UIPasteboard.general.string = text
        #endif
    }
}

struct PillButton: View {
    let title: String
    let systemImage: String
    let action: () -> Void

    var body: some View {
        Button(action: action) {
            Label(title, systemImage: systemImage).font(.subheadline.weight(.semibold))
                .padding(.horizontal, 12).padding(.vertical, 6)
                .background(Capsule().fill(.background))
                .overlay(Capsule().strokeBorder(.quaternary, lineWidth: 1))
        }
        .buttonStyle(.plain)
    }
}

struct SnoozeMenu: View {
    enum Style { case icon, pill, row }

    @ObservedObject var model: BoardModel
    let id: UUID
    var style = Style.icon
    @State private var picking = false

    var body: some View {
        switch style {
        case .row:
            // A row of a form: a plain menu (sizing it like a pill breaks the row's layout).
            Menu { choices } label: { Label("Later…", systemImage: "clock") }
                .accessibilityLabel("Later")
                .sheet(isPresented: $picking) { SnoozePicker(model: model, id: id) }
        case .pill, .icon:
            Menu { choices } label: {
                if style == .pill {
                    Label("Later", systemImage: "clock").font(.subheadline.weight(.semibold))
                        .padding(.horizontal, 12).padding(.vertical, 6)
                        .background(Capsule().fill(.background))
                        .overlay(Capsule().strokeBorder(.quaternary, lineWidth: 1))
                } else {
                    Image(systemName: "clock")
                }
            }
            .menuStyle(.button)
            .buttonStyle(.plain)
            .fixedSize()
            .accessibilityLabel("Later")
            .sheet(isPresented: $picking) { SnoozePicker(model: model, id: id) }
        }
    }

    @ViewBuilder private var choices: some View {
        let now = Date()
        ForEach(model.snoozeChoices) { choice in
            Button { model.snooze(id, until: choice.at) } label: {
                Text("\(choice.label)  ·  \(Snooze.describe(choice.at, now: now, timeZone: .current))")
            }
        }
        Divider()
        Button { picking = true } label: { Label("Pick a time…", systemImage: "calendar") }
        let others = model.waitCandidates(for: id)
        Menu {
            if others.isEmpty { Text("No other open tasks") }
            ForEach(others.prefix(12), id: \.id) { other in
                Button(other.title) { model.waitFor(id, after: other) }
            }
        } label: { Label("After another task", systemImage: "link") }
    }
}

/// "Pick a time…": a date and time on the calendar (it starts at tomorrow 9:00).
struct SnoozePicker: View {
    @ObservedObject var model: BoardModel
    let id: UUID
    @Environment(\.dismiss) private var dismiss
    @State private var at = QuickCaptureParser.tomorrowMorning(now: Date(), timeZone: .current)

    var body: some View {
        VStack(alignment: .leading, spacing: 14) {
            Text("Snooze until…").font(.title3.weight(.semibold))
            DatePicker("When", selection: $at, in: Date()..., displayedComponents: [.date, .hourAndMinute])
                .datePickerStyle(.graphical)
                .labelsHidden()
            Text(Snooze.describe(at, now: Date(), timeZone: .current)).foregroundStyle(.secondary)
            HStack {
                Spacer()
                Button("Cancel", role: .cancel) { dismiss() }.keyboardShortcut(.cancelAction)
                Button("Snooze") {
                    model.snooze(id, until: at)
                    dismiss()
                }
                .keyboardShortcut(.defaultAction)
                .buttonStyle(.borderedProminent)
                .tint(Theme.accent)
            }
        }
        .padding(20)
        .frame(minWidth: 320)
    }
}

struct NoteField: View {
    @ObservedObject var model: BoardModel
    let id: UUID
    @Environment(\.shellState) private var shell
    /// Typing here: the list's Space and Return wait (they'd mark the picked task done, or open it).
    @FocusState private var focused: Bool

    var body: some View {
        HStack {
            TextField("What did you do? What is next?", text: model.draftBinding(for: id))
                .textFieldStyle(.roundedBorder)
                .focused($focused)
                .onSubmit(save)
            Button("Save", action: save).buttonStyle(.borderedProminent).tint(Theme.accent)
        }
        .onChange(of: focused) { _, now in shell?.typing = now }
        .onDisappear { if focused { shell?.typing = false } }
    }

    private func save() {
        let text = model.noteDrafts[id] ?? ""
        guard !text.trimmingCharacters(in: .whitespaces).isEmpty else { return }
        model.addNote(id, text: text)
    }
}

/// A task in Do now or Coming up: a circle to finish it, its title and what to know about it. The rest is a swipe or a
/// right-click away (TaskRowActions), so the list stays calm.
struct TaskRow: View {
    let entry: AgendaEntry
    @ObservedObject var model: BoardModel
    let now: Date

    var body: some View {
        HStack(alignment: .top, spacing: 12) {
            DoneCircle(model: model, entry: entry)
            VStack(alignment: .leading, spacing: 4) {
                Text(Presentation.titleWithIcon(entry.item))
                    .font(.body.weight(.medium))
                    .multilineTextAlignment(.leading)
                    .foregroundStyle(entry.state == .waiting ? .secondary : .primary)
                if !entry.breadcrumb.isEmpty {
                    Text(entry.breadcrumb.joined(separator: " › ")).font(.caption).foregroundStyle(.tertiary).lineLimit(1)
                }
                let chips = Presentation.chips(for: entry, now: now, notes: false)
                if !chips.isEmpty {
                    FlowLayout(spacing: 5) { ForEach(chips, id: \.self) { ChipView(chip: $0) } }
                }
                if let reminder = entry.dueReminder {
                    Label(reminder.message, systemImage: "alarm").font(.caption.weight(.semibold)).foregroundStyle(Theme.warnText)
                }
                if !entry.item.notes.isEmpty {
                    let notesOpen = model.shownNotes.contains(entry.item.id)
                    Button { model.toggleShownNotes(entry.item.id) } label: {
                        Text(Presentation.notesToggle(count: entry.item.notes.count, open: notesOpen)).font(.caption.weight(.semibold)).foregroundStyle(.secondary)
                    }
                    .buttonStyle(.plain)
                    .accessibilityLabel(notesOpen ? "Hide notes" : "Show notes")
                    if notesOpen { CardNotes(item: entry.item, now: now) }
                }
                if model.openNotes.contains(entry.item.id) {
                    NoteField(model: model, id: entry.item.id)
                }
            }
            Spacer(minLength: 0)
            if model.runningTimer?.item.id == entry.item.id {
                Image(systemName: "stopwatch").font(.caption).foregroundStyle(Color(hex: "#ef4444"))
                    .padding(.top, 3)
                    .accessibilityLabel("Being timed")
            }
        }
        .padding(.vertical, 4)
        .contentShape(Rectangle())
        .labelStyle(InlineLabelStyle())
    }
}

/// The circle in front of a task: tap it to finish the task (a waiting one shows a clock and comes back with a tap).
struct DoneCircle: View {
    @ObservedObject var model: BoardModel
    let entry: AgendaEntry
    @State private var hovering = false
    @Environment(\.shellState) private var shell

    var body: some View {
        let color = Color(hex: entry.effectivePriority.hexColor)
        // Only its own snooze or wait can be lifted here (one that waits for its parent is finished like any other).
        if Presentation.canBringBack(entry.item, now: model.now) {
            Button { model.bringBack(entry.item.id) } label: {
                Image(systemName: "clock").font(.body).foregroundStyle(.secondary).frame(width: 22, height: 22)
            }
            .buttonStyle(.plain)
            .accessibilityLabel("Bring back now")
            .help("Bring back now")
        } else {
            Button { markDone(model, shell, entry.item.id) } label: {
                ZStack {
                    Circle().strokeBorder(color, lineWidth: 2)
                    Circle().fill(color.opacity(hovering ? 0.9 : 0.12)).padding(3)
                    if hovering { Image(systemName: "checkmark").font(.system(size: 10, weight: .bold)).foregroundStyle(.white) }
                }
                .frame(width: 22, height: 22)
                .contentShape(Circle())
            }
            .buttonStyle(.plain)
            .onHover { hovering = $0 }
            .accessibilityLabel("Mark done, \(entry.effectivePriority.label) priority")
            .help("Mark done")
        }
    }
}

/// A task waiting for later in Today's side column: its title and when it comes back.
struct ComingUpRow: View {
    let entry: AgendaEntry
    let now: Date
    var selected = false

    var body: some View {
        HStack(alignment: .top, spacing: 10) {
            Image(systemName: entry.needsAttention ? "alarm" : "clock").font(.subheadline)
                .foregroundStyle(entry.needsAttention ? Theme.warnText : .secondary)
                .frame(width: 18)
            VStack(alignment: .leading, spacing: 3) {
                Text(Presentation.titleWithIcon(entry.item)).font(.subheadline.weight(.medium)).multilineTextAlignment(.leading)
                let chips = Presentation.chips(for: entry, now: now, notes: false)
                if !chips.isEmpty { FlowLayout(spacing: 4) { ForEach(chips, id: \.self) { ChipView(chip: $0) } } }
            }
            Spacer(minLength: 0)
        }
        .padding(.vertical, 6).padding(.horizontal, 6)
        .background(RoundedRectangle(cornerRadius: 10, style: .continuous).fill(selected ? Theme.accent.opacity(0.12) : .clear))
        .contentShape(Rectangle())
    }
}

/// Space finishes the picked task, Return opens it (⌘↩ and the Task menu do too).
struct ListKeys: ViewModifier {
    @ObservedObject var model: BoardModel
    let open: (UUID) -> Void
    @Environment(\.shellState) private var shell

    func body(content: Content) -> some View {
        content
            .onKeyPress(.space) {
                guard let shell, let id = shell.keyTarget else { return .ignored }
                shell.toggleDone(id)
                return .handled
            }
            .onKeyPress(.return) {
                guard let id = shell?.keyTarget else { return .ignored }
                open(id)
                return .handled
            }
    }
}

extension View {
    /// Today's lists: grouped cards on the iPhone and iPad, an inset list on a Mac; the backdrop shows through.
    func todayList() -> some View {
        self
            #if os(iOS)
            .listStyle(.insetGrouped)
            .listSectionSpacing(.compact)
            #else
            .listStyle(.inset)
            #endif
            .scrollContentBackground(.hidden)
            .contentMargins(.top, DashboardView.listTop, for: .scrollContent)
    }
}

struct WorkstreamRow: View {
    let entry: OverviewEntry
    let now: Date

    var body: some View {
        let color = Color(hex: entry.topPriority.hexColor)
        VStack(alignment: .leading, spacing: 6) {
            HStack(spacing: 10) {
                PriorityDot(priority: entry.topPriority)
                Text(Presentation.titleWithIcon(entry.item)).fontWeight(.semibold)
                Spacer()
                Image(systemName: "doc.text").font(.caption).foregroundStyle(.tertiary)
            }
            GeometryReader { geo in
                let fraction = entry.totalLeaves == 0 ? 0 : CGFloat(entry.doneLeaves) / CGFloat(entry.totalLeaves)
                ZStack(alignment: .leading) {
                    Capsule().fill(.quaternary.opacity(0.6))
                    Capsule().fill(LinearGradient(colors: [color, color.opacity(0.6)], startPoint: .leading, endPoint: .trailing))
                        .frame(width: max(6, geo.size.width * fraction))
                }
            }
            .frame(height: 6)
            FlowLayout(spacing: 5) { ForEach(chips, id: \.self) { ChipView(chip: $0) } }
        }
        .padding(.vertical, 8)
        .padding(.horizontal, 8)
        .contentShape(Rectangle())
    }

    private var chips: [Chip] {
        var chips = [Chip("\(entry.doneLeaves)/\(entry.totalLeaves) done", .muted)]
        if entry.actionableCount > 0 { chips.append(Chip("\(entry.actionableCount) now", .step)) }
        if entry.waitingCount > 0 { chips.append(Chip("\(entry.waitingCount) waiting", .info)) }
        if let next = entry.nextWakeAt { chips.append(Chip("next \(RelativeTime.format(next, now: now))", .muted)) }
        return chips
    }
}

/// The app icon's mark (wwwroot/img/logo.svg): one lit task, the rest faint. Drawn in the icon's 1024-unit grid.
struct AppIconGlyph: View {
    var body: some View {
        GeometryReader { geometry in
            let s = min(geometry.size.width, geometry.size.height) / 1024
            ZStack(alignment: .topLeading) {
                mark(Circle(), x: 212, y: 220, width: 220, height: 220, opacity: 1, scale: s)
                mark(Capsule(), x: 482, y: 290, width: 332, height: 80, opacity: 1, scale: s)
                mark(Circle(), x: 260, y: 498, width: 124, height: 124, opacity: 0.3, scale: s)
                mark(Capsule(), x: 442, y: 534, width: 300, height: 52, opacity: 0.3, scale: s)
                mark(Circle(), x: 260, y: 678, width: 124, height: 124, opacity: 0.18, scale: s)
                mark(Capsule(), x: 442, y: 714, width: 240, height: 52, opacity: 0.18, scale: s)
            }
            .frame(width: geometry.size.width, height: geometry.size.height, alignment: .topLeading)
        }
        .accessibilityHidden(true)
    }

    private func mark<S: Shape>(_ shape: S, x: CGFloat, y: CGFloat, width: CGFloat, height: CGFloat, opacity: Double, scale: CGFloat) -> some View {
        shape.fill(Color.white.opacity(opacity))
            .frame(width: width * scale, height: height * scale)
            .offset(x: x * scale, y: y * scale)
    }
}
#endif
