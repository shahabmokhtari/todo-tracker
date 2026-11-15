import Foundation
import OneThingKit

/// Where the Apple TV keeps its tasks: only a cache (tvOS has no lasting storage for apps and may empty it when space
/// runs low). The tasks live on the other devices; the TV syncs them in. If tvOS emptied the tasks folder, what sync
/// remembered about it goes too, so the next sync pulls everything first and never tells the others the tasks were
/// deleted.
public enum TVStorage {
    public struct Folders: Equatable, Sendable {
        /// The TV's copy of the tasks folder.
        public let vault: URL
        /// What sync remembers (this device's id, what it agreed on with the others).
        public let sync: URL
        /// tvOS had emptied the tasks folder: sync starts over (it joins again, pulling first).
        public let wasEmptied: Bool
    }

    /// `Library/Caches/OneThing` on the TV.
    public static func defaultBase() -> URL? {
        try? FileManager.default.url(for: .cachesDirectory, in: .userDomainMask, appropriateFor: nil, create: true)
            .appendingPathComponent("OneThing", isDirectory: true)
    }

    /// The folders under `base`, made safe to open: sync state left from a tasks folder that's gone is cleared.
    public static func prepare(base: URL) -> Folders {
        let fm = FileManager.default
        let vault = base.appendingPathComponent("vault", isDirectory: true)
        let sync = base.appendingPathComponent("sync", isDirectory: true)
        let hasTasks = fm.fileExists(atPath: vault.appendingPathComponent(VaultFiles.metaFolder).appendingPathComponent("config.json").path)
        var emptied = false
        if !hasTasks, fm.fileExists(atPath: sync.path) {
            // Only what sync remembered goes (never tasks): the folder it described isn't there any more.
            try? fm.removeItem(at: sync)
            emptied = true
        }
        try? fm.createDirectory(at: base, withIntermediateDirectories: true)
        return Folders(vault: vault, sync: sync, wasEmptied: emptied)
    }
}

/// Setting up sync on the TV: the same private gist the phones use, with a GitHub token typed (or pasted from an
/// iPhone's keyboard) on the TV.
public enum TVPairing {
    public enum Check: Equatable, Sendable {
        case ok(String)
        case problem(String)
    }

    /// The token as it will be kept, or what's wrong with it.
    public static func check(token: String) -> Check {
        let clean = token.trimmingCharacters(in: .whitespacesAndNewlines)
        guard !clean.isEmpty else { return .problem("Type or paste your GitHub token first.") }
        let prefixes = ["ghp_", "github_pat_", "gho_", "ghu_"]
        let allowed = CharacterSet.alphanumerics.union(CharacterSet(charactersIn: "_"))
        guard prefixes.contains(where: clean.hasPrefix), clean.count >= 20,
              clean.unicodeScalars.allSatisfy(allowed.contains) else {
            return .problem("That doesn’t look like a GitHub token. It starts with ghp_ or github_pat_.")
        }
        return .ok(clean)
    }

    /// How the other devices list this TV when syncing (its name in the TV's settings).
    public static func deviceName(_ name: String) -> String {
        let clean = name.trimmingCharacters(in: .whitespacesAndNewlines)
        return clean.isEmpty ? "Apple TV" : clean
    }

    /// Steps on the Sync screen, in order.
    public static let steps = [
        "On your phone or computer, scan the code to make a GitHub token that can only use gists (or use the one your phone already syncs with).",
        "Select GitHub token below and type or paste it. A nearby iPhone offers its keyboard, so you can paste.",
        "Use the same library name as your other devices (OneThing unless you changed it).",
    ]
}
