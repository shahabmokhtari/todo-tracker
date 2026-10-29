import Foundation

// Text the way .NET (the C# core) sees it. Swift's `String` compares, hashes and sorts by canonical equivalence
// ("é" NFC == "e\u{301}" NFD) and by grapheme clusters ("\r\n" is one Character); the vault and sync code must
// compare and index exactly like C#: ordinal, by UTF-16 code units. Everything ported uses these helpers.

/// A string compared, hashed and sorted ordinally (by UTF-16 code units), like `StringComparer.Ordinal`.
public struct OrdinalString: Hashable, Comparable, CustomStringConvertible, Sendable {
    public let value: String

    public init(_ value: String) { self.value = value }

    public static func == (a: OrdinalString, b: OrdinalString) -> Bool { a.value.utf16.elementsEqual(b.value.utf16) }

    public func hash(into hasher: inout Hasher) {
        for unit in value.utf16 { hasher.combine(unit) }
        hasher.combine(0xFFFF_FFFF as UInt32)
    }

    public static func < (a: OrdinalString, b: OrdinalString) -> Bool { Ordinal.less(a.value, b.value) }

    public var description: String { value }
}

/// A string compared and hashed like `StringComparer.OrdinalIgnoreCase` (simple upper-case mapping per character).
public struct OrdinalIgnoreCaseString: Hashable, Sendable {
    public let value: String
    private let folded: [UInt16]

    public init(_ value: String) {
        self.value = value
        folded = Array(Ordinal.upper(value).utf16)
    }

    public static func == (a: OrdinalIgnoreCaseString, b: OrdinalIgnoreCaseString) -> Bool { a.folded == b.folded }

    public func hash(into hasher: inout Hasher) { hasher.combine(folded) }
}

typealias OKey = OrdinalString
typealias OIKey = OrdinalIgnoreCaseString

enum Ordinal {
    static func less(_ a: String, _ b: String) -> Bool { compare(a, b) < 0 }

    /// `string.CompareOrdinal`.
    static func compare(_ a: String, _ b: String) -> Int {
        var ia = a.utf16.makeIterator()
        var ib = b.utf16.makeIterator()
        while true {
            switch (ia.next(), ib.next()) {
            case (nil, nil): return 0
            case (nil, _): return -1
            case (_, nil): return 1
            case let (x?, y?): if x != y { return Int(x) - Int(y) }
            }
        }
    }

    static func equal(_ a: String, _ b: String) -> Bool { a.utf16.elementsEqual(b.utf16) }

    static func equalIgnoreCase(_ a: String?, _ b: String?) -> Bool {
        guard let a, let b else { return a == nil && b == nil }
        return upper(a).utf16.elementsEqual(upper(b).utf16)
    }

    /// `StringComparer.OrdinalIgnoreCase` order.
    static func lessIgnoreCase(_ a: String, _ b: String) -> Bool { compare(upper(a), upper(b)) < 0 }

    /// `ToUpperInvariant`: each character's simple (1:1) mapping; `ß` stays `ß`, and .NET leaves the Turkish dotted
    /// and dotless i (`İ`, `ı`) as they are.
    static func upper(_ s: String) -> String { map(s) { scalar in
        if scalar.value == 0x130 || scalar.value == 0x131 { return scalar }
        let full = scalar.properties.uppercaseMapping.unicodeScalars
        return full.count == 1 ? full.first! : scalar
    } }

    /// `ToLowerInvariant`: each character's simple mapping (`İ` and `ı` stay as they are, as in .NET).
    static func lower(_ s: String) -> String { map(s) { scalar in
        if scalar.value == 0x130 || scalar.value == 0x131 { return scalar }
        let full = scalar.properties.lowercaseMapping.unicodeScalars
        return full.count == 1 ? full.first! : scalar
    } }

    private static func map(_ s: String, _ f: (Unicode.Scalar) -> Unicode.Scalar) -> String {
        if s.utf8.allSatisfy({ $0 < 0x80 && !($0 >= 0x41 && $0 <= 0x5A) && !($0 >= 0x61 && $0 <= 0x7A) }) { return s }
        var out = String.UnicodeScalarView()
        for scalar in s.unicodeScalars { out.append(f(scalar)) }
        return String(out)
    }

    /// `char.IsWhiteSpace`.
    static func isWhite(_ unit: UInt16) -> Bool {
        guard let scalar = Unicode.Scalar(unit) else { return false }
        return scalar.properties.isWhitespace
    }

    /// `char.IsLetterOrDigit` on one UTF-16 unit (a surrogate is never a letter).
    static func isLetterOrDigit(_ unit: UInt16) -> Bool {
        guard let scalar = Unicode.Scalar(unit) else { return false }
        switch scalar.properties.generalCategory {
        case .uppercaseLetter, .lowercaseLetter, .titlecaseLetter, .modifierLetter, .otherLetter, .decimalNumber: return true
        default: return false
        }
    }

    /// `char.IsDigit` (a decimal digit in any script).
    static func isDigit(_ unit: UInt16) -> Bool {
        guard let scalar = Unicode.Scalar(unit) else { return false }
        return scalar.properties.generalCategory == .decimalNumber
    }

    static func isControl(_ unit: UInt16) -> Bool { unit < 0x20 || (unit >= 0x7F && unit <= 0x9F) }

    static func isAsciiLetterOrDigit(_ s: String) -> Bool {
        s.utf16.allSatisfy { ($0 >= 0x30 && $0 <= 0x39) || ($0 >= 0x41 && $0 <= 0x5A) || ($0 >= 0x61 && $0 <= 0x7A) }
    }

    static func isLowerHex(_ s: String) -> Bool { s.utf16.allSatisfy { ($0 >= 0x30 && $0 <= 0x39) || ($0 >= 0x61 && $0 <= 0x66) } }
}

extension String {
    /// Length in UTF-16 units (.NET's `Length`).
    var u16: Int { utf16.count }

    /// UTF-16 unit at `i`.
    func unit(_ i: Int) -> UInt16 { utf16[utf16.index(utf16.startIndex, offsetBy: i)] }

    /// `s[from..to]` by UTF-16 offsets.
    func sub(_ from: Int, _ to: Int? = nil) -> String {
        let ns = self as NSString
        let end = to ?? ns.length
        return ns.substring(with: NSRange(location: from, length: max(0, end - from)))
    }

    func oEq(_ other: String) -> Bool { utf16.elementsEqual(other.utf16) }

    func oStarts(_ prefix: String) -> Bool { utf16.starts(with: prefix.utf16) }

    func oEnds(_ suffix: String) -> Bool {
        let a = Array(utf16), b = Array(suffix.utf16)
        return a.count >= b.count && Array(a[(a.count - b.count)...]) == b
    }

    func oEndsIgnoreCase(_ suffix: String) -> Bool { Ordinal.upper(self).oEnds(Ordinal.upper(suffix)) }

    /// Ordinal `IndexOf` (UTF-16 offset).
    func oIndex(_ needle: String, from: Int = 0) -> Int? {
        let ns = self as NSString
        guard from <= ns.length else { return nil }
        let r = ns.range(of: needle, options: .literal, range: NSRange(location: from, length: ns.length - from))
        return r.location == NSNotFound ? nil : r.location
    }

    func oLastIndex(_ needle: String) -> Int? {
        let r = (self as NSString).range(of: needle, options: [.literal, .backwards])
        return r.location == NSNotFound ? nil : r.location
    }

    func oContains(_ needle: String) -> Bool { oIndex(needle) != nil }

    /// Ordinal replace of every occurrence.
    func oRep(_ a: String, _ b: String) -> String { replacingOccurrences(of: a, with: b, options: .literal) }

    /// `Split('\n')` (by UTF-16 unit; empty parts kept).
    func oSplit(_ separator: Character) -> [String] {
        let sep = separator.utf16.first!
        var parts: [String] = []
        var current: [UInt16] = []
        for unit in utf16 {
            if unit == sep {
                parts.append(String(decoding: current, as: UTF16.self))
                current.removeAll(keepingCapacity: true)
            } else {
                current.append(unit)
            }
        }
        parts.append(String(decoding: current, as: UTF16.self))
        return parts
    }

    /// `Trim()`: Unicode white space at both ends.
    func oTrim() -> String { oTrimStart().oTrimEnd() }

    func oTrimStart() -> String {
        let units = Array(utf16)
        var i = 0
        while i < units.count && Ordinal.isWhite(units[i]) { i += 1 }
        return i == 0 ? self : String(decoding: units[i...], as: UTF16.self)
    }

    func oTrimEnd() -> String {
        let units = Array(utf16)
        var j = units.count
        while j > 0 && Ordinal.isWhite(units[j - 1]) { j -= 1 }
        return j == units.count ? self : String(decoding: units[..<j], as: UTF16.self)
    }

    func oTrim(_ chars: String) -> String { oTrimStart(chars).oTrimEnd(chars) }

    func oTrimStart(_ chars: String) -> String {
        let set = Set(chars.utf16), units = Array(utf16)
        var i = 0
        while i < units.count && set.contains(units[i]) { i += 1 }
        return String(decoding: units[i...], as: UTF16.self)
    }

    func oTrimEnd(_ chars: String) -> String {
        let set = Set(chars.utf16), units = Array(utf16)
        var j = units.count
        while j > 0 && set.contains(units[j - 1]) { j -= 1 }
        return String(decoding: units[..<j], as: UTF16.self)
    }

    var oIsBlank: Bool { utf16.allSatisfy(Ordinal.isWhite) }

    func oUpper() -> String { Ordinal.upper(self) }

    func oLower() -> String { Ordinal.lower(self) }

    func oInsert(_ text: String, at index: Int) -> String { sub(0, index) + text + sub(index) }
}

/// A regular expression with .NET's meaning of `\s` and `\w` (ICU's lack U+000B, U+0085 and some marks) and only `\n`
/// as a line break, matching on UTF-16 offsets like .NET.
final class Rx: @unchecked Sendable {
    let re: NSRegularExpression

    init(_ pattern: String, ignoreCase: Bool = false, multiline: Bool = false) {
        var options: NSRegularExpression.Options = [.useUnixLineSeparators]
        if ignoreCase { options.insert(.caseInsensitive) }
        if multiline { options.insert(.anchorsMatchLines) }
        re = try! NSRegularExpression(pattern: Rx.dotnet(pattern), options: options)
    }

    /// Rewrites `\s` and `\w` to .NET's classes.
    static func dotnet(_ pattern: String) -> String {
        let ws = "\\t\\n\\x{0B}\\f\\r\\x{85}\\p{Z}"
        let word = "\\p{L}\\p{Mn}\\p{Nd}\\p{Pc}"
        var out = ""
        var inClass = false
        var chars = Array(pattern.unicodeScalars)[...]
        while let c = chars.popFirst() {
            if c == "\\", let next = chars.first {
                chars.removeFirst()
                if next == "s" { out += inClass ? ws : "[\(ws)]"; continue }
                if next == "w" { out += inClass ? word : "[\(word)]"; continue }
                out.unicodeScalars.append(c)
                out.unicodeScalars.append(next)
                continue
            }
            if c == "[" && !inClass { inClass = true } else if c == "]" && inClass { inClass = false }
            out.unicodeScalars.append(c)
        }
        return out
    }

    func match(_ s: String, from: Int = 0) -> RxMatch? {
        let ns = s as NSString
        guard let r = re.firstMatch(in: s, range: NSRange(location: from, length: ns.length - from)) else { return nil }
        return RxMatch(result: r, ns: ns)
    }

    func isMatch(_ s: String) -> Bool { match(s) != nil }

    func matches(_ s: String) -> [RxMatch] {
        let ns = s as NSString
        return re.matches(in: s, range: NSRange(location: 0, length: ns.length)).map { RxMatch(result: $0, ns: ns) }
    }

    func replace(_ s: String, _ f: (RxMatch) -> String) -> String {
        let found = matches(s)
        if found.isEmpty { return s }
        let ns = s as NSString
        var out = ""
        var last = 0
        for m in found {
            out += ns.substring(with: NSRange(location: last, length: m.index - last))
            out += f(m)
            last = m.index + m.length
        }
        return out + ns.substring(from: last)
    }
}

struct RxMatch {
    let result: NSTextCheckingResult
    let ns: NSString

    var index: Int { result.range.location }
    var length: Int { result.range.length }
    var value: String { ns.substring(with: result.range) }

    func has(_ name: String) -> Bool { result.range(withName: name).location != NSNotFound }
    func group(_ name: String) -> String {
        let r = result.range(withName: name)
        return r.location == NSNotFound ? "" : ns.substring(with: r)
    }
    func range(_ name: String) -> NSRange { result.range(withName: name) }
    func has(_ i: Int) -> Bool { result.range(at: i).location != NSNotFound }
    func group(_ i: Int) -> String {
        let r = result.range(at: i)
        return r.location == NSNotFound ? "" : ns.substring(with: r)
    }
    func range(_ i: Int) -> NSRange { result.range(at: i) }
}
