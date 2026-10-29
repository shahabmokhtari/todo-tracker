import Foundation
import XCTest
@testable import TodoTrackerKit

/// tests/fixtures/vault/render.json "dormantTabs": a tab with no folder here (its folder is empty on another device,
/// and empty folders don't sync) keeps its place in config.json, so every device writes the same bytes.
final class DormantTabsFixtureTests: XCTestCase {
    func testADormantTabKeepsItsPlaceInTheSettings() throws {
        let fixture = try Fixtures.json("vault/render.json")
        let tz = Fixtures.zone(fixture["timeZoneOffsetMinutes"] as! Int)
        let now = Fixtures.date(fixture["now"] as! String)
        let cases = try XCTUnwrap(fixture["dormantTabs"] as? [[String: Any]])
        XCTAssertGreaterThanOrEqual(cases.count, 5)
        for c in cases {
            let name = c["name"] as! String
            let temp = TempDir()
            let root = temp.url.appendingPathComponent("vault")
            for folder in c["folders"] as! [String] {
                try FileManager.default.createDirectory(at: root.appendingPathComponent(folder), withIntermediateDirectories: true)
            }
            try FileManager.default.createDirectory(at: root.appendingPathComponent(".todo-tracker"), withIntermediateDirectories: true)
            let config = root.appendingPathComponent(".todo-tracker/config.json")
            try Data((c["config"] as! String).utf8).write(to: config)

            let store = try VaultStore(root: root, timeZone: tz, clock: { now })
            _ = try store.read { $0.groups.count }
            if let change = c["change"] as? [String: String] {
                try store.update { board in
                    let group = board.groups.first { $0.name == change["group"] }!
                    if change["kind"] == "rename" {
                        try board.updateGroup(group.id, name: change["to"]!, color: group.color, now: now)
                    } else {
                        try board.deleteGroup(group.id, moveTasksTo: board.groups.first { $0.name == change["moveTo"] }!.id, now: now)
                    }
                }
            }
            XCTAssertEqual(try String(contentsOf: config, encoding: .utf8), c["expected"] as? String, name)
        }
    }
}
