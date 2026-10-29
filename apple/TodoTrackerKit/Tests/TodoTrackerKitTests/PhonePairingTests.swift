import Foundation
#if canImport(Security)
import Security
#endif
import XCTest
@testable import TodoTrackerKit

/// The phone connects to a computer's tasks with the link from Settings › Phones on it (PhonesPlugin on the server).
final class PhonePairingTests: XCTestCase {
    /// A self-signed certificate (DER, as the server's PhoneCertificate makes it), and its fingerprint computed by openssl:
    /// `openssl dgst -sha256 -binary c.der | base64 | tr '+/' '-_' | tr -d '='`.
    static let der = Data(base64Encoded: "MIIBYjCCAQigAwIBAgIUSmLYQf138m7XFhKJXwm4xdehfyYwCgYIKoZIzj0EAwIwFzEVMBMGA1UEAwwMVG9kbyBUcmFja2VyMB4XDTI2MTAyOTA2NDkxMloXDTQ2MTAyNDA2NDkxMlowFzEVMBMGA1UEAwwMVG9kbyBUcmFja2VyMFkwEwYHKoZIzj0CAQYIKoZIzj0DAQcDQgAEmJ96ubhUao7V1wT/tbTX9HYjLdEY3qniiUdd5pNc+u72KfcQwqVtIYLFXcUzK8oqX9CStwyHXVvFxW4g+MW2NKMyMDAwHQYDVR0OBBYEFG6cEu+iWWmTTTIOYePaoadXlcLhMA8GA1UdEwEB/wQFMAMBAf8wCgYIKoZIzj0EAwIDSAAwRQIgftDjMCed32r8hzpVzXqB3KA2cNB3PHRrT5ozjp4Ank0CIQC4rJ9TZ+0ETLCfyX1x5vv7cSmTbRdYPqpyCte6n3p7Aw==")!
    static let fp = "bkHHwIb84H1Te4ePQXbl3y-dAm4kMyZc47CFWjSYarI"

    private let link = "todotracker://pair?v=1&hosts=192.168.1.20:5318,100.101.102.103:5318&code=abc_DEF-123&fp=\(fp)&name=Shahab%27s%20MacBook"

    // MARK: The link

    func testAPairingLinkGivesTheAddressesCodeFingerprintAndName() throws {
        let parsed = try PairingLink.parse(link)
        XCTAssertEqual(parsed.hosts, ["192.168.1.20:5318", "100.101.102.103:5318"])
        XCTAssertEqual(parsed.code, "abc_DEF-123")
        XCTAssertEqual(parsed.fingerprint, Self.fp)
        XCTAssertEqual(parsed.computer, "Shahab's MacBook")
        XCTAssertTrue(PairingLink.isPairing(link))
    }

    func testALinkFromANewerVersionAsksForAnUpdate() {
        XCTAssertThrowsError(try PairingLink.parse(link.replacingOccurrences(of: "v=1", with: "v=2"))) {
            XCTAssertEqual($0 as? PairingLink.Problem, .newerVersion)
        }
    }

    func testALinkMissingAPartIsCutShort() {
        for part in ["v=1&", "hosts=192.168.1.20:5318,100.101.102.103:5318&", "code=abc_DEF-123&", "fp=\(Self.fp)&"] {
            XCTAssertThrowsError(try PairingLink.parse(link.replacingOccurrences(of: part, with: "")), part) {
                if case .missing = $0 as? PairingLink.Problem {} else { XCTFail("\(part): \($0)") }
            }
        }
    }

    func testBadAddressesAndFingerprintsAreRefused() {
        XCTAssertThrowsError(try PairingLink.parse(link.replacingOccurrences(of: "hosts=192.168.1.20:5318,100.101.102.103:5318", with: "hosts=evil.com/x:1,nohost")))
        XCTAssertThrowsError(try PairingLink.parse(link.replacingOccurrences(of: Self.fp, with: "short")))
        XCTAssertThrowsError(try PairingLink.parse(link.replacingOccurrences(of: Self.fp, with: Self.fp + "==")))
    }

    func testOnlyAddressesThatLookRightAreKept() throws {
        let parsed = try PairingLink.parse(link.replacingOccurrences(of: "hosts=192.168.1.20:5318,", with: "hosts=bad:99999,[fd7a::1]:5318,my-pc.ts.net:5318,"))
        XCTAssertEqual(parsed.hosts, ["[fd7a::1]:5318", "my-pc.ts.net:5318", "100.101.102.103:5318"])
    }

    func testOtherLinksArentPairingLinks() {
        XCTAssertFalse(PairingLink.isPairing("todotracker://task/0f8fad5b-d9cb-469f-a165-70867728950e"))
        XCTAssertFalse(PairingLink.isPairing("https://example.com/pair?v=1"))
        XCTAssertThrowsError(try PairingLink.parse("todotracker://task/abc")) { XCTAssertEqual($0 as? PairingLink.Problem, .notAPairingLink) }
        XCTAssertThrowsError(try PairingLink.parse("")) { XCTAssertEqual($0 as? PairingLink.Problem, .notAPairingLink) }
    }

    func testANameIsntNeeded() throws {
        XCTAssertEqual(try PairingLink.parse(link.replacingOccurrences(of: "&name=Shahab%27s%20MacBook", with: "")).computer, "your computer")
    }

    // MARK: The certificate

    func testTheFingerprintIsSHA256OfTheDERInBase64url() {
        XCTAssertEqual(CertificatePin.fingerprint(der: Self.der), Self.fp)
        XCTAssertTrue(CertificatePin.matches(der: Self.der, fingerprint: Self.fp))
        var other = Self.der
        other[other.count - 1] ^= 1
        XCTAssertFalse(CertificatePin.matches(der: other, fingerprint: Self.fp))
        XCTAssertFalse(CertificatePin.matches(der: Self.der, fingerprint: "not-a-fingerprint"))
    }

    #if canImport(Security)
    private func trust(_ der: Data) throws -> SecTrust {
        let certificate = try XCTUnwrap(SecCertificateCreateWithData(nil, der as CFData))
        var trust: SecTrust?
        XCTAssertEqual(SecTrustCreateWithCertificates(certificate, SecPolicyCreateSSL(true, "192.168.1.20" as CFString), &trust), errSecSuccess)
        return try XCTUnwrap(trust)
    }

    func testOnlyThePairedCertificateIsTrusted() throws {
        let server = try trust(Self.der)
        XCTAssertEqual(PinnedTrust(fingerprint: Self.fp).decide(NSURLAuthenticationMethodServerTrust, server).0, .useCredential)
        XCTAssertEqual(PinnedTrust(fingerprint: "bkHHwIb84H1Te4ePQXbl3y-dAm4kMyZc47CFWjSYarA").decide(NSURLAuthenticationMethodServerTrust, server).0, .cancelAuthenticationChallenge)
        XCTAssertEqual(PinnedTrust(fingerprint: Self.fp).decide(NSURLAuthenticationMethodServerTrust, nil).0, .cancelAuthenticationChallenge)
        XCTAssertEqual(PinnedTrust(fingerprint: Self.fp).decide(NSURLAuthenticationMethodHTTPBasic, server).0, .cancelAuthenticationChallenge)
    }
    #endif

    // MARK: Claiming and finding the computer

    /// Answers by host: a status and a body, or nothing (thrown).
    private final class Network: @unchecked Sendable {
        var answers: [String: (Int, String)] = [:]
        var asked: [String] = []
        var lastRequest: URLRequest?
        let lock = NSLock()

        var fetch: PhoneFetch {
            { request in
                let host = "\(request.url!.host!):\(request.url!.port!)"
                let answer: (Int, String)? = self.lock.withLock {
                    self.asked.append("\(request.httpMethod ?? "GET") \(host)\(request.url!.path)")
                    self.lastRequest = request
                    return self.answers[host]
                }
                guard let (status, body) = answer else { throw URLError(.cannotConnectToHost) }
                return (Data(body.utf8), status)
            }
        }
    }

    func testClaimingTriesEachAddressUntilOneAnswers() async throws {
        let network = Network()
        network.answers["100.101.102.103:5318"] = (200, #"{"token":"ttp_secret","computer":"Shahab’s MacBook"}"#)
        let paired = try await PhoneConnect.claim(try PairingLink.parse(link), phoneName: "Shahab's iPhone", fetch: network.fetch)
        XCTAssertEqual(paired, PhoneConnect.Paired(host: "100.101.102.103:5318", token: "ttp_secret", computer: "Shahab’s MacBook"))
        XCTAssertEqual(network.asked, ["POST 192.168.1.20:5318/api/phones/claim", "POST 100.101.102.103:5318/api/phones/claim"])
        XCTAssertEqual(network.lastRequest?.url?.scheme, "https")
        let body = try XCTUnwrap(network.lastRequest?.httpBody)
        XCTAssertEqual(try JSONSerialization.jsonObject(with: body) as? [String: String], ["code": "abc_DEF-123", "name": "Shahab's iPhone"])
    }

    func testARefusedCodeSaysWhatTheComputerSaidAndStops() async throws {
        let network = Network()
        network.answers["192.168.1.20:5318"] = (403, #"{"title":"Forbidden","detail":"That code didn’t work."}"#)
        do {
            _ = try await PhoneConnect.claim(try PairingLink.parse(link), phoneName: "iPhone", fetch: network.fetch)
            XCTFail("Paired")
        } catch {
            XCTAssertEqual(error as? PhoneConnect.Failure, .refused(status: 403, message: "That code didn’t work."))
        }
        XCTAssertEqual(network.asked.count, 1)
    }

    func testNoAddressAnsweringSaysSo() async throws {
        do {
            _ = try await PhoneConnect.claim(try PairingLink.parse(link), phoneName: "iPhone", fetch: Network().fetch)
            XCTFail("Paired")
        } catch {
            XCTAssertEqual(error as? PhoneConnect.Failure, .unreachable(computer: "Shahab's MacBook"))
        }
    }

    func testFindingTheComputerTriesTheLastGoodAddressFirst() async throws {
        let network = Network()
        network.answers["192.168.1.20:5318"] = (200, "ok")
        let host = try await PhoneConnect.findHost(["192.168.1.20:5318", "100.101.102.103:5318"], preferring: "100.101.102.103:5318", token: "ttp_x", computer: "Mac", fetch: network.fetch)
        XCTAssertEqual(host, "192.168.1.20:5318")
        XCTAssertEqual(network.asked, ["GET 100.101.102.103:5318/health", "GET 192.168.1.20:5318/health"])
        XCTAssertEqual(network.lastRequest?.value(forHTTPHeaderField: "Authorization"), "Bearer ttp_x")
    }

    func testADisconnectedPhoneIsToldSoAndNoOtherAddressIsTried() async throws {
        let network = Network()
        network.answers["192.168.1.20:5318"] = (401, "{}")
        network.answers["100.101.102.103:5318"] = (200, "ok")
        do {
            _ = try await PhoneConnect.findHost(["192.168.1.20:5318", "100.101.102.103:5318"], preferring: nil, token: "t", computer: "Mac", fetch: network.fetch)
            XCTFail("Found")
        } catch {
            XCTAssertEqual(error as? PhoneConnect.Failure, .disconnected(computer: "Mac"))
        }
        XCTAssertEqual(network.asked.count, 1)
    }

    func testPhonesTurnedOffOnOneAddressTriesTheNext() async throws {
        let network = Network()
        network.answers["192.168.1.20:5318"] = (403, "{}")
        do {
            _ = try await PhoneConnect.findHost(["192.168.1.20:5318", "100.101.102.103:5318"], preferring: nil, token: "t", computer: "Mac", fetch: network.fetch)
            XCTFail("Found")
        } catch {
            XCTAssertEqual(error as? PhoneConnect.Failure, .unreachable(computer: "Mac"))
        }
        XCTAssertEqual(network.asked.count, 2)
    }

    // MARK: Where the phone stands

    func testTheConnectionMovesBetweenConnectedUnreachableAndDisconnected() {
        var state = RemoteState.connecting
        state = state.after(.answered(host: "a:1"))
        XCTAssertEqual(state, .connected(host: "a:1"))
        XCTAssertTrue(state.canChange)
        state = state.after(.noAnswer)
        XCTAssertEqual(state, .unreachable)
        XCTAssertFalse(state.canChange)
        XCTAssertEqual(state.after(.retrying), .unreachable, "the banner stays while it tries again")
        state = state.after(.answered(host: "b:1"))
        XCTAssertEqual(state, .connected(host: "b:1"))
        XCTAssertEqual(state.after(.retrying), .connecting)
        state = state.after(.unauthorized)
        XCTAssertEqual(state, .revoked)
        XCTAssertEqual(state.after(.answered(host: "a:1")), .revoked, "only pairing again undoes a disconnect")
        XCTAssertFalse(state.canChange)
    }

    func testTheBannerSaysWhenTheTasksAreFrom() {
        let utc = TimeZone(identifier: "UTC")!
        let now = Date(timeIntervalSince1970: 1_800_000_000) // 2027-01-15 08:00 UTC
        let uk = Locale(identifier: "en_GB")
        XCTAssertNil(RemoteState.connected(host: "a:1").banner(computer: "Mac", lastRead: now, now: now, timeZone: utc, locale: uk))
        XCTAssertEqual(RemoteState.unreachable.banner(computer: "Mac", lastRead: now.addingTimeInterval(-600), now: now, timeZone: utc, locale: uk),
                       "Can’t reach Mac. You’re seeing your tasks from 07:50; changes wait until you’re back on its Wi-Fi or Tailscale.")
        XCTAssertEqual(RemoteState.unreachable.banner(computer: "Mac", lastRead: now.addingTimeInterval(-86_400), now: now, timeZone: utc, locale: uk)?.contains("from yesterday 08:00;"), true)
        XCTAssertEqual(RemoteState.unreachable.banner(computer: "Mac", lastRead: now.addingTimeInterval(-5 * 86_400), now: now, timeZone: utc, locale: uk)?.contains("from 10 Jan, 08:00;"), true)
        let us = RemoteState.unreachable.banner(computer: "Mac", lastRead: now.addingTimeInterval(-600), now: now, timeZone: utc, locale: Locale(identifier: "en_US"))
        XCTAssertTrue(us?.contains("7:50") == true && us?.contains("AM") == true, "12-hour where the phone uses it: \(us ?? "")")
        XCTAssertEqual(RemoteState.unreachable.banner(computer: "Mac", lastRead: nil, now: now, timeZone: utc)?.hasPrefix("Can’t reach Mac."), true)
        XCTAssertEqual(RemoteState.revoked.banner(computer: "Mac", lastRead: nil, now: now, timeZone: utc)?.hasPrefix("Mac disconnected this phone"), true)
    }

    // MARK: Keeping the pairing

    func testThePairingIsKeptWithTheTokenAsASecret() throws {
        let suite = "PhonePairingTests-\(UUID().uuidString)"
        let defaults = try XCTUnwrap(UserDefaults(suiteName: suite))
        defer { defaults.removePersistentDomain(forName: suite) }
        let secrets = InMemorySecretStore()
        let store = PairingStore(defaults: defaults, secrets: secrets)
        XCTAssertNil(store.load())

        let paired = PairedComputer(hosts: ["a:1", "b:2"], fingerprint: Self.fp, computer: "Mac", lastHost: "b:2", token: "ttp_secret")
        XCTAssertTrue(store.save(paired))
        XCTAssertEqual(store.load(), paired)
        XCTAssertEqual(secrets.read("phone-token"), "ttp_secret")
        XCTAssertFalse(defaults.dictionaryRepresentation().values.contains { "\($0)".contains("ttp_secret") }, "the token isn't in the settings")

        store.lastRead = Date(timeIntervalSince1970: 100)
        store.forget()
        XCTAssertNil(store.load())
        XCTAssertNil(secrets.read("phone-token"))
        XCTAssertNil(store.lastRead)
    }

    func testThePhonesNameIsRememberedEvenAfterDisconnecting() throws {
        let suite = "PhonePairingTests-\(UUID().uuidString)"
        let defaults = try XCTUnwrap(UserDefaults(suiteName: suite))
        defer { defaults.removePersistentDomain(forName: suite) }
        let store = PairingStore(defaults: defaults, secrets: InMemorySecretStore())
        XCTAssertNil(store.phoneName)
        store.phoneName = "  Shahab’s iPad "
        XCTAssertEqual(store.phoneName, "Shahab’s iPad")
        store.forget()
        XCTAssertEqual(store.phoneName, "Shahab’s iPad", "used again the next time this phone connects")
        store.phoneName = "   "
        XCTAssertNil(store.phoneName)
    }

    func testAPairingWithoutItsTokenIsntOne() throws {
        let suite = "PhonePairingTests-\(UUID().uuidString)"
        let defaults = try XCTUnwrap(UserDefaults(suiteName: suite))
        defer { defaults.removePersistentDomain(forName: suite) }
        let secrets = InMemorySecretStore()
        let store = PairingStore(defaults: defaults, secrets: secrets)
        store.save(PairedComputer(hosts: ["a:1"], fingerprint: Self.fp, computer: "Mac", token: "t"))
        secrets.remove("phone-token")
        XCTAssertNil(store.load())
    }
}
