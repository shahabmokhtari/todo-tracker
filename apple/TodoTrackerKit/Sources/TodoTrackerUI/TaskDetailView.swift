#if canImport(SwiftUI)
import SwiftUI
import TodoTrackerKit

/// Task details (a sheet): every field of the task, its subtasks (each opens here too), reminders, notes and a mini
/// timeline. Same fields as the web app's task panel.
struct TaskDetailView: View {
    @ObservedObject var model: BoardModel
    let itemId: UUID
    @Environment(\.dismiss) private var dismiss

    var body: some View {
        NavigationStack {
            TaskDetailContent(model: model, itemId: itemId, close: { dismiss() })
                // A subtask or a parent opens on top; Back returns to this one.
                .navigationDestination(for: UUID.self) { id in
                    TaskDetailContent(model: model, itemId: id, close: { dismiss() })
                }
        }
        #if os(macOS)
        .frame(minWidth: 460, minHeight: 600)
        #endif
    }
}

/// Repeat rules offered to pick from (any other one can be typed); the same list as the web app (looks.js).
let repeatChoices = [
    "every day", "every weekday", "every week", "every 2 weeks", "every month", "every year",
    "every week on Monday", "every month on the 1st", "every month on the last day", "every month on the last Friday",
    "every 3 days when done",
]

/// Group colors to pick from (the web app's palette, looks.js).
let groupColors: [(name: String, hex: String)] = [
    ("Red", "#ef4444"), ("Orange", "#f97316"), ("Amber", "#f59e0b"), ("Yellow", "#eab308"), ("Green", "#10b981"), ("Teal", "#14b8a6"),
    ("Sky", "#0ea5e9"), ("Blue", "#3b82f6"), ("Violet", "#7c3aed"), ("Pink", "#ec4899"), ("Slate", "#64748b"), ("Brown", "#78350f"),
]

struct TaskDetailContent: View {
    @ObservedObject var model: BoardModel
    let itemId: UUID
    let close: () -> Void

    @State private var note = ""
    @State private var subtask = ""
    @State private var steps = ""
    @State private var hoursBetween = 24.0
    @State private var confirmDelete = false
    @State private var confirmComplete = false
    /// What's being typed (nil: not changed here, so the task's own value shows, also after a refresh from the server).
    @State private var titleDraft: String?
    @State private var detailsDraft: String?
    @State private var repeatDraft: String?
    @State private var repeatProblem: String?
    @State private var pickingDeadline = false
    @State private var addingReminder = false

    var body: some View {
        if let item = model.board.find(itemId) {
            form(item)
                .navigationTitle(item.parent == nil ? "Task" : "Subtask")
                #if os(iOS)
                .navigationBarTitleDisplayMode(.inline)
                #endif
                .toolbar {
                    // "Close", not "Done": next to "Mark done" a bare "Done" reads as finishing the task.
                    ToolbarItem(placement: .confirmationAction) { Button("Close", action: close) }
                }
                .confirmationDialog("Also mark the open subtasks as done?", isPresented: $confirmComplete) {
                    Button("Mark all done") {
                        model.complete(itemId)
                        close()
                    }
                }
                .confirmationDialog(deleteQuestion(item), isPresented: $confirmDelete, titleVisibility: .visible) {
                    Button("Delete", role: .destructive) {
                        model.delete(itemId)
                        close()
                    }
                }
                .sheet(isPresented: $pickingDeadline) {
                    WhenSheet(title: "Deadline", action: "Set deadline", initial: item.deadline ?? Self.tomorrow(hour: 17), allowsPast: true) { at, _ in
                        model.setDeadline(itemId, at)
                    }
                }
                .sheet(isPresented: $addingReminder) {
                    WhenSheet(title: "Remind me", action: "Remind me", initial: Date().addingTimeInterval(3600), choices: model.snoozeChoices, asksMessage: true) { at, message in
                        model.addReminder(itemId, at: at, message: message)
                    }
                }
        } else {
            Text("This task isn’t here any more.").padding()
                .toolbar { ToolbarItem(placement: .confirmationAction) { Button("Close", action: close) } }
        }
    }

    // MARK: Sections

    private func form(_ item: WorkItem) -> some View {
        let state = Agenda.state(of: item, now: model.now)
        return Form {
            Section {
                whereRow(item)
                TextField("Title", text: Binding(get: { titleDraft ?? item.title }, set: { titleDraft = $0 }), axis: .vertical)
                    .font(.title3.weight(.semibold))
                    .accessibilityIdentifier("detail-title")
                    .onSubmit { save(item) }
                if let waiting = Presentation.waiting(item, now: model.now) { waitingBanner(waiting) }
                LabeledContent("State", value: stateText(state))
                LabeledContent("Time spent", value: Presentation.duration(item.timeSpent(model.now)))
            }

            actions(item, state: state)

            Section("Details") {
                TextField("Details, links, context…", text: Binding(get: { detailsDraft ?? item.details ?? "" }, set: { detailsDraft = $0 }), axis: .vertical)
                    .lineLimit(2...8)
                    .accessibilityIdentifier("detail-details")
                if hasDrafts(item) {
                    Button("Save changes") { save(item) }
                        .disabled((titleDraft ?? item.title).trimmingCharacters(in: .whitespacesAndNewlines).isEmpty)
                        .accessibilityIdentifier("detail-save")
                }
                Picker("Priority", selection: Binding(get: { item.priority }, set: { model.edit(itemId, TaskChanges(priority: $0), message: "Priority: \($0.label)") })) {
                    ForEach(Priority.allCases, id: \.self) { Text($0.label).tag($0) }
                }
                deadlineRow(item)
                repeatRow(item)
                Picker("Energy per hour", selection: Binding(get: { item.energy }, set: { model.setEnergy(itemId, level: $0) })) {
                    ForEach(EnergyChoice.all) { Text($0.label).tag($0.level) }
                }
                if item.parent == nil {
                    Picker("Group", selection: Binding(get: { item.groupId }, set: { model.moveToGroup(itemId, groupId: $0) })) {
                        ForEach(model.groups) { Text($0.name).tag($0.id) }
                    }
                    .accessibilityIdentifier("detail-group")
                }
            }

            subtasks(item)
            reminders(item)
            notes(item)

            Section("Timeline") {
                ForEach(Array(((try? model.board.timeline(itemId)) ?? []).prefix(20).enumerated()), id: \.offset) { _, entry in
                    VStack(alignment: .leading) {
                        Text(entry.summary)
                        Text("\(entry.actor.displayName) · \(RelativeTime.format(entry.at, now: model.now))").font(.caption).foregroundStyle(.secondary)
                    }
                }
            }

            Section {
                Button("Delete…", role: .destructive) { confirmDelete = true }
            } footer: {
                if item.isDone && item.parent == nil { Text("Archive puts it away without deleting it.") }
            }
        }
    }

    /// Its group, and the tasks it's part of (each opens).
    private func whereRow(_ item: WorkItem) -> some View {
        let group = model.groups.first { $0.id == item.groupId }
        return FlowLayout(spacing: 6) {
            HStack(spacing: 5) {
                Circle().fill(Color(hex: group?.color ?? "#6366f1")).frame(width: 7, height: 7)
                Text(group?.name ?? "Group")
            }
            ForEach(item.ancestors.reversed()) { parent in
                Text("›").foregroundStyle(.tertiary)
                NavigationLink(value: parent.id) { Text(parent.title).lineLimit(1) }
                    .buttonStyle(.plain)
                    .foregroundStyle(Theme.accent)
                    .accessibilityHint("Opens it")
            }
        }
        .font(.caption)
        .foregroundStyle(.secondary)
    }

    private func waitingBanner(_ waiting: WaitingLine) -> some View {
        VStack(alignment: .leading, spacing: 8) {
            Label(waiting.text, systemImage: "clock").font(.subheadline).foregroundStyle(Theme.color(for: .info))
                .accessibilityIdentifier("detail-waiting")
            HStack(spacing: 16) {
                if waiting.canBringBack {
                    Button { model.bringBack(itemId) } label: { Label("Do now", systemImage: "arrow.uturn.backward") }
                        .buttonStyle(.borderless)
                }
                if let parent = waiting.heldBy {
                    NavigationLink(value: parent.id) { Label("Open parent", systemImage: "arrow.up.forward.square") }
                        .buttonStyle(.borderless)
                }
            }
            .font(.subheadline.weight(.semibold))
        }
        .padding(.vertical, 4)
    }

    @ViewBuilder private func actions(_ item: WorkItem, state: ItemState) -> some View {
        Section {
            if item.isDone {
                Button { model.reopen(itemId) } label: { Label("Reopen", systemImage: "arrow.uturn.backward") }
                if item.parent == nil {
                    if item.archivedAt == nil {
                        Button { model.archive(itemId) } label: { Label("Archive", systemImage: "archivebox") }
                    } else {
                        Button { model.unarchive(itemId) } label: { Label("Unarchive", systemImage: "tray.and.arrow.up") }
                    }
                }
            } else {
                if state != .locked {
                    Button {
                        if item.hasOpenChildren {
                            confirmComplete = true
                        } else {
                            model.complete(itemId)
                            close()
                        }
                    } label: { Label("Mark done", systemImage: "checkmark.circle") }
                }
                SnoozeMenu(model: model, id: itemId, style: .row)
                let timing = model.runningTimer?.item.id == item.id
                Button { model.toggleTimer(item.id) } label: {
                    Label(timing ? "Stop the timer" : "Time this task", systemImage: timing ? "stop.circle" : "stopwatch")
                }
            }
        }
    }

    private func deadlineRow(_ item: WorkItem) -> some View {
        HStack {
            Text("Deadline")
            Spacer()
            if let deadline = item.deadline {
                Button(Snooze.describe(deadline, now: model.now, timeZone: .current)) { pickingDeadline = true }
                    .buttonStyle(.borderless)
                    .foregroundStyle(deadline < model.now ? Theme.color(for: .danger) : Theme.warnText)
                Button { model.setDeadline(itemId, nil) } label: { Image(systemName: "xmark.circle.fill") }
                    .buttonStyle(.borderless)
                    .foregroundStyle(.secondary)
                    .accessibilityLabel("Clear the deadline")
            } else {
                Button("Set…") { pickingDeadline = true }.buttonStyle(.borderless)
                    .accessibilityIdentifier("detail-set-deadline")
            }
        }
    }

    @ViewBuilder private func repeatRow(_ item: WorkItem) -> some View {
        let current = item.repeatRule?.description ?? ""
        HStack {
            TextField("Repeat", text: Binding(get: { repeatDraft ?? current }, set: { repeatDraft = $0; repeatProblem = nil }),
                      prompt: Text("Doesn’t repeat (e.g. every weekday)"))
                .onSubmit { saveRepeat(current) }
                .accessibilityIdentifier("detail-repeat")
            Menu {
                ForEach(repeatChoices, id: \.self) { rule in
                    Button(rule) {
                        repeatDraft = rule
                        saveRepeat(current)
                    }
                }
                if !current.isEmpty {
                    Divider()
                    Button("Doesn’t repeat") {
                        repeatDraft = ""
                        saveRepeat(current)
                    }
                }
            } label: { Image(systemName: "repeat") }
                .menuStyle(.button)
                .buttonStyle(.borderless)
                .fixedSize()
                .accessibilityLabel("Repeat choices")
        }
        if let draft = repeatDraft, draft.trimmingCharacters(in: .whitespaces) != current {
            Button("Save repeat") { saveRepeat(current) }
        }
        if let repeatProblem {
            Text(repeatProblem).font(.caption).foregroundStyle(Theme.color(for: .danger))
        } else if item.repeatRule != nil {
            Text("Done moves it to its next time (its deadline and when it comes back move along).").font(.caption).foregroundStyle(.secondary)
        }
    }

    private func subtasks(_ item: WorkItem) -> some View {
        Section(item.sequential ? "Subtasks · in order" : "Subtasks") {
            let rows = Presentation.subtaskRows(item)
            if rows.isEmpty { Text("No subtasks yet.").foregroundStyle(.secondary) }
            ForEach(rows) { row in subtaskRow(row) }
            HStack {
                TextField("Add a subtask…", text: $subtask).onSubmit(addSubtask)
                    .accessibilityIdentifier("detail-subtask-field")
                Button("Add", action: addSubtask).disabled(subtask.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty)
            }
            if !item.children.isEmpty {
                Toggle("Steps in order", isOn: Binding(get: { item.sequential }, set: { model.setSequential(itemId, $0) }))
                    .accessibilityIdentifier("detail-sequential")
                Picker("Hours between steps", selection: Binding(get: { (item.stepDelay ?? 0) / 3600 }, set: { model.setStepDelay(itemId, hours: $0) })) {
                    ForEach(Presentation.stepDelayChoices(current: item.stepDelay), id: \.self) { Text(Presentation.hoursLabel($0)).tag($0) }
                }
            }
            DisclosureGroup("Add rollout steps") {
                TextField("One step per line", text: $steps, axis: .vertical).lineLimit(3...8)
                Stepper("Hours between the new steps: \(Int(hoursBetween))", value: $hoursBetween, in: 0...168, step: 1)
                Button("Add steps") {
                    model.addSteps(itemId, titles: stepTitles, hoursBetween: hoursBetween)
                    steps = ""
                }
                .disabled(stepTitles.isEmpty)
            }
        }
    }

    private func subtaskRow(_ row: SubtaskRow) -> some View {
        let child = row.item
        let state = Agenda.state(of: child, now: model.now)
        return HStack(spacing: 10) {
            Button {
                if child.isDone { model.reopen(child.id) } else { model.complete(child.id) }
            } label: {
                Image(systemName: child.isDone ? "checkmark.circle.fill" : state == .locked ? "lock.circle" : "circle")
                    .foregroundStyle(child.isDone ? .green : .secondary)
            }
            .buttonStyle(.borderless)
            .disabled(!child.isDone && state == .locked)
            .accessibilityLabel(child.isDone ? "Reopen \(child.title)" : "Mark \(child.title) done")
            NavigationLink(value: child.id) {
                HStack {
                    Text(child.title).strikethrough(child.isDone).foregroundStyle(child.isDone ? .secondary : .primary)
                    Spacer()
                    Text(stateText(state, of: child)).font(.caption).foregroundStyle(.secondary)
                }
            }
        }
        .padding(.leading, CGFloat(row.depth) * 18)
    }

    private func reminders(_ item: WorkItem) -> some View {
        Section("Reminders") {
            ForEach(item.reminders.filter(\.isPending)) { r in
                HStack {
                    Label("\(r.message) · \(Snooze.describe(r.dueAt, now: model.now, timeZone: .current))", systemImage: "alarm")
                        .font(.subheadline)
                    Spacer()
                    Button("Dismiss") { model.dismissReminder(itemId, reminderId: r.id) }.buttonStyle(.borderless)
                }
            }
            if !item.isDone {
                Button { addingReminder = true } label: { Label("Remind me…", systemImage: "bell.badge") }
                    .accessibilityIdentifier("detail-remind")
            }
        }
    }

    private func notes(_ item: WorkItem) -> some View {
        Section("Notes") {
            TextField("What happened? What is next?", text: $note, axis: .vertical).lineLimit(2...6)
                .accessibilityIdentifier("detail-note-field")
            Button("Add note") {
                model.addNote(itemId, text: note)
                note = ""
            }
            .disabled(note.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty)
            ForEach(item.notes.reversed()) { n in
                VStack(alignment: .leading) {
                    Text(n.text)
                    Text("\(n.author.displayName) · \(RelativeTime.format(n.at, now: model.now))").font(.caption).foregroundStyle(.secondary)
                }
            }
        }
    }

    // MARK: Helpers

    private var stepTitles: [String] {
        steps.split(separator: "\n").map { $0.trimmingCharacters(in: .whitespaces) }.filter { !$0.isEmpty }
    }

    private func hasDrafts(_ item: WorkItem) -> Bool {
        (titleDraft.map { $0 != item.title } ?? false) || (detailsDraft.map { $0 != (item.details ?? "") } ?? false)
    }

    /// Saves the title and details typed here: only what changed, so edits made elsewhere meanwhile stay.
    private func save(_ item: WorkItem) {
        var changes = TaskChanges()
        if let title = titleDraft, title != item.title {
            guard !title.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty else { return }
            changes.title = title
        }
        if let details = detailsDraft, details != (item.details ?? "") { changes.details = details }
        if model.edit(itemId, changes) {
            titleDraft = nil
            detailsDraft = nil
        }
    }

    private func saveRepeat(_ current: String) {
        guard let draft = repeatDraft else { return }
        let text = draft.trimmingCharacters(in: .whitespacesAndNewlines)
        guard text != current else {
            repeatDraft = nil
            return
        }
        if let problem = model.setRepeat(itemId, rule: text) {
            repeatProblem = problem
        } else {
            repeatDraft = nil
            repeatProblem = nil
        }
    }

    private func addSubtask() {
        guard !subtask.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty else { return }
        model.addSubtask(itemId, title: subtask)
        subtask = ""
    }

    private func stateText(_ state: ItemState, of item: WorkItem? = nil) -> String {
        switch state {
        case .actionable: return "Do now"
        case .container: return "In progress"
        case .locked: return "After the step before"
        case .done: return "Done"
        case .waiting:
            if let item, let other = item.waitingFor { return "Waiting for “\(other.title)”" }
            if let item, let next = item.nextActionAt, next > model.now { return "Back \(RelativeTime.format(next, now: model.now))" }
            return "Waiting"
        }
    }

    private func deleteQuestion(_ item: WorkItem) -> String {
        let what = item.children.isEmpty ? "“\(item.title)”" : "“\(item.title)” and its subtasks"
        return model.usesServer ? "Delete \(what)? It goes to your tasks folder’s trash." : "Delete \(what)? This can’t be undone."
    }

    private static func tomorrow(hour: Int) -> Date {
        let calendar = Calendar.current
        let day = calendar.date(byAdding: .day, value: 1, to: calendar.startOfDay(for: Date()))!
        return calendar.date(bySettingHour: hour, minute: 0, second: 0, of: day)!
    }
}

/// A date and time to pick (a deadline, a reminder), with quick choices and, for a reminder, a message.
struct WhenSheet: View {
    let title: String
    let action: String
    var choices: [SnoozeChoice] = []
    var asksMessage = false
    var allowsPast = false
    let onSave: (Date, String?) -> Void
    @Environment(\.dismiss) private var dismiss
    @State private var at: Date
    @State private var message = ""

    init(title: String, action: String, initial: Date, choices: [SnoozeChoice] = [], asksMessage: Bool = false, allowsPast: Bool = false,
         onSave: @escaping (Date, String?) -> Void) {
        self.title = title
        self.action = action
        self.choices = choices
        self.asksMessage = asksMessage
        self.allowsPast = allowsPast
        self.onSave = onSave
        _at = State(initialValue: initial)
    }

    var body: some View {
        ScrollView {
            VStack(alignment: .leading, spacing: 14) {
                Text(title).font(.title3.weight(.semibold))
                if !choices.isEmpty {
                    FlowLayout(spacing: 6) {
                        ForEach(choices) { choice in
                            PillButton(title: choice.label, systemImage: "clock") { at = choice.at }
                        }
                    }
                }
                Group {
                    if allowsPast {
                        DatePicker("When", selection: $at, displayedComponents: [.date, .hourAndMinute])
                    } else {
                        DatePicker("When", selection: $at, in: Date()..., displayedComponents: [.date, .hourAndMinute])
                    }
                }
                .datePickerStyle(.graphical)
                .labelsHidden()
                Text(Snooze.describe(at, now: Date(), timeZone: .current)).foregroundStyle(.secondary)
                if asksMessage {
                    TextField("Message (optional)", text: $message).textFieldStyle(.roundedBorder)
                }
                HStack {
                    Spacer()
                    Button("Cancel", role: .cancel) { dismiss() }.keyboardShortcut(.cancelAction)
                    Button(action) {
                        onSave(at, asksMessage ? message : nil)
                        dismiss()
                    }
                    .keyboardShortcut(.defaultAction)
                    .buttonStyle(.borderedProminent)
                    .tint(Theme.accent)
                    .accessibilityIdentifier("when-save")
                }
            }
            .padding(20)
        }
        .frame(minWidth: 320)
    }
}
#endif
