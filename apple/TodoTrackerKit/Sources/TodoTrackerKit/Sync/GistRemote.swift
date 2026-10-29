import Foundation

/// Sends one HTTP request (URLSession in the app, a fake GitHub in tests).
public protocol HTTPTransport: AnyObject {
    func send(_ request: URLRequest) async throws -> (Data, HTTPURLResponse)
}

/// Talks to GitHub with URLSession, never taking the token anywhere else: a redirect to another host is followed
/// without the `Authorization` header (and one to GitHub keeps it).
public final class GitHubSession: NSObject, HTTPTransport, URLSessionTaskDelegate, @unchecked Sendable {
    public static let shared = GitHubSession()

    /// The only hosts that ever get the token.
    public static let tokenHosts: Set<String> = ["api.github.com", "gist.githubusercontent.com"]

    private lazy var session = URLSession(configuration: .ephemeral, delegate: self, delegateQueue: nil)

    public func send(_ request: URLRequest) async throws -> (Data, HTTPURLResponse) {
        let (data, response) = try await session.data(for: request)
        guard let http = response as? HTTPURLResponse else { throw URLError(.badServerResponse) }
        return (data, http)
    }

    public func urlSession(_ session: URLSession, task: URLSessionTask, willPerformHTTPRedirection response: HTTPURLResponse,
                           newRequest request: URLRequest, completionHandler: @escaping (URLRequest?) -> Void) {
        completionHandler(Self.redirect(request))
    }

    /// The request a redirect may go on with: to anywhere but GitHub, without the token.
    public static func redirect(_ request: URLRequest) -> URLRequest {
        guard !mayCarryToken(request.url) else { return request }
        var stripped = request
        stripped.setValue(nil, forHTTPHeaderField: "Authorization")
        return stripped
    }

    static func mayCarryToken(_ url: URL?) -> Bool {
        guard let url, url.scheme?.lowercased() == "https", let host = url.host?.lowercased() else { return false }
        return tokenHosts.contains(host)
    }
}

/// GitHub answered with an error (signed out, rate limited, the gist gone…), said in words the person reads.
public struct GistError: Error, LocalizedError {
    public let status: Int
    public let message: String
    public var errorDescription: String? { message }
}

/// A private GitHub gist as the meeting place: one file per device (`device-<id>.json`: its snapshot and the contents,
/// base64). Checks for changes with the gist's ETag (`If-None-Match`; 304 = nothing new), and only ever changes its own
/// file. Port of C#'s `GistRemote`, byte for byte (sync/manifest.json). The token (a GitHub token with the `gist`
/// scope, from the device flow or pasted) is handed in; it lives in the Keychain, never here.
public final class GistRemote: SyncRemote {
    public static let descriptionPrefix = "Todo Tracker sync: "
    public static let marker = "todo-tracker-sync.md"
    public static let markerText = "# Todo Tracker sync\n\nTodo Tracker keeps each device's tasks here to sync them. Don't edit these files.\n"
    /// GitHub serves at most 10 MB of a gist file; each device's file stays below this.
    public static let maxBundleBytes = 9 * 1024 * 1024

    private let api = "https://api.github.com"
    private let http: HTTPTransport
    private let token: () async throws -> String
    private let loadId: () -> String?
    private let saveId: (String) -> Void
    private let gistDescription: String
    private var contents: [String: [String: String]] = [:]
    private var looked = false

    /// - Parameters:
    ///   - loadId: the gist used before (nil the first time: it is looked up, or made on the first publish).
    ///   - saveId: remembers the gist.
    ///   - library: which set of tasks (one gist each, so separate tasks folders never mix).
    public init(http: HTTPTransport = GitHubSession.shared, token: @escaping () async throws -> String, loadId: @escaping () -> String?, saveId: @escaping (String) -> Void, library: String = "Todo Tracker") {
        self.http = http
        self.token = token
        self.loadId = loadId
        self.saveId = saveId
        gistDescription = Self.descriptionPrefix + library
    }

    public var identity: String { "gist:" + gistDescription }

    public func forget(_ device: String) async throws {
        guard let id = try await gistId(), device.utf16.count > 1, device.utf16.count <= 64, Ordinal.isAsciiLetterOrDigit(device) else { return }
        var request = try await self.request("PATCH", "/gists/\(id)")
        request.httpBody = body(.object(JSONObject([("files", .object(JSONObject([("device-\(device).json", .null)])))])))
        try ensure(try await http.send(request))
    }

    public func read(self device: String, knownVersion: String?) async throws -> RemoteSnapshot? {
        guard let id = try await gistId() else {
            return knownVersion == "none" ? nil : RemoteSnapshot(devices: [], version: "none", selfMissing: true)
        }
        var request = try await self.request("GET", "/gists/\(id)")
        if let knownVersion, case let etag = Self.split(knownVersion).etag, !etag.isEmpty { request.setValue(etag, forHTTPHeaderField: "If-None-Match") }
        let (data, response) = try await http.send(request)
        if response.statusCode == 304 { return nil }
        try ensure((data, response))
        guard let gist = (try? JSONText.parse(data))?.objectValue else { throw GistError(status: response.statusCode, message: "GitHub sent something that isn't a gist.") }
        var devices: [DeviceSnapshot] = []
        var partial = false
        contents.removeAll()
        let files = gist["files"]?.objectValue ?? JSONObject()
        for (name, file) in files.pairs {
            guard name.oStarts("device-"), name.oEnds(".json"), name != "device-\(device).json", let file = file.objectValue else { continue }
            do {
                // Big files are cut short in the gist's JSON: the whole file is at raw_url.
                let text: String?
                if file["truncated"]?.boolValue == true, let raw = file["raw_url"]?.stringValue {
                    text = try await self.raw(raw)
                } else {
                    text = file["content"]?.stringValue
                }
                guard let text, let bundle = try JSONText.parse(text).objectValue, let snapshot = try DeviceSnapshot.from(bundle.get("snapshot")),
                      name == "device-\(snapshot.device).json" else {
                    partial = true
                    continue
                }
                devices.append(snapshot)
                var map: [String: String] = [:]
                for (hash, value) in bundle.get("contents")?.objectValue?.pairs ?? [] { if let s = value.stringValue { map[hash] = s } }
                contents[snapshot.device] = map
            } catch is CancellationError {
                throw CancellationError()
            } catch {
                // Damaged, too big, or from a newer version: skipped, and looked at again next time.
                partial = true
            }
        }
        return RemoteSnapshot(devices: devices, version: Self.join(response.value(forHTTPHeaderField: "ETag"), Self.revision(gist)), partial: partial,
                              selfMissing: files["device-\(device).json"] == nil)
    }

    public func readContent(_ device: DeviceSnapshot, _ entry: SyncEntry) async throws -> Data {
        guard let base64 = contents[device.device]?[entry.hash] else { throw SyncNotArrived("\(entry.path) from \(device.name) isn't in the gist.") }
        guard let data = Data(base64Encoded: base64) else { throw SyncDataError("\(entry.path) from \(device.name) is damaged.") }
        return data
    }

    /// The device's file in the gist: `{"snapshot":…,"contents":{hash: base64}}`.
    public static func bundle(_ snapshot: DeviceSnapshot, read: (SyncEntry) throws -> Data) throws -> String {
        var contents = JSONObject()
        for entry in snapshot.entries where !contents.contains(entry.hash) { contents.pairs.append((entry.hash, .string(try read(entry).base64EncodedString()))) }
        return JSONText.write(.object(JSONObject([("snapshot", snapshot.json), ("contents", .object(contents))])))
    }

    public func publish(_ snapshot: DeviceSnapshot, read: (SyncEntry) throws -> Data, readVersion: String?) async throws -> String? {
        let bundle = try Self.bundle(snapshot, read: read)
        let size = bundle.utf8.count
        if size > Self.maxBundleBytes {
            throw GistError(status: 0, message: "Your tasks are too big for a gist (\(size / (1024 * 1024)) MB with attachments; GitHub serves up to \(Self.maxBundleBytes / (1024 * 1024)) MB per file). Sync through OneDrive or iCloud Drive instead, or move big attachments out of the tasks folder.")
        }
        var files = JSONObject([("device-\(snapshot.device).json", .object(JSONObject([("content", .string(bundle))])))])
        let id = try await gistId()
        var request: URLRequest
        if let id {
            request = try await self.request("PATCH", "/gists/\(id)")
            request.httpBody = body(.object(JSONObject([("files", .object(files))])))
        } else {
            files[Self.marker] = .object(JSONObject([("content", .string(Self.markerText))]))
            request = try await self.request("POST", "/gists")
            request.httpBody = body(.object(JSONObject([("description", .string(gistDescription)), ("public", .bool(false)), ("files", .object(files))])))
        }
        let (data, response) = try await http.send(request)
        try ensure((data, response))
        let gist = (try? JSONText.parse(data))?.objectValue ?? JSONObject()
        if id == nil, let created = gist["id"]?.stringValue {
            saveId(created)
            // Another device may have made one at the same moment: look again next time.
            looked = false
        }
        // Our write is the only change since we read the gist when the version before it is the one we read.
        let history = gist["history"]?.arrayValue ?? []
        let before = history.count > 1 ? history[1].objectValue?["version"]?.stringValue : nil
        if let readVersion, let before, before == Self.split(readVersion).revision {
            return Self.join(response.value(forHTTPHeaderField: "ETag"), Self.revision(gist))
        }
        return nil
    }

    private static func join(_ etag: String?, _ revision: String?) -> String { "\(etag ?? "")|\(revision ?? "")" }

    private static func split(_ version: String) -> (etag: String, revision: String) {
        guard let bar = version.oLastIndex("|") else { return (version, "") }
        return (version.sub(0, bar), version.sub(bar + 1))
    }

    private static func revision(_ gist: JSONObject) -> String? { gist["history"]?.arrayValue?.first?.objectValue?["version"]?.stringValue }

    private func body(_ value: JSONValue) -> Data { Data(JSONText.write(value).utf8) }

    private func ensure(_ result: (Data, HTTPURLResponse)) throws {
        let (data, response) = result
        guard !(200..<300).contains(response.statusCode) else { return }
        let detail = String(decoding: data, as: UTF8.self)
        let message: String
        switch response.statusCode {
        case 401: message = "GitHub didn't accept the sign-in. Sign in to GitHub again (with access to gists)."
        case 404: message = "The sync gist wasn't found (was it deleted?)."
        case 403, 429: message = "GitHub is limiting requests right now; sync tries again later."
        default: message = "GitHub answered \(response.statusCode): \(detail.prefix(200))"
        }
        throw GistError(status: response.statusCode, message: message)
    }

    /// The gist used, found by its description and marker file (made earlier on another device, or before a reinstall).
    /// Looked for once per start (and after making one): if two devices each made one at the same moment, every device
    /// moves to the oldest, so they meet again.
    private func gistId() async throws -> String? {
        let known = loadId().flatMap { $0.isEmpty ? nil : $0 }
        if let known, looked { return known }
        var found: [(id: String, created: String)] = []
        for page in 1...5 {
            let request = try await self.request("GET", "/gists?per_page=100&page=\(page)")
            let (data, response) = try await http.send(request)
            try ensure((data, response))
            let gists = (try? JSONText.parse(data))?.arrayValue ?? []
            for gist in gists {
                guard let o = gist.objectValue, Ordinal.equalIgnoreCase(o["description"]?.stringValue, gistDescription),
                      o["files"]?.objectValue?[Self.marker] != nil, let id = o["id"]?.stringValue else { continue }
                found.append((id, o["created_at"]?.stringValue ?? ""))
            }
            if gists.count < 100 { break }
        }
        looked = true
        guard let oldest = found.sorted(by: { Ordinal.less($0.created, $1.created) || ($0.created == $1.created && Ordinal.less($0.id, $1.id)) }).first?.id else { return known }
        if oldest != known { saveId(oldest) }
        return oldest
    }

    private func raw(_ url: String) async throws -> String {
        // The token only ever goes to GitHub.
        guard let u = URL(string: url) else { throw SyncDataError("Not a link: \(url)") }
        let request = GitHubSession.mayCarryToken(u) ? try await self.request("GET", url) : URLRequest(url: u)
        let (data, response) = try await http.send(request)
        try ensure((data, response))
        if data.count > Self.maxBundleBytes * 2 { throw SyncDataError("A device's sync file is too big.") }
        return String(decoding: data, as: UTF8.self)
    }

    private func request(_ method: String, _ path: String) async throws -> URLRequest {
        var request = URLRequest(url: URL(string: path.hasPrefix("https://") ? path : api + path)!)
        request.httpMethod = method
        if GitHubSession.mayCarryToken(request.url) { request.setValue("Bearer \(try await token())", forHTTPHeaderField: "Authorization") }
        request.setValue("application/vnd.github+json", forHTTPHeaderField: "Accept")
        request.setValue("TodoTracker-Sync", forHTTPHeaderField: "User-Agent")
        request.setValue("2022-11-28", forHTTPHeaderField: "X-GitHub-Api-Version")
        if method != "GET" { request.setValue("application/json; charset=utf-8", forHTTPHeaderField: "Content-Type") }
        request.cachePolicy = .reloadIgnoringLocalCacheData
        return request
    }
}
