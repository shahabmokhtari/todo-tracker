import Foundation
import Yams

/// YAML frontmatter: values are read with a real YAML parser (only its nodes: every value is text, as in C#), while
/// each top-level key's original text is kept so keys the app doesn't own are written back exactly as they were.
/// Port of C#'s `Frontmatter`.
struct Frontmatter {
    /// The top-level properties in file order.
    let values: [(Node, Node)]

    /// Top-level entries in file order: key (nil for leading comments) and raw text (LF, ends with \n).
    let blocks: [(key: String?, raw: String)]

    static let empty = Frontmatter(values: [], blocks: [])

    /// Splits `---` frontmatter off `text` (LF-normalized). Returns the body.
    static func split(_ text: String) throws -> (frontmatter: Frontmatter, body: String, present: Bool) {
        guard text.oStarts("---\n") else { return (.empty, text, false) }
        var end = -1
        var position = 4
        let length = text.u16
        while position <= length {
            let next = text.oIndex("\n", from: position)
            let line = next.map { text.sub(position, $0) } ?? text.sub(position)
            let trimmed = line.oTrimEnd()
            if trimmed == "---" || trimmed == "..." {
                end = position
                position = next.map { $0 + 1 } ?? length
                break
            }
            guard let n = next else { break }
            position = n + 1
        }
        // An opening --- without a closing one is just a horizontal rule in the body.
        if end < 0 { return (.empty, text, false) }
        return (try parse(text.sub(4, end)), text.sub(position), true)
    }

    static func parse(_ yaml: String) throws -> Frontmatter {
        var values: [(Node, Node)] = []
        do {
            if let root = try Yams.compose(yaml: yaml) {
                switch root {
                case .mapping(let mapping):
                    values = Array(mapping)
                case .scalar(let scalar) where scalar.string.isEmpty:
                    break
                default:
                    throw VaultFormatError("The frontmatter must be a list of `key: value` properties.")
                }
            }
        } catch let error as VaultFormatError {
            throw error
        } catch {
            throw VaultFormatError("The frontmatter isn't valid YAML: \(error)")
        }

        var blocks: [(key: String?, raw: String)] = []
        var key: String?
        var raw = ""
        for line in yaml.oSplit("\n") {
            if line.isEmpty && raw.isEmpty && blocks.isEmpty && key == nil { continue }
            // Comments start their own block so they stay where the user put them when a key around them is rewritten.
            let comment = line.oStarts("#")
            let top = topLevelKey(line)
            if top != nil || (comment && key != nil) {
                if !raw.isEmpty || key != nil { blocks.append((key, raw)) }
                key = comment ? nil : top
                raw = ""
            }
            raw += line + "\n"
        }
        if !raw.isEmpty || key != nil {
            // The yaml text ends with "\n" before the closing ---, which splitting turns into a trailing empty line.
            blocks.append((key, raw.oEnds("\n\n") ? raw.sub(0, raw.u16 - 1) : raw))
        }
        return Frontmatter(values: values, blocks: blocks)
    }

    func scalar(_ key: String) -> String? {
        if case .scalar(let s)? = node(key) { return s.string }
        return nil
    }

    func list(_ key: String) -> [String] {
        switch node(key) {
        case .sequence(let seq)?:
            return seq.compactMap { if case .scalar(let s) = $0 { return s.string }; return nil }.filter { !$0.isEmpty }
        case .scalar(let s)? where !s.string.isEmpty:
            return s.string.oSplit(",").map { $0.oTrim() }.filter { !$0.isEmpty }
        default:
            return []
        }
    }

    func maps(_ key: String) -> [[(Node, Node)]] {
        guard case .sequence(let seq)? = node(key) else { return [] }
        return seq.compactMap { if case .mapping(let m) = $0 { return Array(m) }; return nil }
    }

    func has(_ key: String) -> Bool { node(key) != nil }

    static func get(_ map: [(Node, Node)], _ key: String) -> String? {
        guard let value = map.first(where: { if case .scalar(let k) = $0.0 { return Ordinal.equalIgnoreCase(k.string, key) }; return false })?.1,
              case .scalar(let s) = value else { return nil }
        return s.string
    }

    private func node(_ key: String) -> Node? {
        values.first { if case .scalar(let k) = $0.0 { return Ordinal.equalIgnoreCase(k.string, key) }; return false }?.1
    }

    private static func topLevelKey(_ line: String) -> String? {
        guard line.u16 > 0 else { return nil }
        let first = line.unit(0)
        if Ordinal.isWhite(first) || first == 0x23 || first == 0x2D { return nil }
        guard let colon = line.oIndex(":"), colon > 0, colon == line.u16 - 1 || Ordinal.isWhite(line.unit(colon + 1)) else { return nil }
        return line.sub(0, colon).oTrim().oTrim("\"'")
    }
}
