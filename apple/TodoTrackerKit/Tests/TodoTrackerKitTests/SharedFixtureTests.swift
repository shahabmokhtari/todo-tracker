import XCTest
@testable import TodoTrackerKit

/// tests/fixtures/undo.json and search.json: the same cases the C# core (UndoFixtureTests, SearchFixtureTests) and the
/// Kotlin core (FixtureTest) run, so undo and search can't drift between the apps.
final class SharedFixtureTests: XCTestCase {
    private func fixture(_ name: String) throws -> [String: Any] {
        var root = URL(fileURLWithPath: #filePath)
        for _ in 0..<5 { root.deleteLastPathComponent() }
        let data = try Data(contentsOf: root.appendingPathComponent("tests/fixtures/\(name)"))
        return try XCTUnwrap(JSONSerialization.jsonObject(with: data) as? [String: Any])
    }

    private func date(_ value: Any?) -> Date? { (value as? String).flatMap(BoardCodec.parseDate) }

    func testUndoingARepeatingTaskPutsBackWhatMovingOnUndid() throws {
        for case let test as [String: Any] in try fixture("undo.json")["cases"] as? [Any] ?? [] {
            let name = test["name"] as? String ?? "?"
            let board = TaskBoard()
            board.timeZone = try XCTUnwrap(TimeZone(identifier: test["zone"] as? String ?? "UTC"), name)
            var tasks: [String: WorkItem] = [:]
            var reminders: [String: Reminder] = [:]
            for case let step as [String: Any] in test["steps"] as? [Any] ?? [] {
                let at = try XCTUnwrap(date(step["at"]), name)
                let task = (step["task"] as? String).flatMap { tasks[$0] }
                switch step["do"] as? String {
                case "add":
                    let item = try board.addTask(NewTask(step["title"] as! String, parentId: (step["parent"] as? String).flatMap { tasks[$0]?.id },
                                                         deadline: date(step["due"]), sequential: step["inOrder"] as? Bool ?? false), now: at)
                    if let rule = step["repeat"] as? String { try board.update(item.id, TaskChanges(repeatRule: rule), now: at) }
                    tasks[step["key"] as! String] = item
                case "complete": try board.complete(XCTUnwrap(task, name).id, now: at)
                case "reopen": try board.reopen(XCTUnwrap(task, name).id, now: at)
                case "archive": try board.archive(XCTUnwrap(task, name).id, now: at)
                case "remind":
                    reminders[step["key"] as! String] = try board.addReminder(XCTUnwrap(task, name).id, dueAt: XCTUnwrap(date(step["due"])), message: step["message"] as? String, now: at)
                case "check":
                    check(step, at: at, board: board, tasks: tasks, reminders: reminders, name: name)
                default:
                    XCTFail("\(name): unknown step \(step)")
                }
            }
        }
    }

    private func check(_ step: [String: Any], at: Date, board: TaskBoard, tasks: [String: WorkItem], reminders: [String: Reminder], name: String) {
        for (key, value) in step["tasks"] as? [String: [String: Any]] ?? [:] {
            guard let item = tasks[key] else { XCTFail("\(name): no task \(key)"); continue }
            let what = "\(name): \(key)"
            if let done = value["done"] as? Bool { XCTAssertEqual(item.isDone, done, "\(what) done") }
            if value.keys.contains("doneAt") { XCTAssertEqual(item.completedAt, date(value["doneAt"]), "\(what) doneAt") }
            if value.keys.contains("due") { XCTAssertEqual(item.deadline, date(value["due"]), "\(what) due") }
            if value.keys.contains("scheduled") { XCTAssertEqual(item.nextActionAt, date(value["scheduled"]), "\(what) scheduled") }
            if value.keys.contains("rule") { XCTAssertEqual(item.repeatRule?.description, value["rule"] as? String, "\(what) rule") }
            if let archived = value["archived"] as? Bool { XCTAssertEqual(item.archivedAt != nil, archived, "\(what) archived") }
        }
        for (key, value) in step["reminders"] as? [String: [String: Any]] ?? [:] {
            XCTAssertEqual(reminders[key]?.isPending, value["pending"] as? Bool, "\(name): reminder \(key) pending")
        }
        if let expected = step["doNow"] as? [String] {
            let keyOf = Dictionary(uniqueKeysWithValues: tasks.map { ($0.value.id, $0.key) })
            XCTAssertEqual(Agenda.build(board, now: at).now.map { keyOf[$0.item.id] ?? "?" }, expected, "\(name): Do now")
        }
    }

    /// Every query (tags, labels and board columns included) finds exactly what C#'s TaskQuery finds.

    func testSearchFindsWhatTheOtherAppsFind() throws {
        let json = try fixture("search.json")
        let board = TaskBoard()
        XCTAssertEqual(board.groups.map(\.name), json["groups"] as? [String])
        let now = BoardCodec.parseDate("2026-01-05T10:00:00.000Z")!
        var tasks: [String: WorkItem] = [:]
        let specs = json["tasks"] as? [[String: Any]] ?? []
        for spec in specs {
            let group = (spec["group"] as? String).flatMap { name in board.groups.first { $0.name == name }?.id }
            tasks[spec["key"] as! String] = try board.addTask(NewTask(spec["title"] as! String, parentId: (spec["parent"] as? String).flatMap { tasks[$0]?.id },
                                                                       groupId: group, details: spec["details"] as? String), now: now)
        }
        for spec in specs {
            let item = tasks[spec["key"] as! String]!
            item.tags = spec["tags"] as? [String] ?? []
            item.labels = spec["labels"] as? [String] ?? []
            item.stage = (spec["stage"] as? String).flatMap(Stage.init(rawValue:)) ?? .next
        }
        for spec in specs where spec["done"] as? Bool == true { try board.complete(tasks[spec["key"] as! String]!.id, now: now) }
        for spec in specs where spec["archived"] as? Bool == true { try board.archive(tasks[spec["key"] as! String]!.id, now: now) }
        let keyOf = Dictionary(uniqueKeysWithValues: tasks.map { ($0.value.id, $0.key) })

        let queries = json["queries"] as? [[String: Any]] ?? []
        XCTAssertEqual(queries.count, 25)
        for query in queries {
            let q = query["q"] as! String
            XCTAssertEqual(TaskSearch(q).apply(board).map { keyOf[$0.id] ?? "?" }, query["expect"] as? [String], "search \(q)")
        }
    }
}
