import Foundation

/// The command line that ships inside the Mac app (`onething`, and `ot`, its short name, in
/// `Contents/Resources/Server/cli`), and putting it on the PATH: links to both in `~/.local/bin` (no admin rights).
///
/// Only the person installs it (Settings › Command line tool, or One Thing › Install Command Line Tool…). A file there
/// that isn't a link to a One Thing command line is never replaced without asking, and then it's kept beside it
/// (`ot.before-onething`), not deleted. A link into an app that moved or was deleted shows as broken; installing again
/// repairs it. Pure file logic (tests run it on a temporary home folder); the views are in OneThingUI.
public enum CommandLineTool {
    /// Both names, in the order they're shown.
    public static let names = ["onething", "ot"]

    /// What's at `~/.local/bin/<name>` now.
    public enum LinkState: Equatable, Sendable {
        /// Nothing there.
        case missing
        /// A link to the command line in this app.
        case current
        /// A link to a One Thing command line elsewhere (another copy of the app, an older place).
        case otherOneThing(target: String)
        /// A link to a One Thing command line that isn't there any more (the app moved or was deleted).
        case broken(target: String)
        /// Something else: a file of its own, or a link to another program. Replaced only when the person says so.
        case foreign(description: String)

        /// Installing replaces it without asking (it's ours, or nothing).
        public var replaceable: Bool {
            switch self {
            case .missing, .current, .otherOneThing, .broken: return true
            case .foreign: return false
            }
        }
    }

    /// One name: its link, and what running it in a terminal finds.
    public struct Status: Equatable, Sendable {
        public let name: String
        /// `~/.local/bin/<name>`.
        public let link: String
        public let state: LinkState
        /// The file a terminal runs for this name (the first one on the PATH), with links followed; nil: none.
        public let onPath: String?
        /// What a terminal runs is this app's command line.
        public let runsThisApp: Bool
    }

    /// Everything the Settings section shows.
    public struct Report: Equatable, Sendable {
        /// The command line in this app (nil: this copy has none, e.g. a build without it).
        public let bundled: String?
        public let binDirectory: String
        /// `~/.local/bin` is on the PATH a terminal uses.
        public let binOnPath: Bool
        public let statuses: [Status]

        /// Both links point at this app, and a terminal finds them.
        public var installed: Bool { statuses.allSatisfy { $0.state == .current } && binOnPath }

        /// Some link points at an app that isn't there any more.
        public var hasBrokenLink: Bool { statuses.contains { if case .broken = $0.state { return true } else { return false } } }

        /// Names whose file Install would only replace with the person's say-so.
        public var needsAsking: [Status] { statuses.filter { !$0.state.replaceable } }
    }

    /// The command line inside an app bundle (`Contents/Resources/Server/cli/<name>`), if it's there.
    public static func bundled(in resources: URL?, name: String = "onething", fileManager: FileManager = .default) -> URL? {
        guard let path = resources?.appendingPathComponent("Server/cli/\(name)"), fileManager.isExecutableFile(atPath: path.path) else { return nil }
        return path
    }

    public static func binDirectory(home: URL) -> URL { home.appendingPathComponent(".local/bin", isDirectory: true) }

    /// What's installed, against the command line at `bundled` (`Contents/Resources/Server/cli/onething`), for the
    /// PATH a terminal has (`path`, as `echo $PATH` prints it).
    public static func report(home: URL, bundled: URL?, path: String, fileManager: FileManager = .default) -> Report {
        let bin = binDirectory(home: home)
        let folders = path.split(separator: ":").map { expand(String($0), home: home) }
        let binOnPath = folders.contains { standard($0) == standard(bin.path) }
        let statuses = names.map { name -> Status in
            let link = bin.appendingPathComponent(name).path
            let state = linkState(at: link, expected: bundled.map { target(for: name, bundled: $0) }, fileManager: fileManager)
            let found = folders.lazy.map { ($0 as NSString).appendingPathComponent(name) }.first { fileManager.isExecutableFile(atPath: $0) }
            let resolved = found.map { URL(fileURLWithPath: $0).resolvingSymlinksInPath().path }
            let runsThisApp = resolved != nil && bundled != nil && resolved == bundled!.resolvingSymlinksInPath().path
            return Status(name: name, link: link, state: state, onPath: resolved, runsThisApp: runsThisApp)
        }
        return Report(bundled: bundled?.path, binDirectory: bin.path, binOnPath: binOnPath, statuses: statuses)
    }

    /// What a link for `name` points at: the bundled `onething`, or the `ot` next to it.
    static func target(for name: String, bundled: URL) -> String {
        bundled.deletingLastPathComponent().appendingPathComponent(name).path
    }

    static func linkState(at link: String, expected: String?, fileManager: FileManager) -> LinkState {
        guard let destination = try? fileManager.destinationOfSymbolicLink(atPath: link) else {
            // Not a link: a file (or folder) of its own, or nothing.
            return (try? fileManager.attributesOfItem(atPath: link)) == nil ? .missing : .foreign(description: "a file of its own")
        }
        let absolute = destination.hasPrefix("/") ? destination : ((link as NSString).deletingLastPathComponent as NSString).appendingPathComponent(destination)
        if let expected, standard(absolute) == standard(expected) {
            return fileManager.fileExists(atPath: absolute) ? .current : .broken(target: absolute)
        }
        guard isOneThingCli(absolute) else { return .foreign(description: "a link to \(absolute)") }
        return fileManager.fileExists(atPath: absolute) ? .otherOneThing(target: absolute) : .broken(target: absolute)
    }

    /// A One Thing command line: the one in a Mac app (`….app/Contents/Resources/Server/cli/onething` or `ot`), the
    /// Linux app's, or the .NET tool's (which installs `onething` only: an `ot` there is another tool).
    static func isOneThingCli(_ path: String) -> Bool {
        let name = (path as NSString).lastPathComponent
        guard names.contains(name) else { return false }
        if path.contains("/.dotnet/tools/") { return name == "onething" }
        return path.contains("/Contents/Resources/Server/cli/") || path.contains("/onething/app/")
    }

    /// What Install did for one name.
    public enum Outcome: Equatable, Sendable {
        case linked(name: String)
        /// The file that was there is kept under this name.
        case linkedKeeping(name: String, keptAs: String)
        /// Not replaced: not ours, and the person wasn't asked (or said no).
        case skipped(name: String, reason: String)
    }

    /// Links both names into `~/.local/bin` (making it if needed). A link that's ours (or broken) is replaced; anything
    /// else only when its name is in `replacing` (the person said yes), and then it's kept as `<name>.before-onething`.
    @discardableResult
    public static func install(home: URL, bundled: URL, replacing: Set<String> = [], fileManager: FileManager = .default) throws -> [Outcome] {
        let bin = binDirectory(home: home)
        try fileManager.createDirectory(at: bin, withIntermediateDirectories: true)
        return try names.map { name in
            let link = bin.appendingPathComponent(name).path
            let state = linkState(at: link, expected: target(for: name, bundled: bundled), fileManager: fileManager)
            var kept: String?
            switch state {
            case .current:
                return .linked(name: name)
            case .missing:
                break
            case .otherOneThing, .broken:
                try fileManager.removeItem(atPath: link)
            case .foreign(let description):
                guard replacing.contains(name) else { return .skipped(name: name, reason: "\(link) is \(description)") }
                let aside = freeName(for: link + ".before-onething", fileManager: fileManager)
                try fileManager.moveItem(atPath: link, toPath: aside)
                kept = aside
            }
            try fileManager.createSymbolicLink(atPath: link, withDestinationPath: target(for: name, bundled: bundled))
            return kept.map { .linkedKeeping(name: name, keptAs: $0) } ?? .linked(name: name)
        }
    }

    /// The line to add to the shell's profile when `~/.local/bin` isn't on the PATH (zsh, bash, fish).
    public static func profileLine(shell: String?) -> String {
        switch (shell as NSString?)?.lastPathComponent {
        case "fish": return "fish_add_path ~/.local/bin"
        case "bash": return "echo 'export PATH=\"$HOME/.local/bin:$PATH\"' >> ~/.bash_profile"
        default: return "echo 'export PATH=\"$HOME/.local/bin:$PATH\"' >> ~/.zshrc"
        }
    }

    private static func freeName(for path: String, fileManager: FileManager) -> String {
        var candidate = path
        var n = 2
        while (try? fileManager.attributesOfItem(atPath: candidate)) != nil {
            candidate = "\(path)-\(n)"
            n += 1
        }
        return candidate
    }

    private static func expand(_ folder: String, home: URL) -> String {
        if folder == "~" { return home.path }
        if folder.hasPrefix("~/") { return home.appendingPathComponent(String(folder.dropFirst(2))).path }
        return folder.replacingOccurrences(of: "$HOME", with: home.path)
    }

    private static func standard(_ path: String) -> String {
        var p = (path as NSString).standardizingPath
        while p.count > 1 && p.hasSuffix("/") { p.removeLast() }
        return p
    }
}
