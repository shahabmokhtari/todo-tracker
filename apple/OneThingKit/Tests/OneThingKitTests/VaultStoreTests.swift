import Foundation
import XCTest
@testable import OneThingKit

/// The phone's tasks folder: the files are the truth, only touched files are written, nothing is lost.
final class VaultStoreTests: XCTestCase {
    private let tz = TimeZone(secondsFromGMT: -18000)!
    private let now = Fixtures.date("2026-01-05T14:00:00.000Z")

    private func text(_ vault: URL, _ rel: String) -> String? {
        (try? Data(contentsOf: vault.appendingPathComponent(rel))).map { String(decoding: $0, as: UTF8.self) }
    }

    func testANewFolderGetsTheDefaultTabsWithTheSameIdsEverywhere() throws {
        let temp = TempDir()
        let store = try VaultStore(root: temp.url, timeZone: tz, clock: { self.now })
        let groups = try store.read { $0.groups.map { "\($0.name) \(guidD($0.id))" } }
        XCTAssertEqual(groups, ["Work \(guidD(VaultText.stableGuid("group|WORK")))", "Personal \(guidD(VaultText.stableGuid("group|PERSONAL")))"])
        let config = text(temp.url, ".onething/config.json") ?? ""
        XCTAssertTrue(config.hasPrefix("{\n  \"schemaVersion\": 2,\n  \"groups\": [\n    {\n      \"id\": \"\(guidD(VaultText.stableGuid("group|WORK")))\",\n      \"folder\": \"Work\",\n      \"color\": \"#3b82f6\"\n    },"), config)
    }

    func testAChangeWritesOnlyItsTaskAndATitleChangeKeepsTheFileName() throws {
        let temp = TempDir()
        let store = try VaultStore(root: temp.url, timeZone: tz, clock: { self.now })
        let first = try store.update { board in try board.addTask(NewTask("Buy milk"), now: self.now).id }
        let second = try store.update { board in try board.addTask(NewTask("Call mom"), now: self.now).id }
        XCTAssertEqual(store.path(of: first), "Work/Buy milk.md")
        let untouched = try FileManager.default.attributesOfItem(atPath: temp.url.appendingPathComponent("Work/Call mom.md").path)[.modificationDate] as! Date
        Thread.sleep(forTimeInterval: 0.02)

        try store.update { try $0.update(first, title: "Buy oat milk", now: self.now) }
        XCTAssertEqual(store.path(of: first), "Work/Buy milk.md")
        XCTAssertTrue(text(temp.url, "Work/Buy milk.md")!.contains("# Buy oat milk\n"))
        let after = try FileManager.default.attributesOfItem(atPath: temp.url.appendingPathComponent("Work/Call mom.md").path)[.modificationDate] as! Date
        XCTAssertEqual(after, untouched)

        // Moved to another tab: the file moves, keeping its name.
        let personal = try store.read { $0.groups[1].id }
        try store.update { try $0.moveToGroup(second, groupId: personal, now: self.now) }
        XCTAssertEqual(store.path(of: second), "Personal/Call mom.md")
        XCTAssertNil(text(temp.url, "Work/Call mom.md"))

        // Every change says who made it.
        let lines = text(temp.url, ".onething/activity.jsonl")!.split(separator: "\n")
        XCTAssertEqual(lines.count, try store.read { $0.activity.count })
    }

    /// The phone sets a task's icon, color, tags and labels (and renames or removes a label): saved in the files,
    /// read back the same, and kept by later saves (the spec: the phones keep both icon and color).
    func testATasksLookTagsAndLabelsAreSavedAndKept() throws {
        let temp = TempDir()
        let store = try VaultStore(root: temp.url, timeZone: tz, clock: { self.now })
        let id = try store.update { board in try board.addTask(NewTask("Ship it"), now: self.now).id }
        try store.update { board in
            try board.setIcon(id, "🚀", now: self.now)
            try board.setColor(id, "#0EA5E9", now: self.now)
            try board.setTags(id, ["deep work"], now: self.now)
            try board.setLabels(id, ["Urgent", "Q3/Q4"], now: self.now)
        }
        try store.update { try $0.update(id, title: "Ship it now", now: self.now) }
        try store.update { try $0.updateLabel("Q3/Q4", newName: "Later", color: "#10b981", now: self.now) }

        let again = try VaultStore(root: temp.url, timeZone: tz, clock: { self.now })
        let item = try again.read { try $0.get(id) }
        XCTAssertEqual(item.icon, "🚀")
        XCTAssertEqual(item.color, "#0ea5e9")
        XCTAssertEqual(item.tags, ["deep work"])
        XCTAssertEqual(item.labels, ["Urgent", "Later"])
        XCTAssertEqual(try again.read { $0.labels.map { "\($0.name) \($0.color)" } }, ["Urgent #7c3aed", "Later #10b981"])

        try again.update { try $0.deleteLabel("urgent", now: self.now) }
        XCTAssertEqual(try VaultStore(root: temp.url, timeZone: tz, clock: { self.now }).read { try $0.get(id).labels }, ["Later"])
    }

    func testReadingNeverRewritesAHandWrittenFile() throws {
        let temp = TempDir()
        _ = try VaultStore(root: temp.url, timeZone: tz, clock: { self.now })
        let typed = "Shopping list\n\n- [ ] Eggs\n- [ ] Milk\n"
        try Data(typed.utf8).write(to: temp.url.appendingPathComponent("Personal/Groceries.md"))
        let store = try VaultStore(root: temp.url, timeZone: tz, clock: { self.now })
        let steps = try store.read { $0.items.first { $0.title == "Groceries" }!.children.map(\.title) }
        XCTAssertEqual(steps, ["Eggs", "Milk"])
        XCTAssertEqual(text(temp.url, "Personal/Groceries.md"), typed)
    }

    func testAFileThatCantBeReadIsShownAndNeverOverwritten() throws {
        let temp = TempDir()
        let store = try VaultStore(root: temp.url, timeZone: tz, clock: { self.now })
        let broken = "---\ntags: [unclosed\n---\n# Broken\n"
        try Data(broken.utf8).write(to: temp.url.appendingPathComponent("Work/Broken.md"))
        try store.update { _ = try $0.addTask(NewTask("Something else"), now: self.now) }
        XCTAssertEqual(store.problems.map(\.path), ["Work/Broken.md"])
        XCTAssertEqual(text(temp.url, "Work/Broken.md"), broken)
    }

    func testAFileChangedWhileSavingIsReadAgainFirst() throws {
        let temp = TempDir()
        let store = try VaultStore(root: temp.url, timeZone: tz, clock: { self.now })
        let id = try store.update { try $0.addTask(NewTask("Report"), now: self.now).id }
        let url = temp.url.appendingPathComponent("Work/Report.md")
        // Edited elsewhere (another editor, a sync client) between our read and our write.
        let edited = text(temp.url, "Work/Report.md")!.replacingOccurrences(of: "# Report", with: "# Weekly report")
        var once = false
        try store.update { board in
            if !once {
                once = true
                try Data(edited.utf8).write(to: url)
            }
            try board.update(id, details: "Numbers first.", now: self.now)
        }
        let saved = text(temp.url, "Work/Report.md")!
        XCTAssertTrue(saved.contains("# Weekly report\n"), saved)
        XCTAssertTrue(saved.contains("Numbers first."), saved)
    }

    func testADeletedTaskGoesToTheTrash() throws {
        let temp = TempDir()
        let store = try VaultStore(root: temp.url, timeZone: tz, clock: { self.now })
        let id = try store.update { try $0.addTask(NewTask("Old idea"), now: self.now).id }
        try store.update { try $0.delete(id, now: self.now) }
        XCTAssertNil(text(temp.url, "Work/Old idea.md"))
        XCTAssertEqual(try FileManager.default.contentsOfDirectory(atPath: temp.url.appendingPathComponent(".onething/trash").path), ["20260105-140000 Old idea.md"])
    }
}
