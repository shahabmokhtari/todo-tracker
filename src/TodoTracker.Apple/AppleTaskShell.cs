using TodoTracker.Core;

namespace TodoTracker.Apple;

public sealed class AppleTaskShell
{
    private readonly TaskBoard _board;

    public AppleTaskShell(TaskBoard board)
    {
        _board = board;
    }

    public IReadOnlyList<AppleTaskSnapshot> Agenda(DateTimeOffset now)
    {
        return _board.Agenda(now)
            .Select(AppleTaskSnapshot.From)
            .ToList();
    }

    public IReadOnlyList<AppleTaskSnapshot> Waiting(DateTimeOffset now)
    {
        return _board.Waiting(now)
            .Select(AppleTaskSnapshot.From)
            .ToList();
    }
}
