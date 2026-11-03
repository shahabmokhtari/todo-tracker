import CryptoKit
import Foundation

/// A task file that can't be understood safely; the store keeps the last good version and never writes it.
public struct VaultFormatError: Error, LocalizedError, Equatable {
    public let message: String
    public init(_ message: String) { self.message = message }
    public var errorDescription: String? { message }
}

/// How dates are shown (local time zone), what "now" is (for timestamps the file doesn't have yet), the file's folder
/// relative to the vault root (to resolve relative attachment links), and an optional seed (the file's path) that
/// makes ids for hand-written lines stable across reads. Same as C#'s `TaskMarkdownContext`.
public struct TaskMarkdownContext {
    public var timeZone: TimeZone
    public var now: Date
    public var fileDirectory: String
    public var idSeed: String?

    public init(timeZone: TimeZone, now: Date, fileDirectory: String, idSeed: String? = nil) {
        self.timeZone = timeZone
        self.now = now
        self.fileDirectory = fileDirectory
        self.idSeed = idSeed
    }
}

/// A local calendar date and time (no zone), to the millisecond.
struct LocalDateTime: Equatable {
    var year, month, day, hour, minute, second, millisecond: Int

    var date: LocalDay { LocalDay(year: year, month: month, day: day) }

    /// Milliseconds since 1970-01-01T00:00 of this wall-clock time read as if it were UTC.
    var wallMillis: Int64 {
        Int64(date.epochDay) * 86_400_000 + Int64(hour * 3_600_000 + minute * 60_000 + second * 1000 + millisecond)
    }

    init(year: Int, month: Int, day: Int, hour: Int = 0, minute: Int = 0, second: Int = 0, millisecond: Int = 0) {
        (self.year, self.month, self.day, self.hour, self.minute, self.second, self.millisecond) = (year, month, day, hour, minute, second, millisecond)
    }

    init(wallMillis: Int64) {
        let days = Int(floorDiv(wallMillis, 86_400_000))
        var rest = Int(wallMillis - Int64(days) * 86_400_000)
        let d = LocalDay(epochDay: days)
        let h = rest / 3_600_000; rest %= 3_600_000
        let m = rest / 60_000; rest %= 60_000
        self.init(year: d.year, month: d.month, day: d.day, hour: h, minute: m, second: rest / 1000, millisecond: rest % 1000)
    }
}

func floorDiv(_ a: Int64, _ b: Int64) -> Int64 { a >= 0 ? a / b : -((-a + b - 1) / b) }

/// Shared formatting for the markdown vault (dates, durations, YAML scalars, hidden JSON). Port of C#'s `VaultText`.
enum VaultText {
    static let defaultDueTime = "17:00"
    static let defaultScheduledTime = "09:00"
    static let defaultDoneTime = "12:00"

    // ---- Instants ------------------------------------------------------------------

    /// Milliseconds since 1970 (truncated, like .NET's "fff").
    static func millis(_ date: Date) -> Int64 {
        let tenths = (date.timeIntervalSince1970 * 10_000).rounded()
        return floorDiv(Int64(tenths), 10)
    }

    static func date(millis: Int64) -> Date { Date(timeIntervalSince1970: Double(millis) / 1000) }

    private static func two(_ n: Int) -> String { n < 10 ? "0\(n)" : "\(n)" }
    private static func four(_ n: Int) -> String { String(format: "%04d", n) }
    private static func three(_ n: Int) -> String { String(format: "%03d", n) }

    /// `2026-01-05T09:00:00.000Z`.
    static func utc(_ date: Date) -> String {
        let t = LocalDateTime(wallMillis: millis(date))
        return "\(four(t.year))-\(two(t.month))-\(two(t.day))T\(two(t.hour)):\(two(t.minute)):\(two(t.second)).\(three(t.millisecond))Z"
    }

    static func toLocal(_ date: Date, _ tz: TimeZone) -> LocalDateTime {
        let offset = tz.secondsFromGMT(for: date)
        return LocalDateTime(wallMillis: millis(date) + Int64(offset) * 1000)
    }

    /// Local date-time the way Obsidian's date & time property writes it (seconds only when needed).
    static func localDateTime(_ date: Date, _ tz: TimeZone) -> String {
        let t = toLocal(date, tz)
        let base = "\(four(t.year))-\(two(t.month))-\(two(t.day))T\(two(t.hour)):\(two(t.minute))"
        return t.second == 0 && t.millisecond == 0 ? base : base + ":\(two(t.second))"
    }

    static func localDate(_ date: Date, _ tz: TimeZone) -> String {
        let t = toLocal(date, tz)
        return "\(four(t.year))-\(two(t.month))-\(two(t.day))"
    }

    static func localMinute(_ date: Date, _ tz: TimeZone) -> String {
        let t = toLocal(date, tz)
        return "\(four(t.year))-\(two(t.month))-\(two(t.day)) \(two(t.hour)):\(two(t.minute))"
    }

    static func localDay(_ date: Date, _ tz: TimeZone) -> LocalDay { toLocal(date, tz).date }

    /// The instant of a local wall-clock time, with .NET's `TimeZoneInfo.GetUtcOffset` rule: a time in the hour
    /// skipped by daylight saving, or in the repeated hour, gets the zone's standard offset.
    static func atLocal(_ local: LocalDateTime, _ tz: TimeZone) -> Date {
        let wall = local.wallMillis
        let guess = date(millis: wall)
        var candidates: [Int] = []
        for shift in [-86_400.0, 0, 86_400] {
            let o = tz.secondsFromGMT(for: guess.addingTimeInterval(shift))
            if !candidates.contains(o) { candidates.append(o) }
        }
        let valid = candidates.filter { tz.secondsFromGMT(for: date(millis: wall - Int64($0) * 1000)) == $0 }
        let offset: Int
        if valid.count == 1 {
            offset = valid[0]
        } else {
            // Standard time: the smallest offset in use around then minus its daylight-saving part.
            let at = date(millis: wall - Int64(candidates.min()!) * 1000)
            offset = tz.secondsFromGMT(for: at) - Int(tz.daylightSavingTimeOffset(for: at))
        }
        return date(millis: wall - Int64(offset) * 1000)
    }

    static func atLocal(_ day: LocalDay, _ time: String, _ tz: TimeZone) -> Date {
        let parts = time.oSplit(":").compactMap { Int($0) }
        return atLocal(LocalDateTime(year: day.year, month: day.month, day: day.day, hour: parts[0], minute: parts[1]), tz)
    }

    /// `yyyy-MM-dd` exactly (after trimming).
    static func parseDate(_ text: String?) -> LocalDay? {
        guard let t = text?.oTrim(), t.u16 == 10, t.unit(4) == 0x2D, t.unit(7) == 0x2D,
              let y = digits(t.sub(0, 4)), let m = digits(t.sub(5, 7)), let d = digits(t.sub(8, 10)) else { return nil }
        guard (1...12).contains(m), d >= 1, d <= LocalDay.length(y, m), y >= 1 else { return nil }
        return LocalDay(year: y, month: m, day: d)
    }

    private static func digits(_ s: String) -> Int? {
        guard !s.isEmpty, s.utf16.allSatisfy({ $0 >= 0x30 && $0 <= 0x39 }) else { return nil }
        return Int(s)
    }

    private static let hasZone = Rx(#"(Z|[+-]\d{2}:?\d{2})$"#, ignoreCase: true)
    private static let localFormats = Rx(#"^(\d{4})-(\d{2})-(\d{2})(?:T(\d{2}):(\d{2})(?::(\d{2})(?:\.(\d{3}))?)?| (\d{2}):(\d{2})(?::(\d{2}))?)$"#)

    /// ISO instant (with Z/offset), local date-time, or a date (at `defaultTime`).
    static func parseTime(_ text: String?, _ tz: TimeZone, _ defaultTime: String) throws -> Date? {
        guard let text else { return nil }
        let value = text.oTrim().oTrim("\"'")
        if value.isEmpty { return nil }
        if hasZone.isMatch(value), let exact = parseInstant(value) { return exact }
        if let day = parseDate(value) { return atLocal(day, defaultTime, tz) }
        if let m = localFormats.match(value) {
            let y = Int(m.group(1))!, mo = Int(m.group(2))!, d = Int(m.group(3))!
            let h = Int(m.has(4) ? m.group(4) : m.group(8))!, mi = Int(m.has(5) ? m.group(5) : m.group(9))!
            let s = m.has(6) ? Int(m.group(6))! : m.has(10) ? Int(m.group(10))! : 0
            let ms = m.has(7) ? Int(m.group(7))! : 0
            if (1...12).contains(mo), d >= 1, d <= LocalDay.length(y, mo), h < 24, mi < 60, s < 60 {
                return atLocal(LocalDateTime(year: y, month: mo, day: d, hour: h, minute: mi, second: s, millisecond: ms), tz)
            }
        }
        throw VaultFormatError("\"\(text)\" isn't a date (use 2026-01-31, 2026-01-31T17:00, or an ISO time).")
    }

    /// Exact instants written by the app (hidden JSON); nil when missing or unreadable.
    static func parseInstant(_ text: String?) -> Date? {
        guard let text, !text.oIsBlank, let ticks = Instant.parseTicks(text.oTrim()) else { return nil }
        return Instant(utcTicks: ticks).date
    }

    // ---- Durations -----------------------------------------------------------------

    static func duration(_ seconds: TimeInterval) -> String {
        let minutes = Int64((seconds / 60).rounded(.toNearestOrEven))
        return minutes % 1440 == 0 ? "\(minutes / 1440)d" : minutes % 60 == 0 ? "\(minutes / 60)h" : "\(minutes)m"
    }

    private static let durationPart = Rx(#"(\d+)\s*([dhm])"#, ignoreCase: true)

    static func parseDuration(_ text: String?) throws -> TimeInterval? {
        guard let value = text?.oTrim(), !value.isEmpty else { return nil }
        if let plain = parseLong(value) { return TimeInterval(plain * 60) }
        let parts = durationPart.matches(value)
        if parts.isEmpty || parts.map({ $0.value.oRep(" ", "") }).joined().u16 != value.oRep(" ", "").u16 {
            throw VaultFormatError("\"\(text!)\" isn't a duration (use 90m, 24h, or 1d).")
        }
        var total: TimeInterval = 0
        for part in parts {
            guard let n = Int64(part.group(1)) else { throw VaultFormatError("\"\(text!)\" isn't a duration (use 90m, 24h, or 1d).") }
            switch part.group(2).lowercased() {
            case "d": total += TimeInterval(n) * 86_400
            case "h": total += TimeInterval(n) * 3600
            default: total += TimeInterval(n) * 60
            }
        }
        return total
    }

    /// `long.TryParse(NumberStyles.Integer)`: optional white space and sign, ASCII digits.
    static func parseLong(_ text: String) -> Int64? {
        var t = text.oTrim()
        var negative = false
        if t.oStarts("-") { negative = true; t = t.sub(1) } else if t.oStarts("+") { t = t.sub(1) }
        guard !t.isEmpty, t.utf16.allSatisfy({ $0 >= 0x30 && $0 <= 0x39 }), let n = Int64(t) else { return nil }
        return negative ? -n : n
    }

    // ---- JSON and YAML text ----------------------------------------------------------

    private static let safePlain = Rx(#"^[\p{L}\p{N}][\p{L}\p{N} _./+\-]*$"#)
    private static let looksTyped = Rx(#"^(true|false|yes|no|on|off|null|~|[-+]?[\d._]+([eE][-+]?\d+)?|\d{4}-\d{2}-\d{2}.*)$"#, ignoreCase: true)

    /// A YAML scalar: plain when unambiguous, otherwise a double-quoted (JSON-compatible) string.
    static func yamlScalar(_ value: String) -> String {
        safePlain.isMatch(value) && !looksTyped.isMatch(value) ? value : JSONText.quote(value, .relaxed)
    }

    /// Compact JSON for `%%…%%` comments; a literal `%` is escaped so the comment can't end early.
    static func hidden(_ value: JSONValue) -> String { JSONText.write(value, escaping: .relaxed).oRep("%", "\\u0025") }

    // ---- People --------------------------------------------------------------------

    private static let kinds: [(String, ActorKind)] = [("User", .user), ("Agent", .agent), ("Browser", .browser), ("Teams", .teams), ("System", .system), ("Vault", .vault), ("Connector", .connector)]

    static func parseActor(_ text: String?) -> Actor {
        let value = text?.oTrim() ?? ""
        if value.isEmpty || Ordinal.equalIgnoreCase(value, "You") { return .user }
        if Ordinal.equalIgnoreCase(value, "Edited in files") { return Actor(kind: .vault) }
        let colon = value.oIndex(":")
        let kindText = colon.map { value.sub(0, $0) } ?? value
        if let kind = kinds.first(where: { Ordinal.equalIgnoreCase($0.0, kindText.oTrim()) })?.1 {
            let name = colon.map { value.sub($0 + 1).oTrim() }
            return Actor(kind: kind, name: (name?.isEmpty ?? true) ? nil : name)
        }
        return Actor(kind: .user, name: value)
    }

    /// How the vault writes who did something (C#'s `Actor.DisplayName`).
    static func displayName(_ actor: Actor) -> String {
        switch actor.kind {
        case .user: return "You"
        case .vault: return "Edited in files"
        default:
            let kind = kinds.first { $0.1 == actor.kind }!.0
            if let name = actor.name, !name.oIsBlank { return "\(kind): \(name)" }
            return kind
        }
    }

    // ---- Ids -----------------------------------------------------------------------

    /// Stable, short Obsidian block id for a task (unless the file already gave it one).
    static func blockId(_ id: UUID) -> String { "t" + String(guidN(id).suffix(7)) }

    /// A deterministic GUID (version-8 layout) for `key`, in .NET's byte order.
    static func stableGuid(_ key: String) -> UUID {
        var b = Array(SHA256.hash(data: Data(key.utf8)).prefix(16))
        b[7] = (b[7] & 0x0F) | 0x80
        b[8] = (b[8] & 0x3F) | 0x80
        // new Guid(bytes) reads the first three fields little-endian.
        return UUID(uuid: (b[3], b[2], b[1], b[0], b[5], b[4], b[7], b[6], b[8], b[9], b[10], b[11], b[12], b[13], b[14], b[15]))
    }

    // ---- Code fences ---------------------------------------------------------------

    /// Tracks fenced code blocks like CommonMark: a fence closes only with the same character, at least as long.
    struct Fence {
        private var marker: UInt16 = 0
        private var length = 0

        var isOpen: Bool { length > 0 }

        /// Feeds one line; true when the line is part of a code block (including its fence lines).
        mutating func accept(_ line: String) -> Bool {
            let trimmed = Array(line.oTrimStart(" \t").utf16)
            if isOpen {
                let closing = Array(String(decoding: trimmed, as: UTF16.self).oTrimEnd().utf16)
                if closing.count >= length && closing.allSatisfy({ $0 == marker }) { length = 0 }
                return true
            }
            if trimmed.count >= 3 && (trimmed[0] == 0x60 || trimmed[0] == 0x7E) {
                var run = 0
                while run < trimmed.count && trimmed[run] == trimmed[0] { run += 1 }
                if run >= 3 && !(trimmed[0] == 0x60 && trimmed[run...].contains(0x60)) {
                    marker = trimmed[0]
                    length = run
                    return true
                }
            }
            return false
        }

        var closer: String { String(decoding: [UInt16](repeating: marker, count: length), as: UTF16.self) }
    }

    /// Adds a closing fence if `text` ends inside a code block, so it can't swallow what follows.
    static func closeFences(_ text: String) -> String {
        var fence = Fence()
        for line in text.oSplit("\n") { _ = fence.accept(line) }
        return fence.isOpen ? text + "\n" + fence.closer : text
    }
}

/// GUID text the way .NET writes and reads it.
func guidD(_ id: UUID) -> String { id.uuidString.lowercased() }

func guidN(_ id: UUID) -> String { guidD(id).oRep("-", "") }

/// `Guid.TryParse`: 32 hex digits, with dashes, in braces or parentheses.
func parseGuid(_ text: String?) -> UUID? {
    guard var t = text?.oTrim() else { return nil }
    if (t.oStarts("{") && t.oEnds("}")) || (t.oStarts("(") && t.oEnds(")")) { t = t.sub(1, t.u16 - 1) }
    let hex: String
    if t.u16 == 36 {
        let u = Array(t.utf16)
        guard u[8] == 0x2D, u[13] == 0x2D, u[18] == 0x2D, u[23] == 0x2D else { return nil }
        hex = t.oRep("-", "")
    } else if t.u16 == 32 {
        hex = t
    } else {
        return nil
    }
    guard hex.u16 == 32, hex.utf16.allSatisfy({ ($0 >= 0x30 && $0 <= 0x39) || ($0 >= 0x41 && $0 <= 0x46) || ($0 >= 0x61 && $0 <= 0x66) }) else { return nil }
    let h = hex.lowercased()
    let d = "\(h.sub(0, 8))-\(h.sub(8, 12))-\(h.sub(12, 16))-\(h.sub(16, 20))-\(h.sub(20, 32))"
    return UUID(uuidString: d)
}

/// `Guid.TryParseExact(s, "N")`.
func parseGuidN(_ text: String) -> UUID? {
    guard text.u16 == 32, !text.oContains("-"), !text.oContains(" ") else { return nil }
    return parseGuid(text)
}

let emptyGuid = UUID(uuid: (0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0))

/// An instant to the 100 ns tick, as .NET's `DateTimeOffset` keeps it (sync manifests compare `since` and tombstone
/// times exactly; `Date` is a `Double` and would lose the last digits).
public struct Instant: Comparable, Hashable, Sendable, CustomStringConvertible {
    /// 100 ns ticks since 0001-01-01T00:00:00Z.
    public let utcTicks: Int64

    public init(utcTicks: Int64) { self.utcTicks = utcTicks }

    public init(_ date: Date) {
        utcTicks = Instant.unixEpochTicks + Int64((date.timeIntervalSince1970 * 10_000_000).rounded())
    }

    public static let minValue = Instant(utcTicks: 0)
    static let unixEpochTicks: Int64 = 621_355_968_000_000_000
    static let ticksPerSecond: Int64 = 10_000_000

    public var date: Date { Date(timeIntervalSince1970: Double(utcTicks - Instant.unixEpochTicks) / 1e7) }

    public static func < (a: Instant, b: Instant) -> Bool { a.utcTicks < b.utcTicks }

    public func adding(seconds: Int64) -> Instant { Instant(utcTicks: utcTicks + seconds * Instant.ticksPerSecond) }

    /// Seconds from `other` to this one.
    public func seconds(since other: Instant) -> Double { Double(utcTicks - other.utcTicks) / 1e7 }

    /// System.Text.Json's form: `2026-01-05T09:00:00.1234567+00:00` (fraction trimmed of zeros, offset +00:00).
    public var description: String {
        let days = utcTicks / (86_400 * Instant.ticksPerSecond)
        let rest = utcTicks - days * 86_400 * Instant.ticksPerSecond
        let d = LocalDay(epochDay: Int(days) - 719_162)
        let secs = Int(rest / Instant.ticksPerSecond)
        let frac = rest % Instant.ticksPerSecond
        var text = String(format: "%04d-%02d-%02dT%02d:%02d:%02d", d.year, d.month, d.day, secs / 3600, secs / 60 % 60, secs % 60)
        if frac > 0 {
            var digits = String(format: "%07lld", frac)
            while digits.hasSuffix("0") { digits.removeLast() }
            text += "." + digits
        }
        return text + "+00:00"
    }

    private static let iso = Rx(#"^(\d{4})-(\d{2})-(\d{2})(?:[T ](\d{2}):(\d{2})(?::(\d{2})(?:\.(\d{1,7})\d*)?)?)?\s*(Z|[+-]\d{2}(?::?\d{2})?)?$"#, ignoreCase: true)

    /// Parses an ISO 8601 time (`DateTimeOffset.TryParse`, invariant, assuming UTC without an offset) to UTC ticks.
    static func parseTicks(_ text: String) -> Int64? {
        guard let m = iso.match(text) else { return nil }
        let y = Int(m.group(1))!, mo = Int(m.group(2))!, d = Int(m.group(3))!
        guard y >= 1, (1...12).contains(mo), d >= 1, d <= LocalDay.length(y, mo) else { return nil }
        let h = m.has(4) ? Int(m.group(4))! : 0, mi = m.has(5) ? Int(m.group(5))! : 0, s = m.has(6) ? Int(m.group(6))! : 0
        guard h < 24, mi < 60, s < 60 else { return nil }
        var frac: Int64 = 0
        if m.has(7) {
            let f = m.group(7)
            frac = Int64(f + String(repeating: "0", count: 7 - f.count))!
        }
        var offset = 0
        if m.has(8) {
            let z = m.group(8)
            if z.uppercased() != "Z" {
                let sign = z.oStarts("-") ? -1 : 1
                let digits = z.sub(1).oRep(":", "")
                let oh = Int(digits.sub(0, 2))!, om = digits.u16 > 2 ? Int(digits.sub(2, 4))! : 0
                guard oh <= 14, om < 60 else { return nil }
                offset = sign * (oh * 3600 + om * 60)
            }
        }
        let day = Int64(LocalDay(year: y, month: mo, day: d).epochDay + 719_162)
        let seconds = day * 86_400 + Int64(h * 3600 + mi * 60 + s - offset)
        return seconds * ticksPerSecond + frac
    }

    public init?(parsing text: String) {
        guard let ticks = Instant.parseTicks(text.oTrim()) else { return nil }
        utcTicks = ticks
    }
}
