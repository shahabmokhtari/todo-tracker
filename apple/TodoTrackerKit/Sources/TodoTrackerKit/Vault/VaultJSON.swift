import Foundation

/// A JSON value that keeps object property order and number text, like System.Text.Json's `JsonNode` (config merges
/// keep the winner's property order; numbers are written back as they were read).
public indirect enum JSONValue: Sendable {
    case null
    case bool(Bool)
    /// The number's text as read or written.
    case number(String)
    case string(String)
    /// A string written without escaping (System.Text.Json writes dates and GUIDs as they are: `+00:00`, not `\u002B00:00`).
    case verbatim(String)
    case array([JSONValue])
    case object(JSONObject)

    static func int<T: BinaryInteger>(_ n: T) -> JSONValue { .number(String(n)) }

    var stringValue: String? {
        switch self {
        case .string(let s), .verbatim(let s): return s
        default: return nil
        }
    }

    /// A time as System.Text.Json writes a `DateTimeOffset`.
    static func instant(_ value: Instant) -> JSONValue { .verbatim(value.description) }
    var objectValue: JSONObject? { if case .object(let o) = self { return o }; return nil }
    var arrayValue: [JSONValue]? { if case .array(let a) = self { return a }; return nil }
    var boolValue: Bool? { if case .bool(let b) = self { return b }; return nil }
    var isNull: Bool { if case .null = self { return true }; return false }

    /// An integer (`TryGetValue<long>`: a JSON number without fraction or exponent in range).
    var int64Value: Int64? { if case .number(let s) = self { return Int64(s) }; return nil }

    subscript(key: String) -> JSONValue? { objectValue?[key] }

    /// `JsonNode.DeepEquals`: objects compare by property names and values (in any order), strings ordinally.
    static func deepEquals(_ a: JSONValue?, _ b: JSONValue?) -> Bool {
        let a = a.flatMap { $0.isNull ? nil : $0 }, b = b.flatMap { $0.isNull ? nil : $0 }
        switch (a, b) {
        case (nil, nil): return true
        case (nil, _), (_, nil): return false
        case let (.bool(x)?, .bool(y)?): return x == y
        case (.string?, _), (.verbatim?, _): return a?.stringValue.flatMap { x in b?.stringValue.map { x.oEq($0) } } ?? false
        case let (.number(x)?, .number(y)?): return x == y || (Decimal(string: x).map { d in Decimal(string: y) == d } ?? false)
        case let (.array(x)?, .array(y)?): return x.count == y.count && zip(x, y).allSatisfy { deepEquals($0, $1) }
        case let (.object(x)?, .object(y)?):
            return x.count == y.count && x.pairs.allSatisfy { pair in y.contains(pair.0) && deepEquals(pair.1, y[pair.0]) }
        default: return false
        }
    }
}

/// An object's properties in order (names compared ordinally).
public struct JSONObject: Sendable {
    public var pairs: [(String, JSONValue)] = []

    public init(_ pairs: [(String, JSONValue)] = []) { self.pairs = pairs }

    var count: Int { pairs.count }
    var keys: [String] { pairs.map(\.0) }

    func contains(_ key: String) -> Bool { pairs.contains { $0.0.oEq(key) } }

    subscript(key: String) -> JSONValue? {
        get { pairs.first { $0.0.oEq(key) }?.1 }
        set {
            if let i = pairs.firstIndex(where: { $0.0.oEq(key) }) {
                if let newValue { pairs[i].1 = newValue } else { pairs.remove(at: i) }
            } else if let newValue {
                pairs.append((key, newValue))
            }
        }
    }

    /// Case-insensitive lookup (System.Text.Json's web defaults when reading app files).
    func get(_ key: String) -> JSONValue? { pairs.first { Ordinal.equalIgnoreCase($0.0, key) }?.1 }
}

struct JSONError: Error, CustomStringConvertible {
    let description: String
}

/// System.Text.Json's escaping: `relaxed` is `UnsafeRelaxedJsonEscaping` (hidden `%%{…}%%` data, YAML scalars), `standard`
/// is the default encoder (config.json, state.json, activity lines, the Now order, sync manifests).
public enum JSONEscaping: Sendable {
    case relaxed, standard
}

enum JSONText {
    // ---- Reading --------------------------------------------------------------------

    static func parse(_ data: Data) throws -> JSONValue {
        var bytes = [UInt8](data)
        if bytes.starts(with: [0xEF, 0xBB, 0xBF]) { bytes.removeFirst(3) }
        var p = Parser(bytes: bytes)
        p.skipWhite()
        let value = try p.value(depth: 0)
        p.skipWhite()
        guard p.i == bytes.count else { throw JSONError(description: "Unexpected text after the JSON value.") }
        return value
    }

    static func parse(_ text: String) throws -> JSONValue { try parse(Data(text.utf8)) }

    private struct Parser {
        let bytes: [UInt8]
        var i = 0

        mutating func skipWhite() {
            while i < bytes.count, [0x20, 0x09, 0x0A, 0x0D].contains(bytes[i]) { i += 1 }
        }

        func fail(_ what: String) -> JSONError { JSONError(description: "\(what) at byte \(i).") }

        mutating func value(depth: Int) throws -> JSONValue {
            guard depth < 64 else { throw fail("Too deep") }
            guard i < bytes.count else { throw fail("Unexpected end") }
            switch bytes[i] {
            case UInt8(ascii: "{"):
                i += 1
                var object = JSONObject()
                skipWhite()
                if i < bytes.count && bytes[i] == UInt8(ascii: "}") { i += 1; return .object(object) }
                while true {
                    skipWhite()
                    guard i < bytes.count, bytes[i] == UInt8(ascii: "\"") else { throw fail("Expected a property name") }
                    let key = try string()
                    skipWhite()
                    guard i < bytes.count, bytes[i] == UInt8(ascii: ":") else { throw fail("Expected ':'") }
                    i += 1
                    skipWhite()
                    let v = try value(depth: depth + 1)
                    if object.contains(key) { throw fail("Duplicate property \(key)") }
                    object.pairs.append((key, v))
                    skipWhite()
                    guard i < bytes.count else { throw fail("Unexpected end") }
                    if bytes[i] == UInt8(ascii: ",") { i += 1; continue }
                    if bytes[i] == UInt8(ascii: "}") { i += 1; return .object(object) }
                    throw fail("Expected ',' or '}'")
                }
            case UInt8(ascii: "["):
                i += 1
                var array: [JSONValue] = []
                skipWhite()
                if i < bytes.count && bytes[i] == UInt8(ascii: "]") { i += 1; return .array(array) }
                while true {
                    skipWhite()
                    array.append(try value(depth: depth + 1))
                    skipWhite()
                    guard i < bytes.count else { throw fail("Unexpected end") }
                    if bytes[i] == UInt8(ascii: ",") { i += 1; continue }
                    if bytes[i] == UInt8(ascii: "]") { i += 1; return .array(array) }
                    throw fail("Expected ',' or ']'")
                }
            case UInt8(ascii: "\""):
                return .string(try string())
            case UInt8(ascii: "t"): try literal("true"); return .bool(true)
            case UInt8(ascii: "f"): try literal("false"); return .bool(false)
            case UInt8(ascii: "n"): try literal("null"); return .null
            default:
                return .number(try number())
            }
        }

        mutating func literal(_ word: String) throws {
            let w = Array(word.utf8)
            guard i + w.count <= bytes.count, Array(bytes[i..<(i + w.count)]) == w else { throw fail("Unexpected text") }
            i += w.count
        }

        mutating func number() throws -> String {
            let start = i
            func digits() -> Int { let s = i; while i < bytes.count, bytes[i] >= 0x30, bytes[i] <= 0x39 { i += 1 }; return i - s }
            if i < bytes.count, bytes[i] == UInt8(ascii: "-") { i += 1 }
            guard i < bytes.count else { throw fail("Bad number") }
            if bytes[i] == UInt8(ascii: "0") { i += 1 } else if digits() == 0 { throw fail("Bad number") }
            if i < bytes.count, bytes[i] == UInt8(ascii: ".") { i += 1; if digits() == 0 { throw fail("Bad number") } }
            if i < bytes.count, bytes[i] == UInt8(ascii: "e") || bytes[i] == UInt8(ascii: "E") {
                i += 1
                if i < bytes.count, bytes[i] == UInt8(ascii: "+") || bytes[i] == UInt8(ascii: "-") { i += 1 }
                if digits() == 0 { throw fail("Bad number") }
            }
            return String(decoding: bytes[start..<i], as: UTF8.self)
        }

        mutating func string() throws -> String {
            i += 1
            var units: [UInt16] = []
            var raw: [UInt8] = []
            func flush() { if !raw.isEmpty { units.append(contentsOf: String(decoding: raw, as: UTF8.self).utf16); raw.removeAll() } }
            while true {
                guard i < bytes.count else { throw fail("Unclosed string") }
                let b = bytes[i]
                if b == UInt8(ascii: "\"") { i += 1; break }
                if b < 0x20 { throw fail("Control character in a string") }
                if b == UInt8(ascii: "\\") {
                    flush()
                    i += 1
                    guard i < bytes.count else { throw fail("Unclosed string") }
                    let e = bytes[i]
                    i += 1
                    switch e {
                    case UInt8(ascii: "\""): units.append(0x22)
                    case UInt8(ascii: "\\"): units.append(0x5C)
                    case UInt8(ascii: "/"): units.append(0x2F)
                    case UInt8(ascii: "b"): units.append(0x08)
                    case UInt8(ascii: "f"): units.append(0x0C)
                    case UInt8(ascii: "n"): units.append(0x0A)
                    case UInt8(ascii: "r"): units.append(0x0D)
                    case UInt8(ascii: "t"): units.append(0x09)
                    case UInt8(ascii: "u"):
                        guard i + 4 <= bytes.count, let v = UInt16(String(decoding: bytes[i..<(i + 4)], as: UTF8.self), radix: 16) else { throw fail("Bad escape") }
                        units.append(v)
                        i += 4
                    default: throw fail("Bad escape")
                    }
                    continue
                }
                raw.append(b)
                i += 1
            }
            flush()
            return String(decoding: units, as: UTF16.self)
        }
    }

    // ---- Writing --------------------------------------------------------------------

    static func write(_ value: JSONValue, indented: Bool = false, escaping: JSONEscaping = .standard) -> String {
        var out = ""
        write(value, into: &out, indented: indented, escaping: escaping, depth: 0)
        return out
    }

    private static func write(_ value: JSONValue, into out: inout String, indented: Bool, escaping: JSONEscaping, depth: Int) {
        func newline(_ d: Int) { if indented { out += "\n" + String(repeating: "  ", count: d) } }
        switch value {
        case .null: out += "null"
        case .bool(let b): out += b ? "true" : "false"
        case .number(let n): out += n
        case .string(let s): out += quote(s, escaping)
        case .verbatim(let s): out += "\"" + s + "\""
        case .array(let a):
            if a.isEmpty { out += "[]"; return }
            out += "["
            for (k, v) in a.enumerated() {
                if k > 0 { out += "," }
                newline(depth + 1)
                write(v, into: &out, indented: indented, escaping: escaping, depth: depth + 1)
            }
            newline(depth)
            out += "]"
        case .object(let o):
            if o.pairs.isEmpty { out += "{}"; return }
            out += "{"
            for (k, pair) in o.pairs.enumerated() {
                if k > 0 { out += "," }
                newline(depth + 1)
                out += quote(pair.0, escaping) + (indented ? ": " : ":")
                write(pair.1, into: &out, indented: indented, escaping: escaping, depth: depth + 1)
            }
            newline(depth)
            out += "}"
        }
    }

    /// A JSON string literal, escaped exactly as .NET's encoders do.
    static func quote(_ s: String, _ escaping: JSONEscaping) -> String {
        var out = "\""
        func hex(_ u: UInt16) { out += "\\u" + String(format: "%04X", u) }
        for scalar in s.unicodeScalars {
            let v = scalar.value
            switch v {
            case 0x08: out += "\\b"; continue
            case 0x09: out += "\\t"; continue
            case 0x0A: out += "\\n"; continue
            case 0x0C: out += "\\f"; continue
            case 0x0D: out += "\\r"; continue
            case 0x5C: out += "\\\\"; continue
            case 0x22: if escaping == .relaxed { out += "\\\"" } else { hex(0x22) }; continue
            default: break
            }
            let allowed: Bool
            switch escaping {
            case .standard:
                allowed = v >= 0x20 && v < 0x7F && !"&'+<>`".unicodeScalars.contains(scalar)
            case .relaxed:
                allowed = v >= 0x20 && v < 0x10000 && relaxedAllows(scalar)
            }
            if allowed {
                out.unicodeScalars.append(scalar)
            } else {
                for unit in String(scalar).utf16 { hex(unit) }
            }
        }
        return out + "\""
    }

    /// `UnsafeRelaxedJsonEscaping` writes every defined BMP character as itself except controls, private use,
    /// surrogates, unassigned ones, and separators other than the plain space.
    private static func relaxedAllows(_ scalar: Unicode.Scalar) -> Bool {
        switch scalar.properties.generalCategory {
        case .control, .surrogate, .privateUse, .unassigned, .lineSeparator, .paragraphSeparator: return false
        case .spaceSeparator: return scalar.value == 0x20
        default: return true
        }
    }
}
