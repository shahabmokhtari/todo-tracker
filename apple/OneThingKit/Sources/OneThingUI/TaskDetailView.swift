#if canImport(SwiftUI)
import SwiftUI
import OneThingKit

/// Task details (a sheet): every field of the task, its subtasks (each opens here too), reminders, notes and a mini
/// timeline. Same fields as the web app's task panel.
struct TaskDetailView: View {
    @ObservedObject var model: BoardModel
    let itemId: UUID
    /// A sheet of its own size (false: beside a view, as wide as its column).
    var sized = true
    @Environment(\.dismiss) private var dismiss
    @Environment(\.shellState) private var shell

    var body: some View {
        NavigationStack {
            TaskDetailContent(model: model, itemId: itemId, close: close)
                // A subtask or a parent opens on top; Back returns to this one.
                .navigationDestination(for: UUID.self) { id in
                    TaskDetailContent(model: model, itemId: id, close: close)
                }
        }
        // Section names as written ("Details", "Notes"), not in capitals.
        .textCase(nil)
        #if os(macOS)
        .frame(minWidth: sized ? 460 : nil, minHeight: sized ? 600 : nil)
        #endif
    }

    /// Beside a view the details close by the window's own state (dismiss would close the window).
    private func close() {
        if !sized, let shell {
            shell.closeDetails()
        } else {
            dismiss()
        }
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

    // What's typed in the note, subtask and steps boxes lives in the model, by task: picking another task and coming
    // back keeps it.
    private var note: String { model.noteDrafts[itemId] ?? "" }
    private var subtask: String { model.typed(itemId, .subtask) }
    private var steps: String { model.typed(itemId, .steps) }
    @State private var hoursBetween = 24.0
    @State private var confirmDelete = false
    @State private var confirmComplete = false
    @State private var pickingDeadline = false
    @State private var addingReminder = false
    @State private var moving = false
    @State private var newTags = ""
    @State private var newLabel = ""
    @State private var icon: String?

    var body: some View {
        if let item = model.board.find(itemId) {
            form(item)
                // A Mac lays a form out in grouped rows too (not label columns), so the details fit beside a view.
                .formStyle(.grouped)
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
                .sheet(isPresented: $moving) { MoveSheet(model: model, itemId: itemId) }
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
                TextField("Title", text: draftBinding(.title, current: item.title), axis: .vertical)
                    .font(.title3.weight(.semibold))
                    .accessibilityIdentifier("detail-title")
                    .onSubmit { save(item) }
                if let waiting = Presentation.waiting(item, now: model.now) { waitingBanner(waiting) }
                LabeledContent("State", value: stateText(state))
                LabeledContent("Time spent", value: Presentation.duration(item.timeSpent(model.now)))
            }

            actions(item, state: state)

            Section("Details") {
                TextField("Details, links, context…", text: draftBinding(.details, current: item.details ?? ""), axis: .vertical)
                    .lineLimit(2...8)
                    .accessibilityIdentifier("detail-details")
                if model.draft(itemId, .title) != nil || model.draft(itemId, .details) != nil {
                    Button("Save changes") { save(item) }
                        .disabled((model.draft(itemId, .title) ?? item.title).trimmingCharacters(in: .whitespacesAndNewlines).isEmpty)
                        .accessibilityIdentifier("detail-save")
                }
                Picker("Priority", selection: Binding(get: { item.priority }, set: { model.edit(itemId, TaskChanges(priority: $0), message: "Priority: \($0.label)") })) {
                    ForEach(Priority.allCases, id: \.self) { Text($0.label).tag($0) }
                }
                deadlineRow(item)
                repeatRow(item)
                if model.isOn(ExtraSwitches.energy) {
                    Picker("Energy per hour", selection: Binding(get: { item.energy }, set: { model.setEnergy(itemId, level: $0) })) {
                        ForEach(EnergyChoice.all) { Text($0.label).tag($0.level) }
                    }
                }
                if item.parent == nil {
                    Picker("Group", selection: Binding(get: { item.groupId }, set: { model.moveToGroup(itemId, groupId: $0) })) {
                        ForEach(model.groups) { Text($0.name).tag($0.id) }
                    }
                    .accessibilityIdentifier("detail-group")
                    // Its column on the Board (a finished card is in Done; reopening it moves it back).
                    if !item.isDone {
                        Picker("Board", selection: Binding(get: { BoardColumn(rawValue: item.stage.rawValue) ?? .next }, set: { model.moveCard(itemId, to: $0) })) {
                            ForEach([BoardColumn.inbox, .next, .doing]) { Text($0.name).tag($0) }
                        }
                        .accessibilityIdentifier("detail-board")
                    }
                }
                Button { moving = true } label: { Label("Move…", systemImage: "folder") }
                    .help("Another group, under another task, or on its own")
                    .accessibilityIdentifier("detail-move")
            }

            lookSection(item)

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

    /// Its group, then the tasks it's part of: each on its own row, so tapping one opens just that one.
    /// Its icon, its own color (none: its first label's), its tags and its labels: the web details' fields.
    @ViewBuilder private func lookSection(_ item: WorkItem) -> some View {
        Section {
            HStack {
                TextField("Icon (an emoji)", text: Binding(get: { icon ?? item.icon ?? "" }, set: { icon = $0 }))
                    .onSubmit { saveIcon(item) }
                    .accessibilityIdentifier("detail-icon")
                if (icon ?? item.icon ?? "") != (item.icon ?? "") {
                    Button("Save") { saveIcon(item) }
                }
            }
            Menu {
                Button { model.setColor(itemId, nil) } label: {
                    if item.color == nil { Label("None (its label’s)", systemImage: "checkmark") } else { Text("None (its label’s)") }
                }
                ForEach(groupColors, id: \.hex) { color in
                    Button { model.setColor(itemId, color.hex) } label: {
                        if item.color?.lowercased() == color.hex { Label(color.name, systemImage: "checkmark") } else { Text(color.name) }
                    }
                }
            } label: {
                HStack {
                    Text("Color")
                    Spacer()
                    Circle().fill(model.board.accent(of: item).map { Color(hex: $0) } ?? Color.secondary.opacity(0.3)).frame(width: 14, height: 14)
                    Text(item.color.flatMap { hex in groupColors.first { $0.hex == hex.lowercased() }?.name ?? hex } ?? "None").foregroundStyle(.secondary)
                }
            }
            .accessibilityIdentifier("detail-color")

            if !item.tags.isEmpty {
                FlowLayout(spacing: 6) {
                    ForEach(item.tags, id: \.self) { tag in
                        Button { model.removeTag(itemId, tag) } label: {
                            Label("#\(tag)", systemImage: "xmark").labelStyle(TrailingIcon())
                                .font(.caption).padding(.horizontal, 8).padding(.vertical, 4)
                                .background(Capsule().fill(Color.secondary.opacity(0.12)))
                        }
                        .buttonStyle(.plain)
                        .accessibilityLabel("Remove the tag \(tag)")
                    }
                }
            }
            TextField("Add tags (deep work, errands)", text: $newTags)
                .autocorrectionDisabled()
                .onSubmit {
                    model.addTags(itemId, typed: newTags)
                    newTags = ""
                }
                .accessibilityIdentifier("detail-tags")

            if !model.labels.isEmpty {
                FlowLayout(spacing: 6) {
                    ForEach(model.labels, id: \.name) { label in
                        let on = item.labels.contains { $0.caseInsensitiveCompare(label.name) == .orderedSame }
                        Button { model.toggleLabel(itemId, label.name) } label: {
                            Text(label.name).font(.caption.weight(on ? .semibold : .regular))
                                .foregroundStyle(on ? Color.white : Color(hex: label.color))
                                .padding(.horizontal, 8).padding(.vertical, 4)
                                .background(Capsule().fill(on ? Color(hex: label.color) : Color(hex: label.color).opacity(0.12)))
                        }
                        .buttonStyle(.plain)
                        .accessibilityAddTraits(on ? .isSelected : [])
                        .accessibilityLabel("Label \(label.name)")
                    }
                }
            }
            TextField("New label", text: $newLabel)
                .onSubmit {
                    model.addLabel(itemId, newLabel)
                    newLabel = ""
                }
                .accessibilityIdentifier("detail-new-label")
        } header: {
            Text("Look, tags and labels")
        } footer: {
            Text("A task without a color of its own shows its first label’s.")
        }
    }

    private func saveIcon(_ item: WorkItem) {
        guard let typed = icon else { return }
        icon = nil
        if typed.trimmingCharacters(in: .whitespacesAndNewlines) != (item.icon ?? "") { model.setIcon(itemId, typed) }
    }

    @ViewBuilder private func whereRow(_ item: WorkItem) -> some View {
        let group = model.groups.first { $0.id == item.groupId }
        HStack(spacing: 5) {
            Circle().fill(Color(hex: group?.color ?? "#6366f1")).frame(width: 7, height: 7)
            Text(group?.name ?? "Group")
        }
        .font(.caption)
        .foregroundStyle(.secondary)
        ShowInTreeButton(item: item)
        if let parent = item.parent {
            NavigationLink(value: parent.id) {
                LabeledContent("Part of", value: item.path.dropLast().joined(separator: " › "))
            }
            .font(.subheadline)
        }
    }

    /// Why it waits, then Do now and Open parent as rows of their own (a row with a link in it is one big link).
    @ViewBuilder private func waitingBanner(_ waiting: WaitingLine) -> some View {
        Label(waiting.text, systemImage: "clock").font(.subheadline).foregroundStyle(Theme.color(for: .info))
            .accessibilityIdentifier("detail-waiting")
        if waiting.canBringBack {
            Button { model.bringBack(itemId) } label: { Label("Do now", systemImage: "arrow.uturn.backward") }
        }
        if let parent = waiting.heldBy {
            NavigationLink(value: parent.id) { Label("Open parent", systemImage: "arrow.up.forward.square") }
        }
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
            Text("Repeat")
            TextField("Repeat", text: draftBinding(.repeatRule, current: current),
                      prompt: Text("Doesn’t repeat"))
                .multilineTextAlignment(.trailing)
                .labelsHidden()
                .autocorrectionDisabled()
                .onSubmit { saveRepeat(current) }
                .accessibilityIdentifier("detail-repeat")
            Menu {
                ForEach(repeatChoices, id: \.self) { rule in
                    Button(rule) {
                        model.setDraft(itemId, .repeatRule, rule, current: current)
                        saveRepeat(current)
                    }
                }
                if !current.isEmpty {
                    Divider()
                    Button("Doesn’t repeat") {
                        model.setDraft(itemId, .repeatRule, "", current: current)
                        saveRepeat(current)
                    }
                }
            } label: { Image(systemName: "repeat") }
                .menuStyle(.button)
                .buttonStyle(.borderless)
                .fixedSize()
                .accessibilityLabel("Repeat choices")
        }
        if model.draft(itemId, .repeatRule) != nil {
            Button("Save repeat") { saveRepeat(current) }
        }
        if let repeatProblem = model.repeatProblems[itemId] {
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
                TextField("Add a subtask…", text: model.typedBinding(itemId, .subtask)).onSubmit(addSubtask)
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
                TextField("One step per line", text: model.typedBinding(itemId, .steps), axis: .vertical).lineLimit(3...8)
                Stepper("Hours between the new steps: \(Int(hoursBetween))", value: $hoursBetween, in: 0...168, step: 1)
                Button("Add steps") {
                    model.addSteps(itemId, titles: stepTitles, hoursBetween: hoursBetween)
                    model.setTyped(itemId, .steps, "")
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
            TextField("What happened? What is next?", text: model.draftBinding(for: itemId), axis: .vertical).lineLimit(2...6)
                .accessibilityIdentifier("detail-note-field")
            Button("Add note") {
                // Saved, the box empties (the model clears it; if it can't be saved, what was typed stays).
                model.addNote(itemId, text: note)
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

    /// A box showing what's typed (kept in the model until it's saved), else the task's own value, also after a refresh
    /// from the server.
    private func draftBinding(_ field: DraftField, current: String) -> Binding<String> {
        Binding(get: { model.draft(itemId, field) ?? current }, set: { model.setDraft(itemId, field, $0, current: current) })
    }

    /// Saves what's typed in this task's details (the title and details; a repeat rule, if one is typed, too).
    private func save(_ item: WorkItem) { model.saveDrafts(itemId) }

    private func saveRepeat(_ current: String) {
        guard let typed = model.draft(itemId, .repeatRule) else { return }
        if typed.trimmingCharacters(in: .whitespacesAndNewlines) == current {
            model.setDraft(itemId, .repeatRule, current, current: current)
            return
        }
        model.saveDrafts(itemId)
    }

    private func addSubtask() {
        guard !subtask.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty else { return }
        model.addSubtask(itemId, title: subtask)
        model.setTyped(itemId, .subtask, "")
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
/// A label with its icon after the text (a tag's ✕).
struct TrailingIcon: LabelStyle {
    func makeBody(configuration: Configuration) -> some View {
        HStack(spacing: 4) {
            configuration.title
            configuration.icon.imageScale(.small)
        }
    }
}

/// Move… (the web's Move dialog): pick a group, then on its own or under any open task of it (never the task itself or
/// one of its subtasks); back in its own group, where it is now is picked again.
struct MoveSheet: View {
    @ObservedObject var model: BoardModel
    let itemId: UUID
    @Environment(\.dismiss) private var dismiss
    @State private var groupId: UUID?
    @State private var target: UUID?
    @State private var query = ""
    @State private var started = false

    var body: some View {
        NavigationStack {
            if let item = model.board.find(itemId) {
                let group = groupId ?? item.groupId
                Form {
                    Picker("Group", selection: Binding(get: { group }, set: { new in
                        groupId = new
                        target = new == item.groupId ? item.parent?.id : nil
                    })) {
                        ForEach(model.groups) { Text($0.name).tag($0.id) }
                    }
                    Section("Where it goes") {
                        TextField("Find a task to put it under…", text: $query)
                            .autocorrectionDisabled()
                        ForEach(model.moveTargets(itemId, groupId: group, query: query)) { place in
                            Button { target = place.parentId } label: {
                                HStack {
                                    if place.parentId == nil {
                                        Text("On its own").fontWeight(.semibold) + Text("  (a top-level task)").foregroundStyle(.secondary)
                                    } else {
                                        Text(place.depth > 0 ? "↳ " : "").foregroundStyle(.secondary) + Text(place.title)
                                    }
                                    Spacer()
                                    if target == place.parentId { Image(systemName: "checkmark").foregroundStyle(Theme.accent) }
                                }
                                .padding(.leading, CGFloat(place.depth) * 14)
                                .contentShape(Rectangle())
                            }
                            .buttonStyle(.plain)
                            .accessibilityAddTraits(target == place.parentId ? .isSelected : [])
                        }
                    }
                }
                .formStyle(.grouped)
                .navigationTitle("Move “\(item.title)”")
                #if os(iOS)
                .navigationBarTitleDisplayMode(.inline)
                #endif
                .toolbar {
                    ToolbarItem(placement: .cancellationAction) { Button("Cancel") { dismiss() } }
                    ToolbarItem(placement: .confirmationAction) {
                        Button("Move") {
                            model.move(itemId, groupId: group, under: target)
                            dismiss()
                        }
                    }
                }
                .onAppear {
                    guard !started else { return }
                    started = true
                    target = item.parent?.id
                }
            } else {
                Text("This task isn’t here any more.").padding()
            }
        }
        #if os(macOS)
        .frame(minWidth: 420, minHeight: 480)
        #endif
    }
}
#endif
