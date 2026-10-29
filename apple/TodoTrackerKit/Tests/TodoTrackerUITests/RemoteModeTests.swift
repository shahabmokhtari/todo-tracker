import XCTest
@testable import TodoTrackerKit
@testable import TodoTrackerUI

/// Stands in for a computer's phone port: answers the claim, /health and /api/export, or doesn't answer at all, or
/// says the phone was disconnected (401). Records what it's asked.
final class FakeComputer: URLProtocol {
    enum Mode { case up, down, revoked }

    private static let lock = NSLock()
    private static var storedMode = Mode.up
    private static var storedBoard = Data()
    private static var storedRequests: [String] = []

    static var mode: Mode {
        get { lock.withLock { storedMode } }
        set { lock.withLock { storedMode = newValue } }
    }

    static var board: Data {
        get { lock.withLock { storedBoard } }
        set { lock.withLock { storedBoard = newValue } }
    }

    static var requests: [String] {
        get { lock.withLock { storedRequests } }
        set { lock.withLock { storedRequests = newValue } }
    }

    private static var storedName: String?
    /// The name the phone sent with its claim.
    static var claimedName: String? {
        get { lock.withLock { storedName } }
        set { lock.withLock { storedName = newValue } }
    }

    override class func canInit(with request: URLRequest) -> Bool { true }
    override class func canonicalRequest(for request: URLRequest) -> URLRequest { request }

    override func startLoading() {
        let url = request.url!
        let line = "\(request.httpMethod ?? "GET") \(url.path) \(request.value(forHTTPHeaderField: "Authorization") ?? "-")"
        Self.lock.withLock { Self.storedRequests.append(line) }
        if url.path == "/api/phones/claim", let body = Self.body(of: request),
           let object = try? JSONSerialization.jsonObject(with: body) as? [String: Any] {
            Self.claimedName = object["name"] as? String
        }
        let status: Int
        let body: Data
        switch Self.mode {
        case .down:
            client?.urlProtocol(self, didFailWithError: URLError(.cannotConnectToHost))
            return
        case .revoked where url.path != "/api/phones/claim":
            status = 401
            body = Data(#"{"detail":"This phone isn’t connected to Todo Tracker."}"#.utf8)
        default:
            status = 200
            switch url.path {
            case "/api/phones/claim": body = Data(#"{"token":"ttp_test","computer":"Studio Mac"}"#.utf8)
            case "/api/export": body = Self.board
            case "/api/settings": body = Data(#"{"theme":"system"}"#.utf8)
            default: body = Data("{}".utf8)
            }
        }
        client?.urlProtocol(self, didReceive: HTTPURLResponse(url: url, statusCode: status, httpVersion: nil, headerFields: nil)!, cacheStoragePolicy: .notAllowed)
        client?.urlProtocol(self, didLoad: body)
        client?.urlProtocolDidFinishLoading(self)
    }

    override func stopLoading() {}

    /// URLSession hands a protocol the body as a stream.
    private static func body(of request: URLRequest) -> Data? {
        if let data = request.httpBody { return data }
        guard let stream = request.httpBodyStream else { return nil }
        stream.open()
        defer { stream.close() }
        var data = Data()
        var buffer = [UInt8](repeating: 0, count: 4096)
        while stream.hasBytesAvailable {
            let read = stream.read(&buffer, maxLength: buffer.count)
            guard read > 0 else { break }
            data.append(buffer, count: read)
        }
        return data
    }
}

/// An iPhone or iPad using a computer's tasks (Settings › Connect to a computer): pairing, reading and changing them
/// there, a computer that's out of reach, and a phone the computer disconnected.
@MainActor
final class RemoteModeTests: XCTestCase {
    private var folder: URL!
    private var suite: String!
    private var secrets: InMemorySecretStore!

    private let link = "todotracker://pair?v=1&hosts=192.168.1.20:5318,100.101.102.103:5318&code=c0de&fp=bkHHwIb84H1Te4ePQXbl3y-dAm4kMyZc47CFWjSYarI&name=Studio%20Mac"

    override func setUp() async throws {
        folder = FileManager.default.temporaryDirectory.appendingPathComponent(UUID().uuidString, isDirectory: true)
        try FileManager.default.createDirectory(at: folder, withIntermediateDirectories: true)
        suite = "RemoteModeTests-\(UUID().uuidString)"
        secrets = InMemorySecretStore()
        FakeComputer.mode = .up
        FakeComputer.requests = []
        FakeComputer.claimedName = nil
        let computer = TaskBoard()
        _ = try computer.addTask(NewTask("On the computer"), now: Date())
        FakeComputer.board = try BoardCodec.encode(computer)
    }

    override func tearDown() async throws {
        try? FileManager.default.removeItem(at: folder)
        UserDefaults(suiteName: suite)?.removePersistentDomain(forName: suite)
    }

    private func setup() -> PhoneSetup {
        PhoneSetup(pairings: PairingStore(defaults: UserDefaults(suiteName: suite)!, secrets: secrets),
                   cacheURL: folder.appendingPathComponent("computer-board.json"),
                   phoneName: "Test iPhone") { _ in
            let configuration = URLSessionConfiguration.ephemeral
            configuration.protocolClasses = [FakeComputer.self]
            return URLSession(configuration: configuration)
        }
    }

    /// A board on this phone with one task, saved.
    private func phoneStore() throws -> BoardFileStore {
        let store = BoardFileStore(url: folder.appendingPathComponent("board.json"))
        let mine = TaskBoard()
        _ = try mine.addTask(NewTask("On this phone"), now: Date())
        try store.save(mine)
        return store
    }

    private func waitUntil(_ what: String = "", _ condition: () -> Bool) async throws {
        for _ in 0..<250 {
            if condition() { return }
            try await Task.sleep(nanoseconds: 20_000_000)
        }
        XCTFail("Timed out \(what)")
    }

    private func pairedModel(_ store: BoardFileStore) async throws -> BoardModel {
        let model = BoardModel(store: store, phone: setup())
        model.open(link: URL(string: link)!)
        try await waitUntil("pairing") { model.board.items.contains { $0.title == "On the computer" } }
        return model
    }

    func testAPairingLinkConnectsThePhoneAndItUsesTheComputersTasks() async throws {
        let store = try phoneStore()
        let model = try await pairedModel(store)

        XCTAssertEqual(model.remote, .connected(host: "192.168.1.20:5318"))
        XCTAssertEqual(model.remoteComputer, "Studio Mac")
        XCTAssertTrue(model.isRemote)
        XCTAssertNil(model.remoteBanner)
        XCTAssertFalse(model.canOpenInBrowser, "no browser sign-in from a phone")
        XCTAssertEqual(secrets.read("phone-token"), "ttp_test", "the token is a secret")
        XCTAssertTrue(FakeComputer.requests.contains("POST /api/phones/claim -"))
        XCTAssertEqual(FakeComputer.claimedName, "Test iPhone", "the device's name when none was typed")
        XCTAssertTrue(FakeComputer.requests.contains("GET /api/export Bearer ttp_test"))

        let task = try XCTUnwrap(model.board.items.first)
        model.complete(task.id)
        try await waitUntil("the change") { FakeComputer.requests.contains("POST /api/items/\(task.id.uuidString.lowercased())/complete Bearer ttp_test") }
        XCTAssertEqual(try store.load().items.map(\.title), ["On this phone"], "the board on this phone isn't touched")
    }

    func testAComputerOutOfReachShowsTheLastReadWithoutChanges() async throws {
        let store = try phoneStore()
        let model = try await pairedModel(store)
        try await waitUntil("the copy") { model.remoteLastRead != nil }

        FakeComputer.mode = .down
        await model.pullToRefresh()
        try await waitUntil("out of reach") { model.remote == .unreachable }

        XCTAssertEqual(model.board.items.map(\.title), ["On the computer"], "the tasks as last read")
        XCTAssertTrue(model.remoteBanner?.hasPrefix("Can’t reach Studio Mac. You’re seeing your tasks from ") == true)
        let task = try XCTUnwrap(model.board.items.first)
        FakeComputer.requests = []
        model.complete(task.id)
        XCTAssertFalse(task.isDone)
        XCTAssertEqual(model.status, model.remoteBanner)
        XCTAssertFalse(FakeComputer.requests.contains { $0.hasPrefix("POST /api/items") })
        XCTAssertEqual(try store.load().items.map(\.title), ["On this phone"])

        // Both addresses were tried before giving up.
        FakeComputer.requests = []
        FakeComputer.mode = .up
        model.becameActive()
        try await waitUntil("back") { model.remote?.canChange == true }
        XCTAssertNil(model.remoteBanner)
    }

    func testAtLaunchThePairedComputersTasksShowAtOnceFromTheLastRead() async throws {
        let store = try phoneStore()
        _ = try await pairedModel(store)

        FakeComputer.mode = .down
        let next = BoardModel(store: store, phone: setup())
        next.connectToPairedComputer()
        XCTAssertEqual(next.board.items.map(\.title), ["On the computer"], "shown before the computer answers")
        XCTAssertEqual(next.remote, .connecting)
        try await waitUntil("out of reach") { next.remote == .unreachable }
        let tried = FakeComputer.requests.filter { $0.hasPrefix("GET /health") }
        XCTAssertEqual(tried.count, 2, "every address: \(tried)")
    }

    func testADisconnectedPhoneSaysSoAndGoesBackToItsOwnTasks() async throws {
        let store = try phoneStore()
        let model = try await pairedModel(store)

        FakeComputer.mode = .revoked
        await model.pullToRefresh()
        try await waitUntil("disconnected") { model.remote == .revoked }

        XCTAssertFalse(model.isRemote)
        XCTAssertEqual(model.board.items.map(\.title), ["On this phone"])
        XCTAssertTrue(model.remoteBanner?.hasPrefix("Studio Mac disconnected this phone") == true)
        XCTAssertNil(secrets.read("phone-token"), "a token the computer no longer knows is forgotten")

        // Its own tasks can be changed again.
        let mine = try XCTUnwrap(model.board.items.first)
        model.complete(mine.id)
        XCTAssertTrue(mine.isDone)
    }

    func testDisconnectingGoesBackToTheTasksOnThisPhoneAsTheyWere() async throws {
        let store = try phoneStore()
        let model = try await pairedModel(store)

        model.disconnectComputer()

        XCTAssertNil(model.remote)
        XCTAssertFalse(model.usesServer)
        XCTAssertEqual(model.board.items.map(\.title), ["On this phone"])
        XCTAssertEqual(model.status, "Disconnected from Studio Mac. These are the tasks on this phone, as you left them.")
        XCTAssertNil(PairingStore(defaults: UserDefaults(suiteName: suite)!, secrets: secrets).load())
        XCTAssertFalse(FileManager.default.fileExists(atPath: folder.appendingPathComponent("computer-board.json").path))
    }

    func testTheNameTypedForThePhoneIsSentAndRemembered() async throws {
        let model = BoardModel(store: try phoneStore(), phone: setup())
        XCTAssertEqual(model.phoneName, "Test iPhone", "prefilled with the device's name")
        model.pair(link: link, phoneName: "Shahab’s iPad")
        try await waitUntil("pairing") { model.remote?.canChange == true }
        XCTAssertEqual(FakeComputer.claimedName, "Shahab’s iPad")

        model.disconnectComputer()
        XCTAssertEqual(BoardModel(store: nil, phone: setup()).phoneName, "Shahab’s iPad", "offered again next time")
    }

    func testArchivingWhileConnectedGoesToTheComputer() async throws {
        let model = try await pairedModel(try phoneStore())
        let task = try XCTUnwrap(model.board.items.first)
        model.archive(task.id)
        try await waitUntil("archive") { FakeComputer.requests.contains("POST /api/items/\(task.id.uuidString.lowercased())/archive Bearer ttp_test") }
    }

    func testABadLinkSaysWhyAndChangesNothing() async throws {
        let model = BoardModel(store: try phoneStore(), phone: setup())
        model.open(link: URL(string: link.replacingOccurrences(of: "v=1", with: "v=9"))!)
        XCTAssertEqual(model.pairingError, "That link is from a newer Todo Tracker. Update this app, then scan it again.")
        XCTAssertNil(model.remote)
        XCTAssertTrue(FakeComputer.requests.isEmpty)
    }
}
