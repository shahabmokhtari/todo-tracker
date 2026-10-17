import Foundation

/// Energy: how much a task takes per hour (1 to 3 ⚡; a task that doesn't say counts as 1), and how much there is in a
/// day (`TaskBoard.dailyEnergy`). A day's energy is what its planned blocks take: each block's hours on that day times its
/// task's energy, done ones included. Energy.cs in the C# core, same rule (tests/fixtures/energy.json).
public enum Energy {
    public static let levels = 1...3
    public static let unset = 1
    public static let defaultPerDay = 10
    public static let maxPerDay = 100

    public static func of(_ item: WorkItem) -> Int { item.energy ?? unset }

    /// What the blocks planned on the day `day` falls on (in `timeZone`) take, rounded to 0.01.
    public static func planned(on day: Date, in timeZone: TimeZone, board: TaskBoard) -> Double {
        var calendar = Calendar(identifier: .gregorian)
        calendar.timeZone = timeZone
        let start = calendar.startOfDay(for: day)
        guard let end = calendar.date(byAdding: .day, value: 1, to: start) else { return 0 }
        var total = 0.0
        for item in board.allItems where !item.isArchived {
            guard let at = item.plannedAt else { continue }
            let blockEnd = at.addingTimeInterval(Double(item.plannedMinutes ?? 30) * 60)
            let from = max(at, start)
            let to = min(blockEnd, end)
            if to > from { total += to.timeIntervalSince(from) / 3600 * Double(of(item)) }
        }
        return (total * 100).rounded() / 100
    }

    /// "⚡ 4.5 of 10 planned today", and by how much it's past what there is (same words as the web and Windows).
    public static func line(planned: Double, perDay: Int) -> String {
        func n(_ x: Double) -> String {
            let r = (x * 10).rounded() / 10
            return r == r.rounded() ? String(Int(r)) : String(format: "%.1f", r)
        }
        let over = planned - Double(perDay)
        return over > 0 ? "⚡ \(n(planned)) of \(perDay) planned today: \(n(over)) too much." : "⚡ \(n(planned)) of \(perDay) planned today."
    }
}
