import XCTest
@testable import TodoTrackerKit

final class AppleNotesExportTests: XCTestCase {
    func testAGroupBecomesAnEscapedNestedChecklistNote() throws {
        let board = TaskBoard()
        let now = Date()
        let due = Calendar.current.date(from: DateComponents(year: 2026, month: 1, day: 8, hour: 17))!
        let parent = try board.addTask(NewTask("Ship <release> & \"notes\"", deadline: due), now: now)
        _ = try board.addTask(NewTask("Write notes", parentId: parent.id), now: now)
        let done = try board.addTask(NewTask("Old step", parentId: parent.id), now: now)
        try board.complete(done.id, now: now)

        let html = AppleNotesExport.html(board.items, title: "Work")

        XCTAssertEqual(html, "<h1>Work</h1><ul><li>☐ Ship &lt;release&gt; &amp; &quot;notes&quot; <i>(due 2026-01-08)</i><ul><li>☐ Write notes</li></ul></li></ul>")
        XCTAssertTrue(AppleNotesExport.html(board.items, title: "Work", includeDone: true).contains("<li>☑ Old step</li>"))
    }

    func testTheScriptNeverContainsTaskText() {
        // Titles and the note body are passed to osascript as arguments, never written into the script.
        let script = AppleNotesExport.scriptLines.joined(separator: "\n")
        XCTAssertTrue(script.contains("item 1 of argv"))
        XCTAssertTrue(script.contains("item 2 of argv"))
        XCTAssertEqual(AppleNotesExport.arguments(title: "x\" & do shell script \"rm", body: "b").suffix(2), ["x\" & do shell script \"rm", "b"])
    }
}
