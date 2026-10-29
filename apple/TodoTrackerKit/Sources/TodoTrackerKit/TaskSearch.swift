import Foundation

/// The words of a search box: a task matches when every word is in its title, its details or one of its notes
/// (case doesn't matter). Double quotes keep words together ("ru report"). The words part of TodoTracker.Core.TaskQuery;
/// tags and labels aren't on this board.
public struct TaskSearch: Equatable, Sendable {
    public let words: [String]

    public init(_ text: String?) {
        var words: [String] = []
        var current = ""
        var quoted = false
        for c in text ?? "" {
            if c == "\"" || c == "\u{201C}" || c == "\u{201D}" {
                quoted.toggle()
            } else if c.isWhitespace && !quoted {
                if !current.isEmpty { words.append(current) }
                current = ""
            } else {
                current.append(c)
            }
        }
        let last = current.trimmingCharacters(in: .whitespaces)
        if !last.isEmpty { words.append(last) }
        self.words = words
    }

    public var isEmpty: Bool { words.isEmpty }

    public func matches(_ item: WorkItem) -> Bool {
        words.allSatisfy { word in
            Self.contains(item.title, word) || Self.contains(item.details, word) || item.notes.contains { Self.contains($0.text, word) }
        }
    }

    private static func contains(_ text: String?, _ word: String) -> Bool {
        text?.range(of: word, options: .caseInsensitive) != nil
    }
}
