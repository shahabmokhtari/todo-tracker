import XCTest
@testable import OneThingKit

/// Deep links: the same rules as the C# TaskLinkTests.
final class TaskLinkTests: XCTestCase {
    private let id = UUID(uuidString: "4f9d1c2e-3b7a-4e10-9c55-0a1b2c3d4e5f")!

    func testATaskLinkRoundTrips() {
        let link = TaskLinks.forTask(id)
        XCTAssertEqual(link, "onething://task/4f9d1c2e-3b7a-4e10-9c55-0a1b2c3d4e5f")
        XCTAssertEqual(TaskLinks.target(link), .task(id))
    }

    func testOtherShapesStillOpenTheTask() {
        for link in [
            "OneThing://Task/4F9D1C2E-3B7A-4E10-9C55-0A1B2C3D4E5F",
            "onething://task/4f9d1c2e-3b7a-4e10-9c55-0a1b2c3d4e5f/",
            "onething://task/4f9d1c2e3b7a4e109c550a1b2c3d4e5f?from=teams",
            "onething:task/4f9d1c2e-3b7a-4e10-9c55-0a1b2c3d4e5f",
            "  onething://task/4f9d1c2e-3b7a-4e10-9c55-0a1b2c3d4e5f  ",
        ] {
            XCTAssertEqual(TaskLinks.target(link), .task(id), link)
        }
    }

    func testTheBareSchemeJustOpensTheApp() {
        for link in ["onething://", "onething:", "onething://open"] {
            XCTAssertEqual(TaskLinks.target(link), .app, link)
        }
    }

    func testAnythingElseIsIgnored() {
        for link in [
            nil, "", "https://example.com/#/task/4f9d1c2e-3b7a-4e10-9c55-0a1b2c3d4e5f", "onething://task/not-a-guid",
            "onething://task/", "onething://task/4f9d1c2e-3b7a-4e10-9c55-0a1b2c3d4e5f/../../settings",
            "onething://settings/delete-everything", "onething://task/{4f9d1c2e-3b7a-4e10-9c55-0a1b2c3d4e5f}", "--data",
        ] as [String?] {
            XCTAssertNil(TaskLinks.target(link), link ?? "nil")
        }
    }
}
