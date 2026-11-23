import Foundation
import XCTest
@testable import OneThingKit

/// tests/fixtures/labels.json: renaming, recoloring, removing and adding labels, the same in C# (TaskBoard.Organization.cs),
/// Swift and Kotlin.
final class LabelTests: XCTestCase {
    private func fixture() throws -> [String: Any] {
        var root = URL(fileURLWithPath: #filePath)
        for _ in 0..<5 { root.deleteLastPathComponent() }
        let data = try Data(contentsOf: root.appendingPathComponent("tests/fixtures/labels.json"))
        return try XCTUnwrap(JSONSerialization.jsonObject(with: data) as? [String: Any])
    }

    private func snapshot(_ board: TaskBoard) -> String {
        board.labels.map { "\($0.name) \($0.color)" }.joined(separator: ";") + "|" + board.allItems.map { $0.labels.joined(separator: ",") }.joined(separator: ";")
    }

    func testLabelsChangeAsTheSharedFixtureSays() throws {
        let f = try fixture()
        let board = try BoardCodec.decode(try JSONSerialization.data(withJSONObject: try XCTUnwrap(f["board"])))
        let now = Date()
        for step in try XCTUnwrap(f["steps"] as? [[String: Any]]) {
            let label = step["label"] as? String ?? ""
            let color = step["color"] as? String
            let change: () throws -> Void
            switch step["do"] as? String {
            case "update": change = { try board.updateLabel(label, newName: step["name"] as! String, color: color!, now: now) }
            case "define": change = { try board.defineLabel(label, color: color, now: now) }
            case "remove": change = { try board.deleteLabel(label, now: now) }
            case "set": change = { try board.setLabels(UUID(uuidString: step["task"] as! String)!, step["names"] as! [String], now: now) }
            default: return XCTFail("\(step)")
            }
            let before = snapshot(board)
            if step["error"] != nil {
                XCTAssertThrowsError(try change(), "\(step)")
                XCTAssertEqual(snapshot(board), before, "a refused change changes nothing: \(step)")
                continue
            }
            try change()
            let expected = try XCTUnwrap(step["labels"] as? [[String: String]]).map { "\($0["name"]!) \($0["color"]!)" }
            XCTAssertEqual(board.labels.map { "\($0.name) \($0.color)" }, expected, "\(step)")
            for (id, names) in try XCTUnwrap(step["tasks"] as? [String: [String]]) {
                XCTAssertEqual(try board.get(UUID(uuidString: id)!).labels, names, "\(id) after \(step["do"]!)")
            }
        }
    }

    func testTagsAreCleanedAndEachKeptOnce() throws {
        let board = TaskBoard()
        let task = try board.addTask(NewTask("Write"), now: Date())
        try board.setTags(task.id, ["#deep work", "Deep Work", "errands"], now: Date())
        XCTAssertEqual(task.tags, ["deep work", "errands"])
        XCTAssertThrowsError(try board.setTags(task.id, ["123"], now: Date()), "only digits isn't a tag")
        XCTAssertEqual(task.tags, ["deep work", "errands"])
    }
}
