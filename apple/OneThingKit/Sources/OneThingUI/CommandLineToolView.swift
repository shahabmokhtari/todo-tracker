#if canImport(SwiftUI)
import SwiftUI
import OneThingKit
#if os(macOS)
import AppKit
#endif

/// Settings › Command line tool (the Mac): whether `onething` and `ot` are on the PATH and which file they run, and
/// Install, which links both into ~/.local/bin (the logic is `CommandLineTool`). Elsewhere it shows nothing.
public struct CommandLineToolSection: View {
    public init() {}

    public var body: some View {
        #if os(macOS)
        CommandLineToolMacSection()
        #else
        EmptyView()
        #endif
    }
}

/// One Thing › Install Command Line Tool… in the Mac's menu bar (nothing elsewhere).
public struct CommandLineToolCommands: Commands {
    public init() {}

    public var body: some Commands {
        #if os(macOS)
        CommandGroup(after: .appSettings) {
            Button("Install Command Line Tool…") { CommandLineToolPanel.run() }
        }
        #else
        EmptyCommands()
        #endif
    }
}

#if os(macOS)
/// What the section and the menu item work with: this app's command line, the home folder, and a terminal's PATH.
@MainActor
final class CommandLineToolModel: ObservableObject {
    @Published private(set) var report: CommandLineTool.Report?
    @Published var message: String?

    let home = FileManager.default.homeDirectoryForCurrentUser
    let bundled = CommandLineTool.bundled(in: Bundle.main.resourceURL)
    let shell = ProcessInfo.processInfo.environment["SHELL"]

    func refresh() async {
        let path = await TerminalPath.read(shell: shell)
        report = CommandLineTool.report(home: home, bundled: bundled, path: path)
    }

    /// Links both names; `replacing`: names whose own file the person agreed to replace (kept beside the link).
    func install(replacing: Set<String> = []) async {
        guard let bundled else { return }
        do {
            let outcomes = try CommandLineTool.install(home: home, bundled: bundled, replacing: replacing)
            await refresh()
            message = Self.describe(outcomes, report: report, shell: shell)
        } catch {
            message = "Couldn't link the command line into ~/.local/bin: \(error.localizedDescription)"
        }
    }

    static func describe(_ outcomes: [CommandLineTool.Outcome], report: CommandLineTool.Report?, shell: String?) -> String {
        var lines = outcomes.map { outcome -> String in
            switch outcome {
            case .linked(let name): return "\(name) is linked in ~/.local/bin."
            case .linkedKeeping(let name, let kept): return "\(name) is linked in ~/.local/bin (what was there is kept as \((kept as NSString).lastPathComponent))."
            case .skipped(let name, let reason): return "\(name) wasn't linked: \(reason)."
            }
        }
        if report?.binOnPath == false {
            lines.append("~/.local/bin isn't on your PATH yet. Run this once in Terminal, then open a new window:\n\(CommandLineTool.profileLine(shell: shell))")
        } else {
            lines.append("Open a new Terminal window and try: ot now")
        }
        return lines.joined(separator: "\n")
    }
}

/// The PATH a new Terminal window has (the app itself starts with a short one): asked of the login shell, briefly.
enum TerminalPath {
    static func read(shell: String?) async -> String {
        let fallback = ProcessInfo.processInfo.environment["PATH"] ?? "/usr/bin:/bin:/usr/sbin:/sbin"
        return await Task.detached { () -> String in
            let process = Process()
            process.executableURL = URL(fileURLWithPath: shell ?? "/bin/zsh")
            process.arguments = ["-l", "-i", "-c", "printf '%s' \"$PATH\""]
            let out = Pipe()
            process.standardOutput = out
            process.standardError = FileHandle.nullDevice
            process.standardInput = FileHandle.nullDevice
            do { try process.run() } catch { return fallback }
            // A shell profile that waits for something mustn't hold the window: give up after 3 seconds.
            let deadline = Date().addingTimeInterval(3)
            while process.isRunning && Date() < deadline { try? await Task.sleep(nanoseconds: 50_000_000) }
            if process.isRunning { process.terminate(); return fallback }
            let text = String(decoding: out.fileHandleForReading.readDataToEndOfFile(), as: UTF8.self)
            let path = text.split(separator: "\n").last.map(String.init) ?? ""
            return path.contains("/") ? path : fallback
        }.value
    }
}

struct CommandLineToolMacSection: View {
    @StateObject private var model = CommandLineToolModel()
    @State private var asking = false

    var body: some View {
        Section {
            if model.bundled == nil {
                Text("This copy of One Thing has no command line in it.").foregroundStyle(.secondary)
            } else if let report = model.report {
                ForEach(report.statuses, id: \.name) { status in
                    LabeledContent(status.name) {
                        Text(Self.describe(status)).foregroundStyle(status.runsThisApp ? Color.primary : Color.secondary).textSelection(.enabled)
                    }
                }
                Button(report.hasBrokenLink ? "Repair" : report.installed ? "Install Again" : "Install") {
                    if report.needsAsking.isEmpty { Task { await model.install() } } else { asking = true }
                }
                .accessibilityIdentifier("settings-cli-install")
                .confirmationDialog("Replace what's there?", isPresented: $asking) {
                    Button("Replace (keep the old one beside it)") { Task { await model.install(replacing: Set(report.needsAsking.map(\.name))) } }
                    Button("Link only the free names") { Task { await model.install() } }
                } message: {
                    Text(report.needsAsking.map { "\($0.link) is \(Self.foreign($0.state))." }.joined(separator: "\n"))
                }
                if let message = model.message { Text(message).font(.footnote).textSelection(.enabled) }
            } else {
                ProgressView().controlSize(.small)
            }
        } header: {
            Text("Command line tool")
        } footer: {
            Text("ot and onething in Terminal and for AI agents: Install links both into ~/.local/bin (no password needed). Connect an AI app works without it.")
        }
        .task { await model.refresh() }
    }

    static func describe(_ status: CommandLineTool.Status) -> String {
        if case .broken = status.state { return "Broken: it points at an app that moved. Repair links it again." }
        if status.runsThisApp { return "Installed (\(status.link))" }
        if let found = status.onPath { return "Runs \(found)" }
        if status.state == .current { return "Linked, but ~/.local/bin isn't on your PATH" }
        return "Not installed"
    }

    static func foreign(_ state: CommandLineTool.LinkState) -> String {
        if case .foreign(let description) = state { return description }
        return "something else"
    }
}

/// The menu item: says what's installed, installs on OK, and asks before replacing a file that isn't One Thing's.
@MainActor
enum CommandLineToolPanel {
    static func run() {
        let model = CommandLineToolModel()
        Task { @MainActor in
            await model.refresh()
            guard model.bundled != nil, let report = model.report else {
                alert("No command line in this copy", "This copy of One Thing has no onething or ot in it.", buttons: ["OK"])
                return
            }
            let now = report.statuses.map { "\($0.name): \(CommandLineToolMacSection.describe($0))" }.joined(separator: "\n")
            guard alert("Install the command line tool?", "Links onething and ot into ~/.local/bin, for Terminal and AI agents.\n\n\(now)", buttons: [report.hasBrokenLink ? "Repair" : "Install", "Cancel"]) == .alertFirstButtonReturn else { return }
            var replacing = Set<String>()
            if !report.needsAsking.isEmpty {
                let what = report.needsAsking.map { "\($0.link) is \(CommandLineToolMacSection.foreign($0.state))." }.joined(separator: "\n")
                if alert("Replace what's there?", "\(what)\nThe old one is kept beside it.", buttons: ["Replace", "Link only the free names"]) == .alertFirstButtonReturn {
                    replacing = Set(report.needsAsking.map(\.name))
                }
            }
            await model.install(replacing: replacing)
            alert("Command line tool", model.message ?? "", buttons: ["OK"])
        }
    }

    @discardableResult
    private static func alert(_ title: String, _ text: String, buttons: [String]) -> NSApplication.ModalResponse {
        let alert = NSAlert()
        alert.messageText = title
        alert.informativeText = text
        buttons.forEach { alert.addButton(withTitle: $0) }
        return alert.runModal()
    }
}
#endif
#endif
