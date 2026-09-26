#if canImport(SwiftUI)
import SwiftUI
import TodoTrackerKit

extension Color {
    init(hex: String) {
        let value = UInt64(hex.replacingOccurrences(of: "#", with: ""), radix: 16) ?? 0x3B82F6
        self.init(red: Double((value >> 16) & 0xFF) / 255, green: Double((value >> 8) & 0xFF) / 255, blue: Double(value & 0xFF) / 255)
    }
}

/// Design tokens shared with the web dashboard and the Windows sidebar.
enum Theme {
    static let accent = Color(hex: "#6366f1")
    static let brand = LinearGradient(colors: [Color(hex: "#6366f1"), Color(hex: "#8b5cf6"), Color(hex: "#ec4899")], startPoint: .topLeading, endPoint: .bottomTrailing)
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
    @State private var showWaiting = false
    @State private var showNotes = false
    @State private var showNow = true
    @State private var showWorkstreams = true
    @State private var newGroupName = ""
    @State private var addingGroup = false

    public init(model: BoardModel, scrollable: Bool = true) {
        self.model = model
        self.scrollable = scrollable
    }

    public var body: some View {
        VStack(spacing: 12) {
            header
            groupTabs
            captureBar
            if let status = model.status {
                Text(status).font(.caption).foregroundStyle(.secondary).frame(maxWidth: .infinity, alignment: .leading).padding(.horizontal, 4)
            }
            if scrollable {
                ScrollView { sections.padding(.bottom, 8) }.scrollIndicators(.hidden)
            } else {
                sections
            }
            PomodoroBar(model: model)
        }
        .padding(14)
        .background(backdrop)
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
                .overlay(Image(systemName: "checkmark").font(.system(size: 15, weight: .heavy)).foregroundStyle(.white))
                .shadow(color: Theme.accent.opacity(0.35), radius: 6, y: 3)
            VStack(alignment: .leading, spacing: 1) {
                Text(Presentation.greeting(model.now)).font(.title3.weight(.bold))
                Text(Presentation.summary(model.dashboard)).font(.caption).foregroundStyle(.secondary).lineLimit(1)
            }
            Spacer()
        }
    }

    private var sections: some View {
        VStack(alignment: .leading, spacing: 12) {
            focusCard
            Card {
                DisclosureGroup(isExpanded: $showNow) {
                    let rest = model.dashboard.now.filter { $0.item.id != model.dashboard.focus?.item.id }
                    if rest.isEmpty { emptyText("Nothing else right now. Nice.") }
                    ForEach(rest) { entry in
                        TaskRow(entry: entry, model: model, now: model.now) { detailId = entry.item.id }
                    }
                } label: { sectionLabel("Do now", count: model.dashboard.now.count) }
            }
            Card {
                DisclosureGroup(isExpanded: $showWaiting) {
                    if model.dashboard.waiting.isEmpty { emptyText("Nothing is parked.") }
                    ForEach(model.dashboard.waiting) { entry in
                        TaskRow(entry: entry, model: model, now: model.now) { detailId = entry.item.id }
                    }
                } label: { sectionLabel("Waiting", count: model.dashboard.waiting.count) }
            }
            let streams = Presentation.workstreams(model.dashboard)
            Card {
                DisclosureGroup(isExpanded: $showWorkstreams) {
                    if streams.isEmpty { emptyText("Tasks with subtasks or rollout steps show up here.") }
                    ForEach(streams) { o in
                        Button { detailId = o.item.id } label: { WorkstreamRow(entry: o, now: model.now) }.buttonStyle(.plain)
                    }
                } label: { sectionLabel("Workstreams", count: streams.count) }
            }
            Card {
                DisclosureGroup(isExpanded: $showNotes) {
                    ForEach(model.dashboard.recentNotes) { n in
                        VStack(alignment: .leading, spacing: 3) {
                            Text(n.note.text)
                            HStack(spacing: 5) {
                                Avatar(actor: n.note.author)
                                Text("\(n.note.author.displayName) · \(n.item.title) · \(RelativeTime.format(n.note.at, now: model.now))")
                                    .font(.caption).foregroundStyle(.secondary).lineLimit(1)
                            }
                        }
                        .frame(maxWidth: .infinity, alignment: .leading)
                        .padding(.vertical, 5)
                    }
                } label: { sectionLabel("Recent notes", count: model.dashboard.recentNotes.count) }
            }
        }
        .tint(.secondary)
    }

    private var groupTabs: some View {
        ScrollView(.horizontal, showsIndicators: false) {
            HStack(spacing: 2) {
                tab(nil, name: "All", color: nil)
                ForEach(model.groups) { g in tab(g.id, name: g.name, color: g.color ?? "#6366f1") }
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
            TextField("What's on your mind?", text: $model.quickText)
                .textFieldStyle(.plain)
                .onSubmit { model.capture() }
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
            FocusCard(entry: f, model: model, now: model.now) { detailId = f.item.id }
        } else {
            HStack(spacing: 14) {
                RoundedRectangle(cornerRadius: 14, style: .continuous).fill(Color(hex: "#10b981").opacity(0.14))
                    .frame(width: 48, height: 48)
                    .overlay(Image(systemName: "checkmark").font(.title3.weight(.bold)).foregroundStyle(Color(hex: "#10b981")))
                VStack(alignment: .leading, spacing: 3) {
                    Label("ALL CLEAR", systemImage: "sparkles").font(.caption2.weight(.bold)).foregroundStyle(Color(hex: "#10b981"))
                    Text("Nothing is due. Enjoy the calm.").font(.headline)
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
                Label(entry.needsAttention ? "REMINDER" : "DO THIS NOW", systemImage: entry.needsAttention ? "alarm" : "scope")
                    .font(.caption2.weight(.bold))
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
                Text(entry.item.title).font(.title2.weight(.bold)).multilineTextAlignment(.leading).foregroundStyle(.primary)
            }
            .buttonStyle(.plain)
            if !entry.breadcrumb.isEmpty {
                Text(entry.breadcrumb.joined(separator: " › ")).font(.caption).foregroundStyle(.secondary)
            }
            let chips = Presentation.chips(for: entry, now: now)
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
            if let last = entry.item.notes.last {
                Text(last.text).font(.subheadline.italic()).foregroundStyle(.secondary)
                    .padding(.leading, 10)
                    .overlay(alignment: .leading) { Capsule().fill(Theme.accent.opacity(0.25)).frame(width: 3) }
            }
            FlowLayout(spacing: 6) {
                if entry.state == .actionable {
                    Button { model.complete(entry.item.id) } label: {
                        Label("Done", systemImage: "checkmark").font(.subheadline.weight(.semibold)).foregroundStyle(.white)
                            .padding(.horizontal, 14).padding(.vertical, 7)
                            .background(Capsule().fill(Theme.brand))
                    }
                    .buttonStyle(.plain)
                }
                if let reminder = entry.dueReminder {
                    PillButton(title: "Dismiss", systemImage: "bell.slash") { model.dismissReminder(entry.item.id, reminderId: reminder.id) }
                }
                SnoozeMenu(model: model, id: entry.item.id, pill: true)
                PillButton(title: "Note", systemImage: "square.and.pencil") { model.toggleNote(entry.item.id) }
                PillButton(title: "Focus", systemImage: "play.fill") { model.startFocus(entry.item.id) }
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
    @ObservedObject var model: BoardModel
    let id: UUID
    var pill = false

    var body: some View {
        Menu {
            Button("15 min") { model.snooze(id, minutes: 15) }
            Button("1 hour") { model.snooze(id, minutes: 60) }
            Button("3 hours") { model.snooze(id, minutes: 180) }
            Button("Tomorrow 9:00") { model.snoozeUntilTomorrow(id) }
            Button("+24 hours") { model.snooze(id, minutes: 1440) }
        } label: {
            if pill {
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
    }
}

struct NoteField: View {
    @ObservedObject var model: BoardModel
    let id: UUID

    var body: some View {
        HStack {
            TextField("What did you do? What is next?", text: model.draftBinding(for: id))
                .textFieldStyle(.roundedBorder)
                .onSubmit(save)
            Button("Save", action: save).buttonStyle(.borderedProminent).tint(Theme.accent)
        }
    }

    private func save() {
        let text = model.noteDrafts[id] ?? ""
        guard !text.trimmingCharacters(in: .whitespaces).isEmpty else { return }
        model.addNote(id, text: text)
    }
}

struct TaskRow: View {
    let entry: AgendaEntry
    @ObservedObject var model: BoardModel
    let now: Date
    let open: () -> Void

    var body: some View {
        HStack(alignment: .top, spacing: 10) {
            PriorityDot(priority: entry.effectivePriority).padding(.top, 2)
            VStack(alignment: .leading, spacing: 4) {
                Button(action: open) {
                    Text(entry.item.title).fontWeight(.semibold).multilineTextAlignment(.leading).foregroundStyle(.primary)
                }
                .buttonStyle(.plain)
                if !entry.breadcrumb.isEmpty {
                    Text(entry.breadcrumb.joined(separator: " › ")).font(.caption).foregroundStyle(.tertiary)
                }
                let chips = Presentation.chips(for: entry, now: now)
                if !chips.isEmpty {
                    FlowLayout(spacing: 5) { ForEach(chips, id: \.self) { ChipView(chip: $0) } }
                }
                if let reminder = entry.dueReminder {
                    Label(reminder.message, systemImage: "alarm").font(.caption.weight(.semibold)).foregroundStyle(Theme.warnText)
                }
                HStack(spacing: 14) {
                    if entry.state == .actionable {
                        iconButton("checkmark", "Done") { model.complete(entry.item.id) }
                    }
                    if let reminder = entry.dueReminder {
                        iconButton("bell.slash", "Dismiss reminder") { model.dismissReminder(entry.item.id, reminderId: reminder.id) }
                    }
                    if entry.state == .waiting && !entry.needsAttention {
                        iconButton("arrow.uturn.backward", "Bring back now") { model.bringBack(entry.item.id) }
                    }
                    SnoozeMenu(model: model, id: entry.item.id)
                    iconButton("square.and.pencil", "Note") { model.toggleNote(entry.item.id) }
                    if entry.state != .waiting {
                        iconButton("play", "Focus") { model.startFocus(entry.item.id) }
                    }
                }
                .font(.callout)
                .foregroundStyle(.secondary)
                .padding(.top, 2)
                if model.openNotes.contains(entry.item.id) {
                    NoteField(model: model, id: entry.item.id)
                }
            }
            Spacer(minLength: 0)
        }
        .padding(.vertical, 8)
        .padding(.horizontal, 8)
        .background(RoundedRectangle(cornerRadius: 12, style: .continuous).fill(entry.needsAttention ? Color(hex: "#f59e0b").opacity(0.12) : .clear))
    }

    private func iconButton(_ symbol: String, _ label: String, action: @escaping () -> Void) -> some View {
        Button(action: action) { Image(systemName: symbol) }
            .buttonStyle(.plain)
            .accessibilityLabel(label)
            .help(label)
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
                Text(entry.item.title).fontWeight(.semibold)
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

struct PomodoroBar: View {
    @ObservedObject var model: BoardModel

    var body: some View {
        let p = model.board.pomodoro
        let fraction = Presentation.pomodoroFraction(p, now: model.now)
        let ringColor = p.phase == .focus ? Color(hex: "#f43f5e") : p.phase == .idle ? Theme.accent : Color(hex: "#10b981")
        HStack(spacing: 12) {
            ZStack {
                Circle().stroke(.quaternary, lineWidth: 3.5)
                Circle().trim(from: 0, to: fraction).stroke(ringColor, style: StrokeStyle(lineWidth: 3.5, lineCap: .round)).rotationEffect(.degrees(-90))
                Image(systemName: "timer").font(.caption.weight(.semibold)).foregroundStyle(.secondary)
            }
            .frame(width: 34, height: 34)
            VStack(alignment: .leading, spacing: 1) {
                Text(model.pomodoroText).font(.headline.monospacedDigit())
                Text(label(p)).font(.caption).foregroundStyle(.secondary).lineLimit(1)
            }
            Spacer()
            Group {
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
            .buttonStyle(.plain)
            .foregroundStyle(.secondary)
        }
        .padding(.horizontal, 12).padding(.vertical, 9)
        .background(RoundedRectangle(cornerRadius: 18, style: .continuous).fill(.background.secondary))
        .overlay(RoundedRectangle(cornerRadius: 18, style: .continuous).strokeBorder(.quaternary, lineWidth: 1))
    }

    private func label(_ p: PomodoroTimer) -> String {
        let phase: String
        switch p.phase {
        case .shortBreak: phase = "Break"
        case .longBreak: phase = "Long break"
        case .focus: phase = "Focus"
        case .idle: phase = "Focus timer"
        }
        if p.phase != .idle, let id = p.itemId, let item = model.board.find(id) { return "\(phase) · \(item.title)" }
        return phase
    }
}
#endif
