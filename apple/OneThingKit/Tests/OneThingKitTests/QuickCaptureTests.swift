import Foundation
import XCTest
@testable import OneThingKit

/// Quick capture reads the same line the same way as C# QuickCaptureParser (and the other apps): tags, and after @ any
/// time "Pick a time…" understands (C# Snooze.Parse).
final class QuickCaptureTests: XCTestCase {
    private let utc = TimeZone(identifier: "UTC")!
    /// A Wednesday morning.
    private let now = BoardCodec.parseDate("2026-01-07T10:00:00.000Z")!

    private func date(_ text: String) -> Date { BoardCodec.parseDate(text)! }

    func testHashWordsAreTagsAndWordsThatCantBeTagsStayInTheTitle() throws {
        let c = try QuickCaptureParser.parse("Plan the week #work #\"deep work\" #123 ##x #Work", now: now, timeZone: utc)
        XCTAssertEqual(c.title, "Plan the week #123 ##x")
        XCTAssertEqual(c.tags, ["deep work", "work"], "each once, ignoring case; quoted ones first, as C#")
    }

    func testATagOfSeveralWordsCanBeInCurlyQuotes() throws {
        let c = try QuickCaptureParser.parse("#\u{201C}Deep   Work\u{201D} Read the paper", now: now, timeZone: utc)
        XCTAssertEqual(c.title, "Read the paper")
        XCTAssertEqual(c.tags, ["Deep Work"])
        XCTAssertEqual(try QuickCaptureParser.parse("Say #\"1 2\" twice", now: now, timeZone: utc).title, "Say #\"1 2\" twice", "not a tag: kept as typed")
    }

    func testAtTakesWhatPickATimeUnderstands() throws {
        func at(_ text: String, _ zone: TimeZone? = nil) throws -> Date? {
            try QuickCaptureParser.parse("Call mum \(text)", now: now, timeZone: zone ?? utc).nextActionAt
        }
        XCTAssertEqual(try at("@fri"), date("2026-01-09T09:00:00.000Z"))
        XCTAssertEqual(try at("@weekend"), date("2026-01-10T09:00:00.000Z"))
        XCTAssertEqual(try at("@2w"), date("2026-01-21T10:00:00.000Z"), "spans count from now")
        XCTAssertEqual(try at("@next-week"), date("2026-01-12T09:00:00.000Z"), "hyphens for spaces")
        XCTAssertEqual(try at("@14:00"), date("2026-01-07T14:00:00.000Z"))
        XCTAssertEqual(try at("@fri-14:30"), date("2026-01-09T14:30:00.000Z"))
        XCTAssertEqual(try at("@3d"), date("2026-01-10T10:00:00.000Z"))
        XCTAssertEqual(try at("@fri", TimeZone(identifier: "America/New_York")!), date("2026-01-09T14:00:00.000Z"), "9:00 where you are")
        let home = try QuickCaptureParser.parse("Call mum @home", now: now, timeZone: utc)
        XCTAssertEqual(home.title, "Call mum @home")
        XCTAssertNil(home.nextActionAt)
    }

    func testTypedSnoozeRulesReadLikePeopleTalk() {
        XCTAssertEqual(Snooze.parse("tonight at 9", now: now, timeZone: utc), date("2026-01-07T21:00:00.000Z"))
        XCTAssertEqual(Snooze.parse("tomorrow at 3", now: now, timeZone: utc), date("2026-01-08T15:00:00.000Z"))
        XCTAssertEqual(Snooze.parse("in 2 weeks", now: now, timeZone: utc), date("2026-01-21T10:00:00.000Z"))
        XCTAssertEqual(Snooze.parse("next month", now: now, timeZone: utc), date("2026-02-01T09:00:00.000Z"))
        XCTAssertEqual(Snooze.parse("2026-03-01 14:00", now: now, timeZone: utc), date("2026-03-01T14:00:00.000Z"))
        XCTAssertEqual(Snooze.parse("9am", now: now, timeZone: utc), date("2026-01-08T09:00:00.000Z"), "already past today: tomorrow")
        let monday = date("2026-01-05T08:00:00.000Z")
        XCTAssertEqual(Snooze.parse("mon 14:00", now: monday, timeZone: utc), date("2026-01-05T14:00:00.000Z"), "this afternoon")
        XCTAssertEqual(Snooze.parse("mon", now: monday, timeZone: utc), date("2026-01-12T09:00:00.000Z"), "next week's")
        XCTAssertNil(Snooze.parse("3", now: now, timeZone: utc), "a bare number isn't a time")
        XCTAssertNil(Snooze.parse("2026-01-01", now: now, timeZone: utc), "already past")
        XCTAssertEqual(Snooze.hint("2026-01-01", now: now, timeZone: utc), "\"2026-01-01\" is already past. Pick a later time (e.g. tomorrow, or next week).")
        XCTAssertEqual(Snooze.hint("someday", now: now, timeZone: utc), "Couldn't tell when \"someday\" is. Try 3d, 2 weeks, fri 14:00, next week, weekend, tonight, 9am or 2026-03-01.")
    }

    func testCapturedTagsGoOnTheNewTask() throws {
        let board = TaskBoard()
        let c = try QuickCaptureParser.parse("Plan #work #\"deep work\"", now: now, timeZone: utc)
        let item = try board.addTask(NewTask(c.title, tags: c.tags), now: now)
        XCTAssertEqual(item.tags, ["deep work", "work"], "quoted tags are read first, as C#")
        XCTAssertThrowsError(try board.addTask(NewTask("Bad", tags: ["123"]), now: now))
    }

    /// tests/fixtures/quick-capture.json, shared with C# and Kotlin.
    func testQuickCaptureMatchesTheSharedFixture() throws {
        let fixture = try Fixtures.json("quick-capture.json")
        let fixtureNow = try XCTUnwrap(instant(fixture["now"]))
        let zone = try XCTUnwrap((fixture["timeZone"] as? String).flatMap(TimeZone.init(identifier:)))
        let cases = try XCTUnwrap(fixture["cases"] as? [[String: Any]])
        XCTAssertFalse(cases.isEmpty)
        for text in fixture["rejected"] as? [String] ?? [] {
            XCTAssertThrowsError(try QuickCaptureParser.parse(text, now: fixtureNow, timeZone: zone), "rejected: \(text)")
        }
        for c in cases {
            let text = try XCTUnwrap(c["text"] as? String)
            guard let title = c["title"] as? String else {
                XCTAssertThrowsError(try QuickCaptureParser.parse(text, now: fixtureNow, timeZone: zone), text)
                continue
            }
            let parsed = try QuickCaptureParser.parse(text, now: fixtureNow, timeZone: zone)
            XCTAssertEqual(parsed.title, title, text)
            if let tags = c["tags"] as? [String] { XCTAssertEqual(parsed.tags, tags, text) }
            if c.keys.contains("snoozeUntil") { XCTAssertEqual(parsed.nextActionAt, instant(c["snoozeUntil"]), text) }
            if c.keys.contains("deadline") { XCTAssertEqual(parsed.deadline, instant(c["deadline"]), text) }
            if let priority = c["priority"] as? String {
                XCTAssertEqual(parsed.priority.rawValue, priority.lowercased(), text)
            }
        }
    }

    /// An ISO instant (any offset); nil for JSON null.
    private func instant(_ value: Any?) -> Date? {
        guard let text = value as? String else { return nil }
        return VaultText.parseInstant(text) ?? BoardCodec.parseDate(text)
    }
}
