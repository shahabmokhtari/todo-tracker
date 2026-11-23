#if os(macOS)
import AppKit
import Combine
import OneThingKit

/// Asks you about a hook an AI agent added or changed (it waits for approval): who wants it, when it would run, and
/// everything it would do, in full. "Don't allow" is the default (Return); "Allow" runs it from now on; "Later" leaves
/// it waiting in Settings › Hooks. Nothing is ever allowed without your answer. The words are the web prompt's
/// (hook-prompt.js).
@MainActor
public final class HookApprovalPrompt {
    public static let shared = HookApprovalPrompt()

    private var watching: AnyCancellable?
    private var showing: String?

    public func watch(_ model: BoardModel) {
        watching = model.$pendingHook.receive(on: DispatchQueue.main).sink { [weak self, weak model] hook in
            guard let self, let model, let hook else { return }
            DispatchQueue.main.async { self.ask(hook, model: model) }
        }
    }

    private func ask(_ hook: PendingHook, model: BoardModel) {
        guard showing == nil else { return }
        showing = hook.askKey
        let alert = Self.alert(for: hook)
        NSApp.requestUserAttention(.criticalRequest)
        let decide: (NSApplication.ModalResponse) -> Void = { [weak self, weak model] response in
            self?.showing = nil
            guard let model else { return }
            switch response {
            case .alertSecondButtonReturn: model.decideHook(hook, allow: true)
            case .alertFirstButtonReturn: model.decideHook(hook, allow: false)
            default: model.putOffHook(hook)
            }
        }
        if let window = NSApp.windows.first(where: { $0.isVisible && $0.canBecomeMain && $0.styleMask.contains(.titled) && $0.attachedSheet == nil }) {
            window.makeKeyAndOrderFront(nil)
            NSApp.activate(ignoringOtherApps: true)
            alert.beginSheetModal(for: window) { response in MainActor.assumeIsolated { decide(response) } }
        } else {
            NSApp.activate(ignoringOtherApps: true)
            decide(alert.runModal())
        }
    }

    /// The question, with the command in a box you can scroll and select (never cut short).
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
        // The safe answer is the default one (Return); Escape puts it off.
        alert.addButton(withTitle: "Don’t allow")
        alert.addButton(withTitle: "Allow")
        let later = alert.addButton(withTitle: "Later")
        later.keyEquivalent = "\u{1b}"
        later.toolTip = "Decide later in Settings › Hooks"
        return alert
    }
}
#endif
