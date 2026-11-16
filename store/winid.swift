import CoreGraphics
import Foundation

// Prints "<window id> <x> <y> <w> <h>" for the largest on-screen window of the given process id (store/capture-mac.sh).
guard CGPreflightScreenCaptureAccess() else {
    FileHandle.standardError.write("Give the terminal Screen Recording in System Settings › Privacy & Security first.\n".data(using: .utf8)!)
    exit(1)
}
// --check: only that the terminal may capture the screen.
if CommandLine.arguments.dropFirst().first == "--check" { exit(0) }
guard CommandLine.arguments.count > 1, let pid = Int32(CommandLine.arguments[1]) else {
    FileHandle.standardError.write("Usage: winid <process id>\n".data(using: .utf8)!)
    exit(2)
}
let list = CGWindowListCopyWindowInfo([.optionOnScreenOnly], kCGNullWindowID) as? [[String: Any]] ?? []
let wins = list.filter { ($0[kCGWindowOwnerPID as String] as? Int32) == pid && ($0[kCGWindowLayer as String] as? Int) == 0 }
func area(_ w: [String: Any]) -> Double {
    let b = w[kCGWindowBounds as String] as? [String: Double] ?? [:]
    return (b["Width"] ?? 0) * (b["Height"] ?? 0)
}
guard let best = wins.max(by: { area($0) < area($1) }), let b = best[kCGWindowBounds as String] as? [String: Double] else {
    FileHandle.standardError.write("No window for process \(pid)\n".data(using: .utf8)!)
    exit(1)
}
print(best[kCGWindowNumber as String]!, Int(b["X"]!), Int(b["Y"]!), Int(b["Width"]!), Int(b["Height"]!))
