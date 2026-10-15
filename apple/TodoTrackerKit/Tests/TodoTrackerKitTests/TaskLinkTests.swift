import XCTest
@testable import TodoTrackerKit

/// Deep links: the same rules as the C# TaskLinkTests.
final class TaskLinkTests: XCTestCase {
    private let id = UUID(uuidString: "4f9d1c2e-3b7a-4e10-9c55-0a1b2c3d4e5f")!

    func testATaskLinkRoundTrips() {
        let link = TaskLinks.forTask(id)
        XCTAssertEqual(link, "todotracker://task/4f9d1c2e-3b7a-4e10-9c55-0a1b2c3d4e5f")
        XCTAssertEqual(TaskLinks.target(link), .task(id))
    }

    func testOtherShapesStillOpenTheTask() {
        for link in [
            "TodoTracker://Task/4F9D1C2E-3B7A-4E10-9C55-0A1B2C3D4E5F",
            "todotracker://task/4f9d1c2e-3b7a-4e10-9c55-0a1b2c3d4e5f/",
            "todotracker://task/4f9d1c2e3b7a4e109c550a1b2c3d4e5f?from=teams",
            "todotracker:task/4f9d1c2e-3b7a-4e10-9c55-0a1b2c3d4e5f",
            "  todotracker://task/4f9d1c2e-3b7a-4e10-9c55-0a1b2c3d4e5f  ",
        ] {
            XCTAssertEqual(TaskLinks.target(link), .task(id), link)
        }
    }

    func testTheBareSchemeJustOpensTheApp() {
        for link in ["todotracker://", "todotracker:", "todotracker://open"] {
            XCTAssertEqual(TaskLinks.target(link), .app, link)
        }
    }

    func testAnythingElseIsIgnored() {
        for link in [
            nil, "", "https://example.com/#/task/4f9d1c2e-3b7a-4e10-9c55-0a1b2c3d4e5f", "todotracker://task/not-a-guid",
            "todotracker://task/", "todotracker://task/4f9d1c2e-3b7a-4e10-9c55-0a1b2c3d4e5f/../../settings",
            "todotracker://settings/delete-everything", "todotracker://task/{4f9d1c2e-3b7a-4e10-9c55-0a1b2c3d4e5f}", "--data",
        ] as [String?] {
            XCTAssertNil(TaskLinks.target(link), link ?? "nil")
        }
    }
}
