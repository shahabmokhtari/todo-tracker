#if canImport(SwiftUI)
import SwiftUI
import OneThingKit
#if os(iOS)
import UIKit
#elseif os(macOS)
import AppKit
#endif

/// Send feedback: one sheet in every app (the web app's too). A kind, the note, an optional email for a reply, and the
/// fine print; Send files it as an issue through the relay. A note that couldn't go stays here to send again (with the
/// same clientID, so it's never filed twice).
public struct FeedbackSheet: View {
    private let client: FeedbackClient
    private let device: FeedbackDevice?
    @State private var draft: FeedbackDraft
    @State private var sending = false
    @State private var outcome: FeedbackOutcome?
    @FocusState private var typing: Bool
    @Environment(\.dismiss) private var dismiss

    public init(source: FeedbackSource = .button, client: FeedbackClient = FeedbackClient(), device: FeedbackDevice? = nil) {
        self.client = client
        self.device = device
        // The clientID is made here, once: every retry from this sheet sends the same one.
        _draft = State(initialValue: FeedbackDraft(source: source))
    }

    private var length: Int { Feedback.length(draft.message) }
    private var tooLong: Bool { length > Feedback.maxMessage }
    private var canSend: Bool { !sending && length > 0 && !tooLong && outcome != .sent }

    public var body: some View {
        NavigationStack {
            Form {
                Section {
                    Picker("Kind", selection: $draft.kind) {
                        ForEach(FeedbackKind.allCases) { Text($0.label).tag($0) }
                    }
                    .pickerStyle(.segmented)
                    .labelsHidden()
                    .accessibilityIdentifier("feedback-kind")
                }
                Section {
                    TextField(placeholder, text: $draft.message, axis: .vertical)
                        .lineLimit(5...12)
                        .focused($typing)
                        .accessibilityLabel("Your note")
                        .accessibilityIdentifier("feedback-message")
                } footer: {
                    // The counter shows near the limit.
                    if length >= Feedback.maxMessage - 200 {
                        Text("\(length) / \(Feedback.maxMessage)")
                            .monospacedDigit()
                            .foregroundStyle(tooLong ? Color.red : .secondary)
                            .frame(maxWidth: .infinity, alignment: .trailing)
                            .accessibilityLabel(tooLong ? "Too long: \(length - Feedback.maxMessage) characters over" : "\(Feedback.maxMessage - length) characters left")
                    }
                }
                Section {
                    TextField("Email", text: Binding(get: { draft.contact ?? "" }, set: { draft.contact = $0 }), prompt: Text(Feedback.contactHint))
                        .autocorrectionDisabled()
                        #if os(iOS)
                        .textContentType(.emailAddress)
                        .keyboardType(.emailAddress)
                        .textInputAutocapitalization(.never)
                        #endif
                        .accessibilityIdentifier("feedback-contact")
                } footer: {
                    Text(Feedback.finePrint)
                }
                if let outcome {
                    Section {
                        Label(outcome.text, systemImage: outcome == .sent ? "checkmark.circle.fill" : "exclamationmark.triangle")
                            .foregroundStyle(outcome == .sent ? Color.green : .orange)
                            .accessibilityIdentifier("feedback-status")
                    }
                }
            }
            #if os(macOS)
            .formStyle(.grouped)
            #endif
            .navigationTitle("Send feedback")
            #if os(iOS)
            .navigationBarTitleDisplayMode(.inline)
            #endif
            .toolbar {
                ToolbarItem(placement: .cancellationAction) { Button("Cancel") { dismiss() } }
                ToolbarItem(placement: .confirmationAction) {
                    Button(sending ? "Sending…" : "Send") { Task { await send() } }
                        .disabled(!canSend)
                        .accessibilityIdentifier("feedback-send")
                }
            }
            .onAppear { typing = true }
        }
        #if os(macOS)
        .frame(minWidth: 440, minHeight: 460)
        #endif
    }

    private var placeholder: String {
        switch draft.kind {
        case .bug: return "What happened, and what did you expect?"
        case .idea: return "What would make One Thing better for you?"
        case .other: return "What’s on your mind?"
        }
    }

    private func send() async {
        guard canSend, let body = Feedback.body(draft, device: device ?? .current) else { return }
        sending = true
        outcome = nil
        let result = await client.send(body)
        sending = false
        outcome = result
        if result == .sent {
            // Long enough to read the thanks.
            try? await Task.sleep(for: .seconds(1.5))
            dismiss()
        }
    }
}

extension FeedbackDevice {
    /// This device, coarsely: the platform, the app's version, the system, the model, the language and the screen size.
    /// Nothing that identifies the person or the install.
    @MainActor public static var current: FeedbackDevice {
        let info = Bundle.main.infoDictionary
        let version = info?["CFBundleShortVersionString"] as? String
        let build = info?["CFBundleVersion"] as? String
        let language = Locale.preferredLanguages.first
        #if os(iOS)
        let platform = UIDevice.current.userInterfaceIdiom == .pad ? "iPadOS" : "iOS"
        let size = UIScreen.main.bounds.size
        return FeedbackDevice(platform: platform, appVersion: version, build: build, os: "\(platform) \(UIDevice.current.systemVersion)",
                              device: machine(), language: language, screen: "\(Int(min(size.width, size.height)))x\(Int(max(size.width, size.height)))")
        #elseif os(macOS)
        let v = ProcessInfo.processInfo.operatingSystemVersion
        return FeedbackDevice(platform: "macOS", appVersion: version, build: build,
                              os: "macOS \(v.majorVersion).\(v.minorVersion)\(v.patchVersion > 0 ? ".\(v.patchVersion)" : "")",
                              device: sysctl("hw.model"), language: language, screen: nil)
        #else
        return FeedbackDevice(platform: "Apple", appVersion: version, build: build, language: language)
        #endif
    }

    #if os(iOS)
    /// The model's identifier ("iPhone17,1"; the simulator says what it simulates).
    private static func machine() -> String? {
        if let simulated = ProcessInfo.processInfo.environment["SIMULATOR_MODEL_IDENTIFIER"] { return simulated }
        var system = utsname()
        uname(&system)
        let name = withUnsafeBytes(of: &system.machine) { raw in String(decoding: raw.prefix { $0 != 0 }, as: UTF8.self) }
        return name.isEmpty ? nil : name
    }
    #elseif os(macOS)
    private static func sysctl(_ name: String) -> String? {
        var size = 0
        guard sysctlbyname(name, nil, &size, nil, 0) == 0, size > 0 else { return nil }
        var bytes = [CChar](repeating: 0, count: size)
        guard sysctlbyname(name, &bytes, &size, nil, 0) == 0 else { return nil }
        return String(cString: bytes)
    }
    #endif
}

/// Shake to send feedback (iPhone and iPad): on unless switched off in Settings; never over another sheet or a break.
public enum FeedbackShake {
    /// The Settings switch (on by default).
    public static let storageKey = "feedback.shake"
    /// Posted when the device is shaken.
    public static let shaken = Notification.Name("OneThingDeviceShaken")

    #if os(iOS)
    /// Something is already on top of the window (a sheet, a full-screen cover such as the break): a shake does nothing.
    @MainActor static func somethingIsShown() -> Bool {
        UIApplication.shared.connectedScenes
            .compactMap { $0 as? UIWindowScene }
            .flatMap(\.windows)
            .contains { $0.isKeyWindow && $0.rootViewController?.presentedViewController != nil }
    }
    #endif
}

#if os(iOS)
extension UIWindow {
    override open func motionEnded(_ motion: UIEvent.EventSubtype, with event: UIEvent?) {
        if motion == .motionShake { NotificationCenter.default.post(name: FeedbackShake.shaken, object: nil) }
        super.motionEnded(motion, with: event)
    }
}
#endif
#endif
