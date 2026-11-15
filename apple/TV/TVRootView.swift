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
    /// When Play/Pause was last handled: the Now screen and the tabs may both hear one press.
    @State private var lastPlayPause = Date.distantPast

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
                // Focus on the Now screen (the task, Up next): the command stops here.
                .onPlayPauseCommand(perform: playPause)
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
        // Focus on the tab bar (where it goes when the Now screen has nothing to select, like a focus session running
        // after its task was done): the tabs handle it, while Now is showing.
        .onPlayPauseCommand { if tab == .now { playPause() } }
    }

    /// Off: a focus session starts on the task shown. Running: it pauses. Paused: it goes on.
    private func playPause() {
        let pressed = Date()
        guard pressed.timeIntervalSince(lastPlayPause) > 0.3 else { return }
        lastPlayPause = pressed
        let hero = TVScreen.build(model.board, now: model.now).hero?.id
        switch TVRemote.playPause(model.board.pomodoro, hero: hero) {
        case .start(let id): model.startFocus(id)
        case .pause: model.pausePomodoro()
        case .resume: model.resumePomodoro()
        }
    }
}

/// A task to open in its details (a sheet).
struct TVOpenTask: Identifiable, Equatable {
    let id: UUID
}
