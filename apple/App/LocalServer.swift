#if os(macOS)
import AppKit
import Foundation
import TodoTrackerKit
import TodoTrackerUI

/// Runs Todo Tracker's server (it comes inside the app) in the background on this Mac, so the app uses the same tasks
/// folder as the browser and the other computers: `~/Documents/Todo Tracker`, synced through OneDrive or iCloud Drive.
/// It starts with the app, is started again if it stops, and stops when the app quits (`--parent-pid` covers a crash).
/// The first time, it moves the tasks this Mac kept on its own into the folder.
@MainActor
final class LocalServer {
    static let shared = LocalServer()
    nonisolated static let port = 5317
    private static let base = URL(string: "http://127.0.0.1:\(port)")!

    private var process: Process?
    private weak var model: BoardModel?
    private var started = false
    private var quitting = false
    private var restarts = 0
    private var opening = false
    private var quitObserver: NSObjectProtocol?

    enum Failure: Error {
        case notBundled
        case portTaken
        case didNotStart(String)

        var reason: String {
            switch self {
            case .notBundled: return "this copy of the app doesn't include the sync server"
            case .portTaken: return "another program uses port \(LocalServer.port)"
            case let .didNotStart(why): return "the sync server didn't start (\(why))"
            }
        }
    }

    /// Once per run: start (or find) the server and switch the model over to it. Changes wait until then.
    func connect(_ model: BoardModel) {
        guard !started else { return }
        started = true
        self.model = model
        model.beginConnecting()
        Self.rotateLog()
        quitObserver = NotificationCenter.default.addObserver(forName: NSApplication.willTerminateNotification, object: nil, queue: .main) { _ in
            MainActor.assumeIsolated { LocalServer.shared.stop() }
        }
        // A server found already running isn't this app's process: if reads keep failing, it's gone.
        model.onServerLost = { [weak self] in self?.reconnect() }
        Task { await self.open() }
    }

    /// One at a time: start (or find) the server; on success the model uses it, else it goes back to the board on
    /// this Mac (or, once that moved into the folder, shows it without changes).
    private func open() async {
        guard !opening, let model else { return }
        opening = true
        defer { opening = false }
        switch await start() {
        case let .success(client):
            restarts = 0
            if let process { watch(process) }
            model.use(server: client)
        case let .failure(failure):
            model.reportLocalOnly(failure.reason)
        }
    }

    private func reconnect() {
        guard !quitting, !opening, let model else { return }
        restarts += 1
        guard restarts <= 3 else {
            model.reportLocalOnly("Todo Tracker's server keeps stopping; see ~/Library/Logs/TodoTracker/server.log, then quit and open the app again")
            return
        }
        model.serverLost("Reconnecting to your tasks folder…")
        Task { await self.open() }
    }

    private func stop() {
        quitting = true
        process?.terminate()
    }

    /// Only once it's up: a start that fails is retried by `start` itself.
    private func watch(_ process: Process) {
        process.terminationHandler = { [weak self] ended in
            Task { @MainActor in
                guard let self, ended === self.process else { return }
                self.reconnect()
            }
        }
    }
    private func start() async -> Result<ServerClient, Failure> {
        #if arch(arm64)
        let probe = ServerClient(baseURL: Self.base, token: "")
        if await probe.isUp() {
            // Most likely the server of this app's last run, stopping (its app is gone): give it time.
            for _ in 0..<24 {
                if !(await probe.isUp()) { break }
                try? await Task.sleep(nanoseconds: 250_000_000)
            }
            if await probe.isUp() {
                // Still there: fine if it's this Mac's (it knows the token), else the port is someone else's.
                guard let client = Self.client(), (try? await client.send(ServerAPI.settings)) != nil else { return .failure(.portTaken) }
                return .success(client)
            }
        }

        guard let executable = Bundle.main.resourceURL?.appendingPathComponent("Server/TodoTracker.Web"),
              FileManager.default.isExecutableFile(atPath: executable.path) else { return .failure(.notBundled) }
        // A server that just stopped may still hold the folder for a moment: a few tries.
        for _ in 0..<3 {
            let process = Self.makeProcess(executable)
            do {
                try process.run()
            } catch {
                return .failure(.didNotStart(error.localizedDescription))
            }
            self.process = process

            // No time limit while it runs: the first start may wait on macOS asking about the Documents folder, or
            // be moving this Mac's tasks into the folder.
            var checks = 0
            while process.isRunning {
                if await probe.isUp(), let client = Self.client() { return .success(client) }
                checks += 1
                if checks == 40 { model?.connectingSlowly() }
                try? await Task.sleep(nanoseconds: 250_000_000)
            }
            try? await Task.sleep(nanoseconds: 1_500_000_000)
        }
        return .failure(.didNotStart("it stopped; see ~/Library/Logs/TodoTracker/server.log"))
        #else
        return .failure(.didNotStart("it needs a Mac with Apple silicon"))
        #endif
    }

    private static func makeProcess(_ executable: URL) -> Process {
        let process = Process()
        process.executableURL = executable
        var arguments = ["--port", String(port), "--parent-pid", String(ProcessInfo.processInfo.processIdentifier), "--import-legacy"]
        if !hasGit { arguments.append("--no-history") }
        process.arguments = arguments
        var environment = ProcessInfo.processInfo.environment
        // Apps opened from the Finder get a short PATH: add where git usually is.
        environment["PATH"] = "/opt/homebrew/bin:/usr/local/bin:" + (environment["PATH"] ?? "/usr/bin:/bin")
        process.environment = environment
        // Its output goes to a log (never a pipe nobody reads: it would fill up and stall the server).
        let log = logFile()
        process.standardOutput = log
        process.standardError = log
        return process
    }

    /// Version history needs a real git. Without the developer tools, /usr/bin/git only asks to install them (every
    /// time), so history is off then.
    private static var hasGit: Bool {
        ["/Library/Developer/CommandLineTools/usr/bin/git", "/Applications/Xcode.app/Contents/Developer/usr/bin/git", "/opt/homebrew/bin/git", "/usr/local/bin/git"]
            .contains { FileManager.default.isExecutableFile(atPath: $0) }
    }

    /// The server's own token (`api-token` in its data folder, readable only by this user), as the browser uses.
    private static func client() -> ServerClient? {
        let file = FileManager.default.homeDirectoryForCurrentUser.appendingPathComponent("Library/Application Support/TodoTracker/api-token")
        guard let text = try? String(contentsOf: file, encoding: .utf8) else { return nil }
        let token = text.trimmingCharacters(in: .whitespacesAndNewlines)
        return token.isEmpty ? nil : ServerClient(baseURL: base, token: token)
    }

    /// `server.log` (this run of the app) and `server.previous.log` (the run before: why it stopped, after a crash).
    private static var logURL: URL {
        FileManager.default.homeDirectoryForCurrentUser.appendingPathComponent("Library/Logs/TodoTracker/server.log")
    }

    private static func rotateLog() {
        let fm = FileManager.default
        try? fm.createDirectory(at: logURL.deletingLastPathComponent(), withIntermediateDirectories: true)
        let previous = logURL.deletingLastPathComponent().appendingPathComponent("server.previous.log")
        if fm.fileExists(atPath: logURL.path) {
            try? fm.removeItem(at: previous)
            try? fm.moveItem(at: logURL, to: previous)
        }
    }

    /// Appends (every start of this run goes in the same log).
    private static func logFile() -> FileHandle {
        if !FileManager.default.fileExists(atPath: logURL.path) {
            FileManager.default.createFile(atPath: logURL.path, contents: nil)
        }
        guard let handle = try? FileHandle(forWritingTo: logURL) else { return FileHandle.nullDevice }
        handle.seekToEndOfFile()
        return handle
    }
}
#endif
