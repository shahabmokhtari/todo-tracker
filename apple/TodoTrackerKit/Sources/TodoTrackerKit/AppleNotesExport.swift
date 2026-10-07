import Foundation

/// A group's tasks as an Apple Notes note (Mac). Notes has no way for other apps to add real checklists, so tasks
/// are ☐/☑ lines, nested like the outline. The note is written with `osascript`: the title and the note go in as
/// arguments, never into the script's text.
public enum AppleNotesExport {
    public static let folder = "Todo Tracker"

    /// The script osascript runs: makes the folder if needed, then updates the note with this title (or adds it).
    public static let scriptLines = [
        "on run argv",
        "tell application \"Notes\"",
        "if not (exists folder \"\(folder)\") then make new folder with properties {name:\"\(folder)\"}",
        "set theFolder to folder \"\(folder)\"",
        "set found to (notes of theFolder whose name is (item 1 of argv))",
        "if (count of found) > 0 then",
        "set body of (item 1 of found) to (item 2 of argv)",
        "else",
        "make new note at theFolder with properties {name:(item 1 of argv), body:(item 2 of argv)}",
        "end if",
        "end tell",
        "end run",
    ]

    /// osascript's arguments: the script, then the title and the note as plain values.
    public static func arguments(title: String, body: String) -> [String] {
        scriptLines.flatMap { ["-e", $0] } + [title, body]
    }

    public static func html(_ roots: [WorkItem], title: String, includeDone: Bool = false) -> String {
        "<h1>\(escape(title))</h1>" + list(roots, includeDone: includeDone)
    }

    private static func list(_ items: [WorkItem], includeDone: Bool) -> String {
        let shown = items.filter { includeDone || !$0.isDone }
        guard !shown.isEmpty else { return "" }
        return "<ul>" + shown.map { item in
            var line = "<li>\(item.isDone ? "☑" : "☐") \(escape(item.title))"
            if let deadline = item.deadline, !item.isDone {
                line += " <i>(due \(day(deadline)))</i>"
            }

            return line + list(item.children, includeDone: includeDone) + "</li>"
        }.joined() + "</ul>"
    }

    private static func day(_ date: Date) -> String {
        let c = Calendar.current.dateComponents([.year, .month, .day], from: date)
        return String(format: "%04d-%02d-%02d", c.year ?? 0, c.month ?? 0, c.day ?? 0)
    }

    static func escape(_ text: String) -> String {
        text.replacingOccurrences(of: "&", with: "&amp;")
            .replacingOccurrences(of: "<", with: "&lt;")
            .replacingOccurrences(of: ">", with: "&gt;")
            .replacingOccurrences(of: "\"", with: "&quot;")
    }
}
