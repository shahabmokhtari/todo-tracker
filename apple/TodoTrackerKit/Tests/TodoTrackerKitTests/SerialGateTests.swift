import Foundation
import XCTest
@testable import TodoTrackerKit

/// One sync at a time: an actor lets another call in at every `await`, the gate doesn't.
final class SerialGateTests: XCTestCase {
    private final class Log: @unchecked Sendable {
        private let lock = NSLock()
        private var inside = 0
        private(set) var most = 0
        private(set) var order: [Int] = []

        func enter(_ n: Int) {
            lock.withLock {
                inside += 1
                most = max(most, inside)
                order.append(n)
            }
        }

        func leave() { lock.withLock { inside -= 1 } }
    }

    private actor Worker {
        let gate = SerialGate()
        let log: Log

        init(_ log: Log) { self.log = log }

        func work(_ n: Int) async {
            await gate.enter()
            defer { gate.leave() }
            log.enter(n)
            try? await Task.sleep(nanoseconds: 300_000_000)
            log.leave()
        }
    }

    func testCallsIntoAnActorThatAwaitDontOverlapAndGoInTurn() async {
        let log = Log()
        let worker = Worker(log)
        let first = Task { await worker.work(1) }
        try? await Task.sleep(nanoseconds: 50_000_000)
        let second = Task { await worker.work(2) }
        try? await Task.sleep(nanoseconds: 50_000_000)
        let third = Task { await worker.work(3) }
        _ = await (first.value, second.value, third.value)
        XCTAssertEqual(log.most, 1)
        XCTAssertEqual(log.order, [1, 2, 3])
    }
}
