import Foundation
import XCTest
@testable import TodoTrackerKit

/// tests/fixtures/vault/render.json: a board brought into a new tasks folder writes exactly the files the C# core
/// writes (task files, config.json, state.json, activity.jsonl), publishes the same sync entries, and reads back as
/// the same board.
final class VaultStoreFixtureTests: XCTestCase {
    private func files(in root: URL) throws -> [String: Data] {
        var result: [String: Data] = [:]
        let enumerator = FileManager.default.enumerator(atPath: root.path)!
        while let rel = enumerator.nextObject() as? String {
            var isDir: ObjCBool = false
            FileManager.default.fileExists(atPath: root.appendingPathComponent(rel).path, isDirectory: &isDir)
            if isDir.boolValue || rel == "AGENTS.md" || rel.hasPrefix(".todo-tracker/trash/") { continue }
            result[rel] = try Data(contentsOf: root.appendingPathComponent(rel))
        }
        return result
    }

    func testABoardBroughtInWritesTheSameFiles() throws {
        let fixture = try Fixtures.json("vault/render.json")
        let tz = Fixtures.zone(fixture["timeZoneOffsetMinutes"] as! Int)
        let now = Fixtures.date(fixture["now"] as! String)
        for c in fixture["cases"] as! [[String: Any]] {
            let name = c["name"] as! String
            let temp = TempDir()
            let legacy = temp.url.appendingPathComponent("board.json")
            try JSONSerialization.data(withJSONObject: c["board"]!).write(to: legacy)
            let vault = temp.url.appendingPathComponent("vault")
            let store = try VaultStore(root: vault, timeZone: tz, clock: { now }, legacyBoard: legacy)
            XCTAssertTrue(FileManager.default.fileExists(atPath: legacy.path + ".migrated"), name)

            let expected = c["files"] as! [String: String]
            let written = try files(in: vault)
            XCTAssertEqual(Set(written.keys.map { $0.precomposedStringWithCanonicalMapping }), Set(expected.keys.map { $0.precomposedStringWithCanonicalMapping }), name)
            for (path, text) in expected {
                let data = written.first { $0.key.oEq(path) }?.value ?? written.first { $0.key.precomposedStringWithCanonicalMapping == path.precomposedStringWithCanonicalMapping }?.value
                XCTAssertEqual(data.map { String(decoding: $0, as: UTF8.self) }, text, "\(name): \(path)")
            }

            let folders = try FileManager.default.contentsOfDirectory(atPath: vault.path).filter { !$0.hasPrefix(".") && !$0.hasPrefix("_") }
            XCTAssertEqual(folders.sorted(), (c["folders"] as! [String]).sorted(), name)

            let parsed = try store.read { try Fixtures.export($0) }
            XCTAssertNil(Fixtures.diff(parsed, Fixtures.normalized(c["parsed"]!)), name)

            let snapshot = try store.readSync()
            let entries = snapshot.entries.values.sorted { Ordinal.less($0.key, $1.key) }
            let sync = c["sync"] as! [[String: String]]
            XCTAssertEqual(entries.count, sync.count, name)
            for (entry, want) in zip(entries, sync) {
                XCTAssertTrue(entry.key.oEq(want["key"]!), "\(name): \(entry.key) vs \(want["key"]!)")
                XCTAssertTrue(entry.path.oEq(want["path"]!), "\(name): \(entry.path) vs \(want["path"]!)")
                XCTAssertEqual(entry.hash, want["hash"], "\(name): \(entry.path)")
                if let content = want["content"] {
                    XCTAssertEqual(String(decoding: try snapshot.read(entry), as: UTF8.self), content)
                }
            }
        }
    }

    func testTheFilesReadBackAsTheBoardAndAreNeverRewritten() throws {
        let fixture = try Fixtures.json("vault/render.json")
        let tz = Fixtures.zone(fixture["timeZoneOffsetMinutes"] as! Int)
        let now = Fixtures.date(fixture["now"] as! String)
        for c in fixture["cases"] as! [[String: Any]] {
            let name = c["name"] as! String
            let temp = TempDir()
            let vault = temp.url.appendingPathComponent("vault")
            for folder in c["folders"] as! [String] {
                try FileManager.default.createDirectory(at: vault.appendingPathComponent(folder), withIntermediateDirectories: true)
            }
            let files = c["files"] as! [String: String]
            for (path, text) in files {
                let url = vault.appendingPathComponent(path)
                try FileManager.default.createDirectory(at: url.deletingLastPathComponent(), withIntermediateDirectories: true)
                try Data(text.utf8).write(to: url)
            }
            let store = try VaultStore(root: vault, timeZone: tz, clock: { now })
            let parsed = try store.read { try Fixtures.export($0) }
            XCTAssertNil(Fixtures.diff(parsed, Fixtures.normalized(c["parsed"]!)), name)
            // Reading wrote nothing.
            for (path, text) in files {
                XCTAssertEqual(try String(contentsOf: vault.appendingPathComponent(path), encoding: .utf8), text, "\(name): \(path)")
            }

            for (path, text) in files where path.hasSuffix(".md") {
                let context = TaskMarkdownContext(timeZone: tz, now: now, fileDirectory: VaultFiles.directory(path), idSeed: path)
                let file = try TaskMarkdown.parse(text, fileStem: VaultFiles.stem(path), context: context)
                XCTAssertFalse(file.needsWrite, "\(name): \(path) would be rewritten")
                XCTAssertTrue(TaskMarkdown.render(file.root, context: context, previous: file).oEq(text), "\(name): \(path)")
            }
        }
    }
}
