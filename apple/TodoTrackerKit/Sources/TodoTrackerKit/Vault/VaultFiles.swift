import Foundation

/// File names in the tasks folder and what may sync. Port of C#'s `VaultFiles` and `VaultBoardStore.IsSyncablePath`.
public enum VaultFiles {
    public static let metaFolder = ".todo-tracker"
    public static let attachmentsFolder = "_attachments"

    private static let reserved: Set<String> = ["CON", "PRN", "AUX", "NUL", "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
                                                 "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9"]
    private static let unsafeChars = Rx(#"[<>:"/\\|?*#^\[\]\x{00}-\x{1F}]"#)
    private static let spaces = Rx(#"\s{2,}"#)

    /// A file or folder name that works on Windows, macOS, Linux, and in Obsidian links (at most `maxLength` UTF-16
    /// units, never half a surrogate pair).
    public static func safeName(_ title: String, maxLength: Int = 80) -> String {
        var cleaned = unsafeChars.replace(title.precomposedStringWithCanonicalMapping) { _ in " " }
        cleaned = spaces.replace(cleaned) { _ in " " }.oTrim().oTrimEnd(". ").oTrimStart(". ")
        if cleaned.u16 > maxLength {
            let high = cleaned.unit(maxLength - 1)
            let cut = (0xD800...0xDBFF).contains(high) ? maxLength - 1 : maxLength
            cleaned = cleaned.sub(0, cut).oTrimEnd(". ")
        }
        if cleaned.isEmpty { cleaned = "Untitled" }
        return reserved.contains(cleaned.oUpper()) ? cleaned + "_" : cleaned
    }

    /// A group folder name: like `safeName`, without a leading `_` or `.` (those folders are skipped).
    public static func safeFolderName(_ name: String) -> String {
        let cleaned = safeName(name, maxLength: 60).oTrimStart("_. ")
        return cleaned.isEmpty ? "Group" : cleaned
    }

    /// `stem`, or "stem 2", "stem 3"… so it is unique.
    public static func uniqueStem(_ stem: String, taken: (String) -> Bool) -> String {
        if !taken(stem) { return stem }
        var n = 2
        while taken("\(stem) \(n)") { n += 1 }
        return "\(stem) \(n)"
    }

    private static let invalidNameUnits: Set<UInt16> = Set((0..<32).map(UInt16.init) + Array("<>:\"|?*/".utf16))
    private static let reservedName = Rx(#"^(CON|PRN|AUX|NUL|COM[0-9]|LPT[0-9])(\..*)?$"#, ignoreCase: true)

    /// A path from another device is only ever a file inside the vault's own folders, with a name every system keeps
    /// as is (Windows' rules everywhere).
    public static func isSyncablePath(_ rel: String) -> Bool {
        guard rel.u16 > 0, rel.u16 < 400, rel.oContains("/"), !rel.oContains("\\"), !rel.oContains(":"), !rel.oEndsIgnoreCase(".tmp") else { return false }
        return rel.oSplit("/").allSatisfy { s in
            s.u16 > 0 && s != ".." && !s.oStarts(".") && s.oTrim().u16 == s.u16 && !s.oEnds(".")
                && !s.utf16.contains(where: invalidNameUnits.contains) && !reservedName.isMatch(s)
        }
    }

    /// `Path.GetExtension` of a vault path ("" when there is none).
    static func extensionOf(_ path: String) -> String {
        guard let dot = path.oLastIndex("."), dot > (path.oLastIndex("/") ?? -1), dot < path.u16 - 1 else { return "" }
        return path.sub(dot)
    }

    /// `Path.GetFileNameWithoutExtension`.
    static func stem(_ path: String) -> String {
        let name = fileName(path)
        let ext = extensionOf(name)
        return name.sub(0, name.u16 - ext.u16)
    }

    static func fileName(_ path: String) -> String {
        guard let slash = path.oLastIndex("/") else { return path }
        return path.sub(slash + 1)
    }

    /// The folder of a vault path ("" at the top).
    static func directory(_ path: String) -> String {
        guard let slash = path.oLastIndex("/") else { return "" }
        return path.sub(0, slash)
    }

    static func changeExtension(_ path: String, _ ext: String) -> String {
        let current = extensionOf(path)
        return path.sub(0, path.u16 - current.u16) + ext
    }

    /// Writes atomically (temp file + rename) so readers and sync clients never see half a file.
    static func writeAtomic(_ url: URL, _ data: Data) throws {
        let dir = url.deletingLastPathComponent()
        try FileManager.default.createDirectory(at: dir, withIntermediateDirectories: true)
        let temp = dir.appendingPathComponent("." + url.lastPathComponent + "." + String(guidN(UUID()).prefix(8)) + ".tmp")
        do {
            try data.write(to: temp)
            if rename(temp.path, url.path) != 0 { throw POSIXError(POSIXErrorCode(rawValue: errno) ?? .EIO) }
        } catch {
            try? FileManager.default.removeItem(at: temp)
            throw error
        }
    }

    static func writeAtomic(_ url: URL, _ text: String) throws { try writeAtomic(url, Data(text.utf8)) }
}
