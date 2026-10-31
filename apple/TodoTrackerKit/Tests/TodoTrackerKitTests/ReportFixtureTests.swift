import Foundation
import XCTest
@testable import TodoTrackerKit

/// tests/fixtures/reports.json (written by the C# core): the reports for some ranges must have the same numbers here
/// as in TimeReport.cs.
final class ReportFixtureTests: XCTestCase {
    private static let formatter: ISO8601DateFormatter = {
        let f = ISO8601DateFormatter()
        f.formatOptions = [.withInternetDateTime]
        return f
    }()

    private func instant(_ date: Date?) -> Any { date.map { Self.formatter.string(from: $0) } ?? NSNull() }
    private func text(_ id: UUID) -> String { id.uuidString.lowercased() }

    /// The report as the fixture writes it.
    private func json(_ r: TimeReport) -> [String: Any] {
        [
            "trackedSeconds": r.trackedSeconds, "focusSessions": r.focusSessions, "completed": r.completed, "created": r.created,
            "activeDays": r.activeDays, "longestStreak": r.longestStreak,
            "days": r.days.map { ["date": $0.date.description, "trackedSeconds": $0.trackedSeconds, "focusSeconds": $0.focusSeconds, "completed": $0.completed, "created": $0.created] as [String: Any] },
            "groups": r.groups.map { ["groupId": text($0.groupId), "name": $0.name, "color": $0.color ?? NSNull(), "trackedSeconds": $0.trackedSeconds] as [String: Any] },
            "tasks": r.tasks.map { ["id": text($0.id), "title": $0.title, "groupId": text($0.groupId), "done": $0.done, "trackedSeconds": $0.trackedSeconds] as [String: Any] },
            "hourGrid": r.hourGrid,
            "timeline": r.timeline.map { row in
                [
                    "id": text(row.id), "title": row.title, "groupId": text(row.groupId), "createdAt": instant(row.createdAt),
                    "completedAt": instant(row.completedAt), "deadline": instant(row.deadline),
                    "work": row.work.map { ["start": instant($0.start), "end": instant($0.end), "source": $0.source.rawValue] as [String: Any] },
                ] as [String: Any]
            },
        ]
    }

    func testTheReportsHaveTheNumbersOfTheSharedFixture() throws {
        let f = try Fixtures.json("reports.json")
        let board = try BoardCodec.decode(try JSONSerialization.data(withJSONObject: try XCTUnwrap(f["board"])))
        let zone = try XCTUnwrap(TimeZone(identifier: try XCTUnwrap(f["zone"] as? String)))
        let now = Fixtures.date(try XCTUnwrap(f["now"] as? String))
        for c in try XCTUnwrap(f["cases"] as? [[String: Any]]) {
            let from = try XCTUnwrap(LocalDay(c["from"] as? String ?? ""))
            let to = try XCTUnwrap(LocalDay(c["to"] as? String ?? ""))
            let group = (c["group"] as? String).flatMap(UUID.init(uuidString:))
            let report = try TimeReport.build(board, from: from, to: to, timeZone: zone, now: now, groupId: group)
            let expected = try XCTUnwrap(c["expected"])
            let label = "\(c["from"]!)–\(c["to"]!) \(c["group"] ?? "all")"
            let difference = Fixtures.diff(Fixtures.normalized(json(report)), Fixtures.normalized(expected))
            XCTAssertNil(difference, label)
        }
    }

    func testARangeIsAtMostTwoYearsAndEndsAfterItStarts() throws {
        let board = TaskBoard()
        let zone = TimeZone(identifier: "Europe/Amsterdam")!
        XCTAssertThrowsError(try TimeReport.build(board, from: LocalDay("2026-01-02")!, to: LocalDay("2026-01-01")!, timeZone: zone, now: Date()))
        XCTAssertThrowsError(try TimeReport.build(board, from: LocalDay("2024-01-01")!, to: LocalDay("2026-01-03")!, timeZone: zone, now: Date()))
        XCTAssertNoThrow(try TimeReport.build(board, from: LocalDay("2025-01-01")!, to: LocalDay("2026-12-31")!, timeZone: zone, now: Date()))
    }
}
