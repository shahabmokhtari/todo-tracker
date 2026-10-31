#if canImport(SwiftUI) && canImport(Charts)
import Charts
import SwiftUI
import TodoTrackerKit

/// Reports: where the time went and what got done, for a range (7 days, 4 weeks, 3 months, a year, or dates you pick)
/// and the group shown: headline numbers, time per day (focus and other time), per group, top tasks, when you work
/// (weekday × hour), a calendar of active days, and the timeline of the work on each task (tap a row to open it). The
/// same as the web app's Reports; the numbers are worked out here from the board, the same as the server's
/// (TimeReport, tests/fixtures/reports.json).
public struct ReportsView: View {
    @ObservedObject private var model: BoardModel
    private let groupId: UUID?
    private let onOpen: ((UUID) -> Void)?

    @AppStorage("reports.range") private var range = ReportRange.fourWeeks.rawValue
    @State private var customFrom = Calendar.current.date(byAdding: .day, value: -13, to: Date()) ?? Date()
    @State private var customTo = Date()
    @State private var report: TimeReport?
    @State private var problem: String?
    @State private var opened: UUID?
    @Environment(\.horizontalSizeClass) private var sizeClass

    /// - Parameters:
    ///   - groupId: the group whose time is shown (nil: the group picked in the model, or every group).
    ///   - onOpen: opens a task (nil: its details open here).
    public init(model: BoardModel, groupId: UUID? = nil, onOpen: ((UUID) -> Void)? = nil) {
        self.model = model
        self.groupId = groupId
        self.onOpen = onOpen
    }

    private var group: UUID? { groupId ?? model.selectedGroupId }
    private var choice: ReportRange { ReportRange(rawValue: range) ?? .fourWeeks }

    private var dates: (from: LocalDay, to: LocalDay) {
        let zone = TimeZone.current
        if choice == .custom {
            let a = PlanDays.day(of: customFrom, in: zone), b = PlanDays.day(of: customTo, in: zone)
            return (min(a, b), max(a, b))
        }
        let today = PlanDays.day(of: Date(), in: zone)
        return (today.adding(days: -(choice.days - 1)), today)
    }

    /// When to work the report out again: another range or group, a change to the board, or every half minute (a timer
    /// runs).
    private var key: String {
        let d = dates
        return "\(d.from)-\(d.to)-\(group?.uuidString ?? "all")-\(model.board.activity.count)-\(model.board.allItems.count)-\(Int(model.now.timeIntervalSince1970 / 30))"
    }

    public var body: some View {
        ScrollView {
            VStack(alignment: .leading, spacing: 16) {
                rangeBar
                if let problem {
                    Text(problem).foregroundStyle(.secondary)
                } else if let report {
                    ReportBody(report: report, groups: model.board.groups, compact: sizeClass == .compact, open: open)
                }
            }
            .padding(16)
        }
        .task(id: key) { build() }
        .sheet(item: Binding(get: { opened.map(ReportOpened.init) }, set: { opened = $0?.id }), onDismiss: { model.saveAllDrafts() }) { wrapped in
            TaskDetailView(model: model, itemId: wrapped.id)
        }
    }

    private func build() {
        let d = dates
        do {
            report = try model.report(from: d.from, to: d.to, groupId: group)
            problem = nil
        } catch {
            problem = (error as? LocalizedError)?.errorDescription ?? error.localizedDescription
        }
    }

    private func open(_ id: UUID) {
        guard model.board.find(id) != nil else { return }
        if let onOpen { onOpen(id) } else { opened = id }
    }

    private var rangeBar: some View {
        let d = dates
        return ViewThatFits(in: .horizontal) {
            HStack(spacing: 12) {
                rangePicker
                if choice == .custom { customPickers }
                Spacer()
                Text(Self.span(d.from, d.to)).font(.callout).foregroundStyle(.secondary)
            }
            VStack(alignment: .leading, spacing: 8) {
                rangePicker
                if choice == .custom { customPickers }
                Text(Self.span(d.from, d.to)).font(.callout).foregroundStyle(.secondary)
            }
        }
    }

    private var rangePicker: some View {
        Picker("Range", selection: $range) {
            ForEach(ReportRange.allCases) { Text(sizeClass == .compact ? $0.short : $0.label).tag($0.rawValue) }
        }
        .pickerStyle(.segmented)
        .labelsHidden()
        .fixedSize()
    }

    private var customPickers: some View {
        HStack(spacing: 6) {
            DatePicker("From", selection: $customFrom, displayedComponents: .date).labelsHidden()
            Text("to").foregroundStyle(.secondary)
            DatePicker("To", selection: $customTo, in: ...Date.distantFuture, displayedComponents: .date).labelsHidden()
        }
    }

    static func span(_ from: LocalDay, _ to: LocalDay) -> String {
        let f = ReportDates.noon(from).formatted(date: .abbreviated, time: .omitted)
        let t = ReportDates.noon(to).formatted(date: .abbreviated, time: .omitted)
        return from == to ? f : "\(f) – \(t)"
    }
}

private struct ReportOpened: Identifiable { let id: UUID }

/// The ranges offered (the same as the web app), and dates you pick.
enum ReportRange: String, CaseIterable, Identifiable {
    case week = "7", fourWeeks = "28", threeMonths = "90", year = "365", custom

    var id: String { rawValue }
    var days: Int { Int(rawValue) ?? 28 }
    var label: String {
        switch self {
        case .week: return "7 days"
        case .fourWeeks: return "4 weeks"
        case .threeMonths: return "3 months"
        case .year: return "Year"
        case .custom: return "Dates…"
        }
    }
    var short: String {
        switch self {
        case .week: return "7d"
        case .fourWeeks: return "4w"
        case .threeMonths: return "3m"
        case .year: return "1y"
        case .custom: return "Dates"
        }
    }
}

enum ReportDates {
    static func noon(_ day: LocalDay) -> Date { PlanGrid.time(12 * 60, on: day, timeZone: .current) }
}

/// The colors the web app gives groups without one.
enum ReportColors {
    static let fallback = ["#6366f1", "#22c55e", "#f59e0b", "#ec4899", "#0ea5e9", "#8b5cf6", "#14b8a6"]
    static let focus = Color(hex: "#6366f1")
    static let other = Color(hex: "#22d3ee")

    static func group(_ id: UUID, _ groups: [TaskGroup]) -> Color {
        let index = groups.firstIndex { $0.id == id } ?? 0
        return Color(hex: groups.first { $0.id == id }?.color ?? fallback[index % fallback.count])
    }
}

// MARK: - The report

struct ReportBody: View {
    let report: TimeReport
    let groups: [TaskGroup]
    let compact: Bool
    let open: (UUID) -> Void

    var body: some View {
        let r = report
        let focus = r.days.reduce(Int64(0)) { $0 + $1.focusSeconds }
        let average = r.activeDays > 0 ? Double(r.trackedSeconds) / Double(r.activeDays) : 0
        VStack(alignment: .leading, spacing: 16) {
            LazyVGrid(columns: [GridItem(.adaptive(minimum: compact ? 150 : 230), spacing: 12)], spacing: 12) {
                Kpi(title: "Time tracked", value: Presentation.duration(Double(r.trackedSeconds)), sub: average > 0 ? "\(Presentation.duration(average)) per active day" : nil, icon: "timer")
                Kpi(title: "Focus sessions", value: "\(r.focusSessions)", sub: focus > 0 ? "\(Presentation.duration(Double(focus))) of focus" : nil, icon: "scope")
                Kpi(title: "Tasks done", value: "\(r.completed)", sub: r.created > 0 ? "\(r.created) new" : nil, icon: "checkmark.circle")
                Kpi(title: "Longest streak", value: "\(r.longestStreak) day\(r.longestStreak == 1 ? "" : "s")", sub: "\(r.activeDays) active day\(r.activeDays == 1 ? "" : "s")", icon: "flame")
            }
            if r.trackedSeconds == 0 && r.completed == 0 {
                VStack(spacing: 10) {
                    Image(systemName: "chart.bar").font(.largeTitle).foregroundStyle(.secondary)
                    Text("Nothing tracked here yet. Start a timer on a task (▶) or a focus session, and your reports fill in.")
                        .multilineTextAlignment(.center).foregroundStyle(.secondary)
                }
                .frame(maxWidth: .infinity)
                .padding(32)
            } else {
                LazyVGrid(columns: [GridItem(.adaptive(minimum: compact ? 300 : 420), spacing: 16, alignment: .top)], alignment: .leading, spacing: 16) {
                    ReportCard(title: "Time per day", subtitle: "focus and other time · hours") { PerDayChart(report: r) }
                    ReportCard(title: "By group", subtitle: nil) { GroupDonut(report: r, groups: groups) }
                    ReportCard(title: "Where the time went", subtitle: "top tasks") { TopTasks(report: r, groups: groups, open: open) }
                    ReportCard(title: "When you work", subtitle: "weekday × hour") { HourHeatmap(grid: r.hourGrid) }
                    if r.days.count >= 90 {
                        ReportCard(title: "Active days", subtitle: "time tracked or tasks done") { ActiveDays(days: r.days) }
                    }
                }
                ReportCard(title: "Timeline", subtitle: "bars: work · line: from created to done or due · ◆ due · ● done") {
                    if r.timeline.isEmpty {
                        Text("No task was worked on or finished in this range.").foregroundStyle(.secondary)
                    } else {
                        TimelineChart(report: r, compactLabels: compact, groups: groups, open: open)
                    }
                }
            }
        }
    }
}

struct Kpi: View {
    let title: String
    let value: String
    let sub: String?
    let icon: String

    var body: some View {
        VStack(alignment: .leading, spacing: 4) {
            Image(systemName: icon).foregroundStyle(Theme.accent)
            Text(value).font(.title2.weight(.semibold)).monospacedDigit().lineLimit(1).minimumScaleFactor(0.7)
            Text(title).font(.callout)
            if let sub { Text(sub).font(.caption).foregroundStyle(.secondary) }
        }
        .frame(maxWidth: .infinity, minHeight: 96, alignment: .topLeading)
        .padding(12)
        .background(RoundedRectangle(cornerRadius: 12).fill(.background.secondary))
        .accessibilityElement(children: .combine)
    }
}

struct ReportCard<Content: View>: View {
    let title: String
    let subtitle: String?
    @ViewBuilder let content: Content

    var body: some View {
        VStack(alignment: .leading, spacing: 10) {
            HStack(alignment: .firstTextBaseline) {
                Text(title).font(.headline)
                if let subtitle { Text(subtitle).font(.caption).foregroundStyle(.secondary) }
            }
            content
        }
        .padding(14)
        .frame(maxWidth: .infinity, alignment: .topLeading)
        .background(RoundedRectangle(cornerRadius: 14).fill(.background.secondary))
    }
}

/// Hours a day (summed a week at a time for long ranges), focus and other time stacked.
struct PerDayChart: View {
    let report: TimeReport

    private struct Bar: Identifiable {
        let id: String
        let start: Date
        let kind: String
        let hours: Double
    }

    var body: some View {
        let size = report.days.count > 120 ? 7 : 1
        var bars: [Bar] = []
        for i in stride(from: 0, to: report.days.count, by: size) {
            let slice = report.days[i..<min(i + size, report.days.count)]
            let focus = slice.reduce(Int64(0)) { $0 + $1.focusSeconds }
            let all = slice.reduce(Int64(0)) { $0 + $1.trackedSeconds }
            let start = ReportDates.noon(slice.first!.date)
            bars.append(Bar(id: "\(i)-f", start: start, kind: "Focus", hours: Double(focus) / 3600))
            bars.append(Bar(id: "\(i)-o", start: start, kind: "Timer & logged", hours: Double(all - focus) / 3600))
        }
        return Chart(bars) { bar in
            BarMark(x: .value("Day", bar.start, unit: size == 7 ? .weekOfYear : .day), y: .value("Hours", bar.hours))
                .foregroundStyle(by: .value("Kind", bar.kind))
        }
        .chartForegroundStyleScale(["Focus": ReportColors.focus, "Timer & logged": ReportColors.other])
        .chartYAxisLabel("h")
        .chartLegend(position: .bottom, alignment: .leading)
        .frame(height: 200)
        .accessibilityLabel("Hours tracked per day")
    }
}

struct GroupDonut: View {
    let report: TimeReport
    let groups: [TaskGroup]

    var body: some View {
        let total = max(1, report.trackedSeconds)
        HStack(alignment: .center, spacing: 16) {
            Chart(report.groups, id: \.groupId) { g in
                SectorMark(angle: .value("Time", g.trackedSeconds), innerRadius: .ratio(0.62), angularInset: 1.5)
                    .foregroundStyle(ReportColors.group(g.groupId, groups))
                    .accessibilityLabel(g.name)
                    .accessibilityValue(Presentation.duration(Double(g.trackedSeconds)))
            }
            .chartBackground { _ in
                VStack(spacing: 0) {
                    Text(Presentation.duration(Double(report.trackedSeconds))).font(.headline.monospacedDigit())
                    Text("tracked").font(.caption).foregroundStyle(.secondary)
                }
            }
            .frame(width: 160, height: 160)
            VStack(alignment: .leading, spacing: 6) {
                ForEach(report.groups, id: \.groupId) { g in
                    HStack(spacing: 6) {
                        Circle().fill(ReportColors.group(g.groupId, groups)).frame(width: 9, height: 9)
                        Text(g.name).lineLimit(1)
                        Spacer(minLength: 4)
                        Text("\(Presentation.duration(Double(g.trackedSeconds))) · \(Int((Double(g.trackedSeconds) / Double(total) * 100).rounded()))%")
                            .font(.caption.monospacedDigit()).foregroundStyle(.secondary)
                    }
                    .font(.callout)
                }
            }
        }
    }
}

struct TopTasks: View {
    let report: TimeReport
    let groups: [TaskGroup]
    let open: (UUID) -> Void

    var body: some View {
        let top = max(1, report.tasks.first?.trackedSeconds ?? 1)
        VStack(alignment: .leading, spacing: 8) {
            ForEach(report.tasks, id: \.id) { t in
                Button { open(t.id) } label: {
                    VStack(alignment: .leading, spacing: 3) {
                        HStack {
                            Text(t.title + (t.done ? " ✓" : "")).lineLimit(1)
                            Spacer(minLength: 4)
                            Text(Presentation.duration(Double(t.trackedSeconds))).font(.caption.monospacedDigit()).foregroundStyle(.secondary)
                        }
                        GeometryReader { g in
                            Capsule().fill(ReportColors.group(t.groupId, groups))
                                .frame(width: max(4, g.size.width * CGFloat(Double(t.trackedSeconds) / Double(top))))
                        }
                        .frame(height: 6)
                    }
                    .contentShape(Rectangle())
                }
                .buttonStyle(.plain)
                .accessibilityLabel("\(t.title), \(Presentation.duration(Double(t.trackedSeconds)))\(t.done ? ", done" : "")")
            }
        }
    }
}

/// Weekday × hour: when the work happens (Monday first).
struct HourHeatmap: View {
    let grid: [[Int64]]
    private static let names = ["Mon", "Tue", "Wed", "Thu", "Fri", "Sat", "Sun"]

    private struct Cell: Identifiable {
        var id: String { "\(day)-\(hour)" }
        let day: String
        let hour: Int
        let seconds: Int64
    }

    var body: some View {
        // The grid's weekdays start on Sunday (0); shown Monday first.
        let cells = (0..<7).flatMap { row in
            (0..<24).map { hour in Cell(day: Self.names[row], hour: hour, seconds: grid[(row + 1) % 7][hour]) }
        }
        let most = max(1, cells.map(\.seconds).max() ?? 1)
        Chart(cells) { cell in
            RectangleMark(xStart: .value("Hour", Double(cell.hour) + 0.05), xEnd: .value("Hour", Double(cell.hour) + 0.95), y: .value("Day", cell.day), height: .fixed(18))
                .foregroundStyle(Theme.accent.opacity(cell.seconds == 0 ? 0.06 : 0.2 + 0.8 * Double(cell.seconds) / Double(most)))
                .accessibilityLabel("\(cell.day) \(cell.hour):00")
                .accessibilityValue(Presentation.duration(Double(cell.seconds)))
        }
        .chartXScale(domain: 0...24)
        .chartYScale(domain: Self.names)
        .chartXAxis { AxisMarks(values: [0, 6, 12, 18, 24]) { value in AxisValueLabel { Text(String(format: "%02d:00", (value.as(Int.self) ?? 0) % 24)) } } }
        .frame(height: 190)
    }
}

/// A calendar of the days with time tracked or tasks done (weeks across, weekdays down).
struct ActiveDays: View {
    let days: [ReportDay]
    private static let names = ["Mon", "Tue", "Wed", "Thu", "Fri", "Sat", "Sun"]

    private struct Cell: Identifiable {
        var id: String { date.description }
        let date: LocalDay
        let week: Int
        let day: String
        let value: Int64
        let tip: String
    }

    var body: some View {
        let first = days.first.map { $0.date.epochDay - ($0.date.weekday - 1) } ?? 0
        let cells = days.map { d in
            Cell(date: d.date, week: (d.date.epochDay - first) / 7, day: Self.names[d.date.weekday - 1], value: d.trackedSeconds + Int64(d.completed) * 1800,
                 tip: "\(Presentation.duration(Double(d.trackedSeconds))), \(d.completed) done")
        }
        let most = max(1, cells.map(\.value).max() ?? 1)
        Chart(cells) { cell in
            RectangleMark(xStart: .value("Week", Double(cell.week) + 0.08), xEnd: .value("Week", Double(cell.week) + 0.92), y: .value("Day", cell.day), height: .fixed(14))
                .foregroundStyle(Color(hex: "#22c55e").opacity(cell.value == 0 ? 0.08 : 0.25 + 0.75 * Double(cell.value) / Double(most)))
                .accessibilityLabel(ReportDates.noon(cell.date).formatted(date: .abbreviated, time: .omitted))
                .accessibilityValue(cell.tip)
        }
        .chartYScale(domain: Self.names)
        .chartXAxis(.hidden)
        .frame(height: 150)
    }
}

/// The timeline (Gantt): a row per task, its work as bars, a line from created to done (or due), ◆ due, ● done.
struct TimelineChart: View {
    let report: TimeReport
    var compactLabels = false
    let groups: [TaskGroup]
    private static let rowHeight: CGFloat = 26
    let open: (UUID) -> Void

    private struct Span: Identifiable {
        let id: Int
        let row: String
        let start: Date
        let end: Date
        let color: Color
    }

    var body: some View {
        let rangeStart = PlanDays.midnight(report.from, .current)
        let rangeEnd = PlanDays.midnight(report.to.adding(days: 1), .current)
        let clamp = { (d: Date) in min(max(d, rangeStart), rangeEnd) }
        let rows = report.timeline
        var spans: [Span] = []
        for row in rows {
            for w in row.work { spans.append(Span(id: spans.count, row: row.id.uuidString, start: w.start, end: w.end, color: ReportColors.group(row.groupId, groups))) }
        }
        let chart = Chart {
            ForEach(rows) { row in
                let lifeEnd = clamp(row.completedAt ?? row.deadline ?? rangeEnd)
                RuleMark(xStart: .value("From", clamp(row.createdAt)), xEnd: .value("To", max(lifeEnd, clamp(row.createdAt))), y: .value("Task", row.id.uuidString))
                    .foregroundStyle(Color.secondary.opacity(0.45))
                    .lineStyle(StrokeStyle(lineWidth: 1.5))
                if let due = row.deadline, due >= rangeStart, due <= rangeEnd {
                    PointMark(x: .value("Due", due), y: .value("Task", row.id.uuidString))
                        .symbol(.diamond)
                        .symbolSize(60)
                        .foregroundStyle(Theme.warnText)
                }
                if let done = row.completedAt, done >= rangeStart, done <= rangeEnd {
                    PointMark(x: .value("Done", done), y: .value("Task", row.id.uuidString))
                        .symbol(.circle)
                        .symbolSize(50)
                        .foregroundStyle(Color(hex: "#22c55e"))
                }
            }
            ForEach(spans) { span in
                BarMark(xStart: .value("Start", span.start), xEnd: .value("End", max(span.end, span.start.addingTimeInterval(report.days.count > 14 ? 3600 * 3 : 1200))),
                        y: .value("Task", span.row), height: .fixed(12))
                    .foregroundStyle(span.color)
                    .clipShape(RoundedRectangle(cornerRadius: 3))
            }
        }
        .chartXScale(domain: rangeStart...rangeEnd)
        .chartYScale(domain: rows.map(\.id.uuidString))
        .chartYAxis(.hidden)
        .chartXAxis(.hidden)
        .chartOverlay { proxy in
            GeometryReader { geometry in
                Rectangle().fill(.clear).contentShape(Rectangle())
                    .onTapGesture { location in
                        guard let frame = proxy.plotFrame.map({ geometry[$0] }) else { return }
                        let index = Int(((location.y - frame.origin.y) / frame.height * CGFloat(rows.count)).rounded(.down))
                        if rows.indices.contains(index) { open(rows[index].id) }
                    }
            }
        }
        .frame(height: CGFloat(rows.count) * Self.rowHeight)
        .accessibilityHidden(true)
        let labelWidth: CGFloat = compactLabels ? 110 : 190
        let ticks = (0...4).map { rangeStart.addingTimeInterval((rangeEnd.timeIntervalSince(rangeStart) - 1) * Double($0) / 4) }
        return VStack(alignment: .leading, spacing: 4) {
            HStack(alignment: .top, spacing: 8) {
                // The titles: a tap opens the task (and they're what VoiceOver reads).
                VStack(alignment: .trailing, spacing: 0) {
                    ForEach(rows) { row in
                        Button { open(row.id) } label: {
                            Text(row.title).font(.caption).lineLimit(1).frame(maxWidth: .infinity, alignment: .trailing)
                        }
                        .buttonStyle(.plain)
                        .foregroundStyle(.secondary)
                        .frame(height: Self.rowHeight)
                        .accessibilityValue(Presentation.duration(row.work.reduce(0) { $0 + $1.end.timeIntervalSince($1.start) }))
                    }
                }
                .frame(width: labelWidth)
                chart
            }
            HStack(spacing: 8) {
                Color.clear.frame(width: labelWidth, height: 1)
                GeometryReader { g in
                    ForEach(Array(ticks.enumerated()), id: \.offset) { i, tick in
                        Text(tick.formatted(.dateTime.month(.abbreviated).day()))
                            .font(.caption2)
                            .foregroundStyle(.secondary)
                            .fixedSize()
                            .position(x: min(max(g.size.width * CGFloat(i) / 4, 22), g.size.width - 22), y: 7)
                    }
                }
                .frame(height: 14)
            }
            .accessibilityHidden(true)
        }
    }
}
#endif
