#if canImport(SwiftUI)
import SwiftUI
import TodoTrackerKit
#if canImport(AppKit)
import AppKit
#endif

/// Plan: a week (or a day) as a time grid, the same as the web app's Plan view. Drag a task from "Not planned" onto a
/// time to plan a block of work on it; drag a block to move it, its bottom edge to make it longer or shorter, tap it to
/// open the task. While a block is on, its task is the thing to do now. Deadlines show on their day, "now" is a line,
/// done blocks are ticked, and each day says what its blocks take in energy against what there is in a day. On an
/// iPhone it's a day at a time. Without dragging: "Plan…" on a task, a tap on an empty time, or the keyboard on a block
/// (arrows move it, ⇧ arrows change its length, Return opens it, Delete takes it off the plan).
public struct PlanView: View {
    @ObservedObject private var model: BoardModel
    private let groupId: UUID?
    private let onOpen: ((UUID) -> Void)?

    @Environment(\.horizontalSizeClass) private var sizeClass
    @State private var anchor = PlanDays.day(of: Date(), in: .current)
    @State private var settings = PlanSettings()
    @State private var loadedFor: String?
    @State private var showSettings = false
    @State private var showTasks = false
    @State private var sheet: PlanRequest?
    @State private var opened: UUID?

    /// - Parameters:
    ///   - groupId: the group whose tasks are shown (nil: the group picked in the model, or every group).
    ///   - onOpen: opens a task (nil: its details open here).
    public init(model: BoardModel, groupId: UUID? = nil, onOpen: ((UUID) -> Void)? = nil) {
        self.model = model
        self.groupId = groupId
        self.onOpen = onOpen
    }

    private var group: UUID? { groupId ?? model.selectedGroupId }
    private var compact: Bool { sizeClass == .compact }
    /// An iPhone shows a day at a time.
    private var shown: PlanSettings {
        var s = settings
        if compact { s.view = .day }
        return s
    }

    public var body: some View {
        let s = shown
        let first = PlanGrid.firstDay(anchor, view: s.view, weekStart: s.weekStart)
        let days = PlanGrid.shownDays(from: first, settings: s)
        let plan = model.planDays(from: first, days: s.view == .week ? 7 : 1, groupId: group)
        VStack(spacing: 0) {
            toolbar(days: days, plan: plan)
            Divider()
            HStack(spacing: 0) {
                if !compact {
                    PlanTaskList(plan: plan, groups: model.board.groups, open: open, planTask: { sheet = PlanRequest(taskId: $0, start: nextQuarter(), minutes: nil) })
                        .frame(width: 270)
                    Divider()
                }
                PlanGridView(model: model, plan: plan, days: days, settings: s, open: open, ask: { sheet = $0 })
            }
        }
        .onAppear(perform: loadSettings)
        .onChange(of: group) { _, _ in loadSettings() }
        .sheet(item: $sheet) { request in
            PlanSheet(model: model, request: request, choices: plan.unplanned + plan.later)
        }
        .sheet(isPresented: $showTasks) {
            NavigationStack {
                PlanTaskList(plan: plan, groups: model.board.groups, open: { id in
                    showTasks = false
                    open(id)
                }, planTask: { id in
                    showTasks = false
                    sheet = PlanRequest(taskId: id, start: nextQuarter(), minutes: nil)
                })
                .navigationTitle("Not planned")
                #if os(iOS)
                .navigationBarTitleDisplayMode(.inline)
                #endif
                .toolbar { ToolbarItem(placement: .confirmationAction) { Button("Done") { showTasks = false } } }
            }
            .presentationDetents([.medium, .large])
        }
        .sheet(item: Binding(get: { opened.map(OpenedTask.init) }, set: { opened = $0?.id }), onDismiss: { model.saveAllDrafts() }) { wrapped in
            TaskDetailView(model: model, itemId: wrapped.id)
        }
    }

    private func open(_ id: UUID) {
        if let onOpen { onOpen(id) } else { opened = id }
    }

    private func loadSettings() {
        let key = group?.uuidString ?? "all"
        guard loadedFor != key else { return }
        loadedFor = key
        settings = model.planSettings(for: group)
    }

    private func save(_ change: (inout PlanSettings) -> Void) {
        var next = settings
        change(&next)
        guard next.problem == nil else { return }
        settings = next
        model.setPlanSettings(next, for: group)
    }

    private func nextQuarter() -> Date {
        let now = Date()
        let minutes = PlanGrid.minutes(of: now, timeZone: .current)
        let day = PlanDays.day(of: now, in: .current)
        let next = (minutes / 15 + 1) * 15
        return next >= 24 * 60 ? PlanGrid.time(0, on: day.adding(days: 1), timeZone: .current) : PlanGrid.time(next, on: day, timeZone: .current)
    }

    // MARK: Toolbar

    private func toolbar(days: [LocalDay], plan: PlanDays) -> some View {
        let week = shown.view == .week
        return ViewThatFits(in: .horizontal) {
            HStack(spacing: 10) {
                navigation(week: week)
                title(days).font(.title3.weight(.semibold))
                Spacer(minLength: 8)
                pickers
            }
            VStack(alignment: .leading, spacing: 8) {
                HStack(spacing: 10) {
                    navigation(week: week)
                    title(days).font(.headline)
                    Spacer(minLength: 4)
                    if compact { tasksButton(plan) }
                }
                pickers
            }
        }
        .padding(.horizontal, 14)
        .padding(.vertical, 10)
    }

    private func navigation(week: Bool) -> some View {
        HStack(spacing: 4) {
            Button { anchor = anchor.adding(days: week ? -7 : -1) } label: { Image(systemName: "chevron.left") }
                .accessibilityLabel(week ? "Previous week" : "Previous day")
                .keyboardShortcut(.leftArrow, modifiers: [.command, .option])
            Button("Today") { anchor = PlanDays.day(of: Date(), in: .current) }
                .fixedSize()
                .keyboardShortcut("t", modifiers: [.command, .option])
            Button { anchor = anchor.adding(days: week ? 7 : 1) } label: { Image(systemName: "chevron.right") }
                .accessibilityLabel(week ? "Next week" : "Next day")
                .keyboardShortcut(.rightArrow, modifiers: [.command, .option])
        }
        .buttonStyle(.bordered)
        .controlSize(.small)
    }

    private func title(_ days: [LocalDay]) -> some View {
        let dates = days.map { PlanGrid.time(12 * 60, on: $0, timeZone: .current) }
        let text: String
        if dates.count == 1 {
            text = compact ? dates[0].formatted(.dateTime.weekday(.abbreviated).month(.abbreviated).day())
                : dates[0].formatted(.dateTime.weekday(.wide).month(.wide).day())
        } else {
            text = "\(dates[0].formatted(.dateTime.month(.abbreviated).day())) – \(dates[dates.count - 1].formatted(.dateTime.month(.abbreviated).day()))"
        }
        return Text(text).lineLimit(1).minimumScaleFactor(0.8)
    }

    private var pickers: some View {
        HStack(spacing: 8) {
            if !compact {
                Picker("Show", selection: Binding(get: { settings.view }, set: { view in save { $0.view = view } })) {
                    Text("Day").tag(PlanSettings.View.day)
                    Text("Week").tag(PlanSettings.View.week)
                }
                .pickerStyle(.segmented)
                .fixedSize()
            }
            Picker("Hours", selection: Binding(get: { settings.hours }, set: { hours in save { $0.hours = hours } })) {
                Text("Work hours").tag(PlanSettings.Hours.work)
                Text("Full day").tag(PlanSettings.Hours.full)
            }
            .pickerStyle(.segmented)
            .fixedSize()
            Button { showSettings = true } label: { Image(systemName: "gearshape") }
                .buttonStyle(.bordered)
                .controlSize(.small)
                .accessibilityLabel("Work hours and days")
                .help("Work hours and days (this group)")
                .popover(isPresented: $showSettings) {
                    PlanSettingsForm(settings: settings) { next in
                        save { $0 = next }
                        showSettings = false
                    }
                }
        }
        .labelsHidden()
    }

    private func tasksButton(_ plan: PlanDays) -> some View {
        Button { showTasks = true } label: {
            Label("\(plan.unplanned.count)", systemImage: "tray")
                .labelStyle(.titleAndIcon)
                .fixedSize()
        }
        .buttonStyle(.bordered)
        .controlSize(.small)
        .accessibilityLabel("Not planned: \(plan.unplanned.count) tasks")
    }
}

private struct OpenedTask: Identifiable { let id: UUID }

/// Planning without dragging: a task (or one to pick), when, and how long.
struct PlanRequest: Identifiable {
    let id = UUID()
    var taskId: UUID?
    var start: Date
    var minutes: Int?
}

// MARK: - Not planned

/// The tasks to do that aren't planned yet (drag one onto a time), and Later: the ones put off, with when they're back.
struct PlanTaskList: View {
    let plan: PlanDays
    let groups: [TaskGroup]
    let open: (UUID) -> Void
    let planTask: (UUID) -> Void

    var body: some View {
        List {
            Section {
                if plan.unplanned.isEmpty {
                    Text("Everything to do now is planned.").font(.callout).foregroundStyle(.secondary)
                }
                ForEach(plan.unplanned) { row($0, back: nil) }
            } header: {
                Text("Not planned")
            } footer: {
                if !plan.unplanned.isEmpty { Text("Drag a task onto a time to plan it.") }
            }
            if !plan.later.isEmpty {
                Section("Later") {
                    ForEach(plan.later) { entry in
                        row(entry, back: entry.wakeAt.map { "back \(PlanWords.when($0, now: Date()))" } ?? entry.waitingFor.map { "after “\($0.title)”" })
                    }
                }
            }
        }
        .listStyle(.sidebar)
    }

    private func row(_ entry: AgendaEntry, back: String?) -> some View {
        let item = entry.item
        return HStack(spacing: 8) {
            Capsule().fill(PlanColors.accent(item, groups)).frame(width: 4)
            VStack(alignment: .leading, spacing: 2) {
                HStack(spacing: 4) {
                    if let icon = item.icon { Text(icon) }
                    Text(item.title).lineLimit(2)
                    if item.repeatRule != nil { Image(systemName: "repeat").font(.caption2).foregroundStyle(.secondary) }
                    if let energy = item.energy { Text(String(repeating: "⚡", count: energy)).font(.caption2) }
                }
                .font(.callout)
                if !entry.breadcrumb.isEmpty {
                    Text(entry.breadcrumb.joined(separator: " › ")).font(.caption).foregroundStyle(.secondary).lineLimit(1)
                }
                if let back { Text(back).font(.caption).foregroundStyle(.secondary) }
            }
            Spacer(minLength: 4)
            Button { planTask(item.id) } label: { Image(systemName: "calendar.badge.plus") }
                .buttonStyle(.borderless)
                .accessibilityLabel("Plan “\(item.title)”…")
                .help("Plan it at a time")
        }
        .contentShape(Rectangle())
        .onTapGesture { open(item.id) }
        .draggable(PlanDrag.payload(item.id)) {
            Label(item.title, systemImage: "calendar").padding(8).background(.regularMaterial, in: RoundedRectangle(cornerRadius: 8))
        }
        .contextMenu {
            Button("Plan…") { planTask(item.id) }
            Button("Open") { open(item.id) }
        }
        .accessibilityElement(children: .combine)
        .accessibilityAction(named: "Plan…") { planTask(item.id) }
        .accessibilityHint("Opens the task. Drag it onto a time to plan it.")
    }
}

/// What's dragged onto the plan: a task's id.
enum PlanDrag {
    static let prefix = "todotracker-task:"
    static func payload(_ id: UUID) -> String { prefix + id.uuidString }
    static func id(_ payload: String) -> UUID? {
        guard payload.hasPrefix(prefix) else { return nil }
        return UUID(uuidString: String(payload.dropFirst(prefix.count)))
    }
}

enum PlanColors {
    static func accent(_ item: WorkItem, _ groups: [TaskGroup]) -> Color {
        if let color = item.color ?? item.root.color { return Color(hex: color) }
        if let group = groups.first(where: { $0.id == item.groupId }), let color = group.color { return Color(hex: color) }
        return Color(hex: "#6366f1")
    }
}

// MARK: - The grid

/// Days side by side, hours down; blocks where they're planned.
struct PlanGridView: View {
    @ObservedObject var model: BoardModel
    let plan: PlanDays
    let days: [LocalDay]
    let settings: PlanSettings
    let open: (UUID) -> Void
    let ask: (PlanRequest) -> Void

    @Environment(\.horizontalSizeClass) private var sizeClass
    @State private var scrolledFor: String?
    private let gutter: CGFloat = 48
    private var perMinute: CGFloat { sizeClass == .compact ? 1.1 : 1.0 }

    var body: some View {
        let zone = TimeZone.current
        let segments = plan.blocks.flatMap { item in
            PlanGrid.segments(start: item.plannedAt!, minutes: item.plannedLength, timeZone: zone).map { (item: item, segment: $0) }
        }
        let range = PlanGrid.hours(settings: settings, segments: segments.map(\.segment), days: days)
        GeometryReader { geometry in
            let column = max(60, (geometry.size.width - gutter) / CGFloat(max(days.count, 1)))
            VStack(spacing: 0) {
                HStack(alignment: .top, spacing: 0) {
                    Color.clear.frame(width: gutter, height: 1)
                    ForEach(days, id: \.self) { day in
                        PlanDayHead(model: model, day: day, plan: plan, open: open).frame(width: column)
                    }
                }
                Divider()
                ScrollViewReader { proxy in
                    ScrollView(.vertical) {
                        HStack(alignment: .top, spacing: 0) {
                            hourGutter(range)
                            ForEach(Array(days.enumerated()), id: \.element) { index, day in
                                PlanDayColumn(
                                    model: model, day: day, index: index, days: days, range: range, perMinute: perMinute, column: column,
                                    parts: segments.filter { $0.segment.day == day }, open: open, ask: ask)
                                .frame(width: column)
                            }
                        }
                        .padding(.bottom, 12)
                    }
                    .onAppear { scrollToWork(proxy, range) }
                    .onChange(of: settings) { _, _ in scrolledFor = nil; scrollToWork(proxy, range) }
                }
            }
        }
    }

    private func hourGutter(_ range: (from: Int, to: Int)) -> some View {
        ZStack(alignment: .topTrailing) {
            Color.clear
            ForEach(Array(stride(from: (range.from + 59) / 60 * 60, to: range.to, by: 60)), id: \.self) { m in
                Text(String(format: "%02d:00", m / 60))
                    .font(.caption2.monospacedDigit())
                    .foregroundStyle(.secondary)
                    .padding(.trailing, 6)
                    .offset(y: CGFloat(m - range.from) * perMinute - (m == range.from ? 0 : 7))
                    .id("hour-\(m / 60)")
            }
        }
        .frame(width: gutter, height: CGFloat(range.to - range.from) * perMinute)
        .accessibilityHidden(true)
    }

    /// The whole day starts at work hours rather than midnight (once per settings).
    private func scrollToWork(_ proxy: ScrollViewProxy, _ range: (from: Int, to: Int)) {
        let key = "\(settings.hours)-\(settings.workStart)"
        guard scrolledFor != key else { return }
        scrolledFor = key
        guard settings.hours == .full else { return }
        let hour = max(0, settings.workStart / 60 - 1)
        DispatchQueue.main.async { proxy.scrollTo("hour-\(max(hour, 1))", anchor: .top) }
    }
}

/// A day's name and date, what its blocks take in energy, its deadlines and what comes back that day.
struct PlanDayHead: View {
    @ObservedObject var model: BoardModel
    let day: LocalDay
    let plan: PlanDays
    let open: (UUID) -> Void

    var body: some View {
        let zone = TimeZone.current
        let today = day == PlanDays.day(of: Date(), in: zone)
        let noon = PlanGrid.time(12 * 60, on: day, timeZone: zone)
        let deadlines = plan.deadlines.filter { PlanDays.day(of: $0.deadline!, in: zone) == day }
        let back = plan.later.filter { $0.wakeAt.map { PlanDays.day(of: $0, in: zone) == day } ?? false }
        VStack(spacing: 4) {
            HStack(spacing: 4) {
                Text(noon.formatted(.dateTime.weekday(.abbreviated))).font(.caption.weight(.medium)).foregroundStyle(today ? Theme.accent : .secondary)
                Text(String(day.day))
                    .font(.callout.weight(.semibold).monospacedDigit())
                    .foregroundStyle(today ? .white : .primary)
                    .frame(minWidth: 26, minHeight: 26)
                    .background(Circle().fill(today ? Theme.accent : .clear))
            }
            energy
            ForEach(deadlines.prefix(2), id: \.id) { item in
                chip(item.title, systemImage: "flag.fill", tint: item.isDone ? .secondary : Theme.warnText) { open(item.id) }
                    .help("Due \(item.deadline!.formatted(date: .omitted, time: .shortened))")
            }
            ForEach(back.prefix(2)) { entry in
                chip(entry.item.title, systemImage: "clock", tint: .secondary) { open(entry.item.id) }
                    .help("Back \(PlanWords.when(entry.wakeAt!, now: Date()))")
            }
            if deadlines.count + back.count > 4 {
                Text("+\(deadlines.count + back.count - 4) more").font(.caption2).foregroundStyle(.secondary)
            }
        }
        .padding(.vertical, 6)
        .padding(.horizontal, 3)
        .frame(maxWidth: .infinity, alignment: .top)
        .background(today ? Theme.accent.opacity(0.07) : .clear)
        .accessibilityElement(children: .contain)
        .accessibilityLabel(noon.formatted(.dateTime.weekday(.wide).month(.wide).day()) + (today ? ", today" : ""))
    }

    @ViewBuilder private var energy: some View {
        let used = plan.energy[day] ?? 0
        let per = model.board.dailyEnergy
        if used > 0 {
            let over = used > Double(per)
            let n = (used * 10).rounded() / 10
            let shown = n == n.rounded() ? String(Int(n)) : String(format: "%.1f", n)
            HStack(spacing: 4) {
                Text("⚡ \(shown)/\(per)").font(.caption2.monospacedDigit().weight(.medium))
                GeometryReader { g in
                    ZStack(alignment: .leading) {
                        Capsule().fill(Color.secondary.opacity(0.2))
                        Capsule().fill(over ? Color(hex: "#ef4444") : Color(hex: "#ca8a04")).frame(width: g.size.width * min(1, used / Double(per)))
                    }
                }
                .frame(height: 4)
            }
            .foregroundStyle(over ? Color(hex: "#ef4444") : .secondary)
            .help(over ? "\(shown) of \(per) ⚡: more than you have in a day" : "\(shown) of \(per) ⚡ planned (each block: hours × its ⚡ an hour)")
            .accessibilityElement(children: .ignore)
            .accessibilityLabel("Energy: \(shown) of \(per)\(over ? ", more than you have in a day" : "")")
        }
    }

    private func chip(_ title: String, systemImage: String, tint: Color, action: @escaping () -> Void) -> some View {
        Button(action: action) {
            Label(title, systemImage: systemImage)
                .font(.caption2)
                .lineLimit(1)
                .padding(.horizontal, 6)
                .padding(.vertical, 2)
                .frame(maxWidth: .infinity, alignment: .leading)
                .background(Capsule().fill(tint.opacity(0.14)))
                .foregroundStyle(tint)
        }
        .buttonStyle(.plain)
    }
}

/// One day's column: hour lines, the blocks on it, "now", and where a task is dropped.
struct PlanDayColumn: View {
    @ObservedObject var model: BoardModel
    let day: LocalDay
    let index: Int
    let days: [LocalDay]
    let range: (from: Int, to: Int)
    let perMinute: CGFloat
    let column: CGFloat
    let parts: [(item: WorkItem, segment: PlanSegment)]
    let open: (UUID) -> Void
    let ask: (PlanRequest) -> Void

    @State private var targeted = false

    var body: some View {
        let zone = TimeZone.current
        let today = day == PlanDays.day(of: model.now, in: zone)
        let height = CGFloat(range.to - range.from) * perMinute
        let lanes = Self.lanes(parts.map(\.segment))
        ZStack(alignment: .topLeading) {
            background(today: today, height: height)
            ForEach(Array(parts.enumerated()), id: \.offset) { i, part in
                let lane = lanes.lane[i]
                let width = (column - 6) / CGFloat(lanes.count)
                PlanBlock(model: model, item: part.item, segment: part.segment, range: range, perMinute: perMinute,
                          column: column, dayIndex: index, days: days, open: open, ask: ask)
                    .frame(width: max(20, width - 2))
                    .offset(x: 3 + CGFloat(lane) * width)
            }
            if today {
                let minutes = PlanGrid.minutes(of: model.now, timeZone: zone)
                if minutes >= range.from && minutes <= range.to {
                    nowLine.offset(y: CGFloat(minutes - range.from) * perMinute - 4)
                }
            }
        }
        .frame(width: column, height: height, alignment: .topLeading)
        .overlay(alignment: .leading) { Rectangle().fill(Color.secondary.opacity(0.2)).frame(width: 0.5) }
        .dropDestination(for: String.self) { items, location in
            guard let id = items.compactMap(PlanDrag.id).first, model.board.find(id) != nil else { return false }
            let minutes = PlanGrid.snapped(Double(range.from) + location.y / perMinute)
            return model.plan(id, at: PlanGrid.time(minutes, on: day, timeZone: .current))
        } isTargeted: { targeted = $0 }
        .accessibilityElement(children: .contain)
    }

    private func background(today: Bool, height: CGFloat) -> some View {
        ZStack(alignment: .topLeading) {
            Rectangle().fill(targeted ? Theme.accent.opacity(0.12) : today ? Theme.accent.opacity(0.05) : Color.clear)
            Path { path in
                var m = (range.from + 59) / 60 * 60
                while m < range.to {
                    let y = CGFloat(m - range.from) * perMinute
                    path.move(to: CGPoint(x: 0, y: y))
                    path.addLine(to: CGPoint(x: column, y: y))
                    m += 60
                }
            }
            .stroke(Color.secondary.opacity(0.18), lineWidth: 0.5)
        }
        .frame(width: column, height: height)
        .contentShape(Rectangle())
        // A tap on an empty time: plan a task there.
        .onTapGesture { location in
            let minutes = PlanGrid.snapped(Double(range.from) + location.y / perMinute - 7.5)
            ask(PlanRequest(taskId: nil, start: PlanGrid.time(minutes, on: day, timeZone: .current), minutes: nil))
        }
        .accessibilityLabel("Plan a task on this day")
        .accessibilityAddTraits(.isButton)
    }

    private var nowLine: some View {
        HStack(spacing: 0) {
            Circle().fill(Color(hex: "#ef4444")).frame(width: 8, height: 8)
            Rectangle().fill(Color(hex: "#ef4444")).frame(height: 1.5)
        }
        .frame(width: column)
        .allowsHitTesting(false)
        .accessibilityHidden(true)
    }

    /// Side by side where blocks overlap: each block's lane, and how many lanes the day needs.
    static func lanes(_ segments: [PlanSegment]) -> (lane: [Int], count: Int) {
        var ends: [Int] = []
        var lane = [Int](repeating: 0, count: segments.count)
        for i in segments.indices.sorted(by: { segments[$0].from < segments[$1].from }) {
            if let free = ends.firstIndex(where: { $0 <= segments[i].from }) {
                lane[i] = free
                ends[free] = segments[i].to
            } else {
                lane[i] = ends.count
                ends.append(segments[i].to)
            }
        }
        return (lane, max(1, ends.count))
    }
}

/// A block of planned work: drag it to move it (on a touch screen, hold it first), its bottom edge to change how long.
struct PlanBlock: View {
    @ObservedObject var model: BoardModel
    let item: WorkItem
    let segment: PlanSegment
    let range: (from: Int, to: Int)
    let perMinute: CGFloat
    let column: CGFloat
    let dayIndex: Int
    let days: [LocalDay]
    let open: (UUID) -> Void
    let ask: (PlanRequest) -> Void

    @State private var drag: CGSize?
    @State private var stretch: CGFloat?
    @FocusState private var focused: Bool

    private var start: Date { item.plannedAt! }
    private var movable: Bool { !item.isDone }

    var body: some View {
        let zone = TimeZone.current
        let accent = PlanColors.accent(item, model.board.groups)
        let top = CGFloat(segment.from - range.from) * perMinute
        let baseHeight = CGFloat(segment.to - segment.from) * perMinute
        let minutes = stretch.map { PlanGrid.resized(item.plannedLength, by: Double($0 / perMinute)) } ?? item.plannedLength
        let height = max(18, baseHeight + CGFloat(minutes - item.plannedLength) * perMinute)
        let end = start.addingTimeInterval(Double(minutes) * 60)
        let time = "\(Self.hhmm(PlanGrid.minutes(of: start, timeZone: zone)))–\(Self.hhmm(PlanGrid.minutes(of: end, timeZone: zone)))"

        VStack(alignment: .leading, spacing: 1) {
            if height >= 34 { Text(time).font(.caption2.monospacedDigit()).foregroundStyle(.secondary) }
            (Text(item.isDone ? "\(Image(systemName: "checkmark")) " : "")
                + Text(item.icon.map { "\($0) " } ?? "")
                + Text(item.title).strikethrough(item.isDone)
                + Text(item.repeatRule != nil ? " \(Image(systemName: "repeat"))" : "")
                + Text(item.energy.map { " " + String(repeating: "⚡", count: $0) } ?? "").font(.caption2))
                .font(.caption.weight(.medium))
                .lineLimit(max(1, Int((height - (height >= 34 ? 20 : 6)) / 15)))
        }
        .padding(.horizontal, 6)
        .padding(.vertical, 3)
        .frame(maxWidth: .infinity, maxHeight: .infinity, alignment: .topLeading)
        .frame(height: height)
        .background(RoundedRectangle(cornerRadius: 6).fill(accent.opacity(item.isDone ? 0.12 : 0.28)))
        .overlay(alignment: .leading) { UnevenRoundedRectangle(topLeadingRadius: 6, bottomLeadingRadius: 6).fill(accent).frame(width: 3) }
        .overlay { RoundedRectangle(cornerRadius: 6).stroke(focused ? Theme.accent : accent.opacity(0.35), lineWidth: focused ? 2 : 0.5) }
        .overlay(alignment: .bottom) { if movable && segment.isLast { resizeHandle } }
        .opacity(item.isDone ? 0.7 : 1)
        .contentShape(RoundedRectangle(cornerRadius: 6))
        .offset(x: drag?.width ?? 0, y: top + (drag?.height ?? 0))
        .zIndex(drag != nil || stretch != nil ? 10 : 0)
        .shadow(color: .black.opacity(drag != nil ? 0.2 : 0), radius: 6, y: 3)
        .onTapGesture { open(item.id) }
        .gesture(moveGesture, including: movable ? .all : .subviews)
        .focusable(true)
        .focused($focused)
        .onKeyPress(phases: .down, action: key)
        .contextMenu { menu }
        .accessibilityElement(children: .ignore)
        .accessibilityLabel("\(item.title), \(start.formatted(date: .omitted, time: .shortened)) to \(end.formatted(date: .omitted, time: .shortened))\(item.isDone ? ", done" : "")")
        .accessibilityAddTraits(.isButton)
        .accessibilityAction { open(item.id) }
        .accessibilityActions {
            if movable {
                Button("15 minutes later") { move(minutes: 15) }
                Button("15 minutes earlier") { move(minutes: -15) }
                Button("Next day") { move(days: 1) }
                Button("Day before") { move(days: -1) }
                Button("Longer") { resize(by: 15) }
                Button("Shorter") { resize(by: -15) }
                Button("Take off the plan") { model.unplan(item.id) }
            }
        }
        .help(movable ? "Drag to move it, its bottom edge to make it longer or shorter" : "Done")
    }

    @ViewBuilder private var menu: some View {
        Button("Open") { open(item.id) }
        if movable {
            Button("Change…") { ask(PlanRequest(taskId: item.id, start: start, minutes: item.plannedLength)) }
            Divider()
            Button("15 minutes earlier") { move(minutes: -15) }
            Button("15 minutes later") { move(minutes: 15) }
            Button("Longer (+15 min)") { resize(by: 15) }
            Button("Shorter (−15 min)") { resize(by: -15) }
            Divider()
            Button("Take off the plan", role: .destructive) { model.unplan(item.id) }
        }
    }

    private var resizeHandle: some View {
        Capsule()
            .fill(Color.secondary.opacity(0.6))
            .frame(width: 28, height: 4)
            .padding(.vertical, 5)
            .frame(maxWidth: .infinity)
            .contentShape(Rectangle())
            .highPriorityGesture(
                DragGesture(minimumDistance: 0)
                    .onChanged { stretch = $0.translation.height }
                    .onEnded { value in
                        stretch = nil
                        let minutes = PlanGrid.resized(item.plannedLength, by: Double(value.translation.height / perMinute))
                        if minutes != item.plannedLength { model.plan(item.id, at: start, minutes: minutes) }
                    })
            #if os(macOS)
            .onHover { inside in if inside { NSCursor.resizeUpDown.push() } else { NSCursor.pop() } }
            #endif
            .accessibilityHidden(true)
    }

    private var moveGesture: AnyGesture<Void> {
        let drag = DragGesture(minimumDistance: 4)
            .onChanged { self.drag = $0.translation }
            .onEnded { value in
                self.drag = nil
                finishMove(value.translation)
            }
        #if os(iOS)
        // On a touch screen a block is held first, so a plain swipe still scrolls the plan.
        return AnyGesture(LongPressGesture(minimumDuration: 0.25).sequenced(before: drag).map { _ in () })
        #else
        return AnyGesture(drag.map { _ in () })
        #endif
    }

    /// Drops the block where it was dragged: on the day it's over, at the time it was moved to (snapped).
    private func finishMove(_ translation: CGSize) {
        let target = min(max(dayIndex + Int((translation.width / column).rounded()), 0), days.count - 1)
        let dayShift = days[target].epochDay - days[dayIndex].epochDay
        let moved = PlanGrid.moved(start, byMinutes: Double(translation.height / perMinute), days: dayShift, timeZone: .current)
        if moved != start { model.plan(item.id, at: moved) }
    }

    private func move(minutes: Double = 0, days shift: Int = 0) {
        guard movable else { return }
        model.plan(item.id, at: PlanGrid.moved(start, byMinutes: minutes, days: shift, timeZone: .current))
    }

    private func resize(by minutes: Double) {
        guard movable else { return }
        let next = PlanGrid.resized(item.plannedLength, by: minutes)
        if next != item.plannedLength { model.plan(item.id, at: start, minutes: next) }
    }

    private func key(_ press: KeyPress) -> KeyPress.Result {
        let shift = press.modifiers.contains(.shift)
        switch press.key {
        case .return, .space: open(item.id)
        case .upArrow: shift ? resize(by: -15) : move(minutes: -15)
        case .downArrow: shift ? resize(by: 15) : move(minutes: 15)
        case .leftArrow: move(days: -1)
        case .rightArrow: move(days: 1)
        case .delete, .deleteForward: if movable { model.unplan(item.id) }
        default: return .ignored
        }
        return .handled
    }

    static func hhmm(_ minutes: Int) -> String { String(format: "%02d:%02d", minutes / 60, minutes % 60) }
}

// MARK: - Sheets

/// Plan a task at a time for a while, without dragging (or change a block, or take it off the plan).
struct PlanSheet: View {
    @ObservedObject var model: BoardModel
    let request: PlanRequest
    let choices: [AgendaEntry]
    @Environment(\.dismiss) private var dismiss
    @State private var taskId: UUID?
    @State private var start = Date()
    @State private var minutes = TaskBoard.defaultPlanMinutes

    private static let lengths = [15, 30, 45, 60, 90, 120, 180, 240, 360, 480, 720]

    var body: some View {
        NavigationStack {
            Form {
                if request.taskId == nil {
                    if choices.isEmpty {
                        Text("Everything to do now is planned.").foregroundStyle(.secondary)
                    } else {
                        Picker("Task", selection: $taskId) {
                            ForEach(choices) { Text($0.item.title).tag(Optional($0.item.id)) }
                        }
                    }
                } else if let id = taskId, let item = model.board.find(id) {
                    LabeledContent("Task", value: item.title)
                }
                DatePicker("Starts", selection: $start)
                Picker("How long", selection: $minutes) {
                    ForEach(Array(Set(Self.lengths + [minutes])).sorted(), id: \.self) { Text(Self.length($0)).tag($0) }
                }
                if let id = taskId, model.board.find(id)?.plannedAt != nil {
                    Button("Take off the plan", role: .destructive) {
                        model.unplan(id)
                        dismiss()
                    }
                }
            }
            .formStyle(.grouped)
            .navigationTitle(request.taskId == nil ? "Plan a task" : "Plan")
            #if os(iOS)
            .navigationBarTitleDisplayMode(.inline)
            #endif
            .toolbar {
                ToolbarItem(placement: .cancellationAction) { Button("Cancel") { dismiss() } }
                ToolbarItem(placement: .confirmationAction) {
                    Button("Plan") {
                        if let id = taskId, model.plan(id, at: start, minutes: minutes) { dismiss() }
                    }
                    .disabled(taskId == nil)
                }
            }
        }
        .frame(minWidth: 320, minHeight: 280)
        .onAppear {
            taskId = request.taskId ?? choices.first?.item.id
            start = request.start
            minutes = request.minutes ?? taskId.flatMap { model.board.find($0)?.plannedLength } ?? TaskBoard.defaultPlanMinutes
        }
        .presentationDetents([.medium])
    }

    static func length(_ minutes: Int) -> String {
        minutes < 60 ? "\(minutes) min" : minutes % 60 == 0 ? "\(minutes / 60) h" : "\(minutes / 60) h \(minutes % 60) min"
    }
}

/// Work hours and days, and the day a week starts on (each group keeps its own).
struct PlanSettingsForm: View {
    @State var settings: PlanSettings
    let save: (PlanSettings) -> Void
    private static let days = ["Mon", "Tue", "Wed", "Thu", "Fri", "Sat", "Sun"]

    var body: some View {
        Form {
            Picker("Work starts", selection: $settings.workStart) {
                ForEach(Array(stride(from: 0, to: 24 * 60, by: 30)), id: \.self) { Text(PlanBlock.hhmm($0)).tag($0) }
            }
            Picker("Work ends", selection: $settings.workEnd) {
                ForEach(Array(stride(from: 60, through: 24 * 60, by: 30)), id: \.self) { Text(PlanBlock.hhmm($0)).tag($0) }
            }
            Picker("Week starts on", selection: $settings.weekStart) {
                Text("Monday").tag(1)
                Text("Sunday").tag(7)
            }
            Section("Work days") {
                HStack(spacing: 4) {
                    ForEach(1...7, id: \.self) { d in
                        let on = settings.workDays.contains(d)
                        Button(Self.days[d - 1]) {
                            if on { settings.workDays.removeAll { $0 == d } } else { settings.workDays = (settings.workDays + [d]).sorted() }
                        }
                        .buttonStyle(.bordered)
                        .tint(on ? Theme.accent : .secondary)
                        .controlSize(.small)
                        .accessibilityAddTraits(on ? .isSelected : [])
                    }
                }
            }
            if let problem = settings.problem { Text(problem).font(.callout).foregroundStyle(Color(hex: "#ef4444")) }
            Button("Save") { save(settings) }
                .buttonStyle(.borderedProminent)
                .disabled(settings.problem != nil)
        }
        .formStyle(.grouped)
        .frame(minWidth: 340, minHeight: 360)
    }
}
#endif
