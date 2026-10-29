import Foundation
import XCTest
@testable import TodoTrackerKit

/// tests/fixtures/sync/textmerge.json and planner.json: the three-way merge and the sync plans, choice for choice like
/// the C# core (both devices of a pair must end up with the same files).
final class SyncFixtureTests: XCTestCase {
    func testTextMerges() throws {
        let fixture = try Fixtures.json("sync/textmerge.json")
        let cases = fixture["cases"] as! [[String: Any]]
        XCTAssertEqual(cases.count, 25)
        for c in cases {
            let policy: ConflictPolicy = ["first": .first, "second": .second][c["policy"] as! String] ?? .both
            let keys = (c["lineKeys"] as? String) == "task" ? SyncPlanner.taskLineKeys : nil
            let result = TextMerge.merge(base: c["base"] as! String, first: c["first"] as! String, second: c["second"] as! String, lineKeys: keys, policy: policy)
            XCTAssertTrue(result.text.oEq(c["text"] as! String), "\(c["name"]!):\n\(result.text)")
            XCTAssertEqual(result.conflicted, c["conflicted"] as! Bool, c["name"] as! String)
        }
        for c in fixture["taskLineKeys"] as! [[String: Any]] {
            let keys = SyncPlanner.taskLineKeys(c["lines"] as! [String])
            XCTAssertEqual(keys, (c["keys"] as! [Any]).map { $0 as? String })
        }
    }

    private func entries(_ list: Any?) -> (SyncEntries, [String: Data]) {
        var result: SyncEntries = [:]
        var contents: [String: Data] = [:]
        for e in list as? [[String: Any]] ?? [] {
            let entry = SyncEntry(key: e["key"] as! String, path: e["path"] as! String, hash: e["hash"] as! String)
            result[OrdinalString(entry.key)] = entry
            if let text = e["text"] as? String {
                XCTAssertEqual(SyncHash.of(text), entry.hash)
                contents[entry.hash] = Data(text.utf8)
            }
        }
        return (result, contents)
    }

    private func side(_ json: Any?) -> SyncSide {
        let o = json as! [String: Any]
        let (list, contents) = entries(o["entries"])
        return SyncSide(device: o["device"] as! String, name: o["name"] as! String, entries: list) { e in
            guard let data = contents[e.hash] else { throw SyncStaleError("\(e.path) is missing.") }
            return data
        }
    }

    func testSyncPlans() throws {
        let fixture = try Fixtures.json("sync/planner.json")
        let cases = fixture["cases"] as! [[String: Any]]
        XCTAssertEqual(cases.count, 44)
        let tz = TimeZone(secondsFromGMT: -18000)!
        for c in cases {
            let name = c["name"] as! String
            let (base, baseContents) = entries(c["base"])
            let deleted = Set((c["deleted"] as? [[String: String]] ?? []).map { "\($0["key"]!)|\($0["hash"]!)" })
            var options = SyncPlanOptions(deleted: { deleted.contains("\($0.key)|\($0.hash)") })
            if c["validateTasks"] as? Bool == true {
                options.isValidTask = { path, text in
                    let context = TaskMarkdownContext(timeZone: tz, now: Date(), fileDirectory: VaultFiles.directory(path), idSeed: path)
                    return (try? TaskMarkdown.parse(text, fileStem: VaultFiles.stem(path), context: context)) != nil
                }
            }
            let plan = try SyncPlanner.plan(local: side(c["local"]), peer: side(c["peer"]), base: base, readBase: { baseContents[$0] }, options: options)
            let expected = c["expected"] as! [String: Any]
            let ops = expected["ops"] as! [[String: Any]]
            XCTAssertEqual(plan.ops.count, ops.count, "\(name): \(plan.ops.map { "\($0.kind) \($0.path)" })")
            for (op, want) in zip(plan.ops, ops) {
                XCTAssertEqual(op.kind.rawValue, want["kind"] as? String, name)
                XCTAssertTrue(op.path.oEq(want["path"] as! String), "\(name): \(op.path) vs \(want["path"]!)")
                XCTAssertEqual(op.expected, want["expected"] as? String, name)
                XCTAssertEqual(op.to, want["to"] as? String, name)
                XCTAssertEqual(op.content.map { SyncPlanner.utf8($0) }, want["text"] as? String, "\(name): \(op.path)")
            }
            let conflicts = expected["conflicts"] as! [[String: Any]]
            XCTAssertEqual(plan.conflicts.count, conflicts.count, name)
            for (conflict, want) in zip(plan.conflicts, conflicts) {
                XCTAssertEqual(conflict.key, want["key"] as? String, name)
                XCTAssertEqual(conflict.path, want["path"] as? String, name)
                XCTAssertEqual(conflict.peer, want["peer"] as? String, name)
                XCTAssertEqual(conflict.peerName, want["peerName"] as? String, name)
                XCTAssertEqual(conflict.mine, want["mine"] as? String, name)
                XCTAssertEqual(conflict.theirs, want["theirs"] as? String, name)
                XCTAssertEqual(conflict.base, want["base"] as? String, name)
                XCTAssertEqual(conflict.result, want["result"] as? String, name)
            }
        }
    }

    func testNextBase() throws {
        let fixture = try Fixtures.json("sync/planner.json")
        let cases = fixture["nextBase"] as! [[String: Any]]
        XCTAssertEqual(cases.count, 5)
        for c in cases {
            let next = SyncPlanner.nextBase(local: entries(c["local"]).0, peer: entries(c["peer"]).0, oldBase: entries(c["base"]).0)
            let want = entries(c["expected"]).0
            XCTAssertEqual(next, want, c["name"] as! String)
        }
    }
}
