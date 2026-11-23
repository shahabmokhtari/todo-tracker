#if os(macOS)
import AppKit
import Combine
import Foundation
import OneThingKit
import WebKit

/// The One Thing window on a Mac: the web app from this Mac's own server (127.0.0.1), signed in as you, in a window of
/// this app (as the Windows app shows it). Settings' connected apps, sync, hooks, extras, Connect AI apps, the browser
/// extension and Ask AI open here, each on its own panel.
///
/// Only the app itself stays in the window: its other pages (the printable report, an attachment) open in your browser
/// signed in, links elsewhere in your browser, and nothing else loads. Files the page offers to download go to
/// Downloads; picking files to attach works as in a browser. When the server starts again, the window signs in again.
@MainActor
public final class OneThingWindow: NSObject {
    public static let shared = OneThingWindow()

    private weak var model: BoardModel?
    private var window: NSWindow?
    private var web: WKWebView?
    /// The page shown is the app, fully loaded (a path then changes the address in place).
    private var loaded = false
    /// The server session (`BoardModel.serverSession`) the page signed in with.
    private var signedInSession = -1
    /// The path to show once the server answers again.
    private var waitingPath: String?
    private var breaksScript: WKUserScript?
    private var watching = Set<AnyCancellable>()
    private var downloads: [ObjectIdentifier: URL] = [:]
    /// Tries since the server last answered, and the next one (while the window is open: 1, 2, 4… then every 30 s).
    private var retryAttempt = 0
    private var retry: Task<Void, Never>?
    /// The path last loaded with a sign-in link (tried again when it couldn't load).
    private var loadingPath = "/"

    /// Shows a panel of the app (Settings' rows, Ask AI…) in the window.
    public func show(_ panel: WebAppPanel, model: BoardModel) { show(path: panel.path, model: model) }

    /// Shows a path of the app ("/", "/#/task/<id>", "/?sync") in the window, signed in as you.
    public func show(path: String, model: BoardModel) {
        attach(model)
        guard model.usesServer, let origin = model.serverURL else {
            model.status = "The One Thing window needs your tasks folder open. One moment…"
            waitingPath = path
            return
        }
        let web = webView()
        window?.makeKeyAndOrderFront(nil)
        NSApp.activate(ignoringOtherApps: true)
        if signedInSession != model.serverSession { loaded = false }
        switch AppWindowNavigation.plan(current: web.url, loaded: loaded && AppWindowNavigation.classify(origin: origin, web.url) == .app, path: path) {
        case .focus:
            break
        case let .setHash(hash):
            web.evaluateJavaScript("location.hash = \(Self.jsString(hash))") { [weak self] _, error in
                if error != nil { self?.navigate(path) }
            }
        case let .navigate(path):
            navigate(path)
        }
    }

    /// The window and its page (made once; closing the window only hides it, so the page keeps its place).
    private func webView() -> WKWebView {
        if let web { return web }
        let configuration = WKWebViewConfiguration()
        configuration.websiteDataStore = .default()
        let content = WKUserContentController()
        // This app asks about hooks waiting for approval itself (a native prompt): the page doesn't too.
        content.addUserScript(WKUserScript(source: "window.__ttNativeHookPrompt = true;", injectionTime: .atDocumentStart, forMainFrameOnly: true))
        configuration.userContentController = content
        let web = WKWebView(frame: NSRect(x: 0, y: 0, width: 1180, height: 800), configuration: configuration)
        web.navigationDelegate = self
        web.uiDelegate = self
        web.allowsBackForwardNavigationGestures = true
        web.setValue(false, forKey: "drawsBackground")
        self.web = web
        setNativeBreaks()

        let window = NSWindow(contentRect: NSRect(x: 0, y: 0, width: 1180, height: 800),
                              styleMask: [.titled, .closable, .miniaturizable, .resizable, .fullSizeContentView], backing: .buffered, defer: false)
        window.title = "One Thing"
        window.contentView = web
        window.isReleasedWhenClosed = false
        window.minSize = NSSize(width: 420, height: 480)
        window.center()
        window.setFrameAutosaveName("OneThingWebWindow")
        window.tabbingMode = .disallowed
        self.window = window
        return web
    }

    private func attach(_ model: BoardModel) {
        guard self.model !== model else { return }
        self.model = model
        watching.removeAll()
        // The server started again (a new session): the page signs in again, where it was. A path asked for while the
        // tasks folder was opening is shown now.
        model.$serverSession.dropFirst().receive(on: DispatchQueue.main).sink { [weak self] _ in
            guard let self, let model = self.model else { return }
            DispatchQueue.main.async {
                if let path = self.waitingPath {
                    self.waitingPath = nil
                    self.show(path: path, model: model)
                } else if let web = self.web, self.window?.isVisible == true {
                    self.navigate(web.url.map(AppWindowNavigation.path(of:)) ?? "/")
                }
            }
        }.store(in: &watching)
        // The desktop's own breaks cover every screen: the page's break screen stays off while they're on.
        model.$fullScreenBreaks.removeDuplicates().receive(on: DispatchQueue.main).sink { [weak self] _ in
            DispatchQueue.main.async { self?.setNativeBreaks() }
        }.store(in: &watching)
    }

    private func setNativeBreaks() {
        guard let web else { return }
        let script = "window.__ttNativeBreaks = \(model?.fullScreenBreaks ?? true ? "true" : "false");"
        let content = web.configuration.userContentController
        let kept = content.userScripts.filter { $0 !== breaksScript }
        content.removeAllUserScripts()
        kept.forEach(content.addUserScript)
        let fresh = WKUserScript(source: script, injectionTime: .atDocumentStart, forMainFrameOnly: true)
        content.addUserScript(fresh)
        breaksScript = fresh
        if loaded { web.evaluateJavaScript(script) }
    }

    /// Loads a path with a new single-use sign-in link (the owner's: this app sends the owner key).
    private func navigate(_ path: String) {
        guard let model, let web else { return }
        loaded = false
        loadingPath = path
        let session = model.serverSession
        Task { @MainActor in
            guard let url = await model.launchURL(returnPath: path) else {
                // The server isn't answering (it's being started again): shown when it's back.
                serverNotAnswering(path)
                return
            }
            signedInSession = session
            web.load(URLRequest(url: url))
        }
    }

    /// Says so, and tries again soon while the window is open (a new server session also brings it back at once).
    private func serverNotAnswering(_ path: String) {
        waitingPath = path
        showProblem("One Thing’s server isn’t answering right now. This window comes back by itself when it does.")
        retry?.cancel()
        let delay = AppWindowNavigation.retryDelay(attempt: retryAttempt)
        retryAttempt += 1
        retry = Task { @MainActor [weak self] in
            try? await Task.sleep(for: .seconds(delay))
            guard let self, !Task.isCancelled, self.window?.isVisible == true, let path = self.waitingPath else { return }
            self.waitingPath = nil
            self.navigate(path)
        }
    }

    private func showProblem(_ text: String) {
        let escaped = text.replacingOccurrences(of: "&", with: "&amp;").replacingOccurrences(of: "<", with: "&lt;")
        web?.loadHTMLString("""
        <!doctype html><meta charset="utf-8"><body style="font: 15px -apple-system; color: #64748b; display: grid; place-items: center; height: 90vh; margin: 0">
        <p style="max-width: 28em; text-align: center">\(escaped)</p></body>
        """, baseURL: nil)
    }

    /// Opens a link that doesn't stay in the window: the app's other pages in the browser (signed in), links elsewhere
    /// in the browser, other apps only when clicked, and nothing else (`AppWindowNavigation.follow`).
    private func follow(_ url: URL, userInitiated: Bool, inWindow: Bool, mainFrame: Bool) {
        guard let model, let origin = model.serverURL else { return }
        let kind = AppWindowNavigation.classify(origin: origin, url)
        switch AppWindowNavigation.follow(kind, url: url, inWindow: inWindow, mainFrame: mainFrame, userInitiated: userInitiated) {
        case .signedInBrowser:
            let path = AppWindowNavigation.path(of: url)
            Task { @MainActor in if let signedIn = await model.launchURL(returnPath: path) { NSWorkspace.shared.open(signedIn) } }
        case .open:
            // A task link opens in the One Thing app (its own link handler); the rest in the browser or their app.
            NSWorkspace.shared.open(url)
        case .nothing:
            break
        }
    }

    static func jsString(_ text: String) -> String {
        let data = (try? JSONSerialization.data(withJSONObject: [text], options: [.withoutEscapingSlashes])) ?? Data("[\"\"]".utf8)
        return String(String(decoding: data, as: UTF8.self).dropFirst().dropLast())
    }
}

extension OneThingWindow: WKNavigationDelegate {
    public func webView(_ webView: WKWebView, decidePolicyFor action: WKNavigationAction, preferences: WKWebpagePreferences,
                        decisionHandler: @escaping @MainActor (WKNavigationActionPolicy, WKWebpagePreferences) -> Void) {
        guard let origin = model?.serverURL else {
            decisionHandler(action.request.url?.absoluteString == "about:blank" ? .allow : .cancel, preferences)
            return
        }
        let kind = AppWindowNavigation.classify(origin: origin, action.request.url)
        // A file the page offers (a link with "download": the browser extension, an attachment): to Downloads.
        if action.shouldPerformDownload, kind == .app || kind == .ownPageInBrowser {
            decisionHandler(.download, preferences)
            return
        }
        if kind == .app || action.targetFrame?.isMainFrame == false && kind == .ownPageInBrowser {
            decisionHandler(.allow, preferences)
            return
        }
        decisionHandler(.cancel, preferences)
        if let url = action.request.url {
            follow(url, userInitiated: action.navigationType == .linkActivated, inWindow: true, mainFrame: action.targetFrame?.isMainFrame != false)
        }
    }

    public func webView(_ webView: WKWebView, decidePolicyFor response: WKNavigationResponse,
                        decisionHandler: @escaping @MainActor (WKNavigationResponsePolicy) -> Void) {
        decisionHandler(response.canShowMIMEType ? .allow : .download)
    }

    public func webView(_ webView: WKWebView, didFinish navigation: WKNavigation!) {
        guard let origin = model?.serverURL else { return }
        loaded = AppWindowNavigation.classify(origin: origin, webView.url) == .app && webView.url?.absoluteString != "about:blank"
        if loaded {
            retryAttempt = 0
            retry?.cancel()
        }
    }

    public func webView(_ webView: WKWebView, didFailProvisionalNavigation navigation: WKNavigation!, withError error: Error) {
        loaded = false
        // Cancelled on purpose (a link sent elsewhere, a download): nothing went wrong.
        let code = (error as NSError).code
        if code == NSURLErrorCancelled || code == 102 /* frame load interrupted */ { return }
        serverNotAnswering(loadingPath)
    }

    public func webViewWebContentProcessDidTerminate(_ webView: WKWebView) {
        navigate(webView.url.map(AppWindowNavigation.path(of:)) ?? "/")
    }

    public func webView(_ webView: WKWebView, navigationAction: WKNavigationAction, didBecome download: WKDownload) {
        download.delegate = self
    }

    public func webView(_ webView: WKWebView, navigationResponse: WKNavigationResponse, didBecome download: WKDownload) {
        download.delegate = self
    }
}

extension OneThingWindow: WKDownloadDelegate {
    public func download(_ download: WKDownload, decideDestinationUsing response: URLResponse, suggestedFilename: String,
                         completionHandler: @escaping @MainActor (URL?) -> Void) {
        let folder = FileManager.default.urls(for: .downloadsDirectory, in: .userDomainMask).first ?? FileManager.default.temporaryDirectory
        let name = (suggestedFilename as NSString).lastPathComponent.isEmpty ? "download" : (suggestedFilename as NSString).lastPathComponent
        var target = folder.appendingPathComponent(name)
        var n = 2
        // Never over a file that's there: "name 2.zip".
        while FileManager.default.fileExists(atPath: target.path) {
            let base = (name as NSString).deletingPathExtension, ext = (name as NSString).pathExtension
            target = folder.appendingPathComponent(ext.isEmpty ? "\(base) \(n)" : "\(base) \(n).\(ext)")
            n += 1
        }
        downloads[ObjectIdentifier(download)] = target
        completionHandler(target)
    }

    public func downloadDidFinish(_ download: WKDownload) {
        guard let target = downloads.removeValue(forKey: ObjectIdentifier(download)) else { return }
        model?.status = "Saved \(target.lastPathComponent) in Downloads"
        NSWorkspace.shared.activateFileViewerSelecting([target])
    }

    public func download(_ download: WKDownload, didFailWithError error: Error, resumeData: Data?) {
        downloads.removeValue(forKey: ObjectIdentifier(download))
        model?.status = "Couldn’t download the file: \(error.localizedDescription)"
    }
}

extension OneThingWindow: WKUIDelegate {
    /// A new window (target=_blank, window.open): never a bare popup; the link goes where it belongs. Only a clicked
    /// link counts as asked for (a script's window.open to mailto: or obsidian: opens nothing), as `IsUserInitiated` on
    /// Windows.
    public func webView(_ webView: WKWebView, createWebViewWith configuration: WKWebViewConfiguration, for action: WKNavigationAction,
                        windowFeatures: WKWindowFeatures) -> WKWebView? {
        if let url = action.request.url {
            follow(url, userInitiated: action.navigationType == .linkActivated, inWindow: false, mainFrame: true)
        }
        return nil
    }

    public func webView(_ webView: WKWebView, runJavaScriptAlertPanelWithMessage message: String, initiatedByFrame frame: WKFrameInfo,
                        completionHandler: @escaping @MainActor () -> Void) {
        let alert = NSAlert()
        alert.messageText = message
        alert.addButton(withTitle: "OK")
        present(alert) { _ in completionHandler() }
    }

    /// The page's questions ("Remove the label … from every task?"): OK or Cancel, Cancel on Escape.
    public func webView(_ webView: WKWebView, runJavaScriptConfirmPanelWithMessage message: String, initiatedByFrame frame: WKFrameInfo,
                        completionHandler: @escaping @MainActor (Bool) -> Void) {
        let alert = NSAlert()
        alert.messageText = message
        alert.addButton(withTitle: "OK")
        alert.addButton(withTitle: "Cancel")
        present(alert) { completionHandler($0 == .alertFirstButtonReturn) }
    }

    public func webView(_ webView: WKWebView, runJavaScriptTextInputPanelWithPrompt prompt: String, defaultText: String?,
                        initiatedByFrame frame: WKFrameInfo, completionHandler: @escaping @MainActor (String?) -> Void) {
        let alert = NSAlert()
        alert.messageText = prompt
        let field = NSTextField(frame: NSRect(x: 0, y: 0, width: 300, height: 24))
        field.stringValue = defaultText ?? ""
        alert.accessoryView = field
        alert.addButton(withTitle: "OK")
        alert.addButton(withTitle: "Cancel")
        alert.window.initialFirstResponder = field
        present(alert) { completionHandler($0 == .alertFirstButtonReturn ? field.stringValue : nil) }
    }

    /// Attaching files: the Mac's file picker.
    public func webView(_ webView: WKWebView, runOpenPanelWith parameters: WKOpenPanelParameters, initiatedByFrame frame: WKFrameInfo,
                        completionHandler: @escaping @MainActor ([URL]?) -> Void) {
        let panel = NSOpenPanel()
        panel.allowsMultipleSelection = parameters.allowsMultipleSelection
        panel.canChooseDirectories = parameters.allowsDirectories
        panel.canChooseFiles = true
        if let window {
            panel.beginSheetModal(for: window) { completionHandler($0 == .OK ? panel.urls : nil) }
        } else {
            completionHandler(panel.runModal() == .OK ? panel.urls : nil)
        }
    }

    private func present(_ alert: NSAlert, _ done: @escaping @MainActor (NSApplication.ModalResponse) -> Void) {
        if let window, window.isVisible {
            alert.beginSheetModal(for: window) { response in MainActor.assumeIsolated { done(response) } }
        } else {
            done(alert.runModal())
        }
    }
}
#endif
