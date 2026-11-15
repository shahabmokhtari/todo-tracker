import SwiftUI
import OneThingKit
import OneThingTV

/// The Board to look at: Inbox, Next, Doing and Done side by side, every group together. Cards open their details;
/// nothing moves from here (the phone, the computer or an agent does that).
struct TVBoardView: View {
    @ObservedObject var model: BoardModel
    @State private var open: TVOpenTask?

    var body: some View {
        let columns = TVBoard.columns(model.board, now: model.now)
        HStack(alignment: .top, spacing: 36) {
            ForEach(columns) { column in
                VStack(alignment: .leading, spacing: 12) {
                    HStack(alignment: .firstTextBaseline, spacing: 14) {
                        Text(column.title).font(.system(size: 38, weight: .bold))
                        Text("\(column.cards.count)").font(.system(size: 30, weight: .semibold)).foregroundStyle(.white.opacity(0.45))
                    }
                    .padding(.leading, 8)
                    if column.cards.isEmpty {
                        Text("Nothing here")
                            .font(.system(size: 28))
                            .foregroundStyle(.white.opacity(0.35))
                            .padding(.top, 20)
                            .padding(.leading, 8)
                    }
                    ScrollView(.vertical) {
                        LazyVStack(spacing: 32) {
                            ForEach(column.cards) { card in
                                Button { open = TVOpenTask(id: card.id) } label: {
                                    TVCardView(card: card, width: nil, titleSize: 30)
                                }
                                .buttonStyle(.card)
                            }
                        }
                        .padding(.vertical, 24)
                        .padding(.horizontal, 6)
                    }
                    .scrollClipDisabled()
                }
                .frame(maxWidth: .infinity, alignment: .topLeading)
            }
        }
        .fullScreenCover(item: $open) { task in
            TVDetailsView(model: model, id: task.id, canMarkDone: false)
        }
    }
}
