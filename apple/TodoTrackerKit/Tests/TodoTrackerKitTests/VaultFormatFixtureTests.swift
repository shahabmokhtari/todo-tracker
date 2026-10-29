import Foundation
import XCTest
@testable import TodoTrackerKit

/// tests/fixtures/vault/names.json and parse.json: file names, ids, JSON/YAML escaping, local times, syncable paths,
/// and task files as people type them — read and written back exactly like the C# core.
final class VaultFormatFixtureTests: XCTestCase {
    func testTitlesMakeTheSameFileAndFolderNames() throws {
        let fixture = try Fixtures.json("vault/names.json")
        for c in fixture["titles"] as! [[String: String]] {
            XCTAssertTrue(VaultFiles.safeName(c["title"]!).oEq(c["file"]!), "file name for \(c["title"]!): \(VaultFiles.safeName(c["title"]!))")
            XCTAssertTrue(VaultFiles.safeFolderName(c["title"]!).oEq(c["folder"]!), "folder for \(c["title"]!): \(VaultFiles.safeFolderName(c["title"]!))")
        }
    }

    func testStableIdsGroupIdsAndBlockIds() throws {
        let fixture = try Fixtures.json("vault/names.json")
        for c in fixture["stableIds"] as! [[String: String]] {
            XCTAssertEqual(guidD(VaultText.stableGuid(c["key"]!)), c["id"]!, c["key"]!)
        }
        for c in fixture["groupIds"] as! [[String: String]] {
            let key = "group|" + c["folder"]!.oUpper()
            XCTAssertTrue(key.oEq(c["key"]!), "key for \(c["folder"]!): \(key)")
            XCTAssertEqual(guidD(VaultText.stableGuid(key)), c["id"]!, c["folder"]!)
        }
        for c in fixture["blockIds"] as! [[String: String]] {
            XCTAssertEqual(VaultText.blockId(parseGuid(c["id"]!)!), c["blockId"]!)
        }
    }

    func testStringsAreEscapedLikeDotNet() throws {
        let fixture = try Fixtures.json("vault/names.json")
        for c in fixture["strings"] as! [[String: String]] {
            let text = c["text"]!
            XCTAssertTrue(VaultText.hidden(.string(text)).oEq(c["hidden"]!), "hidden \(text): \(VaultText.hidden(.string(text)))")
            XCTAssertTrue(VaultText.yamlScalar(text).oEq(c["yaml"]!), "yaml \(text): \(VaultText.yamlScalar(text))")
            XCTAssertTrue(JSONText.quote(text, .standard).oEq(c["json"]!), "json \(text): \(JSONText.quote(text, .standard))")
        }
    }

    func testLocalTimesAcrossDaylightSaving() throws {
        let fixture = try Fixtures.json("vault/names.json")
        let times = fixture["localTimes"] as! [String: Any]
        let tz = TimeZone(identifier: times["zone"] as! String)!
        for c in times["typed"] as! [[String: String]] {
            let at = try VaultText.parseTime(c["text"]!, tz, c["defaultTime"]!)
            XCTAssertEqual(at.map(VaultText.utc), c["utc"]!, c["text"]!)
        }
        for c in times["shown"] as! [[String: String]] {
            let at = Fixtures.date(c["utc"]!)
            XCTAssertEqual(VaultText.localDateTime(at, tz), c["localDateTime"]!)
            XCTAssertEqual(VaultText.localDate(at, tz), c["localDate"]!)
            XCTAssertEqual(VaultText.localMinute(at, tz), c["localMinute"]!)
        }
    }

    func testSyncablePaths() throws {
        let fixture = try Fixtures.json("vault/names.json")
        for c in fixture["syncablePaths"] as! [[String: Any]] {
            XCTAssertEqual(VaultFiles.isSyncablePath(c["path"] as! String), c["syncable"] as! Bool, c["path"] as! String)
        }
    }

    func testTaskFilesAsPeopleTypeThem() throws {
        let fixture = try Fixtures.json("vault/parse.json")
        let tz = Fixtures.zone(fixture["timeZoneOffsetMinutes"] as! Int)
        let now = Fixtures.date(fixture["now"] as! String)
        let cases = fixture["cases"] as! [[String: Any]]
        XCTAssertEqual(cases.count, 12)
        for c in cases {
            let name = c["name"] as! String
            let path = c["path"] as! String
            let context = TaskMarkdownContext(timeZone: tz, now: now, fileDirectory: VaultFiles.directory(path), idSeed: path)
            let text = c["text"] as! String
            if c["error"] as? Bool == true {
                XCTAssertThrowsError(try TaskMarkdown.parse(text, fileStem: VaultFiles.stem(path), context: context), name)
                continue
            }
            let parsed = try TaskMarkdown.parse(text, fileStem: VaultFiles.stem(path), context: context)
            XCTAssertEqual(parsed.needsWrite, c["needsWrite"] as! Bool, name)
            XCTAssertEqual(parsed.hasAppId, c["hasAppId"] as! Bool, name)
            let item = try Fixtures.export(parsed.root)
            XCTAssertNil(Fixtures.diff(item, Fixtures.normalized(c["item"]!)), name)
            let rewritten = TaskMarkdown.render(parsed.root, context: context, previous: parsed)
            XCTAssertTrue(rewritten.oEq(c["rewritten"] as! String), "\(name) rewritten:\n\(rewritten)")
        }
    }
}
