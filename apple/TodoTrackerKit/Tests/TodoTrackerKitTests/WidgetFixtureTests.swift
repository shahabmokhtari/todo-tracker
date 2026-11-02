import Foundation
import XCTest
@testable import TodoTrackerKit

/// tests/fixtures/widget.json (written by the C# core): the snapshot the app writes for the home-screen widgets and what
/// they show at some moments. The same rules as WidgetSnapshot.cs and the Kotlin port.
final class WidgetFixtureTests: XCTestCase {
    private func load() throws -> (fixture: [String: Any], board: TaskBoard, zone: TimeZone, now: Date) {
        let f = try Fixtures.json("widget.json")
        let board = try BoardCodec.decode(try JSONSerialization.data(withJSONObject: try XCTUnwrap(f["board"])))
        let zone = try XCTUnwrap(TimeZone(identifier: try XCTUnwrap(f["zone"] as? String)))
        board.timeZone = zone
        return (f, board, zone, Fixtures.date(try XCTUnwrap(f["now"] as? String)))
    }

    private func ids(_ value: Any?) -> [UUID] { (value as? [String] ?? []).compactMap(UUID.init(uuidString:)) }

    func testTheSnapshotIsWhatTheSharedFixtureSays() throws {
        let (f, board, zone, now) = try load()
        let expected = try XCTUnwrap(WidgetSnapshot.decode(try XCTUnwrap(f["snapshot"] as? [String: Any])))
        let built = WidgetSnapshot.build(board, now: now, timeZone: zone)
        XCTAssertEqual(built.items, expected.items)
        XCTAssertEqual(built.frames.count, expected.frames.count)
        for (a, b) in zip(built.frames, expected.frames) { XCTAssertEqual(a, b, "frame at \(b.at)") }
        XCTAssertEqual(built, expected)
    }

    func testTheSnapshotWritesTheSharedFormat() throws {
        let (f, board, zone, now) = try load()
        let written = try JSONSerialization.jsonObject(with: try WidgetSnapshot.build(board, now: now, timeZone: zone).encoded())
        let expected = try XCTUnwrap(f["snapshot"])
        XCTAssertNil(Fixtures.diff(Fixtures.normalized(written), Fixtures.normalized(expected)))
    }

    func testTheWidgetShowsWhatTheSharedFixtureSays() throws {
        let (f, _, _, _) = try load()
        let snapshot = try XCTUnwrap(WidgetSnapshot.decode(try XCTUnwrap(f["snapshot"] as? [String: Any])))
        for v in try XCTUnwrap(f["views"] as? [[String: Any]]) {
            let at = Fixtures.date(try XCTUnwrap(v["at"] as? String))
            let view = snapshot.view(at: at, hiding: Set(ids(v["hidden"])))
            let label = "\(v["at"]!) hiding \(v["hidden"]!)"
            XCTAssertEqual(view.focus?.id, (v["focus"] as? String).flatMap(UUID.init(uuidString:)), label)
            XCTAssertEqual(view.next.map(\.id), ids(v["next"]), label)
            XCTAssertEqual(view.more, v["more"] as? Int, label)
            XCTAssertEqual(view.today.map(\.id), ids(v["today"]), label)
            let later = try XCTUnwrap(v["later"] as? [[String: Any]])
            XCTAssertEqual(view.later.map(\.item.id), later.compactMap { ($0["id"] as? String).flatMap(UUID.init(uuidString:)) }, label)
            XCTAssertEqual(view.later.map(\.at), later.compactMap { ($0["at"] as? String).map(Fixtures.date) }, label)
            XCTAssertEqual(view.nowCount, v["nowCount"] as? Int, label)
            XCTAssertEqual(view.laterCount, v["laterCount"] as? Int, label)
        }
    }

    func testNothingButTitlesGroupsIdsAndTimesGoesIntoTheSnapshot() throws {
        let (_, board, zone, now) = try load()
        let text = String(decoding: try WidgetSnapshot.build(board, now: now, timeZone: zone).encoded(), as: UTF8.self)
        XCTAssertFalse(text.contains("secret"))
    }

    func testAnEmptyBoardShowsNothingToDo() {
        let view = WidgetSnapshot.build(TaskBoard(), now: Date(), timeZone: .current).view(at: Date())
        XCTAssertNil(view.focus)
        XCTAssertEqual(view.more, 0)
    }
}

final class WidgetShelfTests: XCTestCase {
    func testTheSnapshotIsKeptAndReadBack() throws {
        let dir = TempDir()
        let shelf = WidgetShelf(directory: dir.url.appendingPathComponent("Widgets"))
        let board = TaskBoard()
        _ = try board.addTask(NewTask("Answer mail"), now: Date())
        let snapshot = WidgetSnapshot.build(board, now: Date(timeIntervalSince1970: 1_800_000_000), timeZone: .current)
        try shelf.write(snapshot)
        XCTAssertEqual(shelf.snapshot(), snapshot)
    }

    func testADoneTappedOnAWidgetWaitsForTheAppAndHidesTheTask() throws {
        let dir = TempDir()
        let shelf = WidgetShelf(directory: dir.url)
        let task = UUID()
        let request = try shelf.requestDone(task)
        XCTAssertEqual(shelf.pendingDone(), [request])
        XCTAssertEqual(shelf.hidden(), [task])
        shelf.remove(request)
        XCTAssertEqual(shelf.pendingDone(), [])
    }

    func testARequestIsAppliedOnceEvenToARepeatingTask() throws {
        let board = TaskBoard()
        let start = Date(timeIntervalSince1970: 1_800_000_000)
        let item = try board.addTask(NewTask("Stretch"), now: start)
        try board.update(item.id, TaskChanges(repeatRule: "every day"), now: start)
        let request = WidgetDoneRequest(task: item.id, at: start.addingTimeInterval(60))
        XCTAssertEqual(request.outcome(on: board), .complete)

        try board.complete(item.id, now: start.addingTimeInterval(120))
        XCTAssertFalse(board.find(item.id)!.isDone, "a repeating task moves on to its next time")
        XCTAssertEqual(request.outcome(on: board), .alreadyDone)
    }

    func testARequestForATaskDoneOrGoneDoesNothing() throws {
        let board = TaskBoard()
        let now = Date()
        let item = try board.addTask(NewTask("Answer mail"), now: now)
        try board.complete(item.id, now: now)
        XCTAssertEqual(WidgetDoneRequest(task: item.id, at: now.addingTimeInterval(10)).outcome(on: board), .alreadyDone)
        XCTAssertEqual(WidgetDoneRequest(task: UUID(), at: now).outcome(on: board), .alreadyDone)
    }
}
