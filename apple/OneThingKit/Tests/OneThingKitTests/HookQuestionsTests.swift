import XCTest
@testable import OneThingKit

/// The Mac asks about hooks waiting for approval one at a time; a question that went stale goes, and the next one is
/// always asked.
final class HookQuestionsTests: XCTestCase {
    private func hook(_ id: String, _ version: String = "v1") -> PendingHook {
        PendingHook(id: id, version: version, event: "taskCompleted", eventText: nil, addedBy: "Claude", description: "bash: echo \(id)")
    }

    private func waiting(_ hooks: PendingHook...) -> HookApprovals { HookApprovals(canApprove: true, appAsks: false, waiting: hooks) }

    func testAHookDecidedElsewhereWhileItIsShownMakesWayForTheNextAndThePromptShowsIt() {
        let a = hook("a"), b = hook("b")
        var questions = HookQuestions()
        var prompt = HookPromptState()
        questions.read(waiting(a, b))
        XCTAssertEqual(questions.asking, a)
        XCTAssertEqual(prompt.update(pending: questions.asking, appActive: true), [.show(a)])

        // A was allowed in the browser meanwhile.
        questions.read(waiting(b))
        XCTAssertEqual(questions.asking, b)
        XCTAssertEqual(prompt.update(pending: questions.asking, appActive: true), [.dismiss, .show(b)], "A's alert goes, B is asked")
        XCTAssertFalse(prompt.answered(a), "an answer from A's old alert counts for nothing")
        XCTAssertEqual(prompt.showing, b)

        XCTAssertTrue(prompt.answered(b))
        questions.answered(b)
        XCTAssertNil(questions.asking)
        XCTAssertEqual(prompt.update(pending: questions.asking, appActive: true), [])
    }

    func testAHookChangedWhileItIsShownIsAskedAboutAgainInItsNewVersion() {
        let a1 = hook("a", "v1"), a2 = hook("a", "v2")
        var questions = HookQuestions()
        var prompt = HookPromptState()
        questions.read(waiting(a1))
        _ = prompt.update(pending: questions.asking, appActive: true)

        questions.read(waiting(a2))
        XCTAssertEqual(questions.asking, a2)
        XCTAssertEqual(prompt.update(pending: questions.asking, appActive: true), [.dismiss, .show(a2)])
        XCTAssertFalse(prompt.answered(a1), "the old version can't be allowed")
    }

    func testLaterPutsItOffWithoutLosingItAndTheNextOneIsAskedAtOnce() {
        let a = hook("a"), b = hook("b")
        var questions = HookQuestions()
        questions.read(waiting(a, b))
        questions.answered(a)
        XCTAssertEqual(questions.asking, b, "the next one waiting, without waiting for the next read")
        questions.answered(b)
        XCTAssertNil(questions.asking)

        questions.read(waiting(a, b))
        XCTAssertNil(questions.asking, "not asked again in this run: it waits in Settings › Hooks")
        questions.read(waiting(hook("a", "v2"), b))
        XCTAssertEqual(questions.asking, hook("a", "v2"), "changed by an agent: asked again")
    }

    func testAQuestionShownStaysWhileItsHookWaitsAndGoesWhenNothingWaits() {
        let a = hook("a"), b = hook("b")
        var questions = HookQuestions()
        questions.read(waiting(a))
        questions.read(waiting(b, a))
        XCTAssertEqual(questions.asking, a, "one at a time: it stays until it's answered")
        questions.read(waiting())
        XCTAssertNil(questions.asking)
        questions.read(waiting(b))
        XCTAssertEqual(questions.asking, b)
        questions.read(HookApprovals(canApprove: true, appAsks: true, waiting: [b]))
        XCTAssertNil(questions.asking, "another app asks itself now")
    }

    func testInTheBackgroundTheAppOnlyAsksForAttentionAndShowsTheQuestionOnceItIsActive() {
        let a = hook("a")
        var prompt = HookPromptState()
        XCTAssertEqual(prompt.update(pending: a, appActive: false), [.requestAttention])
        XCTAssertEqual(prompt.update(pending: a, appActive: false), [], "asked for attention once")
        XCTAssertNil(prompt.showing)
        XCTAssertEqual(prompt.update(pending: a, appActive: true), [.show(a)])
        XCTAssertEqual(prompt.update(pending: a, appActive: false), [], "shown: it stays")
        XCTAssertEqual(prompt.update(pending: nil, appActive: true), [.dismiss])
    }
}
