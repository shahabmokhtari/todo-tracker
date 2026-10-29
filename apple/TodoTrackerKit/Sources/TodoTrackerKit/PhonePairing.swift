import Foundation
#if canImport(CryptoKit)
import CryptoKit
#endif
#if canImport(Security)
import Security
#endif

/// A pairing link from Settings › Phones on a computer (as a QR code or copied):
/// `todotracker://pair?v=1&hosts=192.168.1.20:5318,100.101.102.103:5318&code=…&fp=…&name=…`.
public struct PairingLink: Equatable, Sendable {
    /// Where the computer answers (host:port), in the order to try: its network first, then Tailscale.
    public let hosts: [String]
    public let code: String
    /// The computer's certificate: SHA-256 of its DER bytes, base64url without padding.
    public let fingerprint: String
    /// The computer's name, as its owner sees it.
    public let computer: String

    public init(hosts: [String], code: String, fingerprint: String, computer: String) {
        self.hosts = hosts
        self.code = code
        self.fingerprint = fingerprint
        self.computer = computer
    }

    /// Why a link can't be used, said for the person holding the phone.
    public enum Problem: Error, Equatable, LocalizedError {
        case notAPairingLink
        case newerVersion
        case missing(String)

        public var errorDescription: String? {
            switch self {
            case .notAPairingLink:
                return "That isn’t a Todo Tracker pairing link. On your computer, open Settings › Phones and connect a phone."
            case .newerVersion:
                return "That link is from a newer Todo Tracker. Update this app, then scan it again."
            case .missing:
                return "That pairing link is cut short. On your computer, open Settings › Phones and copy it again."
            }
        }
    }

    /// Whether the text is meant to be a pairing link (so a task link isn't mistaken for one).
    public static func isPairing(_ text: String?) -> Bool {
        guard let text = text?.trimmingCharacters(in: .whitespacesAndNewlines),
              let components = URLComponents(string: text) else { return false }
        return components.scheme?.lowercased() == TaskLinks.scheme && pairRoute(components)
    }

    public static func parse(_ text: String) throws -> PairingLink {
        let clean = text.trimmingCharacters(in: .whitespacesAndNewlines)
        guard let components = URLComponents(string: clean), components.scheme?.lowercased() == TaskLinks.scheme, pairRoute(components) else {
            throw Problem.notAPairingLink
        }
        var values: [String: String] = [:]
        for item in components.queryItems ?? [] where values[item.name] == nil {
            values[item.name] = item.value ?? ""
        }
        guard let version = values["v"], !version.isEmpty else { throw Problem.missing("v") }
        guard version == "1" else { throw Problem.newerVersion }

        let hosts = (values["hosts"] ?? "").split(separator: ",").map { $0.trimmingCharacters(in: .whitespaces) }.filter(isHostAndPort)
        guard !hosts.isEmpty else { throw Problem.missing("hosts") }
        guard let code = values["code"]?.trimmingCharacters(in: .whitespaces), !code.isEmpty, code.count <= 64 else { throw Problem.missing("code") }
        guard let fingerprint = values["fp"], CertificatePin.digest(fingerprint) != nil else { throw Problem.missing("fp") }
        let name = values["name"]?.trimmingCharacters(in: .whitespacesAndNewlines) ?? ""
        return PairingLink(hosts: hosts, code: code, fingerprint: fingerprint, computer: name.isEmpty ? "your computer" : String(name.prefix(60)))
    }

    /// `todotracker://pair?…` (the host part) or `todotracker:pair?…` / `todotracker:///pair?…` (the path).
    private static func pairRoute(_ components: URLComponents) -> Bool {
        let route = ((components.host ?? "") + components.path).trimmingCharacters(in: CharacterSet(charactersIn: "/")).lowercased()
        return route == "pair"
    }

    /// `192.168.1.20:5318`, `[fd7a::1]:5318` or `my-pc.tail1234.ts.net:5318`: a host and a port, nothing else.
    static func isHostAndPort(_ text: String) -> Bool {
        guard let colon = text.lastIndex(of: ":"), let port = Int(text[text.index(after: colon)...]), (1...65535).contains(port) else { return false }
        let host = String(text[..<colon])
        guard !host.isEmpty else { return false }
        if host.hasPrefix("[") { return host.hasSuffix("]") && host.dropFirst().dropLast().allSatisfy { $0.isHexDigit || $0 == ":" || $0 == "." } }
        return host.allSatisfy { $0.isLetter || $0.isNumber || $0 == "." || $0 == "-" } && host.allSatisfy(\.isASCII)
    }

    /// The address to use for a host from the link.
    public static func baseURL(_ host: String) -> URL? { URL(string: "https://\(host)") }
}

/// The phone trusts the computer by its certificate's fingerprint (from the pairing link), never by a certificate
/// authority: the certificate is the computer's own, and its name won't match the address.
public enum CertificatePin {
    /// The 32 bytes a fingerprint stands for (nil: not a base64url SHA-256).
    public static func digest(_ fingerprint: String) -> Data? {
        var text = fingerprint.trimmingCharacters(in: .whitespaces).replacingOccurrences(of: "-", with: "+").replacingOccurrences(of: "_", with: "/")
        guard !text.contains("=") else { return nil }
        while text.count % 4 != 0 { text += "=" }
        guard let data = Data(base64Encoded: text), data.count == 32 else { return nil }
        return data
    }

    /// SHA-256 of the DER bytes, base64url without padding (PhoneCertificate.Fingerprint on the computer).
    public static func fingerprint(der: Data) -> String {
        #if canImport(CryptoKit)
        let hash = Data(SHA256.hash(data: der))
        #else
        let hash = Data()
        #endif
        return hash.base64EncodedString().replacingOccurrences(of: "+", with: "-").replacingOccurrences(of: "/", with: "_").replacingOccurrences(of: "=", with: "")
    }

    public static func matches(der: Data, fingerprint: String) -> Bool {
        guard let expected = digest(fingerprint) else { return false }
        #if canImport(CryptoKit)
        return Data(SHA256.hash(data: der)) == expected
        #else
        return false
        #endif
    }

    #if canImport(Security)
    /// Whether the server's certificate (the leaf of what it presented) is the one the link named.
    public static func trusts(_ trust: SecTrust, fingerprint: String) -> Bool {
        guard let chain = SecTrustCopyCertificateChain(trust) as? [SecCertificate], let leaf = chain.first else { return false }
        return matches(der: SecCertificateCopyData(leaf) as Data, fingerprint: fingerprint)
    }
    #endif
}

#if canImport(Security)
/// A URLSession delegate that accepts the computer's certificate only when its fingerprint is the paired one, and
/// turns every other server away. Nothing else about TLS (or App Transport Security) is loosened.
public final class PinnedTrust: NSObject, URLSessionDelegate, @unchecked Sendable {
    public let fingerprint: String

    public init(fingerprint: String) {
        self.fingerprint = fingerprint
    }

    /// An ephemeral session (no cookies or cache kept) that only talks to the paired computer.
    public static func session(fingerprint: String, configuration: URLSessionConfiguration = .ephemeral) -> URLSession {
        configuration.urlCache = nil
        configuration.httpCookieStorage = nil
        configuration.waitsForConnectivity = false
        return URLSession(configuration: configuration, delegate: PinnedTrust(fingerprint: fingerprint), delegateQueue: nil)
    }

    public func urlSession(_ session: URLSession, didReceive challenge: URLAuthenticationChallenge,
                           completionHandler: @escaping (URLSession.AuthChallengeDisposition, URLCredential?) -> Void) {
        let (disposition, credential) = decide(challenge.protectionSpace.authenticationMethod, challenge.protectionSpace.serverTrust)
        completionHandler(disposition, credential)
    }

    /// The decision on its own (tested without a network).
    public func decide(_ method: String, _ trust: SecTrust?) -> (URLSession.AuthChallengeDisposition, URLCredential?) {
        guard method == NSURLAuthenticationMethodServerTrust else { return (.cancelAuthenticationChallenge, nil) }
        guard let trust, CertificatePin.trusts(trust, fingerprint: fingerprint) else { return (.cancelAuthenticationChallenge, nil) }
        return (.useCredential, URLCredential(trust: trust))
    }
}
#endif

/// One HTTP exchange (the network in the app, a stand-in in tests). Throws only when nothing answered.
public typealias PhoneFetch = @Sendable (URLRequest) async throws -> (Data, Int)

/// Trading the pairing code for this phone's own token, and finding which of the computer's addresses answers.
public enum PhoneConnect {
    public struct Paired: Equatable, Sendable {
        public let host: String
        public let token: String
        public let computer: String
    }

    public enum Failure: Error, Equatable, LocalizedError {
        /// No address answered (another network, or the computer is asleep).
        case unreachable(computer: String)
        /// The computer answered and said no (its own words: an old code, too many tries…).
        case refused(status: Int, message: String)
        /// The computer no longer knows this phone (disconnected there).
        case disconnected(computer: String)

        public var errorDescription: String? {
            switch self {
            case let .unreachable(computer):
                return "Couldn’t reach \(computer). Make sure this phone is on the same Wi-Fi as it (or both are on Tailscale) and Todo Tracker is open there, then try again."
            case let .refused(_, message): return message
            case let .disconnected(computer):
                return "\(computer) disconnected this phone. To use its tasks again, connect again from Settings › Phones on it."
            }
        }
    }

    /// POST /api/phones/claim on each address in turn until one answers; the first answer (yes or no) is the answer.
    public static func claim(_ link: PairingLink, phoneName: String, fetch: PhoneFetch) async throws -> Paired {
        let body = ServerAPI.json(["code": link.code, "name": String(phoneName.prefix(60))])
        for host in link.hosts {
            guard let base = PairingLink.baseURL(host), let url = URL(string: "/api/phones/claim", relativeTo: base) else { continue }
            var request = URLRequest(url: url.absoluteURL)
            request.httpMethod = "POST"
            request.timeoutInterval = 8
            request.httpBody = Data(body.utf8)
            request.setValue("application/json", forHTTPHeaderField: "Content-Type")
            let answer: (Data, Int)
            do {
                answer = try await fetch(request)
            } catch {
                continue
            }
            let (data, status) = answer
            guard (200..<300).contains(status) else {
                throw Failure.refused(status: status, message: ServerClient.message(data, status: status))
            }
            let object = try? JSONSerialization.jsonObject(with: data) as? [String: Any]
            guard let token = object?["token"] as? String, !token.isEmpty else {
                throw Failure.refused(status: status, message: "\(link.computer) answered, but didn’t connect this phone. Try again.")
            }
            let computer = (object?["computer"] as? String).flatMap { $0.isEmpty ? nil : $0 } ?? link.computer
            return Paired(host: host, token: token, computer: computer)
        }
        throw Failure.unreachable(computer: link.computer)
    }

    /// The first address where the computer answers with this phone's token (GET /health), the one that worked last
    /// time first. A 401 means the computer disconnected this phone: no other address is tried.
    public static func findHost(_ hosts: [String], preferring last: String?, token: String, computer: String, fetch: PhoneFetch) async throws -> String {
        for host in order(hosts, preferring: last) {
            guard let base = PairingLink.baseURL(host), let url = URL(string: ServerAPI.health.path, relativeTo: base) else { continue }
            var request = URLRequest(url: url.absoluteURL)
            request.timeoutInterval = 5
            request.setValue("Bearer \(token)", forHTTPHeaderField: "Authorization")
            guard let (_, status) = try? await fetch(request) else { continue }
            if status == 401 { throw Failure.disconnected(computer: computer) }
            if (200..<300).contains(status) { return host }
            // Something else answered there (403: phones turned off on the computer): try the next address.
        }
        throw Failure.unreachable(computer: computer)
    }

    /// The hosts to try, the last good one first (no repeats).
    public static func order(_ hosts: [String], preferring last: String?) -> [String] {
        guard let last, hosts.contains(last) else { return hosts }
        return [last] + hosts.filter { $0 != last }
    }

    /// The network, through a session (the pinned one in the app).
    public static func fetch(with session: URLSession) -> PhoneFetch {
        { request in
            let (data, response) = try await session.data(for: request)
            return (data, (response as? HTTPURLResponse)?.statusCode ?? 0)
        }
    }
}

/// Where the phone stands with the computer it's connected to.
public enum RemoteState: Equatable, Sendable {
    /// Finding the computer (at launch, or after it stopped answering): the last board read is shown meanwhile.
    case connecting
    case connected(host: String)
    /// No address answers: the last board read is shown, without changes.
    case unreachable
    /// The computer disconnected this phone: its tasks aren't used any more.
    case revoked

    /// What happened on the way to the computer.
    public enum Event: Equatable, Sendable {
        case answered(host: String)
        case noAnswer
        case unauthorized
        case retrying
    }

    /// The state after an event. Disconnected stays disconnected (only pairing again undoes it).
    public func after(_ event: Event) -> RemoteState {
        if self == .revoked { return .revoked }
        switch event {
        case let .answered(host): return .connected(host: host)
        case .noAnswer: return .unreachable
        case .unauthorized: return .revoked
        case .retrying: return self == .unreachable ? .unreachable : .connecting
        }
    }

    /// Changes go to the computer only while it answers.
    public var canChange: Bool {
        if case .connected = self { return true }
        return false
    }

    /// The calm line shown above the tasks (nil: nothing to say). Times as the phone writes them (12- or 24-hour).
    public func banner(computer: String, lastRead: Date?, now: Date, timeZone: TimeZone = .current, locale: Locale = .current) -> String? {
        switch self {
        case .connected: return nil
        case .connecting: return "Connecting to \(computer)…"
        case .unreachable:
            guard let lastRead else {
                return "Can’t reach \(computer). Your tasks show up here once you’re back on its Wi-Fi or Tailscale."
            }
            return "Can’t reach \(computer). You’re seeing your tasks from \(Self.when(lastRead, now: now, timeZone: timeZone, locale: locale)); changes wait until you’re back on its Wi-Fi or Tailscale."
        case .revoked:
            return "\(computer) disconnected this phone, so these are the tasks on this phone again. Connect again to use the computer’s."
        }
    }

    /// "10:42 AM", "yesterday 6:05 PM" or "3 Mar, 6:05 PM" (as the phone writes times).
    static func when(_ date: Date, now: Date, timeZone: TimeZone, locale: Locale) -> String {
        var calendar = Calendar(identifier: .gregorian)
        calendar.timeZone = timeZone
        let format = DateFormatter()
        format.timeZone = timeZone
        format.locale = locale
        format.dateStyle = .none
        format.timeStyle = .short
        let time = format.string(from: date)
        if calendar.isDate(date, inSameDayAs: now) { return time }
        if let yesterday = calendar.date(byAdding: .day, value: -1, to: now), calendar.isDate(date, inSameDayAs: yesterday) {
            return "yesterday \(time)"
        }
        format.setLocalizedDateFormatFromTemplate("d MMM")
        return "\(format.string(from: date)), \(time)"
    }
}
