import Foundation
import XCTest
@testable import OneThingKit

/// tests/fixtures/feedback.json: what Send feedback sends and what each answer means, the same in C#, the web app,
/// Swift and Kotlin. The relay is never called: a fake answers instead.
final class FeedbackTests: XCTestCase {
    private func fixture() throws -> [String: Any] {
        var root = URL(fileURLWithPath: #filePath)
        for _ in 0..<5 { root.deleteLastPathComponent() }
        let data = try Data(contentsOf: root.appendingPathComponent("tests/fixtures/feedback.json"))
        return try XCTUnwrap(JSONSerialization.jsonObject(with: data) as? [String: Any])
    }

    private func string(_ value: Any?) -> String? { value as? String }

    func testTheRelayKeyAndTextsAreTheSharedOnes() throws {
        let f = try fixture()
        let relay = try XCTUnwrap(f["relay"] as? [String: Any])
        XCTAssertEqual(Feedback.relayURL.absoluteString, relay["url"] as? String)
        XCTAssertEqual(Feedback.publicKey, relay["publicKey"] as? String)
        XCTAssertEqual(Feedback.maxMessage, relay["maxMessage"] as? Int)
        if let maxContact = relay["maxContact"] as? Int { XCTAssertEqual(Feedback.maxContact, maxContact) }
        let texts = try XCTUnwrap(f["texts"] as? [String: String])
        XCTAssertEqual(Feedback.sentText, texts["sent"])
        XCTAssertEqual(Feedback.retryText, texts["retry"])
        XCTAssertEqual(Feedback.failedText, texts["failed"])
        XCTAssertEqual(Feedback.finePrint, texts["finePrint"])
        XCTAssertEqual(Feedback.contactHint, texts["contactHint"])
        let kinds = try XCTUnwrap(f["kinds"] as? [[String: String]])
        XCTAssertEqual(FeedbackKind.allCases.map(\.rawValue), kinds.map { $0["id"]! })
        XCTAssertEqual(FeedbackKind.allCases.map(\.label), kinds.map { $0["label"]! })
        XCTAssertEqual(FeedbackSource.allCases.map(\.rawValue), f["sources"] as? [String])
    }

    func testEveryDraftBecomesTheBodyTheFixtureSays() throws {
        for c in try XCTUnwrap(fixture()["bodies"] as? [[String: Any]]) {
            let name = c["name"] as? String ?? ""
            let d = try XCTUnwrap(c["draft"] as? [String: Any])
            let v = try XCTUnwrap(c["device"] as? [String: Any])
            let draft = FeedbackDraft(kind: try XCTUnwrap(FeedbackKind(rawValue: d["kind"] as! String)), message: d["message"] as! String,
                                      contact: string(d["contact"]), source: try XCTUnwrap(FeedbackSource(rawValue: d["source"] as! String)),
                                      clientID: d["clientID"] as! String)
            let device = FeedbackDevice(platform: v["platform"] as! String, appVersion: string(v["appVersion"]), build: string(v["build"]),
                                        os: string(v["os"]), device: string(v["device"]), language: string(v["language"]), screen: string(v["screen"]))
            let body = Feedback.body(draft, device: device, maxMessage: c["maxMessage"] as? Int ?? Feedback.maxMessage,
                                     maxContact: c["maxContact"] as? Int ?? Feedback.maxContact)
            guard let expected = c["body"] as? [String: Any] else {
                XCTAssertNil(body, name)
                continue
            }
            let made = try XCTUnwrap(body, name)
            let json = try XCTUnwrap(JSONSerialization.jsonObject(with: made.json()) as? NSDictionary, name)
            XCTAssertEqual(json, expected as NSDictionary, name)

            // The keys in the relay's order, metadata's too.
            let text = String(decoding: made.json(), as: UTF8.self)
            let topKeys = ["message", "title", "contact", "clientID", "labels", "metadata"].filter { expected[$0] != nil }
            let positions = topKeys.map { text.range(of: "\"\($0)\":")!.lowerBound }
            XCTAssertEqual(positions, positions.sorted(), name)
            let order = try metadataKeys(name: name)
            XCTAssertEqual(made.metadata.map(\.0), order, name)
            let metadataText = String(text[text.range(of: "\"metadata\":")!.upperBound...])
            let metaPositions = order.map { metadataText.range(of: "\"\($0)\":")!.lowerBound }
            XCTAssertEqual(metaPositions, metaPositions.sorted(), name)
        }
    }

    /// The metadata keys of a fixture case in the order the file lists them (JSONSerialization forgets the order).
    private func metadataKeys(name: String) throws -> [String] {
        var root = URL(fileURLWithPath: #filePath)
        for _ in 0..<5 { root.deleteLastPathComponent() }
        let text = try String(contentsOf: root.appendingPathComponent("tests/fixtures/feedback.json"), encoding: .utf8)
        let encoder = JSONEncoder()
        encoder.outputFormatting = .withoutEscapingSlashes
        let caseText = String(text[text.range(of: "\"name\": \(String(decoding: try encoder.encode(name), as: UTF8.self))")!.upperBound...])
        let meta = caseText[caseText.range(of: "\"metadata\": {")!.upperBound...]
        let block = meta[..<meta.firstIndex(of: "}")!]
        let regex = try NSRegularExpression(pattern: "\"([^\"]+)\":")
        let s = String(block)
        return regex.matches(in: s, range: NSRange(s.startIndex..., in: s)).map { String(s[Range($0.range(at: 1), in: s)!]) }
    }

    func testEveryAnswerMeansWhatTheFixtureSays() throws {
        for c in try XCTUnwrap(fixture()["outcomes"] as? [[String: Any]]) {
            XCTAssertEqual(Feedback.classify(status: c["status"] as? Int).rawValue, c["outcome"] as? String, "\(c)")
        }
    }

    func testTheCounterCountsCodePoints() {
        XCTAssertEqual(Feedback.length("  😀 a\u{7} "), 3)
        XCTAssertEqual(Feedback.cleanMessage(String(repeating: "😀", count: 2001)).unicodeScalars.count, 2000)
    }

    func testTheTitleIsTheNotesFirstLineCutTo256CodePoints() {
        XCTAssertEqual(Feedback.title(.bug, platform: "iOS", message: "It crashes\r\nwhen I tap Done"), "Bug · iOS: It crashes")
        let long = Feedback.title(.idea, platform: "macOS", message: String(repeating: "😀", count: 300))
        XCTAssertEqual(long.unicodeScalars.count, 256)
        XCTAssertTrue(long.hasPrefix("Idea · macOS: 😀"))
        XCTAssertTrue(long.hasSuffix("😀…"), "cut to 255, then an ellipsis")
    }

    func testAReplyAddressLosesControlCharactersAndIsCutTo200CodePoints() {
        XCTAssertEqual(Feedback.cleanContact(" \u{7}me@example.com\r\n\u{0} "), "me@example.com")
        XCTAssertEqual(Feedback.cleanContact("me@\u{85}example.com"), "me@example.com")
        XCTAssertNil(Feedback.cleanContact(" \u{1b}\t "))
        XCTAssertNil(Feedback.cleanContact(nil))
        XCTAssertEqual(Feedback.cleanContact(String(repeating: "😀", count: 201))?.unicodeScalars.count, 200)
        XCTAssertEqual(Feedback.cleanContact("ab cd", maxContact: 3), "ab")
    }

    func testDebugBuildsOnlySendWithTheFlag() {
        XCTAssertTrue(Feedback.sendsForReal(debugBuild: false, environment: [:]))
        XCTAssertFalse(Feedback.sendsForReal(debugBuild: true, environment: [:]))
        XCTAssertFalse(Feedback.sendsForReal(debugBuild: true, environment: ["ONETHING_FEEDBACK_SEND": "0"]))
        XCTAssertTrue(Feedback.sendsForReal(debugBuild: true, environment: ["ONETHING_FEEDBACK_SEND": "1"]))
    }

    func testANoteThatCouldntBeSentIsSentAgainWithTheSameClientID() async throws {
        FakeRelay.reset(statuses: [503, 202])
        let config = URLSessionConfiguration.ephemeral
        config.protocolClasses = [FakeRelay.self]
        let client = FeedbackClient(session: URLSession(configuration: config), endpoint: URL(string: "https://relay.test/v1/issues")!, sendsForReal: true)
        let body = try XCTUnwrap(Feedback.body(FeedbackDraft(kind: .idea, message: "More colors", clientID: "6f1c2d3e-4b5a-4c7d-8e9f-0a1b2c3d4e5f"),
                                               device: FeedbackDevice(platform: "iOS", appVersion: "1.0")))

        let first = await client.send(body)
        XCTAssertEqual(first, .retry)
        XCTAssertEqual(first.text, Feedback.retryText)
        let second = await client.send(body)
        XCTAssertEqual(second, .sent)

        let seen = FakeRelay.seen()
        XCTAssertEqual(seen.count, 2)
        for request in seen {
            XCTAssertEqual(request.url?.host, "relay.test")
            XCTAssertEqual(request.httpMethod, "POST")
            XCTAssertEqual(request.value(forHTTPHeaderField: "Authorization"), "Bearer pk_gGPPMjRSsuHaXRdgdXvUevvL")
            XCTAssertEqual(request.value(forHTTPHeaderField: "Content-Type"), "application/json")
        }
        let ids = try FakeRelay.bodies().map { try XCTUnwrap(JSONSerialization.jsonObject(with: $0) as? [String: Any])["clientID"] as? String }
        XCTAssertEqual(ids, ["6f1c2d3e-4b5a-4c7d-8e9f-0a1b2c3d4e5f", "6f1c2d3e-4b5a-4c7d-8e9f-0a1b2c3d4e5f"])
    }

    func testOfflineMeansTryAgainAndARefusalFails() async throws {
        let config = URLSessionConfiguration.ephemeral
        config.protocolClasses = [FakeRelay.self]
        let client = FeedbackClient(session: URLSession(configuration: config), endpoint: URL(string: "https://relay.test/v1/issues")!, sendsForReal: true)
        let body = try XCTUnwrap(Feedback.body(FeedbackDraft(message: "Hi"), device: FeedbackDevice(platform: "macOS")))
        FakeRelay.reset(statuses: [-1])
        let offline = await client.send(body)
        XCTAssertEqual(offline, .retry)
        FakeRelay.reset(statuses: [400])
        let refused = await client.send(body)
        XCTAssertEqual(refused, .failed)
    }

    func testADebugBuildLogsTheNoteInsteadOfSendingIt() async throws {
        FakeRelay.reset(statuses: [500])
        let config = URLSessionConfiguration.ephemeral
        config.protocolClasses = [FakeRelay.self]
        let logged = Logged()
        let client = FeedbackClient(session: URLSession(configuration: config), endpoint: URL(string: "https://relay.test/v1/issues")!, sendsForReal: false, log: { logged.add($0) })
        let body = try XCTUnwrap(Feedback.body(FeedbackDraft(message: "Hello"), device: FeedbackDevice(platform: "iOS")))
        let outcome = await client.send(body)
        XCTAssertEqual(outcome, .sent)
        XCTAssertTrue(FakeRelay.seen().isEmpty)
        XCTAssertTrue(logged.lines.first?.contains("\"message\":\"Hello\"") == true)
    }
}

private final class Logged: @unchecked Sendable {
    private let lock = NSLock()
    private var all: [String] = []
    func add(_ line: String) { lock.lock(); all.append(line); lock.unlock() }
    var lines: [String] { lock.lock(); defer { lock.unlock() }; return all }
}

/// Answers in place of the relay: the next status from the list (-1: no connection), and remembers each request.
final class FakeRelay: URLProtocol {
    private static let lock = NSLock()
    nonisolated(unsafe) private static var statuses: [Int] = []
    nonisolated(unsafe) private static var requests: [URLRequest] = []
    nonisolated(unsafe) private static var payloads: [Data] = []

    static func reset(statuses: [Int]) {
        lock.lock()
        self.statuses = statuses
        requests = []
        payloads = []
        lock.unlock()
    }

    static func seen() -> [URLRequest] { lock.lock(); defer { lock.unlock() }; return requests }
    static func bodies() -> [Data] { lock.lock(); defer { lock.unlock() }; return payloads }

    override class func canInit(with request: URLRequest) -> Bool { true }
    override class func canonicalRequest(for request: URLRequest) -> URLRequest { request }

    override func startLoading() {
        var data = request.httpBody ?? Data()
        if data.isEmpty, let stream = request.httpBodyStream {
            stream.open()
            var buffer = [UInt8](repeating: 0, count: 4096)
            while stream.hasBytesAvailable {
                let n = stream.read(&buffer, maxLength: buffer.count)
                if n <= 0 { break }
                data.append(buffer, count: n)
            }
            stream.close()
        }
        Self.lock.lock()
        Self.requests.append(request)
        Self.payloads.append(data)
        let status = Self.statuses.isEmpty ? 500 : Self.statuses.removeFirst()
        Self.lock.unlock()
        if status < 0 {
            client?.urlProtocol(self, didFailWithError: URLError(.notConnectedToInternet))
            return
        }
        let response = HTTPURLResponse(url: request.url!, statusCode: status, httpVersion: "HTTP/1.1", headerFields: ["Content-Type": "application/json"])!
        client?.urlProtocol(self, didReceive: response, cacheStoragePolicy: .notAllowed)
        client?.urlProtocol(self, didLoad: Data("{\"ref\":\"IS-TEST\",\"status\":\"queued\"}".utf8))
        client?.urlProtocolDidFinishLoading(self)
    }

    override func stopLoading() {}
}
