import Foundation

/// Which hook waiting for approval the Mac asks about now: one at a time, each version once in a run (hook-prompt.js).
/// A question that went stale (decided in the browser or with `ot`, or changed by an agent) goes, and the next one
/// waiting is always asked.
public struct HookQuestions: Equatable, Sendable {
    /// The hook asked about now.
    public private(set) var asking: PendingHook?
    private var asked = Set<String>()
    private var last = HookApprovals.none

    public init() {}

    /// What `GET /api/hooks` says now. The hook asked about stays while it waits, in the same version; otherwise the
    /// next one waiting is asked.
    public mutating func read(_ approvals: HookApprovals) {
        last = approvals
        if let shown = asking, approvals.canApprove, !approvals.appAsks, approvals.waiting.contains(shown) { return }
        asking = nil
        next()
    }

    /// Answered (Allow, Don't allow) or put off (Later): not asked again in this run (it waits in Settings › Hooks,
    /// and is asked again if an agent changes it); the next one waiting is asked at once.
    public mutating func answered(_ hook: PendingHook) {
        asked.insert(hook.askKey)
        guard asking?.askKey == hook.askKey else { return }
        asking = nil
        next()
    }

    /// Nothing asked about (the server is gone).
    public mutating func reset() {
        asking = nil
        last = .none
    }

    private mutating func next() {
        guard let hook = last.nextToAsk(asked: asked) else { return }
        asked.insert(hook.askKey)
        asking = hook
    }
}

/// What the Mac's hook alert does when the hook asked about changes or the app becomes active: a stale alert is
/// dismissed (its answer counts for nothing), and a question is only shown while the app is active. In the background
/// the app only asks for your attention, once; it never comes forward by itself.
public struct HookPromptState: Equatable, Sendable {
    public enum Step: Equatable, Sendable {
        /// Close the alert shown, answering nothing.
        case dismiss
        /// Show the question.
        case show(PendingHook)
        /// Bounce the Dock icon once (the app is in the background).
        case requestAttention
    }

    /// The hook whose alert is up.
    public private(set) var showing: PendingHook?
    private var attentionFor: String?

    public init() {}

    public mutating func update(pending: PendingHook?, appActive: Bool) -> [Step] {
        if let showing, showing.askKey == pending?.askKey { return [] }
        var steps: [Step] = []
        if showing != nil {
            steps.append(.dismiss)
            showing = nil
        }
        guard let pending else {
            attentionFor = nil
            return steps
        }
        if appActive {
            showing = pending
            attentionFor = nil
            steps.append(.show(pending))
        } else if attentionFor != pending.askKey {
            attentionFor = pending.askKey
            steps.append(.requestAttention)
        }
        return steps
    }

    /// The person answered the alert for `hook`: true when it's the one shown (the answer counts).
    public mutating func answered(_ hook: PendingHook) -> Bool {
        guard showing?.askKey == hook.askKey else { return false }
        showing = nil
        return true
    }
}
