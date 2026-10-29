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

    // File names are written with POSIX calls: Foundation's file APIs turn a name into its decomposed (NFD)
    // "file system representation", so "Café.md" would be written as "Cafe\u{301}.md" and every other device would
    // see a different path. These keep the exact UTF-8 bytes of the name.

    /// Creates a folder and the folders above it.
    static func makeDirectories(_ path: String) throws {
        var current = ""
        for part in path.oSplit("/") {
            if current.isEmpty && part.isEmpty && path.oStarts("/") { current = "/"; continue }
            if part.isEmpty { continue }
            current += current.isEmpty || current == "/" ? part : "/" + part
            if mkdir(current, 0o755) != 0 && errno != EEXIST { throw posixError(current) }
        }
    }

    /// Writes a file and flushes it to the disk itself (`F_FULLFSYNC`), so a rename after it never publishes a file
    /// whose bytes are still only in a cache when the phone loses power.
    static func writeFile(_ path: String, _ data: Data, exclusive: Bool = false) throws {
        let fd = open(path, O_WRONLY | O_CREAT | (exclusive ? O_EXCL : O_TRUNC), 0o644)
        if fd < 0 { throw posixError(path) }
        defer { close(fd) }
        try data.withUnsafeBytes { (buffer: UnsafeRawBufferPointer) in
            var offset = 0
            while offset < buffer.count {
                let n = Darwin.write(fd, buffer.baseAddress! + offset, buffer.count - offset)
                if n < 0 { throw posixError(path) }
                offset += n
            }
        }
        if fcntl(fd, F_FULLFSYNC) != 0 && fsync(fd) != 0 { throw posixError(path) }
    }

    /// Moves a file or folder. The target must be free: a file that appeared there meanwhile is never replaced
    /// (`RENAME_EXCL`), except the same file in other letter case (a case-only rename).
    static func move(_ from: String, _ to: String) throws {
        var a = stat(), b = stat()
        let sameFile = lstat(to, &b) == 0 && lstat(from, &a) == 0 && a.st_ino == b.st_ino && a.st_dev == b.st_dev
        if sameFile {
            if rename(from, to) != 0 { throw posixError(from) }
        } else if renamex_np(from, to, UInt32(RENAME_EXCL)) != 0 {
            if errno == EEXIST { throw CocoaError(.fileWriteFileExists, userInfo: [NSFilePathErrorKey: to]) }
            throw posixError(from)
        }
    }

    static func posixError(_ path: String) -> Error {
        let code = errno
        return NSError(domain: NSPOSIXErrorDomain, code: Int(code), userInfo: [NSFilePathErrorKey: path, NSLocalizedDescriptionKey: String(cString: strerror(code)) + ": " + path])
    }

    /// Writes atomically (temp file + rename) so readers and sync clients never see half a file. `exclusive`: the file
    /// is new, and one that appeared there meanwhile is never replaced (the write fails instead).
    static func writeAtomic(_ path: String, _ data: Data, exclusive: Bool = false) throws {
        let dir = (path as NSString).deletingLastPathComponent
        try makeDirectories(dir)
        let temp = dir + "/." + (path as NSString).lastPathComponent + "." + String(guidN(UUID()).prefix(8)) + ".tmp"
        do {
            try writeFile(temp, data)
            if exclusive {
                if renamex_np(temp, path, UInt32(RENAME_EXCL)) != 0 {
                    if errno == EEXIST { throw CocoaError(.fileWriteFileExists, userInfo: [NSFilePathErrorKey: path]) }
                    throw posixError(path)
                }
            } else if rename(temp, path) != 0 {
                throw posixError(path)
            }
        } catch {
            unlink(temp)
            throw error
        }
    }

    static func writeAtomic(_ path: String, _ text: String, exclusive: Bool = false) throws { try writeAtomic(path, Data(text.utf8), exclusive: exclusive) }

    /// The key two names share when an Apple file system (APFS, HFS+) treats them as one file: the same letters in
    /// another Unicode normalization or letter case.
    static func fileSystemKey(_ path: String) -> String { Ordinal.upper(path.precomposedStringWithCanonicalMapping) }
}
