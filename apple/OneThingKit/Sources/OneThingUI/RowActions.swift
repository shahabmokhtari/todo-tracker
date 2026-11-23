#if canImport(SwiftUI)
import SwiftUI
import OneThingKit

/// What a task's row offers without an icon row under it: a swipe from the leading edge finishes it (or brings it back,
/// or reopens it), one from the trailing edge puts it off (Later) or shows the rest (More); a right-click or a long
/// press shows everything (the context menu). The same on iPhone, iPad and Mac.
struct TaskRowActions: ViewModifier {
    @ObservedObject var model: BoardModel
    let id: UUID
    /// The row's details (Open details, More › Open details).
    let open: () -> Void
    /// Note opens a box under the row (Today), instead of the details.
    var inlineNote = false
    @State private var later = false
    @State private var more = false
    @State private var picking = false
    @Environment(\.shellState) private var shell

    func body(content: Content) -> some View {
        let item = model.board.find(id)
        let actions = Presentation.rowActions(isDone: item?.isDone ?? false, canBringBack: item.map { Presentation.canBringBack($0, now: model.now) } ?? false)
        content
            .swipeActions(edge: .leading, allowsFullSwipe: true) {
                ForEach(actions.leading, id: \.self) { swipeButton($0) }
            }
            .swipeActions(edge: .trailing, allowsFullSwipe: false) {
                ForEach(actions.trailing, id: \.self) { swipeButton($0) }
            }
            .contextMenu { TaskContextMenu(model: model, id: id, inlineNote: inlineNote) }
            .confirmationDialog(item.map { "Put off “\($0.title)” until…" } ?? "Later", isPresented: $later, titleVisibility: .visible) {
                ForEach(model.snoozeChoices) { choice in
                    Button("\(choice.label) · \(Snooze.describe(choice.at, now: Date(), timeZone: .current))") { model.snooze(id, until: choice.at) }
                }
                Button("Pick a time…") { picking = true }
                Button("Cancel", role: .cancel) {}
            }
            .confirmationDialog(item?.title ?? "", isPresented: $more, titleVisibility: .visible) {
                if let item, !item.isDone {
                    Button("Add a note") { note() }
                    if model.isOn(ExtraSwitches.focusTimer) { Button("Focus on this") { model.startFocus(id) } }
                    Button(model.runningTimer?.item.id == id ? "Stop the timer" : "Start the timer") { model.toggleTimer(id) }
                }
                Button("Open details", action: open)
                Button("Copy link") { Clipboard.copy(TaskLinks.forTask(id)) }
                Button("Cancel", role: .cancel) {}
            }
            .sheet(isPresented: $picking) { SnoozePicker(model: model, id: id) }
    }

    private func note() {
        if inlineNote {
            if !model.openNotes.contains(id) { model.toggleNote(id) }
        } else {
            open()
        }
    }

    @ViewBuilder private func swipeButton(_ action: RowAction) -> some View {
        switch action {
        case .done:
            Button { markDone(model, shell, id) } label: { Label(action.title, systemImage: action.symbol) }.tint(.green)
        case .reopen:
            Button { model.reopen(id) } label: { Label(action.title, systemImage: action.symbol) }.tint(Theme.accent)
        case .bringBack:
            Button { model.bringBack(id) } label: { Label(action.title, systemImage: action.symbol) }.tint(Theme.accent)
        case .later:
            Button { later = true } label: { Label(action.title, systemImage: action.symbol) }.tint(Color(hex: "#f59e0b"))
        case .more:
            Button { more = true } label: { Label(action.title, systemImage: action.symbol) }.tint(.gray)
        }
    }
}

/// Marks a task done through the window (a task with open subtasks asks first); on its own, at once.
@MainActor
func markDone(_ model: BoardModel, _ shell: ShellState?, _ id: UUID) {
    if let shell { shell.markDone(id) } else { model.complete(id) }
}

extension View {
    /// Swipe actions and the context menu of a task's row (see TaskRowActions).
    func taskRowActions(_ model: BoardModel, id: UUID, inlineNote: Bool = false, open: @escaping () -> Void) -> some View {
        modifier(TaskRowActions(model: model, id: id, open: open, inlineNote: inlineNote))
    }
}

/// A narrow screen's timer strip, under the view and above the tabs (nothing while no timer runs, unless `showIdle`).
struct TimerBelow: ViewModifier {
    @ObservedObject var model: BoardModel
    var showIdle = false

    func body(content: Content) -> some View {
        VStack(spacing: 0) {
            content
            TimerStrip(model: model, style: .bar, showIdle: showIdle)
                .padding(.horizontal, 16).padding(.vertical, 6)
        }
    }
}

/// The two timers in one strip: the task being timed (its clock, its name, Stop) and the focus timer (its ring, its
/// clock, pause or play, and a menu with Skip, Reset and full-screen breaks). In the toolbar on an iPad and a Mac; a
/// bar above the tabs on an iPhone.
struct TimerStrip: View {
    enum Style { case bar, toolbar }

    @ObservedObject var model: BoardModel
    var style = Style.bar
    /// Offer the focus timer even when it isn't running.
    var showIdle = false
    @Environment(\.shellState) private var shell

    var body: some View {
        let pomodoro = model.board.pomodoro
        if let layout = Presentation.timerStrip(tracking: model.runningTimer != nil, focusPhase: pomodoro.phase, showIdle: showIdle,
                                                focusOn: model.isOn(ExtraSwitches.focusTimer)) {
            HStack(spacing: style == .bar ? 12 : 10) {
                if layout.tracker, let running = model.runningTimer {
                    tracker(running.item, running.entry, alone: layout.focus == nil)
                }
                if layout.tracker && layout.focus != nil {
                    Rectangle().fill(.quaternary).frame(width: 1, height: 20).accessibilityHidden(true)
                }
                if let focus = layout.focus {
                    focusTimer(pomodoro, full: focus == .full)
                }
            }
            .font(.subheadline)
            .padding(.horizontal, style == .bar ? 14 : 10)
            .padding(.vertical, style == .bar ? 8 : 4)
            .frame(maxWidth: style == .bar ? .infinity : nil, alignment: .leading)
            .background {
                if style == .bar {
                    Capsule().fill(.regularMaterial)
                        .overlay(Capsule().strokeBorder(.quaternary, lineWidth: 1))
                        .shadow(color: .black.opacity(0.08), radius: 10, y: 3)
                } else {
                    Capsule().fill(Color.secondary.opacity(0.12))
                }
            }
            .accessibilityElement(children: .contain)
            .accessibilityLabel("Timers")
        }
    }

    private func tracker(_ item: WorkItem, _ entry: TimeEntry, alone: Bool) -> some View {
        HStack(spacing: 8) {
            Circle().fill(Color(hex: "#ef4444")).frame(width: 7, height: 7).accessibilityHidden(true)
            Text(Presentation.clock(entry.duration(model.now)))
                .font(.subheadline.weight(.semibold).monospacedDigit())
                .accessibilityLabel("Timing for \(Presentation.duration(entry.duration(model.now)))")
                .fixedSize()
            Button { shell?.open(item.id) } label: {
                Text(item.title).lineLimit(1).truncationMode(.tail).foregroundStyle(.secondary)
            }
            .buttonStyle(.plain)
            .frame(maxWidth: style == .toolbar ? 180 : (alone ? .infinity : 150), alignment: .leading)
            .fixedSize(horizontal: style == .toolbar, vertical: false)
            .help("Open \(item.title)")
            Button { model.stopTimer() } label: { Image(systemName: "stop.fill").font(.caption) }
                .buttonStyle(.plain)
                .foregroundStyle(.secondary)
                .accessibilityLabel("Stop timing \(item.title)")
                .help("Stop the timer")
        }
    }

    private func focusTimer(_ p: PomodoroTimer, full: Bool) -> some View {
        let fraction = Presentation.pomodoroFraction(p, now: model.now)
        let ring = p.phase == .focus ? Color(hex: "#f43f5e") : p.phase == .idle ? Theme.accent : Color(hex: "#10b981")
        let title = p.itemId.flatMap { model.board.find($0)?.title }
        return HStack(spacing: 8) {
            ZStack {
                Circle().stroke(.quaternary, lineWidth: 2.5)
                Circle().trim(from: 0, to: fraction).stroke(ring, style: StrokeStyle(lineWidth: 2.5, lineCap: .round)).rotationEffect(.degrees(-90))
            }
            .frame(width: 16, height: 16)
            .accessibilityHidden(true)
            Text(model.pomodoroText).font(.subheadline.weight(.semibold).monospacedDigit()).fixedSize()
            if full {
                Text(Presentation.focusLabel(p, taskTitle: style == .bar ? title : nil))
                    .foregroundStyle(.secondary).lineLimit(1)
                    .frame(maxWidth: style == .bar ? .infinity : nil, alignment: .leading)
            }
            Group {
                if p.phase == .idle {
                    Button { model.startFocus(nil) } label: { Image(systemName: "play.fill") }.accessibilityLabel("Start focus").help("Start focus")
                } else if p.isRunning {
                    Button { model.pausePomodoro() } label: { Image(systemName: "pause.fill") }.accessibilityLabel("Pause").help("Pause")
                } else {
                    Button { model.resumePomodoro() } label: { Image(systemName: "play.fill") }.accessibilityLabel("Resume").help("Resume")
                }
            }
            .font(.caption)
            .buttonStyle(.plain)
            .foregroundStyle(.secondary)
            Menu {
                if p.phase != .idle {
                    Button { model.skipPomodoro() } label: { Label(p.phase == .focus ? "Skip to the break" : "Skip the break", systemImage: "forward.end") }
                    Button { model.resetPomodoro() } label: { Label("Reset", systemImage: "arrow.counterclockwise") }
                    Divider()
                }
                Toggle(isOn: $model.fullScreenBreaks) { Label("Full-screen breaks", systemImage: "rectangle.inset.filled") }
            } label: {
                Image(systemName: "ellipsis").font(.caption.weight(.semibold)).frame(width: 18, height: 18).contentShape(Rectangle())
            }
            .menuStyle(.button)
            .buttonStyle(.plain)
            .menuIndicator(.hidden)
            .fixedSize()
            .foregroundStyle(.secondary)
            .accessibilityLabel("Focus timer options")
            .help("Skip, reset, full-screen breaks")
        }
    }
}
#endif
