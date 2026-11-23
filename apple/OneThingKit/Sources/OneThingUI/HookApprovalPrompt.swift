#if os(macOS)
import AppKit
import Combine
import OneThingKit

/// Asks you about a hook an AI agent added or changed (it waits for approval): who wants it, when it would run, and
/// everything it would do, in full. "Allow" runs it from now on; "Don't allow" removes it; "Later" (Return or Escape)
/// leaves it waiting in Settings › Hooks. Allow and Don't allow need a click: no key answers them. Nothing is ever
/// allowed without your answer. The words are the web prompt's (hook-prompt.js).
///
/// It never brings the app forward by itself: in the background the Dock icon asks for your attention, and the question
/// is shown once you switch to the app. A question that went stale (decided in the browser or with `ot`, or changed
/// by an agent) is closed, and the next one is asked (`HookPromptState`).
@MainActor
public final class HookApprovalPrompt {
    public static let shared = HookApprovalPrompt()

    private weak var model: BoardModel?
    private var watching = Set<AnyCancellable>()
    private var state = HookPromptState()
    private var shown: (alert: NSAlert, window: NSWindow?)?
    private var escape: Any?

    public func watch(_ model: BoardModel) {
        self.model = model
        watching.removeAll()
        model.$pendingHook.receive(on: DispatchQueue.main).sink { [weak self] _ in
            DispatchQueue.main.async { self?.update() }
        }.store(in: &watching)
        NotificationCenter.default.publisher(for: NSApplication.didBecomeActiveNotification).sink { [weak self] _ in
            DispatchQueue.main.async { self?.update() }
        }.store(in: &watching)
    }

    private func update() {
        guard let model else { return }
        for step in state.update(pending: model.pendingHook, appActive: NSApp.isActive) {
            switch step {
            case .dismiss: dismiss()
            case .requestAttention: NSApp.requestUserAttention(.informationalRequest)
            case let .show(hook): show(hook)
            }
        }
    }

    private func show(_ hook: PendingHook) {
        let alert = Self.alert(for: hook)
        let decide: (NSApplication.ModalResponse) -> Void = { [weak self] response in
            guard let self else { return }
            self.closed(alert)
            // Closed because it went stale: its answer counts for nothing.
            guard response != .abort, self.state.answered(hook), let model = self.model else { return }
            switch response {
            case .alertSecondButtonReturn: model.decideHook(hook, allow: true)
            case .alertThirdButtonReturn: model.decideHook(hook, allow: false)
            default: model.putOffHook(hook)
            }
        }
        // Escape means Later too (Return is Later's key).
        escape = NSEvent.addLocalMonitorForEvents(matching: .keyDown) { event in
            guard event.window === alert.window, event.keyCode == 53 else { return event }
            alert.buttons.first?.performClick(nil)
            return nil
        }
        if let window = NSApp.windows.first(where: { $0.isVisible && $0.canBecomeMain && $0.styleMask.contains(.titled) && $0.attachedSheet == nil }) {
            shown = (alert, window)
            alert.beginSheetModal(for: window) { response in MainActor.assumeIsolated { decide(response) } }
        } else {
            shown = (alert, nil)
            decide(alert.runModal())
        }
    }

    /// Closes the alert shown, answering nothing.
    private func dismiss() {
        guard let (alert, window) = shown.map({ ($0.alert, $0.window) }) else { return }
        if let window, window.attachedSheet === alert.window {
            window.endSheet(alert.window, returnCode: .abort)
        } else if window == nil {
            NSApp.stopModal(withCode: .abort)
        }
        closed(alert)
    }

    private func closed(_ alert: NSAlert) {
        guard shown?.alert === alert else { return }
        shown = nil
        if let escape { NSEvent.removeMonitor(escape) }
        escape = nil
    }

    /// The question, with the command in a box you can scroll and select (never cut short). Buttons, right to left:
    /// Later (Return), Allow, Don't allow.
    static func alert(for hook: PendingHook) -> NSAlert {
        let alert = NSAlert()
        alert.alertStyle = .warning
        alert.messageText = hook.title
        alert.informativeText = "\(hook.when)\n\n\(PendingHook.fineprint)"
        let text = NSTextView(frame: NSRect(x: 0, y: 0, width: 460, height: 10))
        text.string = hook.description.isEmpty ? "(It says nothing it would run.)" : hook.description
        text.isEditable = false
        text.isSelectable = true
        text.font = .monospacedSystemFont(ofSize: NSFont.smallSystemFontSize, weight: .regular)
        text.textContainerInset = NSSize(width: 6, height: 6)
        text.isVerticallyResizable = true
        text.textContainer?.widthTracksTextView = true
        text.layoutManager?.ensureLayout(for: text.textContainer!)
        let needed = (text.layoutManager?.usedRect(for: text.textContainer!).height ?? 40) + 14
        let scroll = NSScrollView(frame: NSRect(x: 0, y: 0, width: 460, height: min(240, max(44, needed))))
        scroll.documentView = text
        scroll.hasVerticalScroller = true
        scroll.borderType = .bezelBorder
        alert.accessoryView = scroll
        let later = alert.addButton(withTitle: "Later")
        later.keyEquivalent = "\r"
        later.toolTip = "Decide later in Settings › Hooks"
        // Both answers need a click: no key allows a hook or removes it.
        for title in ["Allow", "Don’t allow"] {
            let button = alert.addButton(withTitle: title)
            button.keyEquivalent = ""
        }
        return alert
    }
}
#endif
