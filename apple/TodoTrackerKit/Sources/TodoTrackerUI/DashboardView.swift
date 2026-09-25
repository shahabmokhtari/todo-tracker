#if canImport(SwiftUI)
import SwiftUI
import TodoTrackerKit

extension Color {
    init(hex: String) {
        let value = UInt64(hex.replacingOccurrences(of: "#", with: ""), radix: 16) ?? 0x3B82F6
        self.init(red: Double((value >> 16) & 0xFF) / 255, green: Double((value >> 8) & 0xFF) / 255, blue: Double(value & 0xFF) / 255)
    }
}

/// ADHD-friendly dashboard shared by iOS and macOS: group tabs, quick capture, one focus card,
/// and collapsible Do now / Waiting / Workstreams / Recent notes sections.
public struct DashboardView: View {
    @ObservedObject var model: BoardModel
    @State private var detailId: UUID?
    @State private var showWaiting = false
    @State private var showNotes = false
    @State private var showNow = true
    @State private var showWorkstreams = true
    @State private var newGroupName = ""
    @State private var addingGroup = false

    public init(model: BoardModel) {
        self.model = model
    }

    public var body: some View {
        VStack(spacing: 10) {
            groupTabs
            captureBar
            if let status = model.status {
                Text(status).font(.caption).foregroundStyle(.secondary).frame(maxWidth: .infinity, alignment: .leading)
            }
            ScrollView {
                VStack(alignment: .leading, spacing: 12) {
                    focusCard
                    DisclosureGroup(isExpanded: $showNow) {
                        ForEach(model.dashboard.now.filter { $0.item.id != model.dashboard.focus?.item.id }) { entry in
                            TaskRow(entry: entry, model: model, now: model.now) { detailId = entry.item.id }
                        }
                    } label: { sectionLabel("Do now", count: model.dashboard.now.count) }
                    DisclosureGroup(isExpanded: $showWaiting) {
                        ForEach(model.dashboard.waiting) { entry in
                            TaskRow(entry: entry, model: model, now: model.now) { detailId = entry.item.id }
                        }
                    } label: { sectionLabel("Waiting", count: model.dashboard.waiting.count) }
                    DisclosureGroup(isExpanded: $showWorkstreams) {
                        ForEach(model.dashboard.overview) { o in
                            Button { detailId = o.item.id } label: { WorkstreamRow(entry: o, now: model.now) }.buttonStyle(.plain)
                        }
                    } label: { sectionLabel("Workstreams", count: model.dashboard.overview.count) }
                    DisclosureGroup(isExpanded: $showNotes) {
                        ForEach(model.dashboard.recentNotes) { n in
                            VStack(alignment: .leading, spacing: 2) {
                                Text(n.note.text)
                                Text("\(n.item.title) · \(n.note.author.displayName) · \(RelativeTime.format(n.note.at, now: model.now))")
                                    .font(.caption).foregroundStyle(.secondary)
                            }
                            .frame(maxWidth: .infinity, alignment: .leading)
                            .padding(.vertical, 4)
                        }
                    } label: { sectionLabel("Recent notes", count: model.dashboard.recentNotes.count) }
                }
                .padding(.horizontal, 2)
            }
            PomodoroBar(model: model)
        }
        .padding(12)
        .sheet(item: Binding(get: { detailId.map(IdentifiedID.init) }, set: { detailId = $0?.id })) { wrapped in
            TaskDetailView(model: model, itemId: wrapped.id)
        }
        .alert("New group", isPresented: $addingGroup) {
            TextField("Name", text: $newGroupName)
            Button("Add") {
                model.addGroup(newGroupName)
                newGroupName = ""
            }
            Button("Cancel", role: .cancel) {}
        }
    }

    private var groupTabs: some View {
        ScrollView(.horizontal, showsIndicators: false) {
            HStack(spacing: 6) {
                tab(nil, name: "All", color: "#6366f1")
                ForEach(model.groups) { g in tab(g.id, name: g.name, color: g.color ?? "#6366f1") }
                Button { addingGroup = true } label: { Image(systemName: "plus") }
                    .buttonStyle(.borderless)
                    .accessibilityLabel("Add group")
            }
        }
    }

    private func tab(_ id: UUID?, name: String, color: String) -> some View {
        let selected = model.selectedGroupId == id
        let count = model.count(for: id)
        return Button { model.select(group: id) } label: {
            HStack(spacing: 4) {
                Circle().fill(Color(hex: color)).frame(width: 7, height: 7)
                Text(name).fontWeight(selected ? .semibold : .regular)
                if count > 0 { Text("\(count)").font(.caption2).padding(.horizontal, 5).background(Capsule().fill(.quaternary)) }
            }
            .padding(.horizontal, 10).padding(.vertical, 4)
            .background(Capsule().strokeBorder(selected ? Color(hex: color) : .clear, lineWidth: 1.5))
        }
        .buttonStyle(.plain)
        .accessibilityAddTraits(selected ? .isSelected : [])
    }

    private var captureBar: some View {
        HStack {
            TextField("Add a task…  !! @2h due:tomorrow", text: $model.quickText)
                .textFieldStyle(.roundedBorder)
                .onSubmit { model.capture() }
            Button("Add") { model.capture() }.disabled(model.quickText.trimmingCharacters(in: .whitespaces).isEmpty)
        }
    }

    @ViewBuilder private var focusCard: some View {
        VStack(alignment: .leading, spacing: 6) {
            Text(model.dashboard.focus?.needsAttention == true ? "⏰ REMINDER" : "DO THIS NOW").font(.caption).fontWeight(.semibold).foregroundStyle(.secondary)
            if let f = model.dashboard.focus {
                TaskRow(entry: f, model: model, now: model.now, big: true) { detailId = f.item.id }
            } else {
                Text("Nothing is due. Enjoy the calm ✨").font(.title3).fontWeight(.semibold)
                if let next = model.dashboard.waiting.first, let wake = next.wakeAt {
                    Text("Next: \(next.item.title) \(RelativeTime.format(wake, now: model.now))").font(.caption).foregroundStyle(.secondary)
                }
            }
        }
        .padding(12)
        .frame(maxWidth: .infinity, alignment: .leading)
        .background(RoundedRectangle(cornerRadius: 12).fill(.background.secondary))
    }

    private func sectionLabel(_ title: String, count: Int) -> some View {
        HStack {
            Text(title).font(.headline)
            if count > 0 { Text("\(count)").foregroundStyle(.secondary) }
        }
    }
}

struct IdentifiedID: Identifiable {
    let id: UUID
}

struct TaskRow: View {
    let entry: AgendaEntry
    @ObservedObject var model: BoardModel
    let now: Date
    var big = false
    let open: () -> Void

    var body: some View {
        HStack(alignment: .top, spacing: 8) {
            RoundedRectangle(cornerRadius: 2).fill(Color(hex: entry.effectivePriority.hexColor)).frame(width: 4)
                .accessibilityLabel("\(entry.effectivePriority.label) priority")
            VStack(alignment: .leading, spacing: 3) {
                Button(action: open) {
                    Text(entry.item.title).font(big ? .title3 : .body).fontWeight(.semibold).multilineTextAlignment(.leading)
                }
                .buttonStyle(.plain)
                if !meta.isEmpty { Text(meta).font(.caption).foregroundStyle(entry.isOverdue ? .red : .secondary) }
                if let reminder = entry.dueReminder { Text("⏰ \(reminder.message)").font(.caption).fontWeight(.semibold).foregroundStyle(.orange) }
                HStack(spacing: 10) {
                    if entry.state == .actionable {
                        Button { model.complete(entry.item.id) } label: { Label("Done", systemImage: "checkmark") }
                    }
                    if let reminder = entry.dueReminder {
                        Button { model.dismissReminder(entry.item.id, reminderId: reminder.id) } label: { Label("Dismiss", systemImage: "bell.slash") }
                    }
                    if entry.state == .waiting && !entry.needsAttention {
                        Button { model.bringBack(entry.item.id) } label: { Label("Now", systemImage: "arrow.uturn.backward") }
                    }
                    Menu {
                        Button("15 min") { model.snooze(entry.item.id, minutes: 15) }
                        Button("1 hour") { model.snooze(entry.item.id, minutes: 60) }
                        Button("3 hours") { model.snooze(entry.item.id, minutes: 180) }
                        Button("Tomorrow 9:00") { model.snoozeUntilTomorrow(entry.item.id) }
                        Button("+24 hours") { model.snooze(entry.item.id, minutes: 1440) }
                    } label: { Label("Later", systemImage: "alarm") }
                    Button { model.toggleNote(entry.item.id) } label: { Label("Note", systemImage: "square.and.pencil") }
                    Button { model.startFocus(entry.item.id) } label: { Label("Focus", systemImage: "timer") }
                }
                .labelStyle(big ? AnyLabelStyle(.titleAndIcon) : AnyLabelStyle(.iconOnly))
                .buttonStyle(.borderless)
                .font(.callout)
                if model.openNotes.contains(entry.item.id) {
                    HStack {
                        TextField("What did you do? What is next?", text: model.draftBinding(for: entry.item.id)).textFieldStyle(.roundedBorder).onSubmit(save)
                        Button("Save", action: save)
                    }
                }
            }
            Spacer(minLength: 0)
        }
        .padding(.vertical, 6)
        .padding(.horizontal, 4)
        .background(RoundedRectangle(cornerRadius: 8).fill(entry.needsAttention ? Color.orange.opacity(0.12) : .clear))
    }

    private var meta: String {
        var bits: [String] = []
        if !entry.breadcrumb.isEmpty { bits.append(entry.breadcrumb.joined(separator: " › ")) }
        if let step = entry.stepLabel { bits.append(step) }
        if let wake = entry.wakeAt { bits.append("back \(RelativeTime.format(wake, now: now))") }
        if let deadline = entry.item.deadline { bits.append((entry.isOverdue ? "overdue " : "due ") + RelativeTime.format(deadline, now: now)) }
        return bits.joined(separator: " · ")
    }

    private func save() {
        let text = model.noteDrafts[entry.item.id] ?? ""
        guard !text.trimmingCharacters(in: .whitespaces).isEmpty else { return }
        model.addNote(entry.item.id, text: text)
    }
}

struct AnyLabelStyle: LabelStyle {
    private let make: (Configuration) -> AnyView

    init<S: LabelStyle>(_ style: S) {
        make = { AnyView(style.makeBody(configuration: $0)) }
    }

    func makeBody(configuration: Configuration) -> some View { make(configuration) }
}

struct WorkstreamRow: View {
    let entry: OverviewEntry
    let now: Date

    var body: some View {
        VStack(alignment: .leading, spacing: 4) {
            Text(entry.item.title).fontWeight(.semibold)
            ProgressView(value: Double(entry.doneLeaves), total: Double(max(entry.totalLeaves, 1)))
                .tint(Color(hex: entry.topPriority.hexColor))
            Text(summary).font(.caption).foregroundStyle(.secondary)
        }
        .padding(.vertical, 4)
        .frame(maxWidth: .infinity, alignment: .leading)
    }

    private var summary: String {
        var bits = ["\(entry.doneLeaves)/\(entry.totalLeaves) done"]
        if entry.actionableCount > 0 { bits.append("\(entry.actionableCount) now") }
        if entry.waitingCount > 0 { bits.append("\(entry.waitingCount) waiting") }
        if let next = entry.nextWakeAt { bits.append("next \(RelativeTime.format(next, now: now))") }
        return bits.joined(separator: " · ")
    }
}

struct PomodoroBar: View {
    @ObservedObject var model: BoardModel

    var body: some View {
        let p = model.board.pomodoro
        HStack(spacing: 10) {
            Text("🍅")
            Text(model.pomodoroText).font(.title3.monospacedDigit()).fontWeight(.semibold)
            Text(label(p)).font(.caption).foregroundStyle(.secondary).lineLimit(1)
            Spacer()
            if p.phase == .idle {
                Button { model.startFocus(nil) } label: { Image(systemName: "play.fill") }.accessibilityLabel("Start focus")
            } else {
                if p.isRunning {
                    Button { model.pausePomodoro() } label: { Image(systemName: "pause.fill") }.accessibilityLabel("Pause")
                } else {
                    Button { model.resumePomodoro() } label: { Image(systemName: "play.fill") }.accessibilityLabel("Resume")
                }
                Button { model.skipPomodoro() } label: { Image(systemName: "forward.end.fill") }.accessibilityLabel("Skip")
                Button { model.resetPomodoro() } label: { Image(systemName: "arrow.counterclockwise") }.accessibilityLabel("Reset")
            }
        }
        .buttonStyle(.borderless)
        .padding(10)
        .background(RoundedRectangle(cornerRadius: 12).fill(.background.secondary))
    }

    private func label(_ p: PomodoroTimer) -> String {
        let phase: String
        switch p.phase {
        case .shortBreak: phase = "Break"
        case .longBreak: phase = "Long break"
        default: phase = "Focus"
        }
        if p.phase != .idle, let id = p.itemId, let item = model.board.find(id) { return "\(phase) · \(item.title)" }
        return phase
    }
}
#endif
