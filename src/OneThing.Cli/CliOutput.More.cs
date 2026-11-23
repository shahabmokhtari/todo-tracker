using System.Globalization;
using OneThing.Server;

namespace OneThing.Cli;

/// <summary>Text output for the timeline, the plan, the focus timer, settings and the rest.</summary>
internal sealed partial class CliOutput
{
    public void Timeline(IReadOnlyList<TimelineDto> entries)
    {
        if (entries.Count == 0)
        {
            Writer.WriteLine("Nothing has happened yet.");
        }

        foreach (var e in entries)
        {
            Writer.WriteLine($"  {Local(e.At)}  {e.Actor,-12} {e.Summary}");
        }
    }
}
