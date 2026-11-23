import Foundation

// What the Mac app reads from One Thing's server besides the board: which extras are on (Settings › Extras), hooks
// waiting for approval, where the tasks folder is, and the panels of the web app it opens in its One Thing window.

/// The extras (plugins) switched on, as `GET /api/plugins` says. Nothing read yet (or a board on this device, with no
/// server): everything is on, as the phones have it.
public struct ExtraSwitches: Equatable, Sendable {
    /// Plugin ids that are on; nil: not known (everything shows).
    public let on: Set<String>?
    /// Every plugin the server has (on or off).
    public let known: Set<String>

    public static let everything = ExtraSwitches(on: nil, known: [])

    public init(on: Set<String>?, known: Set<String>) {
        self.on = on
        self.known = known
    }

    /// The ids the apps check (PluginHost.BuiltIn's).
    public static let plan = "plan", energy = "energy", focusTimer = "focus-timer", feedback = "feedback", obsidian = "obsidian",
                      askAI = "agent-chat", connectAI = "connect-ai", browserExtension = "browser-extension", history = "history"

    /// Whether the extra is on. One the server doesn't have (an older server) counts as on, like the web app's.
    public func isOn(_ id: String) -> Bool {
        guard let on else { return true }
        return on.contains(id) || !known.contains(id)
    }

    /// Whether any sync extra (sync-onedrive, sync-icloud, sync-gist) is on.
    public var syncIsOn: Bool { on?.contains { $0.hasPrefix("sync-") } ?? true }

    /// `GET /api/plugins`: [{ id, name, description, enabled }]. Anything else isn't a list of extras (nil).
    public static func decode(_ data: Data) -> ExtraSwitches? {
        guard let list = try? JSONSerialization.jsonObject(with: data) as? [[String: Any]] else { return nil }
        var on = Set<String>(), known = Set<String>()
        for entry in list {
            guard let id = entry["id"] as? String else { continue }
            known.insert(id)
            if entry["enabled"] as? Bool == true { on.insert(id) }
        }
        return ExtraSwitches(on: on, known: known)
    }
}

/// A hook an AI agent added or changed, waiting for the person's approval (`GET /api/hooks`, state "needs approval").
public struct PendingHook: Equatable, Sendable, Identifiable {
    public let id: String
    /// The version shown: approving or turning it down names it (one changed meanwhile is neither).
    public let version: String
    public let event: String
    /// What the event is, in words ("A task was marked done"), when the server says.
    public let eventText: String?
    /// Who added or changed it (an agent's name).
    public let addedBy: String?
    /// Everything it would do, one line each (`HookDescription.describe`).
    public let description: String

    /// The key it's asked about by: each version once.
    public var askKey: String { "\(id)\u{1f}\(version)" }

    /// "Claude wants to run a hook" (the web prompt's words).
    public var title: String { "\(addedBy ?? "An AI agent") wants to run a hook" }

    /// "When taskCompleted (a task was marked done), it would:".
    public var when: String { "When \(event)\(eventText.map { " (\($0.lowercased()))" } ?? ""), it would:" }

    public static let fineprint = "Allow it and it runs from now on, without asking again. If it is changed later by an agent, you’re asked again."
}

/// What `GET /api/hooks` says about approvals: whether this session may approve, whether another app asks itself, and
/// the hooks waiting.
public struct HookApprovals: Equatable, Sendable {
    public let canApprove: Bool
    public let appAsks: Bool
    public let waiting: [PendingHook]

    public static let none = HookApprovals(canApprove: false, appAsks: false, waiting: [])

    public init(canApprove: Bool, appAsks: Bool, waiting: [PendingHook]) {
        self.canApprove = canApprove
        self.appAsks = appAsks
        self.waiting = waiting
    }

    /// The next hook to ask about (hook-prompt.js nextToAsk): waiting, not asked about in this version yet, and only
    /// when this app may approve and no other app asks itself.
    public func nextToAsk(asked: Set<String>) -> PendingHook? {
        guard canApprove, !appAsks else { return nil }
        return waiting.first { !asked.contains($0.askKey) }
    }

    public static func decode(_ data: Data) -> HookApprovals? {
        guard let object = try? JSONSerialization.jsonObject(with: data) as? [String: Any] else { return nil }
        let events = object["events"] as? [String: String] ?? [:]
        let hooks = object["hooks"] as? [[String: Any]] ?? []
        let waiting: [PendingHook] = hooks.compactMap { entry in
            guard entry["state"] as? String == "needs approval", let id = entry["id"] as? String,
                  let version = entry["version"] as? String, let event = entry["event"] as? String else { return nil }
            let hook = entry["hook"] as? [String: Any] ?? [:]
            return PendingHook(id: id, version: version, event: event, eventText: events[event], addedBy: hook["addedBy"] as? String,
                               description: HookDescription.describe(hook))
        }
        return HookApprovals(canApprove: object["canApprove"] as? Bool ?? false, appAsks: object["appAsks"] as? Bool ?? false, waiting: waiting)
    }
}

/// Everything a hook would do, one line each (hook-prompt.js describeHook): a name alone could say anything.
public enum HookDescription {
    public static func describe(_ hook: [String: Any]) -> String {
        func text(_ key: String) -> String? { (hook[key] as? String).flatMap { $0.isEmpty ? nil : $0 } }
        func pairs(_ key: String, _ separator: String) -> String? {
            guard let map = hook[key] as? [String: Any], !map.isEmpty else { return nil }
            // In the order the file has them is lost in JSON objects: sorted, so the same hook always reads the same.
            return map.keys.sorted().map { "\($0)\(separator)\(map[$0].map { "\($0)" } ?? "")" }.joined(separator: ", ")
        }
        let match = hook["match"] as? [String: Any] ?? [:]
        let only = [("groups", "groups"), ("tags", "tags"), ("labels", "labels"), ("done by", "actors")].compactMap { name, key -> String? in
            guard let values = match[key] as? [String], !values.isEmpty else { return nil }
            return "\(name) \(values.joined(separator: ", "))"
        }.joined(separator: "; ")
        let timeout = (hook["timeoutSec"] as? NSNumber).flatMap { $0.intValue > 0 ? "Time limit: \($0.intValue) s" : nil }
        return [
            text("powershell").map { "PowerShell: \($0)" },
            text("bash").map { "bash: \($0)" },
            text("url").map { "Web address: \($0)" },
            pairs("headers", ": ").map { "Headers: \($0)" },
            pairs("env", "=").map { "Variables: \($0)" },
            text("cwd").map { "Folder: \($0)" },
            timeout,
            only.isEmpty ? nil : "Only for: \(only)",
        ].compactMap { $0 }.joined(separator: "\n")
    }
}

/// Where the tasks are (`GET /api/vault`): the folder, files that need fixing, and Obsidian.
public struct VaultInfo: Equatable, Sendable {
    public struct Problem: Equatable, Sendable {
        public let path: String
        public let message: String
    }

    public let path: String
    public let problems: [Problem]
    public let obsidianURL: URL?
    public let obsidianInstalled: Bool

    public init(path: String, problems: [Problem] = [], obsidianURL: URL? = nil, obsidianInstalled: Bool = false) {
        self.path = path
        self.problems = problems
        self.obsidianURL = obsidianURL
        self.obsidianInstalled = obsidianInstalled
    }

    /// The notice above the lists (app.js renderProblems), or nil when every file reads.
    public var problemNotice: String? {
        guard let first = problems.first else { return nil }
        return problems.count == 1 ? "A task file needs fixing: \(first.path) – \(first.message)"
            : "\(problems.count) task files need fixing (first: \(first.path))."
    }

    public static func decode(_ data: Data) -> VaultInfo? {
        guard let object = try? JSONSerialization.jsonObject(with: data) as? [String: Any], let path = object["path"] as? String else { return nil }
        let problems = (object["problems"] as? [[String: Any]] ?? []).compactMap { p -> Problem? in
            guard let path = p["path"] as? String else { return nil }
            return Problem(path: path, message: p["message"] as? String ?? "")
        }
        return VaultInfo(path: path, problems: problems, obsidianURL: (object["obsidianUrl"] as? String).flatMap(URL.init(string:)),
                         obsidianInstalled: object["obsidianInstalled"] as? Bool ?? false)
    }
}

/// A part of the web app the Mac opens in its One Thing window: each lands on its own panel, not just on Settings.
public enum WebAppPanel: String, CaseIterable, Identifiable, Sendable {
    case app, connected, sync, hooks, extras, connectAI, browserExtension, askAI

    public var id: String { rawValue }

    /// The path the sign-in link returns to (ApiEndpoints.SafeReturnPath: same-origin).
    public var path: String {
        switch self {
        case .app: return "/"
        case .connected: return "/#/settings/connected"
        case .sync: return "/?sync#/settings/sync"
        case .hooks: return "/#/settings/hooks"
        case .extras: return "/#/settings/plugins"
        case .connectAI: return "/?connect"
        case .browserExtension: return "/?extension"
        case .askAI: return "/#/ask"
        }
    }

    public var title: String {
        switch self {
        case .app: return "Open the full app"
        case .connected: return "Connected apps"
        case .sync: return "Sync"
        case .hooks: return "Hooks"
        case .extras: return "Extras"
        case .connectAI: return "Connect AI apps"
        case .browserExtension: return "Browser extension"
        case .askAI: return "Ask AI"
        }
    }

    /// What it's for, in a few words (under the row in Settings).
    public var subtitle: String {
        switch self {
        case .app: return "Board, reports and settings, as in the browser"
        case .connected: return "Notion, Microsoft To Do and GitHub Issues"
        case .sync: return "Keep your tasks folder in step with your other computers"
        case .hooks: return "Run your own script when something happens"
        case .extras: return "Switch on or off what you use"
        case .connectAI: return "Claude, Copilot, VS Code, ChatGPT and other AI apps"
        case .browserExtension: return "Add tasks from Safari, Chrome or Edge"
        case .askAI: return "Chat with an AI agent about your tasks"
        }
    }

    public var symbol: String {
        switch self {
        case .app: return "macwindow"
        case .connected: return "link"
        case .sync: return "arrow.triangle.2.circlepath.icloud"
        case .hooks: return "bolt.horizontal"
        case .extras: return "puzzlepiece.extension"
        case .connectAI: return "sparkles"
        case .browserExtension: return "safari"
        case .askAI: return "bubble.left.and.text.bubble.right"
        }
    }

    /// The extra it needs (nil: always there); switched off, its row goes.
    public var plugin: String? {
        switch self {
        case .connectAI: return ExtraSwitches.connectAI
        case .browserExtension: return ExtraSwitches.browserExtension
        case .askAI: return ExtraSwitches.askAI
        default: return nil
        }
    }

    /// The rows Settings lists, in the web Settings' order (its sections, then its header panels).
    public static let settingsRows: [WebAppPanel] = [.connected, .sync, .hooks, .extras, .connectAI, .browserExtension, .askAI]
}

/// Where a link from the One Thing window goes (OneThing.Desktop.AppNavigation, the Windows window's rules).
public enum AppWindowLink: Equatable, Sendable {
    /// The app itself (its page, sign-in, a blank page): stays in the window.
    case app
    /// Another page of the app (the printable report, an attachment): the browser, signed in with a single-use link.
    case ownPageInBrowser
    /// A web page elsewhere: the default browser.
    case outside
    /// Another app (mail, Obsidian): only when the person clicked it.
    case outsideIfAsked
    /// Never (data:, javascript:, file: and the like).
    case blocked
    /// A onething://task/<id> link: the task, in the app.
    case taskLink
}

/// What showing a path of the app in its window takes.
public enum AppWindowOpen: Equatable, Sendable {
    /// It's on screen already: bring the window forward.
    case focus
    /// Change the address in place (no reload: nothing typed is lost).
    case setHash(String)
    /// Load it (with a new sign-in link).
    case navigate(String)
}

/// Where a link the window doesn't keep goes.
public enum AppWindowFollow: Equatable, Sendable {
    /// Nowhere.
    case nothing
    /// The app's other page, in the browser with a single-use sign-in link.
    case signedInBrowser
    /// The browser, or the app the link belongs to.
    case open
}

public enum AppWindowNavigation {
    /// Where a link that left the window goes (`inWindow`: it was to load in the window, not a new one; `mainFrame`: in
    /// the page itself, not a frame inside it; `userInitiated`: a click). Only a click opens another app, and a frame
    /// inside the page opens nothing on its own. As the Windows app's `Follow`.
    public static func follow(_ kind: AppWindowLink, url: URL, inWindow: Bool, mainFrame: Bool, userInitiated: Bool) -> AppWindowFollow {
        if !mainFrame && !userInitiated { return .nothing }
        switch kind {
        case .app: return !inWindow && url.absoluteString != "about:blank" ? .signedInBrowser : .nothing
        case .ownPageInBrowser: return .signedInBrowser
        case .outside: return .open
        case .outsideIfAsked, .taskLink: return userInitiated ? .open : .nothing
        case .blocked: return .nothing
        }
    }

    /// Seconds before the window tries the server again after `attempt` tries that failed: 1, 2, 4… then every 30.
    public static func retryDelay(attempt: Int) -> Int { attempt >= 5 ? 30 : 1 << max(0, attempt) }

    public static func classify(origin: URL, _ url: URL?) -> AppWindowLink {
        guard let url else { return .blocked }
        if url.absoluteString == "about:blank" { return .app }
        if case .task = TaskLinks.target(url.absoluteString) { return .taskLink }
        let scheme = url.scheme?.lowercased() ?? ""
        if scheme == "http" || scheme == "https" {
            if sameOrigin(url, origin) {
                // The page, and its sign-in link (/auth?code=…), stay in the window.
                return isShell(url) || url.path == "/auth" ? .app : .ownPageInBrowser
            }
            return .outside
        }
        return scheme == "mailto" || scheme == "obsidian" ? .outsideIfAsked : .blocked
    }

    /// Showing `path` ("/", "/#/settings/hooks", "/?sync") while the window is on `current`.
    public static func plan(current: URL?, loaded: Bool, path: String) -> AppWindowOpen {
        let onShell = loaded && current.map(isShell) == true
        if onShell && path == "/" { return .focus }
        return onShell && path.hasPrefix("/#") ? .setHash(String(path.dropFirst())) : .navigate(path)
    }

    /// The path, query and fragment of a link (to open it in the browser with a sign-in link).
    public static func path(of url: URL) -> String {
        guard let parts = URLComponents(url: url, resolvingAgainstBaseURL: false) else { return "/" }
        var path = parts.percentEncodedPath.isEmpty ? "/" : parts.percentEncodedPath
        if let query = parts.percentEncodedQuery { path += "?\(query)" }
        if let fragment = parts.percentEncodedFragment { path += "#\(fragment)" }
        return path
    }

    static func sameOrigin(_ a: URL, _ b: URL) -> Bool {
        a.scheme?.lowercased() == b.scheme?.lowercased() && a.host?.lowercased() == b.host?.lowercased() && port(a) == port(b)
    }

    private static func port(_ url: URL) -> Int { url.port ?? (url.scheme?.lowercased() == "https" ? 443 : 80) }

    private static func isShell(_ url: URL) -> Bool { url.path == "/" || url.path == "/index.html" || url.path.isEmpty }
}

extension ServerAPI {
    public static let plugins = ServerRequest(method: "GET", path: "/api/plugins")
    public static let hooks = ServerRequest(method: "GET", path: "/api/hooks")
    public static let vault = ServerRequest(method: "GET", path: "/api/vault")

    /// Allow: it runs from now on (names the version shown).
    public static func approveHook(_ id: String, version: String) -> ServerRequest {
        ServerRequest(method: "POST", path: "/api/hooks/\(pathPart(id))/approve", body: json(["version": version]))
    }

    /// Don't allow: it's removed, if it still waits in the version shown.
    public static func rejectHook(_ id: String, version: String) -> ServerRequest {
        ServerRequest(method: "POST", path: "/api/hooks/\(pathPart(id))/reject", body: json(["version": version]))
    }

    /// One part of a path, escaped (a hook id is the server's, but never trusted to be plain).
    static func pathPart(_ text: String) -> String {
        text.addingPercentEncoding(withAllowedCharacters: .alphanumerics.union(CharacterSet(charactersIn: "-._~"))) ?? ""
    }
}

extension ServerClient {
    /// Which extras are on (nil: the server didn't say).
    public func extras() async throws -> ExtraSwitches? { ExtraSwitches.decode(try await send(ServerAPI.plugins)) }

    public func hookApprovals() async throws -> HookApprovals { HookApprovals.decode(try await send(ServerAPI.hooks)) ?? .none }

    public func vault() async throws -> VaultInfo? { VaultInfo.decode(try await send(ServerAPI.vault)) }
}
