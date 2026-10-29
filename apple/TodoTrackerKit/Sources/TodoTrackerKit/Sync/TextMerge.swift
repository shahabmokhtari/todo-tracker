import Foundation

public struct TextMergeResult: Equatable, Sendable {
    public var text: String
    public var conflicted: Bool
}

/// What to do where both sides changed the same lines differently.
public enum ConflictPolicy: Sendable {
    /// Keep both sides' lines (the first side's first): nothing typed is lost.
    case both
    /// Keep only the first side's lines there (everything else still merges).
    case first
    /// Keep only the second side's lines there.
    case second
}

/// Line-based three-way merge (diff3), a port of C#'s `TextMerge`: changes to different lines are combined; where both
/// sides changed the same lines differently, both versions' lines are kept (the first side's first). Lines compare
/// ordinally and keep their own line endings.
public enum TextMerge {
    /// `lineKeys` gives each line of a file its identity (or nil), e.g. a step's block id: keyed lines merge one by one.
    public static func merge(base: String, first: String, second: String, lineKeys: (([String]) -> [String?])? = nil, policy: ConflictPolicy = .both) -> TextMergeResult {
        let o = split(base), a = split(first), b = split(second)
        // Lines as numbers (equal text, equal number), so comparing is ordinal and cheap.
        var ids: [OKey: Int] = [:]
        func number(_ lines: [String]) -> [Int] {
            lines.map { line in
                if let n = ids[OKey(line)] { return n }
                ids[OKey(line)] = ids.count
                return ids.count - 1
            }
        }
        let no = number(o), na = number(a), nb = number(b)
        let ko = lineKeys?(o), ka = lineKeys?(a), kb = lineKeys?(b)
        let ma = match(no, na), mb = match(no, nb)
        var output: [String] = []
        var conflicted = false
        var io = 0, ia = 0, ib = 0
        while true {
            while io < o.count && ma[io] == ia && mb[io] == ib {
                output.append(o[io])
                io += 1; ia += 1; ib += 1
            }
            if io >= o.count && ia >= a.count && ib >= b.count { break }

            // The next base line both sides kept ends this unstable chunk.
            var next = io
            while next < o.count && (ma[next] < 0 || mb[next] < 0) { next += 1 }
            let ea = next < o.count ? ma[next] : a.count
            let eb = next < o.count ? mb[next] : b.count
            let co = Array(no[io..<next]), ca = Array(na[ia..<ea]), cb = Array(nb[ib..<eb])
            let la = Array(a[ia..<ea]), lb = Array(b[ib..<eb])
            if co == ca {
                output.append(contentsOf: lb)
            } else if co == cb || ca == cb {
                output.append(contentsOf: la)
            } else if let ko, let keyed = mergeByKey(Array(o[io..<next]), la, lb, Array(ko[io..<next]), Array(ka![ia..<ea]), Array(kb![ib..<eb]), policy) {
                output.append(contentsOf: keyed.lines)
                conflicted = conflicted || keyed.conflicted
            } else if ko != nil && co.isEmpty && distinctKeyed(Array(ka![ia..<ea]), Array(kb![ib..<eb])) {
                // Both added lines that each carry their own identity (time logged on two devices): both stay.
                output.append(contentsOf: la)
                output.append(contentsOf: lb)
            } else {
                conflicted = true
                switch policy {
                case .first: output.append(contentsOf: la)
                case .second: output.append(contentsOf: lb)
                case .both:
                    output.append(contentsOf: la)
                    output.append(contentsOf: zip(lb, cb).filter { !ca.contains($0.1) }.map(\.0))
                }
            }
            io = next
            ia = ea
            ib = eb
        }
        return TextMergeResult(text: join(output), conflicted: conflicted)
    }

    /// Keyed lines merge one by one when the clashing stretch has the same keys in the same order on all three sides.
    private static func mergeByKey(_ o: [String], _ a: [String], _ b: [String], _ ko: [String?], _ ka: [String?], _ kb: [String?], _ policy: ConflictPolicy) -> (lines: [String], conflicted: Bool)? {
        guard o.count == a.count, o.count == b.count, !o.isEmpty else { return nil }
        var lines: [String] = []
        var conflicted = false
        for i in 0..<o.count {
            guard let key = ko[i], ka[i].map({ $0.oEq(key) }) == true, kb[i].map({ $0.oEq(key) }) == true else { return nil }
            if a[i].oEq(o[i]) || a[i].oEq(b[i]) {
                lines.append(b[i].oEq(o[i]) ? a[i] : b[i])
            } else if b[i].oEq(o[i]) {
                lines.append(a[i])
            } else {
                // The same step changed on both sides: one version (a step can't be two lines).
                conflicted = true
                lines.append(policy == .second ? b[i] : a[i])
            }
        }
        return (lines, conflicted)
    }

    private static func distinctKeyed(_ a: [String?], _ b: [String?]) -> Bool {
        guard !a.isEmpty, !b.isEmpty, a.allSatisfy({ $0 != nil }), b.allSatisfy({ $0 != nil }) else { return false }
        let sa = Set(a.map { OKey($0!) })
        return !b.contains { sa.contains(OKey($0!)) }
    }

    /// Lines with their line endings (the last may have none).
    static func split(_ text: String) -> [String] {
        var lines: [String] = []
        var current: [UInt16] = []
        for unit in text.utf16 {
            current.append(unit)
            if unit == 0x0A {
                lines.append(String(decoding: current, as: UTF16.self))
                current.removeAll(keepingCapacity: true)
            }
        }
        if !current.isEmpty { lines.append(String(decoding: current, as: UTF16.self)) }
        return lines
    }

    private static func join(_ lines: [String]) -> String {
        var out = ""
        for line in lines {
            // A last line without its newline that ends up in the middle gets one.
            if !out.isEmpty && out.utf16.last != 0x0A { out += "\n" }
            out += line
        }
        return out
    }

    /// For each base line, the index of the line it matches in `other` (longest common subsequence), or -1.
    private static func match(_ o: [Int], _ other: [Int]) -> [Int] {
        var map = Array(repeating: -1, count: o.count)
        var prefix = 0
        while prefix < o.count && prefix < other.count && o[prefix] == other[prefix] {
            map[prefix] = prefix
            prefix += 1
        }
        var suffix = 0
        while suffix < o.count - prefix && suffix < other.count - prefix && o[o.count - 1 - suffix] == other[other.count - 1 - suffix] {
            map[o.count - 1 - suffix] = other.count - 1 - suffix
            suffix += 1
        }
        let n = o.count - prefix - suffix, m = other.count - prefix - suffix
        if n == 0 || m == 0 { return map }
        var lcs = Array(repeating: Array(repeating: 0, count: m + 1), count: n + 1)
        for i in stride(from: n - 1, through: 0, by: -1) {
            for j in stride(from: m - 1, through: 0, by: -1) {
                lcs[i][j] = o[prefix + i] == other[prefix + j] ? lcs[i + 1][j + 1] + 1 : max(lcs[i + 1][j], lcs[i][j + 1])
            }
        }
        var i = 0, j = 0
        while i < n && j < m {
            if o[prefix + i] == other[prefix + j] {
                map[prefix + i] = prefix + j
                i += 1
                j += 1
            } else if lcs[i + 1][j] >= lcs[i][j + 1] {
                i += 1
            } else {
                j += 1
            }
        }
        return map
    }
}
