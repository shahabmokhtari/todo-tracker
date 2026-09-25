import XCTest
@testable import TodoTrackerKit

final class SharedScenarioTests: XCTestCase {
    /// Runs the same fixture files as the C# SharedScenarioTests, so the Swift and C# agenda rules cannot drift.
    func testAllSharedScenariosMatchExpectedAgenda() throws {
        var root = URL(fileURLWithPath: #filePath)
        for _ in 0..<5 { root.deleteLastPathComponent() }
        let dir = root.appendingPathComponent("tests/fixtures/scenarios")
        let files = try FileManager.default.contentsOfDirectory(at: dir, includingPropertiesForKeys: nil)
            .filter { $0.pathExtension == "json" }
            .sorted { $0.lastPathComponent < $1.lastPathComponent }
        XCTAssertGreaterThanOrEqual(files.count, 4, "expected shared scenarios in \(dir.path)")

        for file in files {
            let json = try JSONSerialization.jsonObject(with: Data(contentsOf: file)) as! [String: Any]
            let name = file.lastPathComponent
            let now = BoardCodec.parseDate(json["now"] as! String)!
            let boardData = try JSONSerialization.data(withJSONObject: json["board"]!)
            let board = try BoardCodec.decode(boardData)
            let group = (json["group"] as? String).flatMap(UUID.init(uuidString:))
            let expect = json["expect"] as! [String: Any]

            let dashboard = Agenda.build(board, now: now, groupId: group)

            XCTAssertEqual(dashboard.now.map(\.item.id), ids(expect["now"]), "\(name): now")
            XCTAssertEqual(dashboard.waiting.map(\.item.id), ids(expect["waiting"]), "\(name): waiting")
            XCTAssertEqual(dashboard.now.filter(\.needsAttention).map(\.item.id), ids(expect["attention"]), "\(name): attention")
            XCTAssertEqual(dashboard.focus?.item.id, (expect["focus"] as? String).flatMap(UUID.init(uuidString:)), "\(name): focus")
            for (id, state) in expect["states"] as! [String: String] {
                let item = try board.get(UUID(uuidString: id)!)
                XCTAssertEqual(Agenda.state(of: item, now: now).rawValue, state, "\(name): state of \(item.title)")
            }
        }
    }

    private func ids(_ value: Any?) -> [UUID] {
        (value as? [String] ?? []).compactMap(UUID.init(uuidString:))
    }
}

final class TaskBoardTests: XCTestCase {
    let t0 = BoardCodec.parseDate("2026-01-05T09:00:00.000Z")!

    func testAddTaskTrimsAndLogs() throws {
        let board = TaskBoard()
        let item = try board.addTask(NewTask("  Roll out feature X  ", priority: .critical), now: t0)
        XCTAssertEqual(item.title, "Roll out feature X")
        XCTAssertEqual(item.groupId, board.defaultGroupId)
        XCTAssertEqual(board.activity.last?.kind, "created")
        XCTAssertThrowsError(try board.addTask(NewTask("   "), now: t0))
    }

    func testDefaultGroupsAndChildrenInheritGroup() throws {
        let board = TaskBoard()
        XCTAssertEqual(board.groups.map(\.name), ["Work", "Personal"])
        let personal = board.groups[1].id
        let root = try board.addTask(NewTask("Dentist", groupId: personal), now: t0)
        let child = try board.addTask(NewTask("Call", parentId: root.id), now: t0)
        XCTAssertEqual(child.groupId, personal)
        XCTAssertThrowsError(try board.moveToGroup(child.id, groupId: board.defaultGroupId, now: t0))
        try board.moveToGroup(root.id, groupId: board.defaultGroupId, now: t0)
        XCTAssertEqual(child.groupId, board.defaultGroupId)
        XCTAssertThrowsError(try board.addGroup(name: "work", now: t0))
        XCTAssertThrowsError(try board.addGroup(name: "Hobby", color: "red", now: t0))
    }

    func testSequentialStepsAreGatedAndOrdered() throws {
        let board = TaskBoard()
        let feature = try board.addTask(NewTask("Feature A"), now: t0)
        let steps = try board.addSteps(parentId: feature.id, titles: ["Ring 0", " ", "Ring 1"], stepDelay: 24 * 3600, now: t0)
        XCTAssertEqual(steps.map(\.title), ["Ring 0", "Ring 1"])

        XCTAssertThrowsError(try board.complete(steps[1].id, now: t0)) { error in
            XCTAssertTrue((error as? BoardError)?.errorDescription?.contains("Ring 0") ?? false)
        }
        try board.complete(steps[0].id, now: t0.addingTimeInterval(3600))
        XCTAssertEqual(steps[1].nextActionAt, t0.addingTimeInterval(25 * 3600))
        XCTAssertEqual(Agenda.state(of: steps[1], now: t0.addingTimeInterval(2 * 3600)), .waiting)
        XCTAssertEqual(Agenda.state(of: steps[1], now: t0.addingTimeInterval(25 * 3600)), .actionable)

        try board.complete(steps[1].id, now: t0.addingTimeInterval(26 * 3600))
        XCTAssertTrue(feature.isDone, "completing the last step completes the sequence")
    }

    func testScheduleWithNotifyReplacesPendingScheduleReminder() throws {
        let board = TaskBoard()
        let item = try board.addTask(NewTask("Feature B"), now: t0)
        try board.scheduleNextAction(item.id, at: t0.addingTimeInterval(3600), notify: true, now: t0)
        try board.scheduleNextAction(item.id, at: t0.addingTimeInterval(5 * 3600), notify: true, message: "Continue", now: t0)
        let pending = item.reminders.filter(\.isPending)
        XCTAssertEqual(pending.count, 1)
        XCTAssertEqual(pending.first?.message, "Continue")
        try board.clearNextAction(item.id, now: t0)
        XCTAssertNil(item.nextActionAt)
    }

    func testNotesRejectUnsafeSources() throws {
        let board = TaskBoard()
        let item = try board.addTask(NewTask("Research"), now: t0)
        let note = try board.addNote(item.id, text: " Found it ", actor: .agent("copilot"), now: t0, sourceUrl: "https://example.com")
        XCTAssertEqual(note.text, "Found it")
        XCTAssertEqual(note.author.displayName, "Agent: copilot")
        XCTAssertThrowsError(try board.addNote(item.id, text: "x", now: t0, sourceUrl: "javascript:alert(1)"))
    }

    func testCompleteCascadesAndReopenRestoresAncestors() throws {
        let board = TaskBoard()
        let parent = try board.addTask(NewTask("X"), now: t0)
        let child = try board.addTask(NewTask("Y", parentId: parent.id), now: t0)
        try board.complete(parent.id, now: t0)
        XCTAssertTrue(child.isDone)
        try board.reopen(child.id, now: t0)
        XCTAssertFalse(parent.isDone)
    }

    func testDeleteRemovesSubtree() throws {
        let board = TaskBoard()
        let parent = try board.addTask(NewTask("X"), now: t0)
        let child = try board.addTask(NewTask("Y", parentId: parent.id), now: t0)
        try board.delete(parent.id, now: t0)
        XCTAssertNil(board.find(child.id))
        XCTAssertTrue(board.items.isEmpty)
    }

    func testTimelineIsNewestFirstForSubtree() throws {
        let board = TaskBoard()
        let parent = try board.addTask(NewTask("X"), now: t0)
        let child = try board.addTask(NewTask("Y", parentId: parent.id), now: t0.addingTimeInterval(60))
        try board.addTask(NewTask("Other"), now: t0.addingTimeInterval(120))
        try board.addNote(child.id, text: "did it", now: t0.addingTimeInterval(180))
        XCTAssertEqual(try board.timeline(parent.id).map(\.kind), ["noteAdded", "created", "created"])
    }
}

final class AgendaTests: XCTestCase {
    let t0 = BoardCodec.parseDate("2026-01-05T09:00:00.000Z")!

    func testDueReminderSurfacesWaitingTaskFirst() throws {
        let board = TaskBoard()
        let urgent = try board.addTask(NewTask("Critical", priority: .critical), now: t0)
        let b = try board.addTask(NewTask("Feature B"), now: t0)
        try board.scheduleNextAction(b.id, at: t0.addingTimeInterval(2 * 86_400), now: t0)
        try board.addReminder(b.id, dueAt: t0.addingTimeInterval(3600), message: "Check", now: t0)

        let d = Agenda.build(board, now: t0.addingTimeInterval(3600))

        XCTAssertEqual(d.now.map(\.item.id), [b.id, urgent.id])
        XCTAssertTrue(d.now[0].needsAttention)
        XCTAssertTrue(d.waiting.isEmpty)
    }

    func testOverviewAndRecentNotes() throws {
        let board = TaskBoard()
        let x = try board.addTask(NewTask("X", priority: .high), now: t0)
        let a = try board.addTask(NewTask("A", parentId: x.id), now: t0)
        let steps = try board.addSteps(parentId: a.id, titles: ["A1", "A2"], stepDelay: 86_400, now: t0)
        try board.complete(steps[0].id, now: t0)
        try board.addNote(steps[0].id, text: "first", now: t0)
        try board.addNote(x.id, text: "second", now: t0.addingTimeInterval(60))

        let d = Agenda.build(board, now: t0, recentNoteCount: 1)

        XCTAssertEqual(d.overview.first?.doneLeaves, 1)
        XCTAssertEqual(d.overview.first?.totalLeaves, 2)
        XCTAssertEqual(d.overview.first?.nextWakeAt, t0.addingTimeInterval(86_400))
        XCTAssertEqual(d.recentNotes.map(\.note.text), ["second"])
        XCTAssertEqual(d.waiting.first?.stepLabel, "Step 2 of 2")
    }
}

final class PomodoroAndFormattingTests: XCTestCase {
    let t0 = BoardCodec.parseDate("2026-01-05T09:00:00.000Z")!

    func testPomodoroCycle() {
        var timer = PomodoroTimer()
        let item = UUID()
        timer.startFocus(now: t0, itemId: item)
        XCTAssertEqual(timer.remaining(t0.addingTimeInterval(600)), 900)
        timer.pause(now: t0.addingTimeInterval(300))
        XCTAssertEqual(timer.remaining(t0.addingTimeInterval(9999)), 1200)
        timer.resume(now: t0.addingTimeInterval(600))
        let evt = timer.tick(now: t0.addingTimeInterval(1800))
        XCTAssertEqual(evt?.kind, .focusCompleted)
        XCTAssertEqual(evt?.itemId, item)
        XCTAssertEqual(timer.phase, .shortBreak)
        timer.skip(now: t0.addingTimeInterval(1850))
        XCTAssertEqual(timer.phase, .idle)
    }

    func testBoardTickLogsFocusCompletion() throws {
        let board = TaskBoard()
        let item = try board.addTask(NewTask("Deep work"), now: t0)
        try board.startFocus(item.id, now: t0)
        let events = board.tickPomodoro(now: t0.addingTimeInterval(40 * 60))
        XCTAssertEqual(events.map(\.kind), [.focusCompleted, .breakCompleted])
        XCTAssertTrue(board.activity.contains { $0.kind == "focusCompleted" })
    }

    func testQuickCapture() throws {
        let now = BoardCodec.parseDate("2026-01-05T14:30:00.000Z")!
        let utc = TimeZone(identifier: "UTC")!
        let c = try QuickCaptureParser.parse("Check canary !! @2h due:tomorrow", now: now, timeZone: utc)
        XCTAssertEqual(c.title, "Check canary")
        XCTAssertEqual(c.priority, .critical)
        XCTAssertEqual(c.nextActionAt, now.addingTimeInterval(7200))
        XCTAssertEqual(c.deadline, BoardCodec.parseDate("2026-01-06T17:00:00.000Z"))
        let pacific = TimeZone(secondsFromGMT: -8 * 3600)!
        XCTAssertEqual(try QuickCaptureParser.parse("Call @tomorrow", now: now, timeZone: pacific).nextActionAt, BoardCodec.parseDate("2026-01-06T17:00:00.000Z"))
        XCTAssertEqual(try QuickCaptureParser.parse("Email @someone", now: now, timeZone: utc).title, "Email @someone")
        XCTAssertThrowsError(try QuickCaptureParser.parse("!! @2h", now: now, timeZone: utc))
    }

    func testRelativeTimeMatchesOtherPlatforms() {
        let cases: [(Double, String)] = [(0, "now"), (5, "in 5m"), (90, "in 1h 30m"), (120, "in 2h"), (1430, "in 23h"), (1440, "in 1d"), (-5, "5m ago"), (-4320, "3d ago")]
        for (minutes, expected) in cases {
            XCTAssertEqual(RelativeTime.format(t0.addingTimeInterval(minutes * 60), now: t0), expected)
        }
        XCTAssertEqual(RelativeTime.format(BoardCodec.parseDate("2026-02-20T00:00:00Z")!, now: t0), "Feb 20")
    }
}

final class ReviewRegressionTests: XCTestCase {
    let t0 = BoardCodec.parseDate("2026-01-05T09:00:00.000Z")!

    func testSnoozingAfterReminderFiredMovesItemToWaiting() throws {
        let board = TaskBoard()
        let item = try board.addTask(NewTask("Feature B"), now: t0)
        try board.scheduleNextAction(item.id, at: t0.addingTimeInterval(600), notify: true, now: t0)
        board.markNotified(item.id, reminderId: item.reminders[0].id, now: t0.addingTimeInterval(600))

        try board.scheduleNextAction(item.id, at: t0.addingTimeInterval(7200), notify: true, now: t0.addingTimeInterval(660))

        let d = Agenda.build(board, now: t0.addingTimeInterval(720))
        XCTAssertTrue(d.now.isEmpty)
        XCTAssertEqual(d.waiting.map(\.item.id), [item.id])
    }

    func testStepsOfALockedStepCannotBeCompleted() throws {
        let board = TaskBoard()
        let rollout = try board.addTask(NewTask("Rollout", sequential: true), now: t0)
        try board.addTask(NewTask("Step 1", parentId: rollout.id), now: t0)
        let step2 = try board.addTask(NewTask("Step 2", parentId: rollout.id), now: t0)
        let sub = try board.addTask(NewTask("Step 2a", parentId: step2.id), now: t0)
        XCTAssertThrowsError(try board.complete(sub.id, now: t0))
    }

    func testReopenUndoesCascade() throws {
        let board = TaskBoard()
        let parent = try board.addTask(NewTask("X"), now: t0)
        let earlier = try board.addTask(NewTask("Done before", parentId: parent.id), now: t0)
        let open = try board.addTask(NewTask("Open", parentId: parent.id), now: t0)
        try board.complete(earlier.id, now: t0)
        try board.complete(parent.id, now: t0.addingTimeInterval(3600))
        try board.reopen(parent.id, now: t0.addingTimeInterval(7200))
        XCTAssertFalse(open.isDone)
        XCTAssertTrue(earlier.isDone)
    }

    func testQuickCaptureEdgeCasesMatchCSharp() throws {
        let utc = TimeZone(identifier: "UTC")!
        let lateNight = BoardCodec.parseDate("2026-01-06T00:30:00.000Z")!
        XCTAssertEqual(try QuickCaptureParser.parse("Call @tomorrow", now: lateNight, timeZone: utc).nextActionAt, BoardCodec.parseDate("2026-01-06T09:00:00.000Z"))
        let evening = BoardCodec.parseDate("2026-01-05T19:00:00.000Z")!
        XCTAssertEqual(try QuickCaptureParser.parse("Ship due:today", now: evening, timeZone: utc).deadline, BoardCodec.parseDate("2026-01-05T23:59:00.000Z"))
        XCTAssertEqual(try QuickCaptureParser.parse("Ship due:2026-13-01", now: t0, timeZone: utc).title, "Ship due:2026-13-01")
        XCTAssertEqual(try QuickCaptureParser.parse("Ship @+5m", now: t0, timeZone: utc).title, "Ship @+5m")
        XCTAssertEqual(try QuickCaptureParser.parse("Ship due:2026-02-01", now: t0, timeZone: utc).deadline, BoardCodec.parseDate("2026-02-01T17:00:00.000Z"))
    }

    func testNewerSchemaIsNeverReplacedByBackup() throws {
        let dir = FileManager.default.temporaryDirectory.appendingPathComponent(UUID().uuidString)
        defer { try? FileManager.default.removeItem(at: dir) }
        let store = BoardFileStore(url: dir.appendingPathComponent("board.json"))
        let board = try store.load()
        try store.save(board)
        try store.save(board)
        try Data(#"{"schemaVersion": 99, "items": []}"#.utf8).write(to: store.url)

        XCTAssertThrowsError(try store.load()) { error in
            guard case .unsupportedSchema(_)? = error as? BoardError else { return XCTFail("expected unsupportedSchema, got \(error)") }
        }
        XCTAssertTrue(String(data: try Data(contentsOf: store.url), encoding: .utf8)!.contains("99"))
    }

    func testCorruptBoardIsSetAsideAndBackupRestored() throws {
        let dir = FileManager.default.temporaryDirectory.appendingPathComponent(UUID().uuidString)
        defer { try? FileManager.default.removeItem(at: dir) }
        let store = BoardFileStore(url: dir.appendingPathComponent("board.json"))
        let board = try store.load()
        try board.addTask(NewTask("keep me"), now: t0)
        try store.save(board)
        try store.save(board)
        try Data("{ broken".utf8).write(to: store.url)

        XCTAssertEqual(try store.load().items.map(\.title), ["keep me"])
        let files = try FileManager.default.contentsOfDirectory(atPath: dir.path)
        XCTAssertTrue(files.contains { $0.contains("corrupt-") }, "\(files)")
        XCTAssertEqual(try store.load().items.map(\.title), ["keep me"], "main file restored from backup")
    }
}

final class CodecTests: XCTestCase {
    let t0 = BoardCodec.parseDate("2026-01-05T09:00:00.000Z")!

    func testRoundTripPreservesEverything() throws {
        let board = TaskBoard()
        let x = try board.addTask(NewTask("X", priority: .high, details: "d", deadline: t0.addingTimeInterval(86_400)), now: t0)
        let steps = try board.addSteps(parentId: x.id, titles: ["S1", "S2"], stepDelay: 86_400, now: t0)
        try board.addNote(steps[0].id, text: "n", actor: .agent("copilot"), now: t0, sourceUrl: "https://e.x", sourceTitle: "E")
        try board.addReminder(x.id, dueAt: t0, message: "r", now: t0)
        try board.complete(steps[0].id, now: t0)
        try board.startFocus(x.id, now: t0)

        let data = try BoardCodec.encode(board)
        let restored = try BoardCodec.decode(data)

        XCTAssertEqual(try BoardCodec.encode(restored), data)
        XCTAssertEqual(restored.items[0].children[1].nextActionAt, t0.addingTimeInterval(86_400))
        XCTAssertEqual(restored.items[0].children[0].notes[0].author, .agent("copilot"))
        XCTAssertTrue(restored.items[0].sequential)
        let text = String(data: data, encoding: .utf8)!
        XCTAssertTrue(text.contains("\"createdAt\" : \"2026-01-05T09:00:00.000Z\""), text)
        XCTAssertTrue(text.contains("\"stepDelayMinutes\" : 1440"))
    }

    func testReadsDotNetStyleTimestampsAndRejectsNewerSchema() throws {
        XCTAssertEqual(BoardCodec.parseDate("2026-01-05T09:00:00.1234567+00:00")?.timeIntervalSince(t0) ?? -1, 0.123, accuracy: 0.001)
        XCTAssertEqual(BoardCodec.parseDate("2026-01-05T01:00:00-08:00"), t0)
        XCTAssertThrowsError(try BoardCodec.decode(Data(#"{"schemaVersion": 99}"#.utf8)))
    }

    func testFileStoreKeepsBackup() throws {
        let dir = FileManager.default.temporaryDirectory.appendingPathComponent(UUID().uuidString)
        defer { try? FileManager.default.removeItem(at: dir) }
        let store = BoardFileStore(url: dir.appendingPathComponent("board.json"))
        let board = try store.load()
        try board.addTask(NewTask("first"), now: t0)
        try store.save(board)
        try board.addTask(NewTask("second"), now: t0)
        try store.save(board)
        try Data("{ broken".utf8).write(to: store.url)

        XCTAssertEqual(try store.load().items.map(\.title), ["first"])
    }
}
