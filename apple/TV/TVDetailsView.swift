import SwiftUI
import OneThingKit
import OneThingTV

/// A task's details, to read from the sofa: its group, when it's due, its description, steps, reminders and notes.
/// Menu closes it. From Up next it can be marked done (after asking).
struct TVDetailsView: View {
    @ObservedObject var model: BoardModel
    let id: UUID
    let canMarkDone: Bool
    @Environment(\.dismiss) private var dismiss
    @State private var confirming = false

    var body: some View {
        ZStack {
            TVBackdrop()
            if let item = model.board.find(id) {
                let details = TVDetails(item, board: model.board, now: model.now)
                content(details)
            } else {
                Text("This task isn’t here any more.").font(.system(size: 40))
            }
        }
        // Menu on the remote goes back.
        .onExitCommand { dismiss() }
    }

    private func content(_ details: TVDetails) -> some View {
        HStack(alignment: .top, spacing: 80) {
            VStack(alignment: .leading, spacing: 28) {
                if let group = details.card.group {
                    HStack(spacing: 14) {
                        Circle().fill(Color(tvHex: details.card.groupColor) ?? TVBrand.violet).frame(width: 22, height: 22)
                        Text(group.uppercased()).font(.system(size: 26, weight: .bold)).tracking(3).foregroundStyle(.white.opacity(0.7))
                    }
                }
                if let parent = details.card.parentTitle {
                    Text(parent).font(.system(size: 32)).foregroundStyle(.white.opacity(0.6))
                }
                Text(details.card.title)
                    .font(.system(size: 72, weight: .bold, design: .rounded))
                    .lineLimit(3)
                    .minimumScaleFactor(0.6)
                HStack(spacing: 18) {
                    if details.card.isDone { TVChip(text: "Done", systemImage: "checkmark.circle.fill", tint: Color(tvHex: "#34d399")!) }
                    if let when = details.card.when { TVChip(text: when.text, systemImage: when.symbol, tint: when.tint) }
                    if let progress = details.card.progress { TVChip(text: progress, systemImage: "checklist") }
                }
                if let text = details.details {
                    Text(text)
                        .font(.system(size: 32))
                        .foregroundStyle(.white.opacity(0.85))
                        .lineLimit(8)
                }
                Spacer(minLength: 0)
                HStack(spacing: 40) {
                    if canMarkDone, !details.card.isDone {
                        Button { confirming = true } label: { Label("Mark done", systemImage: "checkmark") }
                    }
                    Button("Close") { dismiss() }
                }
                .font(.system(size: 30, weight: .semibold))
            }
            .frame(maxWidth: .infinity, alignment: .leading)

            if !details.steps.isEmpty || !details.notes.isEmpty || !details.reminders.isEmpty {
                ScrollView(.vertical) {
                    VStack(alignment: .leading, spacing: 18) {
                        if !details.steps.isEmpty {
                            heading("Steps")
                            ForEach(details.steps) { step in
                                Label {
                                    Text(step.title).strikethrough(step.isDone, color: .white.opacity(0.4))
                                        .foregroundStyle(step.isDone ? .white.opacity(0.5) : .white)
                                } icon: {
                                    Image(systemName: step.isDone ? "checkmark.circle.fill" : "circle")
                                        .foregroundStyle(step.isDone ? AnyShapeStyle(TVBrand.gradient) : AnyShapeStyle(.white.opacity(0.5)))
                                }
                                .font(.system(size: 30))
                                .padding(.leading, CGFloat(step.depth) * 40)
                            }
                        }
                        if !details.reminders.isEmpty {
                            heading("Reminders")
                            ForEach(details.reminders, id: \.self) { Label($0, systemImage: "bell").font(.system(size: 28)) }
                        }
                        if !details.notes.isEmpty {
                            heading("Notes")
                            ForEach(details.notes) { note in
                                VStack(alignment: .leading, spacing: 6) {
                                    Text(note.text).font(.system(size: 28)).lineLimit(4)
                                    Text(note.meta).font(.system(size: 22)).foregroundStyle(.white.opacity(0.5))
                                }
                                .padding(.vertical, 6)
                            }
                        }
                    }
                    .frame(maxWidth: .infinity, alignment: .leading)
                    // Long lists scroll with the remote.
                    .focusable()
                }
                .frame(width: 720)
            }
        }
        .padding(.horizontal, 40)
        .alert("Mark “\(details.card.title)” done?", isPresented: $confirming) {
            Button("Mark done") {
                model.complete(id)
                dismiss()
            }
            Button("Not yet", role: .cancel) {}
        }
    }

    private func heading(_ text: String) -> some View {
        Text(text.uppercased())
            .font(.system(size: 22, weight: .bold))
            .tracking(3)
            .foregroundStyle(.white.opacity(0.5))
            .padding(.top, 14)
    }
}
