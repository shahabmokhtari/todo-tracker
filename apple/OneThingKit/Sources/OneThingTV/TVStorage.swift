import Foundation
import OneThingKit

/// Where the Apple TV keeps its tasks: only a cache (tvOS has no lasting storage for apps and may empty it when space
/// runs low). The tasks live on the other devices; the TV syncs them in. If tvOS emptied the tasks folder (or only
/// some of its files), what sync remembered about it goes too, so the next sync pulls everything first and never tells
/// the others the tasks were deleted. The TV's device id is kept outside the cache, so it always rejoins as itself.
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

    /// Where the TV's device id is kept outside the cache (the Keychain on the TV).
    static let deviceIdKey = "tv-sync-device-id"

    /// The folders under `base`, made safe to open: sync state left from a tasks folder that's gone is cleared, and
    /// the TV's device id (kept in `identity`, which tvOS never empties) is put back. With the same id, the TV's old
    /// copy in the gist is its own and is simply replaced; a new id would leave it there as a stranger with no common
    /// base, and merging against it would undo edits, bring deleted tasks back and make "(from …)" copies.
    public static func prepare(base: URL, identity: SecretStore) -> Folders {
        let fm = FileManager.default
        let vault = base.appendingPathComponent("vault", isDirectory: true)
        let sync = base.appendingPathComponent("sync", isDirectory: true)
        let hasTasks = fm.fileExists(atPath: vault.appendingPathComponent(VaultFiles.metaFolder).appendingPathComponent("config.json").path)
        var emptied = false
        if !hasTasks, fm.fileExists(atPath: sync.path) {
            // Only what sync remembered goes (never tasks): the folder it described isn't there any more.
            forgetSyncState(sync)
            emptied = true
        }
        try? fm.createDirectory(at: base, withIntermediateDirectories: true)
        keepDeviceId(sync, identity)
        return Folders(vault: vault, sync: sync, wasEmptied: emptied)
    }

    /// Once the TV's copy of the tasks is open: if a file sync last sent the others is missing (tvOS emptied only part
    /// of the cache), what sync remembered goes, so the TV joins again (pulling first) instead of sending that file
    /// as deleted. Returns whether it did.
    public static func forgetIfTasksMissing(_ folders: Folders, vault: VaultStore) -> Bool {
        let fm = FileManager.default
        let places = ((try? fm.contentsOfDirectory(at: folders.sync, includingPropertiesForKeys: [.isDirectoryKey])) ?? [])
            .filter { (try? $0.resourceValues(forKeys: [.isDirectoryKey]).isDirectory) == true }
        let sent = places.flatMap { place -> [SyncEntry] in
            (try? SyncState(directory: place, deviceDirectory: folders.sync))?.publishedEntries ?? []
        }
        guard !sent.isEmpty else { return false }
        // Can't be listed right now (a broken file): sync itself waits, nothing to decide here.
        guard let here = try? vault.readSync().entries else { return false }
        let missing = sent.contains { entry in
            switch SyncKeys.kind(of: entry.key) {
            case .task, .rich, .file, .config: return here[OrdinalString(entry.key)] == nil
            default: return false
            }
        }
        guard missing else { return false }
        forgetSyncState(folders.sync)
        return true
    }

    /// Clears what sync remembered about every place, keeping this device's id.
    private static func forgetSyncState(_ sync: URL) {
        let fm = FileManager.default
        for name in (try? fm.contentsOfDirectory(atPath: sync.path)) ?? [] where name != "device.json" {
            try? fm.removeItem(at: sync.appendingPathComponent(name))
        }
    }

    /// `sync/device.json` and the id kept outside the cache agree: the file (from an earlier version, or this one)
    /// is remembered, and one tvOS removed is written again from what was remembered.
    private static func keepDeviceId(_ sync: URL, _ identity: SecretStore) {
        let file = sync.appendingPathComponent("device.json")
        let onDisk = (try? Data(contentsOf: file))
            .flatMap { try? JSONSerialization.jsonObject(with: $0) as? [String: Any] }
            .flatMap { $0["id"] as? String }
            .flatMap(validId)
        if let onDisk {
            if identity.read(deviceIdKey) != onDisk { identity.write(deviceIdKey, onDisk) }
            return
        }
        // A damaged file is left alone: sync pauses and says so rather than this device joining as someone else.
        guard !FileManager.default.fileExists(atPath: file.path) else { return }
        let id = identity.read(deviceIdKey).flatMap(validId) ?? UUID().uuidString.replacingOccurrences(of: "-", with: "").lowercased()
        guard let data = try? JSONSerialization.data(withJSONObject: ["id": id, "name": "Apple TV"]) else { return }
        try? FileManager.default.createDirectory(at: sync, withIntermediateDirectories: true)
        guard (try? data.write(to: file, options: .atomic)) != nil else { return }
        if identity.read(deviceIdKey) != id { identity.write(deviceIdKey, id) }
    }

    private static func validId(_ id: String) -> String? {
        (2...64).contains(id.count) && id.allSatisfy { $0.isASCII && ($0.isLetter || $0.isNumber) } ? id : nil
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
