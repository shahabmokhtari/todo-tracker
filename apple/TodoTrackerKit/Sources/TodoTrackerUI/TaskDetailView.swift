#if canImport(SwiftUI)
import SwiftUI
import TodoTrackerKit

/// Task details: subtasks, gated rollout steps, reminders, notes, and a mini timeline.
struct TaskDetailView: View {
    @ObservedObject var model: BoardModel
    let itemId: UUID
    @Environment(\.dismiss) private var dismiss
    @State private var note = ""
    @State private var subtask = ""
    @State private var steps = ""
    @State private var hoursBetween = 24.0
    @State private var confirmDelete = false

    var body: some View {
        NavigationStack {
            if let item = model.board.find(itemId) {
                Form {
                    Section {
                        Text(item.title).font(.title3).fontWeight(.semibold)
                        if item.path.count > 1 { Text(item.path.dropLast().joined(separator: " › ")).font(.caption).foregroundStyle(.secondary) }
                        if let details = item.details { Text(details) }
                        LabeledContent("Priority", value: item.priority.label)
                        LabeledContent("State", value: Agenda.state(of: item, now: model.now).rawValue)
                        if let next = item.nextActionAt { LabeledContent("Next action", value: RelativeTime.format(next, now: model.now)) }
                        if let deadline = item.deadline { LabeledContent("Deadline", value: RelativeTime.format(deadline, now: model.now)) }
                    }

                    Section(item.sequential ? "Steps (in order)" : "Subtasks") {
                        ForEach(item.children) { child in
                            HStack {
                                Image(systemName: child.isDone ? "checkmark.circle.fill" : "circle")
                                Text(child.title).strikethrough(child.isDone)
                                Spacer()
                                Text(Agenda.state(of: child, now: model.now).rawValue).font(.caption).foregroundStyle(.secondary)
                            }
                        }
                        HStack {
                            TextField("Add a subtask…", text: $subtask).onSubmit(addSubtask)
                            Button("Add", action: addSubtask)
                        }
                    }

                    Section("Rollout steps") {
                        TextField("One step per line", text: $steps, axis: .vertical).lineLimit(3...8)
                        Stepper("Hours between steps: \(Int(hoursBetween))", value: $hoursBetween, in: 0...168, step: 1)
                        Button("Add steps") {
                            let titles = steps.split(separator: "\n").map(String.init)
                            model.addSteps(itemId, titles: titles, hoursBetween: hoursBetween)
                            steps = ""
                        }
                    }

                    Section("Notes") {
                        TextField("What happened? What is next?", text: $note, axis: .vertical).lineLimit(2...6)
                        Button("Add note") {
                            model.addNote(itemId, text: note)
                            note = ""
                        }
                        ForEach(item.notes.reversed()) { n in
                            VStack(alignment: .leading) {
                                Text(n.text)
                                Text("\(n.author.displayName) · \(RelativeTime.format(n.at, now: model.now))").font(.caption).foregroundStyle(.secondary)
                            }
                        }
                    }

                    Section("Timeline") {
                        ForEach(Array(((try? model.board.timeline(itemId)) ?? []).prefix(20).enumerated()), id: \.offset) { _, entry in
                            VStack(alignment: .leading) {
                                Text(entry.summary)
                                Text("\(entry.actor.displayName) · \(RelativeTime.format(entry.at, now: model.now))").font(.caption).foregroundStyle(.secondary)
                            }
                        }
                    }

                    Section {
                        if item.isDone {
                            Button("Reopen") { model.reopen(itemId) }
                        } else if Agenda.state(of: item, now: model.now) != .locked {
                            Button("Mark done") {
                                model.complete(itemId)
                                dismiss()
                            }
                        }
                        Button("Delete", role: .destructive) { confirmDelete = true }
                    }
                }
                .navigationTitle(item.title)
                .toolbar {
                    ToolbarItem(placement: .confirmationAction) { Button("Done") { dismiss() } }
                }
                .confirmationDialog("Delete \"\(item.title)\" and its subtasks?", isPresented: $confirmDelete) {
                    Button("Delete", role: .destructive) {
                        model.delete(itemId)
                        dismiss()
                    }
                }
            } else {
                Text("This task no longer exists.").padding()
            }
        }
        #if os(macOS)
        .frame(minWidth: 420, minHeight: 520)
        #endif
    }

    private func addSubtask() {
        guard !subtask.trimmingCharacters(in: .whitespaces).isEmpty else { return }
        model.addSubtask(itemId, title: subtask)
        subtask = ""
    }
}
#endif
