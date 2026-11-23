import XCTest
@testable import OneThingKit

/// Settings › Command line tool: links to the app's onething and ot in ~/.local/bin. Always on a temporary home folder.
final class CommandLineToolTests: XCTestCase {
    private var root: URL!
    private var home: URL!
    private var fm: FileManager { .default }

    override func setUpWithError() throws {
        root = fm.temporaryDirectory.appendingPathComponent("cli-tool-\(UUID().uuidString)", isDirectory: true)
        home = root.appendingPathComponent("home", isDirectory: true)
        try fm.createDirectory(at: home, withIntermediateDirectories: true)
    }

    override func tearDownWithError() throws {
        try? fm.removeItem(at: root)
    }

    /// A Mac app with the command line in it, as build/mac-cli.sh puts it there.
    private func app(_ name: String = "OneThing.app") throws -> URL {
        let cli = root.appendingPathComponent("\(name)/Contents/Resources/Server/cli", isDirectory: true)
        try fm.createDirectory(at: cli, withIntermediateDirectories: true)
        let onething = cli.appendingPathComponent("onething")
        try Data("#!/bin/sh\necho 2.0.0\n".utf8).write(to: onething)
        try fm.setAttributes([.posixPermissions: 0o755], ofItemAtPath: onething.path)
        try fm.createSymbolicLink(atPath: cli.appendingPathComponent("ot").path, withDestinationPath: "onething")
        return onething
    }

    private var bin: URL { CommandLineTool.binDirectory(home: home) }

    private var pathWithBin: String { "/usr/bin:/bin:\(bin.path)" }

    func testTheCommandLineIsFoundInTheAppBundle() throws {
        let onething = try app()
        let resources = onething.deletingLastPathComponent().deletingLastPathComponent().deletingLastPathComponent()

        XCTAssertEqual(CommandLineTool.bundled(in: resources)?.path, onething.path)
        XCTAssertNil(CommandLineTool.bundled(in: root))
    }

    func testNothingInstalledShowsBothNamesMissing() throws {
        let report = CommandLineTool.report(home: home, bundled: try app(), path: "/usr/bin:/bin")

        XCTAssertEqual(report.statuses.map(\.name), ["onething", "ot"])
        XCTAssertEqual(report.statuses.map(\.state), [.missing, .missing])
        XCTAssertFalse(report.binOnPath)
        XCTAssertFalse(report.installed)
    }

    func testInstallMakesTheFolderAndLinksBothNamesToTheApp() throws {
        let onething = try app()

        let outcomes = try CommandLineTool.install(home: home, bundled: onething)

        XCTAssertEqual(outcomes, [.linked(name: "onething"), .linked(name: "ot")])
        XCTAssertEqual(try fm.destinationOfSymbolicLink(atPath: bin.appendingPathComponent("ot").path), onething.deletingLastPathComponent().appendingPathComponent("ot").path)
        let report = CommandLineTool.report(home: home, bundled: onething, path: pathWithBin)
        XCTAssertTrue(report.installed)
        XCTAssertTrue(report.statuses.allSatisfy(\.runsThisApp))
    }

    func testAPathWrittenWithATildeCounts() throws {
        let onething = try app()
        try CommandLineTool.install(home: home, bundled: onething)

        XCTAssertTrue(CommandLineTool.report(home: home, bundled: onething, path: "~/.local/bin:/usr/bin").binOnPath)
        XCTAssertTrue(CommandLineTool.report(home: home, bundled: onething, path: "$HOME/.local/bin/:/usr/bin").binOnPath)
    }

    func testAMovedAppLeavesABrokenLinkThatInstallRepairs() throws {
        let old = try app()
        try CommandLineTool.install(home: home, bundled: old)
        try fm.removeItem(at: root.appendingPathComponent("OneThing.app"))
        let moved = try app("Apps/OneThing.app")

        let report = CommandLineTool.report(home: home, bundled: moved, path: pathWithBin)
        XCTAssertTrue(report.hasBrokenLink)
        XCTAssertEqual(report.statuses[0].state, .broken(target: old.path))
        XCTAssertTrue(report.needsAsking.isEmpty)

        try CommandLineTool.install(home: home, bundled: moved)
        XCTAssertTrue(CommandLineTool.report(home: home, bundled: moved, path: pathWithBin).installed)
    }

    func testALinkToAnotherCopyOfTheAppIsReplacedWithoutAsking() throws {
        let other = try app("Other/OneThing.app")
        let this = try app()
        try CommandLineTool.install(home: home, bundled: other)

        XCTAssertEqual(CommandLineTool.report(home: home, bundled: this, path: pathWithBin).statuses[0].state, .otherOneThing(target: other.path))
        try CommandLineTool.install(home: home, bundled: this)
        XCTAssertTrue(CommandLineTool.report(home: home, bundled: this, path: pathWithBin).installed)
    }

    func testOnlyOnethingIsOursAmongDotnetTools() {
        // The .NET tool installs onething only: an ot there is someone else's tool.
        XCTAssertTrue(CommandLineTool.isOneThingCli("/Users/me/.dotnet/tools/onething"))
        XCTAssertFalse(CommandLineTool.isOneThingCli("/Users/me/.dotnet/tools/ot"))
        XCTAssertTrue(CommandLineTool.isOneThingCli("/Applications/OneThing.app/Contents/Resources/Server/cli/ot"))
    }

    func testAFileThatIsntOursIsNeverReplacedWithoutAsking() throws {
        let onething = try app()
        try fm.createDirectory(at: bin, withIntermediateDirectories: true)
        let mine = bin.appendingPathComponent("ot")
        try Data("#!/bin/sh\necho my own ot\n".utf8).write(to: mine)

        let report = CommandLineTool.report(home: home, bundled: onething, path: pathWithBin)
        XCTAssertEqual(report.needsAsking.map(\.name), ["ot"])
        let outcomes = try CommandLineTool.install(home: home, bundled: onething)

        XCTAssertEqual(outcomes[0], .linked(name: "onething"))
        guard case .skipped(name: "ot", _) = outcomes[1] else { return XCTFail("ot was replaced: \(outcomes)") }
        XCTAssertEqual(try String(contentsOf: mine, encoding: .utf8), "#!/bin/sh\necho my own ot\n")
    }

    func testReplacingAFileThatIsntOursKeepsItBesideTheLink() throws {
        let onething = try app()
        try fm.createDirectory(at: bin, withIntermediateDirectories: true)
        try fm.createSymbolicLink(atPath: bin.appendingPathComponent("ot").path, withDestinationPath: "/usr/local/bin/some-other-ot")

        let outcomes = try CommandLineTool.install(home: home, bundled: onething, replacing: ["ot"])

        let kept = bin.appendingPathComponent("ot.before-onething").path
        XCTAssertEqual(outcomes[1], .linkedKeeping(name: "ot", keptAs: kept))
        XCTAssertEqual(try fm.destinationOfSymbolicLink(atPath: kept), "/usr/local/bin/some-other-ot")
        XCTAssertTrue(CommandLineTool.report(home: home, bundled: onething, path: pathWithBin).installed)
    }

    func testWhatATerminalRunsIsTheFirstOnThePath() throws {
        let onething = try app()
        try CommandLineTool.install(home: home, bundled: onething)
        let earlier = root.appendingPathComponent("earlier", isDirectory: true)
        try fm.createDirectory(at: earlier, withIntermediateDirectories: true)
        let other = earlier.appendingPathComponent("ot")
        try Data("#!/bin/sh\n".utf8).write(to: other)
        try fm.setAttributes([.posixPermissions: 0o755], ofItemAtPath: other.path)

        let report = CommandLineTool.report(home: home, bundled: onething, path: "\(earlier.path):\(bin.path)")

        XCTAssertTrue(report.statuses[0].runsThisApp)
        XCTAssertFalse(report.statuses[1].runsThisApp)
        XCTAssertEqual(report.statuses[1].onPath, other.resolvingSymlinksInPath().path)
    }

    func testTheProfileLineFitsTheShell() {
        XCTAssertTrue(CommandLineTool.profileLine(shell: "/bin/zsh").contains("~/.zshrc"))
        XCTAssertTrue(CommandLineTool.profileLine(shell: "/bin/bash").contains("~/.bash_profile"))
        XCTAssertEqual(CommandLineTool.profileLine(shell: "/opt/homebrew/bin/fish"), "fish_add_path ~/.local/bin")
        XCTAssertTrue(CommandLineTool.profileLine(shell: nil).contains("~/.zshrc"))
    }
}
