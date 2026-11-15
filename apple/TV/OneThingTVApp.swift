import SwiftUI
import UIKit
import OneThingKit
import OneThingTV

@main
struct OneThingTVApp: App {
    @StateObject private var model = TVLaunch.makeModel()
    @Environment(\.scenePhase) private var scenePhase

    var body: some Scene {
        WindowGroup {
            TVRootView(model: model, isDemo: TVLaunch.isDemo, start: TVLaunch.startTab)
                .preferredColorScheme(.dark)
                // Back on screen: a sync runs now (then every minute while it's open).
                .onChange(of: scenePhase) { _, phase in
                    if phase == .active { model.becameActive() }
                    if phase == .background { model.wentToBackground() }
                }
        }
    }
}

/// How the TV opens its tasks: a copy kept in the cache, synced with the other devices through the same private gist
/// the phones use. A debug build started with ONETHING_DEMO=1 shows a made-up board kept in memory instead
/// (screenshots), and ONETHING_TAB=board (done, sync) opens on that tab.
@MainActor
enum TVLaunch {
    static var isDemo: Bool {
        #if DEBUG
        return ProcessInfo.processInfo.environment["ONETHING_DEMO"] == "1"
        #else
        return false
        #endif
    }

    static var startTab: TVTab {
        #if DEBUG
        return ProcessInfo.processInfo.environment["ONETHING_TAB"].flatMap(TVTab.init(rawValue:)) ?? .now
        #else
        return .now
        #endif
    }

    static func makeModel() -> BoardModel {
        #if DEBUG
        if isDemo { return DemoBoard.model() }
        #endif
        return live()
    }

    /// The TV's cache (Library/Caches/OneThing): its copy of the tasks folder and what sync remembers.
    static func live() -> BoardModel {
        let name = TVPairing.deviceName(UIDevice.current.name)
        let phone = PhoneSetup(phoneName: name, deviceKind: "Apple TV")
        let settings = SyncSettingsStore(secrets: KeychainSecretStore())
        guard let base = TVStorage.defaultBase() else {
            return BoardModel(store: nil, phone: phone, openError: "Couldn't open this Apple TV's storage. Restart the app to try again.")
        }
        let folders = TVStorage.prepare(base: base)
        if folders.wasEmptied, var setup = settings.load() {
            // tvOS emptied the cache: the TV joins again (everything comes in before anything goes out).
            setup.joined = false
            settings.save(setup)
        }
        do {
            let vault = try VaultStore(root: folders.vault)
            let syncer = PhoneSyncer(store: vault, directory: folders.sync, settings: settings, deviceName: name)
            let sync = SyncModel(syncer: syncer, settings: settings)
            let model = BoardModel(store: VaultBoardStore(vault), phone: phone, sync: sync)
            sync.start()
            return model
        } catch {
            let why = (error as? LocalizedError)?.errorDescription ?? error.localizedDescription
            return BoardModel(store: nil, phone: phone, openError: "Couldn't open the tasks on this Apple TV (\(why)). Restart the app to try again.")
        }
    }
}
