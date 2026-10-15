import Foundation

/// Deep links that open the app on a task: `todotracker://task/<id>` (TaskLinks in the C# core, same rules).
public enum TaskLinks {
    public static let scheme = "todotracker"

    public static func forTask(_ id: UUID) -> String { "\(scheme)://task/\(id.uuidString.lowercased())" }

    /// What a link opens: `.task(id)`, `.app` for the bare scheme, or nil when it isn't a link the app follows.
    public enum Target: Equatable {
        case app
        case task(UUID)
    }

    public static func target(_ link: String?) -> Target? {
        guard let text = link?.trimmingCharacters(in: .whitespacesAndNewlines),
              text.lowercased().hasPrefix(scheme + ":") else { return nil }
        var rest = Substring(text.dropFirst(scheme.count + 1))
        while rest.first == "/" { rest = rest.dropFirst() }
        if let end = rest.firstIndex(where: { $0 == "?" || $0 == "#" }) { rest = rest[..<end] }
        while rest.last == "/" { rest = rest.dropLast() }
        let parts = rest.split(separator: "/", omittingEmptySubsequences: false).map(String.init)
        if parts == [""] || parts == ["open"] { return .app }
        guard parts.count == 2, parts[0].lowercased() == "task", let id = uuid(parts[1]) else { return nil }
        return .task(id)
    }

    /// With or without dashes, any case (like .NET's Guid parsing of the forms we hand out).
    private static func uuid(_ text: String) -> UUID? {
        if let id = UUID(uuidString: text) { return id }
        guard text.count == 32, text.allSatisfy(\.isHexDigit) else { return nil }
        let chars = Array(text)
        let dashed = [0..<8, 8..<12, 12..<16, 16..<20, 20..<32].map { String(chars[$0]) }.joined(separator: "-")
        return UUID(uuidString: dashed)
    }
}
