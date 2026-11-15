import SwiftUI
import OneThingKit
import OneThingTV

/// The tabs along the top: Now, Board, Done and Sync.
enum TVTab: String, Hashable {
    case now, board, done, sync
}

struct TVRootView: View {
    @ObservedObject var model: BoardModel
    let isDemo: Bool
    @State private var tab: TVTab

    init(model: BoardModel, isDemo: Bool, start: TVTab = .now) {
        self.model = model
        self.isDemo = isDemo
        _tab = State(initialValue: start)
    }

    /// Not set up to sync yet (and not the made-up board): the Now screen offers it.
    private var needsSetup: Bool { !isDemo && model.sync?.isOn != true }

    var body: some View {
        TabView(selection: $tab) {
            TVNowView(model: model, needsSetup: needsSetup) { tab = .sync }
                .frame(maxWidth: .infinity, maxHeight: .infinity, alignment: .top)
                .background(TVBackdrop())
                .tabItem { Text("Now") }
                .tag(TVTab.now)
            TVBoardView(model: model)
                .frame(maxWidth: .infinity, maxHeight: .infinity, alignment: .top)
                .background(TVBackdrop())
                .tabItem { Text("Board") }
                .tag(TVTab.board)
            TVDoneView(model: model)
                .frame(maxWidth: .infinity, maxHeight: .infinity, alignment: .top)
                .background(TVBackdrop())
                .tabItem { Text("Done") }
                .tag(TVTab.done)
            TVSyncView(model: model, isDemo: isDemo)
                .frame(maxWidth: .infinity, maxHeight: .infinity, alignment: .top)
                .background(TVBackdrop())
                .tabItem { Text("Sync") }
                .tag(TVTab.sync)
        }
        .background(TVBackdrop())
    }
}

/// A task to open in its details (a sheet).
struct TVOpenTask: Identifiable, Equatable {
    let id: UUID
}
