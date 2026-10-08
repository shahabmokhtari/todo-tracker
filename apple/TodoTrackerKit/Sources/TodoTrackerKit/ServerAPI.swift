import Foundation

/// What the Mac app asks Todo Tracker's server to do: the same REST API the web app and the Windows app use. The
/// server keeps the tasks folder (markdown) and syncs it with the other computers.
public enum ServerAction: Equatable, Sendable {
    case capture(text: String, groupId: UUID?)
    case complete(UUID)
    case reopen(UUID)
    case schedule(UUID, at: Date)
    /// Snoozed until another task is done.
    case waitFor(UUID, after: UUID)
    case bringBack(UUID)
    case dismissReminder(UUID, reminderId: UUID)
    case addNote(UUID, text: String)
    case update(UUID, title: String, priority: Priority)
    case addSubtask(parent: UUID, title: String)
    case addSteps(parent: UUID, titles: [String], stepDelayMinutes: Int?)
    case delete(UUID)
    case addGroup(name: String)
    case pomodoro(PomodoroCommand, itemId: UUID?)
    case setTheme(String)
}

public enum PomodoroCommand: String, Sendable {
    case start, pause, resume, skip, reset
}

/// An HTTP request to the server; the body is JSON with sorted keys (nil: none).
public struct ServerRequest: Equatable, Sendable {
    public let method: String
    public let path: String
    public let body: String?

    public init(method: String, path: String, body: String? = nil) {
        self.method = method
        self.path = path
        self.body = body
    }
}

public enum ServerAPI {
    /// The whole board, in the JSON the apps share (BoardCodec).
    public static let export = ServerRequest(method: "GET", path: "/api/export")
    public static let settings = ServerRequest(method: "GET", path: "/api/settings")
    public static let health = ServerRequest(method: "GET", path: "/health")

    /// A sign-in link for the browser (the full window: board, reports, settings, sync).
    public static func launch(returnPath: String = "/") -> ServerRequest {
        ServerRequest(method: "POST", path: "/api/launch", body: json(["return": returnPath]))
    }

    public static func request(for action: ServerAction) -> ServerRequest {
        switch action {
        case let .capture(text, groupId):
            return post("/api/capture", ["text": text, "groupId": groupId.map(id)])
        case let .complete(item):
            return post("/api/items/\(id(item))/complete")
        case let .reopen(item):
            return post("/api/items/\(id(item))/reopen")
        case let .schedule(item, at):
            return post("/api/items/\(id(item))/schedule", ["at": BoardCodec.formatDate(at), "notify": true])
        case let .waitFor(item, after):
            return post("/api/items/\(id(item))/after", ["afterId": id(after)])
        case let .bringBack(item):
            return post("/api/items/\(id(item))/schedule", ["clear": true])
        case let .dismissReminder(item, reminderId):
            return post("/api/items/\(id(item))/reminders/\(id(reminderId))/dismiss")
        case let .addNote(item, text):
            return post("/api/items/\(id(item))/notes", ["text": text])
        case let .update(item, title, priority):
            return ServerRequest(method: "PATCH", path: "/api/items/\(id(item))", body: json(["title": title, "priority": priority.rawValue]))
        case let .addSubtask(parent, title):
            return post("/api/items", ["title": title, "parentId": id(parent)])
        case let .addSteps(parent, titles, stepDelayMinutes):
            return post("/api/items/\(id(parent))/steps", ["titles": titles, "stepDelayMinutes": stepDelayMinutes])
        case let .delete(item):
            return ServerRequest(method: "DELETE", path: "/api/items/\(id(item))")
        case let .addGroup(name):
            return post("/api/groups", ["name": name])
        case let .pomodoro(command, itemId):
            return post("/api/pomodoro/\(command.rawValue)", itemId.map { ["itemId": id($0)] as [String: Any?] })
        case let .setTheme(theme):
            return ServerRequest(method: "PUT", path: "/api/settings/theme", body: json(["theme": theme]))
        }
    }

    static func id(_ uuid: UUID) -> String { uuid.uuidString.lowercased() }

    private static func post(_ path: String, _ body: [String: Any?]? = nil) -> ServerRequest {
        ServerRequest(method: "POST", path: path, body: body.map(json))
    }

    static func json(_ object: [String: Any?]) -> String {
        let present = object.compactMapValues { $0 }
        // Only strings, numbers, booleans and arrays of strings go in: always valid JSON.
        let data = (try? JSONSerialization.data(withJSONObject: present, options: [.sortedKeys, .withoutEscapingSlashes])) ?? Data("{}".utf8)
        return String(decoding: data, as: UTF8.self)
    }
}

public enum ServerError: Error, LocalizedError, Equatable {
    case unreachable(String)
    case refused(status: Int, message: String)

    public var errorDescription: String? {
        switch self {
        case let .unreachable(reason): return "Todo Tracker's server isn't answering (\(reason))."
        case let .refused(_, message): return message
        }
    }
}

/// Talks to the server on this computer (loopback only), with its token.
public final class ServerClient: @unchecked Sendable {
    public let baseURL: URL
    private let token: String
    private let session: URLSession

    public init(baseURL: URL, token: String, session: URLSession = .shared) {
        self.baseURL = baseURL
        self.token = token
        self.session = session
    }

    @discardableResult
    public func send(_ request: ServerRequest) async throws -> Data {
        let (data, status, _) = try await exchange(request)
        guard (200..<300).contains(status) else { throw ServerError.refused(status: status, message: Self.message(data, status: status)) }
        return data
    }

    public func perform(_ action: ServerAction) async throws -> Data {
        try await send(ServerAPI.request(for: action))
    }

    /// The whole board as the server has it now.
    public func board() async throws -> TaskBoard {
        try BoardCodec.decode(try await send(ServerAPI.export))
    }

    /// The board if it changed since the read that gave `etag` (nil: unchanged, nothing to do), and its tag now.
    public func board(ifChangedFrom etag: String?) async throws -> (board: TaskBoard?, etag: String?) {
        let (data, status, response) = try await exchange(ServerAPI.export, headers: etag.map { ["If-None-Match": $0] } ?? [:])
        if status == 304 { return (nil, etag) }
        guard (200..<300).contains(status) else { throw ServerError.refused(status: status, message: Self.message(data, status: status)) }
        return (try BoardCodec.decode(data), response?.value(forHTTPHeaderField: "ETag"))
    }

    private func exchange(_ request: ServerRequest, headers: [String: String] = [:]) async throws -> (Data, Int, HTTPURLResponse?) {
        guard let url = URL(string: request.path, relativeTo: baseURL)?.absoluteURL else { throw ServerError.unreachable("bad address") }
        var http = URLRequest(url: url)
        http.httpMethod = request.method
        http.timeoutInterval = 15
        // Always asked fresh (an ETag is sent on purpose, and a 304 must come back as one).
        http.cachePolicy = .reloadIgnoringLocalCacheData
        http.setValue("Bearer \(token)", forHTTPHeaderField: "Authorization")
        for (name, value) in headers { http.setValue(value, forHTTPHeaderField: name) }
        if let body = request.body {
            http.httpBody = Data(body.utf8)
            http.setValue("application/json", forHTTPHeaderField: "Content-Type")
        }

        let result: (Data, URLResponse)
        do {
            result = try await session.data(for: http)
        } catch {
            throw ServerError.unreachable(error.localizedDescription)
        }

        let response = result.1 as? HTTPURLResponse
        return (result.0, response?.statusCode ?? 0, response)
    }
    /// The light/dark choice every window shares ("system", "light" or "dark").
    public func theme() async throws -> String? {
        let object = try JSONSerialization.jsonObject(with: try await send(ServerAPI.settings)) as? [String: Any]
        return object?["theme"] as? String
    }

    public func launchURL(returnPath: String = "/") async throws -> URL {
        let object = try JSONSerialization.jsonObject(with: try await send(ServerAPI.launch(returnPath: returnPath))) as? [String: Any]
        guard let text = object?["url"] as? String, let url = URL(string: text) else { throw ServerError.refused(status: 200, message: "No sign-in link came back.") }
        return url
    }

    /// Whether something answers on the address (without the token).
    public func isUp() async -> Bool {
        guard let url = URL(string: ServerAPI.health.path, relativeTo: baseURL)?.absoluteURL else { return false }
        var http = URLRequest(url: url)
        http.timeoutInterval = 2
        guard let result = try? await session.data(for: http) else { return false }
        return (result.1 as? HTTPURLResponse)?.statusCode == 200
    }

    /// The server's own words for a refusal (problem details, or {"error": ...}).
    static func message(_ data: Data, status: Int) -> String {
        if let object = try? JSONSerialization.jsonObject(with: data) as? [String: Any] {
            for key in ["detail", "error", "title"] {
                if let text = object[key] as? String, !text.isEmpty { return text }
            }
        }
        let text = String(decoding: data.prefix(300), as: UTF8.self).trimmingCharacters(in: .whitespacesAndNewlines)
        return text.isEmpty ? "The server said no (HTTP \(status))." : text
    }
}
