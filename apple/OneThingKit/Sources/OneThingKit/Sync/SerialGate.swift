import Foundation

/// One at a time, in the order they came: what C#'s `SemaphoreSlim(1, 1)` gate does for a sync. An actor alone isn't
/// enough: another call gets in at every `await`, so two syncs (or a sync and a forget) would interleave.
///
///     await gate.enter()
///     defer { gate.leave() }
///
/// A task waiting its turn isn't woken early when it's cancelled: it runs after the one before, and finds out then.
public final class SerialGate: @unchecked Sendable {
    private let lock = NSLock()
    private var busy = false
    private var waiters: [CheckedContinuation<Void, Never>] = []

    public init() {}

    /// Waits until everything that entered before has left.
    public func enter() async {
        await withCheckedContinuation { (waiter: CheckedContinuation<Void, Never>) in
            let now: Bool = lock.withLock {
                guard busy else {
                    busy = true
                    return true
                }
                waiters.append(waiter)
                return false
            }
            if now { waiter.resume() }
        }
    }

    /// Lets the next one in (the first that waited).
    public func leave() {
        let next: CheckedContinuation<Void, Never>? = lock.withLock {
            guard !waiters.isEmpty else {
                busy = false
                return nil
            }
            return waiters.removeFirst()
        }
        next?.resume()
    }
}
