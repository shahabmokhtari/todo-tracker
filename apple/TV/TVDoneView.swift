import SwiftUI
import OneThingKit
import OneThingTV

/// What got done: today's count, big, with a cheer, and the tasks themselves (today, then the last two weeks).
struct TVDoneView: View {
    @ObservedObject var model: BoardModel
    @State private var open: TVOpenTask?

    var body: some View {
        let screen = TVScreen.build(model.board, now: model.now)
        let today = Set(screen.done.map(\.id))
        let recent = (TVBoard.columns(model.board, now: model.now).last?.cards ?? []).filter { !today.contains($0.id) }
        HStack(alignment: .top, spacing: 80) {
            VStack(alignment: .leading, spacing: 20) {
                Text("Done today").font(.system(size: 36, weight: .semibold)).foregroundStyle(.white.opacity(0.7))
                Text("\(screen.doneToday)")
                    .font(.system(size: 260, weight: .heavy, design: .rounded))
                    .foregroundStyle(TVBrand.gradient)
                    .monospacedDigit()
                    .contentTransition(.numericText())
                Text(screen.cheer ?? "Nothing yet today. The first one is the hardest, and it’s right there on Now.")
                    .font(.system(size: 36, weight: .medium))
                    .foregroundStyle(.white.opacity(0.8))
                    .fixedSize(horizontal: false, vertical: true)
            }
            .frame(width: 620, alignment: .leading)
            .padding(.top, 20)

            ScrollView(.vertical) {
                LazyVStack(alignment: .leading, spacing: 24) {
                    if !screen.done.isEmpty {
                        section("Today")
                        ForEach(screen.done) { card in row(card) }
                    }
                    if !recent.isEmpty {
                        section("Last two weeks")
                        ForEach(recent) { card in row(card) }
                    }
                    if screen.done.isEmpty && recent.isEmpty {
                        Text("Finished tasks show up here.")
                            .font(.system(size: 30))
                            .foregroundStyle(.white.opacity(0.45))
                    }
                }
                .padding(.vertical, 24)
                .padding(.horizontal, 6)
            }
            .scrollClipDisabled()
        }
        .fullScreenCover(item: $open) { task in
            TVDetailsView(model: model, id: task.id, canMarkDone: false)
        }
    }

    private func section(_ title: String) -> some View {
        Text(title.uppercased())
            .font(.system(size: 24, weight: .bold))
            .tracking(3)
            .foregroundStyle(.white.opacity(0.5))
            .padding(.top, 12)
    }

    private func row(_ card: TVCard) -> some View {
        Button { open = TVOpenTask(id: card.id) } label: {
            HStack(spacing: 24) {
                Image(systemName: "checkmark.circle.fill")
                    .font(.system(size: 40))
                    .foregroundStyle(TVBrand.gradient)
                VStack(alignment: .leading, spacing: 4) {
                    if let parent = card.parentTitle {
                        Text(parent).font(.system(size: 22)).foregroundStyle(.white.opacity(0.5)).lineLimit(1)
                    }
                    Text(card.title).font(.system(size: 34, weight: .medium)).lineLimit(1)
                }
                Spacer(minLength: 0)
                if let group = card.group { Text(group).font(.system(size: 24)).foregroundStyle(.white.opacity(0.5)) }
            }
            .padding(.horizontal, 32)
            .padding(.vertical, 22)
            .frame(maxWidth: .infinity, alignment: .leading)
            .background(TVBrand.card)
        }
        .buttonStyle(.card)
    }
}
