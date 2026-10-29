import Foundation
import XCTest
@testable import TodoTrackerKit

/// The shared fixtures in tests/fixtures (written by the C# core's tests; the Swift and Kotlin ports must agree).
enum Fixtures {
    static var root: URL {
        var root = URL(fileURLWithPath: #filePath)
        for _ in 0..<5 { root.deleteLastPathComponent() }
        return root.appendingPathComponent("tests/fixtures")
    }

    static func json(_ name: String) throws -> [String: Any] {
        let data = try Data(contentsOf: root.appendingPathComponent(name))
        return try JSONSerialization.jsonObject(with: data) as! [String: Any]
    }

    /// A fixture's JSON value without UUID case differences (Swift writes UUIDs upper case), for comparing exports.
    static func normalized(_ value: Any) -> Any {
        switch value {
        case let d as [String: Any]: return d.mapValues(normalized)
        case let a as [Any]: return a.map(normalized)
        case let s as String where UUID(uuidString: s) != nil && s.count == 36: return s.lowercased()
        default: return value
        }
    }

    static func export(_ item: WorkItem) throws -> Any {
        let data = try BoardCodec.encoder.encode(ItemDocument(item))
        return normalized(try JSONSerialization.jsonObject(with: data))
    }

    static func export(_ board: TaskBoard) throws -> Any {
        normalized(try JSONSerialization.jsonObject(with: try BoardCodec.encode(board)))
    }

    static func same(_ a: Any, _ b: Any) -> Bool { (normalized(a) as AnyObject).isEqual(normalized(b)) }

    /// The first difference between two JSON values (for a readable failure).
    static func diff(_ a: Any, _ b: Any, _ path: String = "") -> String? {
        switch (a, b) {
        case let (x as [String: Any], y as [String: Any]):
            for key in Set(x.keys).union(y.keys).sorted() {
                guard let xv = x[key] else { return "\(path)/\(key): missing on the left, right has \(y[key]!)" }
                guard let yv = y[key] else { return "\(path)/\(key): missing on the right, left has \(xv)" }
                if let d = diff(xv, yv, "\(path)/\(key)") { return d }
            }
            return nil
        case let (x as [Any], y as [Any]):
            if x.count != y.count { return "\(path): \(x.count) vs \(y.count) items" }
            for (i, pair) in zip(x, y).enumerated() { if let d = diff(pair.0, pair.1, "\(path)[\(i)]") { return d } }
            return nil
        default:
            return (a as AnyObject).isEqual(b) ? nil : "\(path): \(a) vs \(b)"
        }
    }

    static func zone(_ minutes: Int) -> TimeZone { TimeZone(secondsFromGMT: minutes * 60)! }

    static func date(_ text: String) -> Date { VaultText.parseInstant(text)! }
}

/// A temporary folder removed after the test.
final class TempDir {
    let url: URL

    init() {
        url = FileManager.default.temporaryDirectory.appendingPathComponent("tt-swift-" + UUID().uuidString)
        try! FileManager.default.createDirectory(at: url, withIntermediateDirectories: true)
    }

    deinit { try? FileManager.default.removeItem(at: url) }
}
