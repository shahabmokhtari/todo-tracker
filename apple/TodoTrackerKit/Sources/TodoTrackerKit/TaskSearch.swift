import Foundation

/// The search box: the same syntax and rules as TodoTracker.Core.TaskQuery (tests/fixtures/search.json). Words match
/// the title or details (any case); double quotes keep words together ("call the"); group:name, is:open / is:done and
/// is:archived (archived tasks are found only then) narrow it down; every term must match. This board has no tags,
/// labels or board columns, so #tag, label:name, stage:x and is:inbox / is:next / is:doing find nothing here.
public struct TaskSearch: Equatable, Sendable {
    public private(set) var words: [String] = []
    public private(set) var groups: [String] = []
    /// is:done (true) or is:open (false); nil: either.
    public private(set) var done: Bool?
    public private(set) var archived = false
    /// A tag, label or board-column term: nothing on this board has one.
    public private(set) var unmatchable = false

    public init(_ text: String?) {
        for token in Self.tokenize(text ?? "") {
            let lower = token.lowercased()
            if (token.hasPrefix("#") && token.count > 1) || Self.value(token, "label:") != nil {
                unmatchable = true
            } else if let value = Self.value(token, "group:") {
                groups.append(value)
            } else if lower == "is:open" || lower == "is:done" {
                done = lower == "is:done"
            } else if lower == "is:archived" {
                archived = true
            } else if ["is:inbox", "is:next", "is:doing", "stage:inbox", "stage:next", "stage:doing"].contains(lower) {
                unmatchable = true
            } else {
                words.append(token)
            }
        }
    }

    public var isEmpty: Bool { words.isEmpty && groups.isEmpty && done == nil && !archived && !unmatchable }

    public func matches(_ item: WorkItem, in board: TaskBoard) -> Bool {
        if unmatchable { return false }
        if let done, item.isDone != done { return false }
        // Archived tasks are put away: found only when asked for (and then only they are).
        if item.isArchived != archived { return false }
        let groupId = item.groupId
        guard groups.allSatisfy({ name in
            board.groups.contains { $0.id == groupId && ($0.name.caseInsensitiveCompare(name) == .orderedSame || $0.id.uuidString.lowercased() == name.lowercased()) }
        }) else { return false }
        return words.allSatisfy { Self.contains(item.title, $0) || Self.contains(item.details, $0) }
    }

    /// The tasks found, in tree order (a task, then its subtasks).
    public func apply(_ board: TaskBoard) -> [WorkItem] {
        board.allItems.filter { matches($0, in: board) }
    }

    private static func contains(_ text: String?, _ word: String) -> Bool {
        text?.range(of: word, options: .caseInsensitive) != nil
    }

    private static func value(_ token: String, _ prefix: String) -> String? {
        guard token.count > prefix.count, token.lowercased().hasPrefix(prefix) else { return nil }
        return String(token.dropFirst(prefix.count))
    }

    /// Splits on whitespace; double quotes (straight or curly) group words and are dropped, also inside label:"a b".
    private static func tokenize(_ text: String) -> [String] {
        var tokens: [String] = []
        var current = ""
        var quoted = false
        for c in text {
            if c == "\"" || c == "\u{201C}" || c == "\u{201D}" {
                quoted.toggle()
            } else if c.isWhitespace && !quoted {
                if !current.isEmpty { tokens.append(current) }
                current = ""
            } else {
                current.append(c)
            }
        }
        if !current.isEmpty { tokens.append(current) }
        return tokens
    }
}
