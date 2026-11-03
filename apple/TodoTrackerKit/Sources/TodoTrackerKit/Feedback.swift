import Foundation

/// Send feedback: a note to the people who make Onething, filed as a GitHub issue through the shared issues relay
/// (never with a GitHub token). The same body and the same reading of the answer as every other app
/// (tests/fixtures/feedback.json): only the note, an optional email and coarse facts about the device, never the tasks.
public enum Feedback {
    /// The relay that files the note (create-only: it can't read anything back).
    public static let relayURL = URL(string: "https://issues.awesomehq.app/v1/issues")!
    /// The app's public key (meant to be in the app: it can only open a new issue in Onething's repository).
    public static let publicKey = "pk_gGPPMjRSsuHaXRdgdXvUevvL"
    /// The longest note, in code points.
    public static let maxMessage = 2000
    /// The longest reply address, in code points.
    public static let maxContact = 200
    /// Debug builds log the request instead of sending it unless this is "1" (set it in the Xcode scheme).
    public static let sendVariable = "ONETHING_FEEDBACK_SEND"

    // What the sheet says (the same words in every app).
    public static let sentText = "Thanks — we read every note."
    public static let retryText = "Couldn’t send — check your connection and try again."
    public static let failedText = "Couldn’t send your note: something went wrong on our side. Please try again later."
    public static let finePrint = "We’ll include your app version, device model and language."
    public static let contactHint = "Only if you’d like a reply. It’s shown with your note on GitHub."

    /// The note as it's sent: control characters gone (new lines and tabs stay), trimmed, at most
    /// <paramref name="maxMessage"/> code points, trimmed again. Empty: nothing to send.
    public static func cleanMessage(_ text: String, maxMessage: Int = maxMessage) -> String {
        var scalars = String.UnicodeScalarView()
        scalars.append(contentsOf: text.unicodeScalars.filter { $0 == "\n" || $0 == "\t" || $0.properties.generalCategory != .control })
        let trimmed = trim(String(scalars))
        guard trimmed.unicodeScalars.count > maxMessage else { return trimmed }
        var cut = String.UnicodeScalarView()
        cut.append(contentsOf: trimmed.unicodeScalars.prefix(maxMessage))
        return trim(String(cut))
    }

    /// The reply address as it's sent: every control character gone (new lines and tabs too), trimmed, at most
    /// `maxContact` code points, trimmed again. Nil when nothing is left.
    public static func cleanContact(_ text: String?, maxContact: Int = maxContact) -> String? {
        guard let text else { return nil }
        var scalars = String.UnicodeScalarView()
        scalars.append(contentsOf: text.unicodeScalars.filter { $0.properties.generalCategory != .control })
        var clean = trim(String(scalars))
        if clean.unicodeScalars.count > maxContact {
            var cut = String.UnicodeScalarView()
            cut.append(contentsOf: clean.unicodeScalars.prefix(maxContact))
            clean = trim(String(cut))
        }
        return clean.isEmpty ? nil : clean
    }

    /// How long the note is, in code points (what the counter shows).
    public static func length(_ text: String) -> Int { cleanMessage(text, maxMessage: Int.max).unicodeScalars.count }

    /// The request body for a draft (nil: nothing to send).
    public static func body(_ draft: FeedbackDraft, device: FeedbackDevice, maxMessage: Int = maxMessage, maxContact: Int = maxContact) -> FeedbackBody? {
        let message = cleanMessage(draft.message, maxMessage: maxMessage)
        guard !message.isEmpty else { return nil }
        let contact = cleanContact(draft.contact, maxContact: maxContact)
        let platform = device.platform.lowercased()
        let version = device.appVersion.flatMap(nonEmpty).map { v in device.build.flatMap(nonEmpty).map { "\(v) (\($0))" } ?? v }
        let metadata: [(String, String)] = [
            ("Platform", platform), ("App version", version), ("OS", device.os), ("Device", device.device),
            ("Language", device.language), ("Screen", device.screen), ("Source", draft.source.rawValue),
        ].compactMap { key, value in value.flatMap(nonEmpty).map { (key, $0) } }
        return FeedbackBody(
            message: message,
            title: "\(draft.kind.label) · \(device.platform): \(message)",
            contact: contact,
            clientID: draft.clientID,
            labels: ["kind:\(draft.kind.rawValue)", "platform:\(platform)", "source:\(draft.source.rawValue)"],
            metadata: metadata)
    }

    /// What an answer means: 2xx sent; no answer, 408, 429 or 5xx try again (same clientID); anything else failed.
    public static func classify(status: Int?) -> FeedbackOutcome {
        guard let status else { return .retry }
        if (200..<300).contains(status) { return .sent }
        if status == 408 || status == 429 || (500..<600).contains(status) { return .retry }
        return .failed
    }

    /// Whether this build sends for real: release builds always; debug builds only with ONETHING_FEEDBACK_SEND=1.
    public static func sendsForReal(debugBuild: Bool, environment: [String: String]) -> Bool {
        !debugBuild || environment[sendVariable] == "1"
    }

    /// A debug build (this package built with DEBUG).
    public static var isDebugBuild: Bool {
        #if DEBUG
        return true
        #else
        return false
        #endif
    }

    private static func trim(_ text: String) -> String { text.trimmingCharacters(in: .whitespacesAndNewlines) }
    private static func nonEmpty(_ text: String) -> String? { text.isEmpty ? nil : text }
}

public enum FeedbackKind: String, CaseIterable, Identifiable, Sendable {
    case bug, idea, other
    public var id: String { rawValue }
    public var label: String {
        switch self {
        case .bug: return "Bug"
        case .idea: return "Idea"
        case .other: return "Other"
        }
    }
}

/// How the sheet was opened.
public enum FeedbackSource: String, CaseIterable, Identifiable, Sendable {
    case button, shake, page, qr
    public var id: String { rawValue }
}

public enum FeedbackOutcome: String, Sendable {
    case sent, retry, failed

    /// What the sheet says.
    public var text: String {
        switch self {
        case .sent: return Feedback.sentText
        case .retry: return Feedback.retryText
        case .failed: return Feedback.failedText
        }
    }
}

/// What was typed in the sheet. The clientID is made once per sheet and sent again on every retry.
public struct FeedbackDraft: Sendable {
    public var kind: FeedbackKind
    public var message: String
    public var contact: String?
    public var source: FeedbackSource
    public var clientID: String

    public init(kind: FeedbackKind = .bug, message: String = "", contact: String? = nil, source: FeedbackSource = .button, clientID: String = UUID().uuidString.lowercased()) {
        self.kind = kind
        self.message = message
        self.contact = contact
        self.source = source
        self.clientID = clientID
    }
}

/// Coarse facts about the device (no ids of the person or the install, no location, nothing from the tasks).
public struct FeedbackDevice: Sendable {
    /// iOS, iPadOS, macOS… (as shown in the title).
    public var platform: String
    public var appVersion: String?
    public var build: String?
    public var os: String?
    public var device: String?
    public var language: String?
    public var screen: String?

    public init(platform: String, appVersion: String? = nil, build: String? = nil, os: String? = nil, device: String? = nil, language: String? = nil, screen: String? = nil) {
        self.platform = platform
        self.appVersion = appVersion
        self.build = build
        self.os = os
        self.device = device
        self.language = language
        self.screen = screen
    }
}

/// The request body, in the relay's key order (metadata keys in order too).
public struct FeedbackBody: Sendable, Equatable {
    public var message: String
    public var title: String
    public var contact: String?
    public var clientID: String
    public var labels: [String]
    public var metadata: [(String, String)]

    public static func == (a: FeedbackBody, b: FeedbackBody) -> Bool {
        a.message == b.message && a.title == b.title && a.contact == b.contact && a.clientID == b.clientID && a.labels == b.labels
            && a.metadata.map(\.0) == b.metadata.map(\.0) && a.metadata.map(\.1) == b.metadata.map(\.1)
    }

    /// The JSON, written by hand so the keys keep their order.
    public func json() -> Data {
        func string(_ s: String) -> String {
            let encoder = JSONEncoder()
            encoder.outputFormatting = .withoutEscapingSlashes
            return String(decoding: (try? encoder.encode(s)) ?? Data("\"\"".utf8), as: UTF8.self)
        }
        var parts = ["\"message\":\(string(message))", "\"title\":\(string(title))"]
        if let contact { parts.append("\"contact\":\(string(contact))") }
        parts.append("\"clientID\":\(string(clientID))")
        parts.append("\"labels\":[\(labels.map(string).joined(separator: ","))]")
        parts.append("\"metadata\":{\(metadata.map { "\(string($0.0)):\(string($0.1))" }.joined(separator: ","))}")
        return Data("{\(parts.joined(separator: ","))}".utf8)
    }
}

/// Sends a note to the relay. Debug builds log it instead unless ONETHING_FEEDBACK_SEND=1.
public struct FeedbackClient: Sendable {
    public var session: URLSession
    public var endpoint: URL
    public var sendsForReal: Bool
    public var log: @Sendable (String) -> Void

    public init(session: URLSession = .shared,
                endpoint: URL = Feedback.relayURL,
                sendsForReal: Bool = Feedback.sendsForReal(debugBuild: Feedback.isDebugBuild, environment: ProcessInfo.processInfo.environment),
                log: @escaping @Sendable (String) -> Void = { print($0) }) {
        self.session = session
        self.endpoint = endpoint
        self.sendsForReal = sendsForReal
        self.log = log
    }

    public func request(_ body: FeedbackBody) -> URLRequest {
        var request = URLRequest(url: endpoint, timeoutInterval: 20)
        request.httpMethod = "POST"
        request.setValue("Bearer \(Feedback.publicKey)", forHTTPHeaderField: "Authorization")
        request.setValue("application/json", forHTTPHeaderField: "Content-Type")
        request.httpBody = body.json()
        return request
    }

    public func send(_ body: FeedbackBody) async -> FeedbackOutcome {
        let request = request(body)
        guard sendsForReal else {
            log("Feedback (not sent: a debug build; set \(Feedback.sendVariable)=1 to send) POST \(endpoint.absoluteString) \(String(decoding: body.json(), as: UTF8.self))")
            return .sent
        }
        do {
            let (_, response) = try await session.data(for: request)
            return Feedback.classify(status: (response as? HTTPURLResponse)?.statusCode)
        } catch {
            return .retry
        }
    }
}
