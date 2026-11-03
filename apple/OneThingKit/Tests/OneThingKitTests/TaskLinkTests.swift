import Foundation
import XCTest
@testable import OneThingKit

/// Deep links: the same rules as C# and Kotlin (tests/fixtures/links.json).
final class TaskLinkTests: XCTestCase {
    private func fixture() throws -> [String: Any] {
        var root = URL(fileURLWithPath: #filePath)
        for _ in 0..<5 { root.deleteLastPathComponent() }
        let data = try Data(contentsOf: root.appendingPathComponent("tests/fixtures/links.json"))
        return try XCTUnwrap(JSONSerialization.jsonObject(with: data) as? [String: Any])
    }

    private func id() throws -> UUID {
        try XCTUnwrap(UUID(uuidString: try XCTUnwrap(try fixture()["id"] as? String)))
    }

    /// The lines under `name`; JSON null is a missing link.
    private func lines(_ name: String) throws -> [String?] {
        try XCTUnwrap(try fixture()[name] as? [Any]).map { $0 as? String }
    }

    func testATaskLinkRoundTrips() throws {
        let link = TaskLinks.forTask(try id())
        XCTAssertEqual(link, try fixture()["link"] as? String)
        XCTAssertEqual(TaskLinks.target(link), .task(try id()))
    }

    func testOtherShapesStillOpenTheTask() throws {
        let id = try id()
        XCTAssertFalse(try lines("opensTheTask").isEmpty)
        for link in try lines("opensTheTask") {
            XCTAssertEqual(TaskLinks.target(link), .task(id), link ?? "nil")
        }
    }

    func testTheBareSchemeJustOpensTheApp() throws {
        XCTAssertFalse(try lines("opensTheApp").isEmpty)
        for link in try lines("opensTheApp") {
            XCTAssertEqual(TaskLinks.target(link), .app, link ?? "nil")
        }
    }

    func testAnythingElseIsIgnored() throws {
        XCTAssertTrue(try lines("ignored").contains(nil))
        for link in try lines("ignored") {
            XCTAssertNil(TaskLinks.target(link), link ?? "nil")
        }
    }
}
