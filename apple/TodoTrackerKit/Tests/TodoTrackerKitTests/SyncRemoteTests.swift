import Foundation
import XCTest
@testable import TodoTrackerKit

/// An in-memory GitHub gist API (the parts sync uses), like the C# tests' fake.
final class FakeGitHub: HTTPTransport {
    struct Gist {
        var id: String
        var description: String
        var files: [String: String]
        var history: [String]
        var created: String
    }

    var gists: [Gist] = []
    var requests: [URLRequest] = []
    var tokens: [String?] = []
    private var counter = 0

    private func json(_ gist: Gist) -> JSONValue {
        let files = JSONObject(gist.files.sorted { $0.key < $1.key }.map { ($0.key, .object(JSONObject([("filename", .string($0.key)), ("content", .string($0.value)), ("truncated", .bool(false))]))) })
        return .object(JSONObject([("id", .string(gist.id)), ("description", .string(gist.description)), ("created_at", .string(gist.created)), ("files", .object(files)),
                                   ("history", .array(gist.history.reversed().map { .object(JSONObject([("version", .string($0))])) }))]))
    }

    private func respond(_ request: URLRequest, _ status: Int, _ body: JSONValue?, etag: String? = nil) -> (Data, HTTPURLResponse) {
        var headers: [String: String] = [:]
        if let etag { headers["ETag"] = etag }
        let response = HTTPURLResponse(url: request.url!, statusCode: status, httpVersion: "HTTP/1.1", headerFields: headers)!
        return (body.map { Data(JSONText.write($0).utf8) } ?? Data(), response)
    }

    func send(_ request: URLRequest) async throws -> (Data, HTTPURLResponse) {
        requests.append(request)
        tokens.append(request.value(forHTTPHeaderField: "Authorization"))
        let url = request.url!
        let path = url.path
        let body = request.httpBody.flatMap { try? JSONText.parse($0).objectValue }
        switch (request.httpMethod ?? "GET", path) {
        case ("GET", "/gists"):
            return respond(request, 200, .array(gists.map(json)))
        case ("POST", "/gists"):
            counter += 1
            var gist = Gist(id: "gist\(counter)", description: body?["description"]?.stringValue ?? "", files: [:], history: [], created: "2026-01-0\(counter)T00:00:00Z")
            for (name, file) in body?["files"]?.objectValue?.pairs ?? [] { gist.files[name] = file.objectValue?["content"]?.stringValue }
            counter += 1
            gist.history.append("rev\(counter)")
            gists.append(gist)
            return respond(request, 201, json(gist), etag: "\"rev\(counter)\"")
        default:
            let id = String(path.dropFirst("/gists/".count))
            guard let i = gists.firstIndex(where: { $0.id == id }) else { return respond(request, 404, nil) }
            if request.httpMethod == "PATCH" {
                for (name, file) in body?["files"]?.objectValue?.pairs ?? [] { gists[i].files[name] = file.isNull ? nil : file.objectValue?["content"]?.stringValue }
                counter += 1
                gists[i].history.append("rev\(counter)")
            }
            let etag = "\"\(gists[i].history.last!)\""
            if request.httpMethod == "GET", request.value(forHTTPHeaderField: "If-None-Match") == etag { return respond(request, 304, nil, etag: etag) }
            return respond(request, 200, json(gists[i]), etag: etag)
        }
    }
}

/// tests/fixtures/sync/manifest.json: what a device publishes in a folder and in a gist, byte for byte.
final class SyncRemoteTests: XCTestCase {
    private func fixtureSnapshot() throws -> (snapshot: DeviceSnapshot, contents: [String: Data], fixture: [String: Any]) {
        let fixture = try Fixtures.json("sync/manifest.json")
        let folder = fixture["folder"] as! [String: Any]
        let snapshot = try XCTUnwrap(try DeviceSnapshot.decode(Data((folder["device"] as! String).utf8)))
        let contents = (fixture["contents"] as! [String: String]).mapValues { Data($0.utf8) }
        return (snapshot, contents, fixture)
    }

    func testTheDeviceFileIsWrittenExactlyAsCSharpWritesIt() throws {
        let (snapshot, _, fixture) = try fixtureSnapshot()
        let folder = fixture["folder"] as! [String: Any]
        XCTAssertEqual(String(decoding: snapshot.encoded(), as: UTF8.self), folder["device"] as! String)
        // Ticks survive: the fraction is kept exactly.
        XCTAssertEqual(snapshot.entries.first { $0.key == "order" }?.since?.description, "2026-01-05T09:00:00.123+00:00")
    }

    func testAFolderGetsTheBlobsAndTheDeviceFile() async throws {
        let (snapshot, contents, fixture) = try fixtureSnapshot()
        let folder = fixture["folder"] as! [String: Any]
        let temp = TempDir()
        let remote = FolderRemote(root: temp.url)
        _ = try await remote.publish(snapshot, read: { contents[$0.hash]! }, readVersion: nil)
        let device = try Data(contentsOf: temp.url.appendingPathComponent(folder["path"] as! String))
        XCTAssertEqual(String(decoding: device, as: UTF8.self), folder["device"] as! String)
        for blob in folder["blobs"] as! [String] {
            let data = try Data(contentsOf: temp.url.appendingPathComponent(blob))
            XCTAssertEqual(SyncHash.of(data), (blob as NSString).lastPathComponent)
        }

        // Another device reads it back (and nothing new next time).
        let read = try await remote.read(self: "ffffffffffffffffffffffffffffffff", knownVersion: nil)
        XCTAssertEqual(read?.devices.map(\.device), [snapshot.device])
        let again = try await remote.read(self: "ffffffffffffffffffffffffffffffff", knownVersion: read!.version)
        // The reader hasn't published yet (so it's always told), but the version is the same.
        XCTAssertEqual(again?.version, read?.version)
        XCTAssertEqual(again?.selfMissing, true)
        let entry = snapshot.entries.first { $0.key.hasPrefix("t/") }!
        let content = try await remote.readContent(snapshot, entry)
        XCTAssertEqual(content, contents[entry.hash])
    }

    func testAGistGetsTheBundleAndTheMarker() async throws {
        let (snapshot, contents, fixture) = try fixtureSnapshot()
        let gistFixture = fixture["gist"] as! [String: Any]
        XCTAssertEqual(try GistRemote.bundle(snapshot, read: { contents[$0.hash]! }), gistFixture["bundle"] as! String)

        let github = FakeGitHub()
        var saved: String?
        let remote = GistRemote(http: github, token: { "secret" }, loadId: { saved }, saveId: { saved = $0 })
        _ = try await remote.publish(snapshot, read: { contents[$0.hash]! }, readVersion: nil)
        let gist = try XCTUnwrap(github.gists.first)
        XCTAssertEqual(gist.description, gistFixture["description"] as! String)
        XCTAssertEqual(gist.files.keys.sorted(), gistFixture["files"] as! [String])
        XCTAssertEqual(gist.files[GistRemote.marker], gistFixture["marker"] as? String)
        XCTAssertEqual(gist.files["device-\(snapshot.device).json"], gistFixture["bundle"] as? String)
        XCTAssertEqual(saved, gist.id)
        XCTAssertTrue(github.tokens.allSatisfy { $0 == "Bearer secret" })

        // Another device finds the gist, reads the snapshot and its contents; unchanged is a 304.
        let other = GistRemote(http: github, token: { "secret" }, loadId: { nil }, saveId: { _ in })
        let read = try await other.read(self: "ffffffffffffffffffffffffffffffff", knownVersion: nil)
        XCTAssertEqual(read?.devices.map(\.device), [snapshot.device])
        let entry = snapshot.entries.first { $0.key.hasPrefix("t/") }!
        let content = try await other.readContent(read!.devices[0], entry)
        XCTAssertEqual(content, contents[entry.hash])
        let again = try await other.read(self: "ffffffffffffffffffffffffffffffff", knownVersion: read!.version)
        XCTAssertNil(again)
        XCTAssertEqual(github.requests.last?.value(forHTTPHeaderField: "If-None-Match"), "\"\(gist.history.last!)\"")
    }

    func testAGistTooBigIsRefusedWithTheSameWords() async throws {
        let github = FakeGitHub()
        let remote = GistRemote(http: github, token: { "t" }, loadId: { nil }, saveId: { _ in })
        let big = Data(repeating: 0x41, count: GistRemote.maxBundleBytes)
        let entry = SyncEntry(key: "f/Work/big.bin", path: "Work/big.bin", hash: SyncHash.of(big), since: .minValue)
        let snapshot = DeviceSnapshot(device: "0a1b2c3d4e5f60718293a4b5c6d7e8f9", name: "Phone", at: .minValue, entries: [entry])
        do {
            _ = try await remote.publish(snapshot, read: { _ in big }, readVersion: nil)
            XCTFail("published")
        } catch {
            XCTAssertTrue(error.localizedDescription.hasPrefix("Your tasks are too big for a gist (12 MB with attachments; GitHub serves up to 9 MB per file)."), error.localizedDescription)
        }
        XCTAssertTrue(github.gists.isEmpty)
    }

    func testTheTokenOnlyEverGoesToGitHub() async throws {
        // A redirect elsewhere goes on without the token; one to GitHub keeps it.
        var request = URLRequest(url: URL(string: "https://evil.example.com/x")!)
        request.setValue("Bearer secret", forHTTPHeaderField: "Authorization")
        XCTAssertNil(GitHubSession.redirect(request).value(forHTTPHeaderField: "Authorization"))
        request.url = URL(string: "https://gist.githubusercontent.com/raw/x")!
        XCTAssertEqual(GitHubSession.redirect(request).value(forHTTPHeaderField: "Authorization"), "Bearer secret")
        request.url = URL(string: "http://api.github.com/x")!
        XCTAssertNil(GitHubSession.redirect(request).value(forHTTPHeaderField: "Authorization"))
        request.url = URL(string: "https://raw.githubusercontent.com.evil.example/x")!
        XCTAssertNil(GitHubSession.redirect(request).value(forHTTPHeaderField: "Authorization"))

        // A big device file is fetched from its raw_url: with the token only if that is GitHub's.
        let github = TruncatingGitHub()
        let remote = GistRemote(http: github, token: { "secret" }, loadId: { "g1" }, saveId: { _ in })
        _ = try await remote.read(self: "ffffffffffffffffffffffffffffffff", knownVersion: nil)
        let raw = try XCTUnwrap(github.requests.first { $0.url?.host == "evil.example.com" })
        XCTAssertNil(raw.value(forHTTPHeaderField: "Authorization"))
        XCTAssertTrue(github.requests.filter { $0.url?.host == "api.github.com" }.allSatisfy { $0.value(forHTTPHeaderField: "Authorization") == "Bearer secret" })
    }
}

/// A gist whose device file is "too big" and points somewhere that isn't GitHub.
final class TruncatingGitHub: HTTPTransport {
    var requests: [URLRequest] = []

    func send(_ request: URLRequest) async throws -> (Data, HTTPURLResponse) {
        requests.append(request)
        let ok = HTTPURLResponse(url: request.url!, statusCode: 200, httpVersion: nil, headerFields: ["ETag": "\"e\""])!
        if request.url?.host == "evil.example.com" { return (Data("{}".utf8), ok) }
        if request.url?.path == "/gists" { return (Data("[]".utf8), ok) }
        let file = JSONObject([("truncated", .bool(true)), ("raw_url", .string("https://evil.example.com/device.json"))])
        let gist = JSONObject([("id", .string("g1")), ("files", .object(JSONObject([("device-0a1b2c3d4e5f60718293a4b5c6d7e8f9.json", .object(file))]))),
                               ("history", .array([.object(JSONObject([("version", .string("r1"))]))]))])
        return (Data(JSONText.write(.object(gist)).utf8), ok)
    }
}
