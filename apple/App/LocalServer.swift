#if os(macOS)
import Foundation
import TodoTrackerKit
import TodoTrackerUI

/// Runs Todo Tracker's server (it comes inside the app) in the background on this Mac, so the app uses the same tasks
/// folder as the browser and the other computers: `~/Documents/Todo Tracker`, synced through OneDrive or iCloud Drive.
/// It starts with the app and stops when the app quits (`--parent-pid`). The first time, it moves the tasks this Mac
/// kept on its own into the folder.
@MainActor
final class LocalServer {
    static let shared = LocalServer()
    nonisolated static let port = 5317

    private var process: Process?
    private var started = false

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

    /// Once per run: start (or find) the server and switch the model over to it.
    func connect(_ model: BoardModel) {
        guard !started else { return }
        started = true
        Task {
            switch await start() {
            case let .success(client): model.use(server: client)
            case let .failure(failure): model.reportLocalOnly(failure.reason)
            }
        }
    }

    func stop() {
        process?.terminate()
    }

    private func start() async -> Result<ServerClient, Failure> {
        let client = ServerClient(baseURL: URL(string: "http://127.0.0.1:\(Self.port)")!, token: Self.token())
        if await client.isUp() {
            // Most likely the server of this app's last run, about to stop (its app is gone): wait for it.
            for _ in 0..<12 {
                if !(await client.isUp()) { break }
                try? await Task.sleep(nanoseconds: 250_000_000)
            }
            if await client.isUp() {
                // Still there: fine if it's ours (it knows our token), else the port is someone else's.
                return (try? await client.send(ServerAPI.settings)) != nil ? .success(client) : .failure(.portTaken)
            }
        }

        guard let executable = Bundle.main.resourceURL?.appendingPathComponent("Server/TodoTracker.Web"),
              FileManager.default.isExecutableFile(atPath: executable.path) else { return .failure(.notBundled) }
        let process = Process()
        process.executableURL = executable
        process.arguments = ["--port", String(Self.port), "--parent-pid", String(ProcessInfo.processInfo.processIdentifier)]
        var environment = ProcessInfo.processInfo.environment
        environment["TODOTRACKER_TOKEN"] = Self.token()
        process.environment = environment
        // Its output goes to a log (never a pipe nobody reads: it would fill up and stall the server).
        let log = Self.logFile()
        process.standardOutput = log
        process.standardError = log
        do {
            try process.run()
        } catch {
            return .failure(.didNotStart(error.localizedDescription))
        }
        self.process = process

        // The first start can take a while (it may be moving this Mac's tasks into the tasks folder).
        for _ in 0..<160 {
            if await client.isUp() { return .success(client) }
            if !process.isRunning { return .failure(.didNotStart("it stopped; see ~/Library/Logs/TodoTracker/server.log")) }
            try? await Task.sleep(nanoseconds: 250_000_000)
        }
        return .failure(.didNotStart("it took too long"))
    }

    /// The secret the app and its server share (kept in the app's preferences; the server only listens on this Mac).
    private static func token() -> String {
        let key = "serverToken"
        if let saved = UserDefaults.standard.string(forKey: key), saved.count >= 32 { return saved }
        let fresh = (UUID().uuidString + UUID().uuidString).replacingOccurrences(of: "-", with: "").lowercased()
        UserDefaults.standard.set(fresh, forKey: key)
        return fresh
    }

    private static func logFile() -> FileHandle {
        let folder = FileManager.default.homeDirectoryForCurrentUser.appendingPathComponent("Library/Logs/TodoTracker", isDirectory: true)
        try? FileManager.default.createDirectory(at: folder, withIntermediateDirectories: true)
        let file = folder.appendingPathComponent("server.log")
        FileManager.default.createFile(atPath: file.path, contents: nil)
        return (try? FileHandle(forWritingTo: file)) ?? FileHandle.nullDevice
    }
}
#endif
