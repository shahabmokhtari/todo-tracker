import Foundation

/// The search box: the same syntax and rules as TodoTracker.Core.TaskQuery (tests/fixtures/search.json). Words match
/// the title or details (any case); double quotes keep words together ("call the"); group:name, is:open / is:done and
/// is:archived (archived tasks are found only then), #tag (nested tags too: #finance finds finance/q3), label:name and
/// the board column (is:inbox / is:next / is:doing, or stage:doing) narrow it down; tags and labels count for subtasks
/// too. Every term must match.
public struct TaskSearch: Equatable, Sendable {
    public private(set) var words: [String] = []
    public private(set) var groups: [String] = []
    /// is:done (true) or is:open (false); nil: either.
    public private(set) var done: Bool?
    public private(set) var archived = false
    public private(set) var tags: [String] = []
    public private(set) var labels: [String] = []
    /// The board column of the task's top-level task (an open one).
    public private(set) var stage: Stage?

    public init(_ text: String?) {
        for token in Self.tokenize(text ?? "") {
            let lower = token.lowercased()
            if token.hasPrefix("#") && token.count > 1 {
                tags.append(String(token.dropFirst()))
            } else if let value = Self.value(token, "label:") {
                labels.append(value)
            } else if let value = Self.value(token, "group:") {
                groups.append(value)
            } else if lower == "is:open" || lower == "is:done" {
                done = lower == "is:done"
            } else if lower == "is:archived" {
                archived = true
            } else if let value = Self.value(token, "is:") ?? Self.value(token, "stage:"), let parsed = Stage(rawValue: value.lowercased()) {
                stage = parsed
            } else {
                words.append(token)
            }
        }
    }

    public var isEmpty: Bool { words.isEmpty && tags.isEmpty && labels.isEmpty && groups.isEmpty && done == nil && !archived && stage == nil }

    public func matches(_ item: WorkItem, in board: TaskBoard) -> Bool {
        if let done, item.isDone != done { return false }
        // Archived tasks are put away: found only when asked for (and then only they are).
        if item.isArchived != archived { return false }
        if let stage, item.root.stage != stage || item.root.isDone { return false }
        let lineage = [item] + item.ancestors
        guard tags.allSatisfy({ t in lineage.contains { $0.tags.contains { Self.isTagMatch($0, t) } } }),
              labels.allSatisfy({ l in lineage.contains { $0.labels.contains { $0.caseInsensitiveCompare(l) == .orderedSame } } }) else { return false }
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

    /// The tag, or a tag nested under it (#finance finds finance/q3, not financial).
    private static func isTagMatch(_ tag: String, _ query: String) -> Bool {
        tag.caseInsensitiveCompare(query) == .orderedSame || tag.lowercased().hasPrefix(query.lowercased() + "/")
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
