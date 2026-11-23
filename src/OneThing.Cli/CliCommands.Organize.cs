using System.Globalization;
using OneThing.Core;
using OneThing.Server;

namespace OneThing.Cli;

/// <summary>Groups, labels, tags and the timeline.</summary>
internal static partial class CliCommands
{
    /// <summary>A group by name or id; an unknown one says how to make it (a typo never makes a group).</summary>
    private static Guid? Group(TaskBoard board, string? name) => TodoTools.ResolveGroup(board, name, n => $"ot group add \"{n}\"");

    private static Guid RequireGroup(TaskBoard board, string name) => Group(board, name) ?? throw new CliUsageException("Say which group.");

    private static async Task GroupCommand(CliSession s, CliArgs a)
    {
        var verb = a.Words.Count > 0 ? a.Words[0].ToLowerInvariant() : "list";
        switch (verb)
        {
            case "list":
                a.Allow();
                var groups = await s.Read(b => Wire.Dashboard(b, s.Now, null, 0).Groups).ConfigureAwait(false);
                await s.Print(groups, (o, g) => o.Groups(g)).ConfigureAwait(false);
                return;
            case "add":
                a.Allow("color");
                var name = a.WordsFrom(1, "the group's name");
                var added = await ChangeGroup(s, (b, now) => b.AddGroup(name, Color(a.Value("color")), s.Actor, now).Id).ConfigureAwait(false);
                await s.Print(added, (o, g) => o.Line($"Added group {g.Name}. Add to it: ot add <title> -g \"{g.Name}\"")).ConfigureAwait(false);
                return;
            case "rename":
                a.Allow();
                var group = a.Word(1, "which group");
                var newName = a.WordsFrom(2, "the new name");
                var renamed = await ChangeGroup(s, (b, now) =>
                {
                    var g = b.GetGroup(RequireGroup(b, group));
                    b.UpdateGroup(g.Id, newName, g.Color, s.Actor, now);
                    return g.Id;
                }).ConfigureAwait(false);
                await s.Print(renamed, (o, g) => o.Line($"Renamed the group to {g.Name}")).ConfigureAwait(false);
                return;
            case "color" or "colour":
                a.Allow();
                var which = a.Word(1, "which group");
                var color = Color(a.Word(2, "the color (#rrggbb, or none)"));
                var colored = await ChangeGroup(s, (b, now) =>
                {
                    var g = b.GetGroup(RequireGroup(b, which));
                    b.UpdateGroup(g.Id, g.Name, color, s.Actor, now);
                    return g.Id;
                }).ConfigureAwait(false);
                await s.Print(colored, (o, g) => o.Line(g.Color is null ? $"{g.Name} has no color now" : $"{g.Name} is {g.Color} now")).ConfigureAwait(false);
                return;
            case "delete" or "remove":
                throw new CliUsageException("Groups are deleted only in the app, by you (its tasks move to another group there). ot group rename and ot move -g reorganize them.");
            default:
                throw new CliUsageException("ot group takes add, rename or color. See ot help group.");
        }
    }

    private static async Task<GroupDto> ChangeGroup(CliSession s, Func<TaskBoard, DateTimeOffset, Guid> change)
    {
        var group = await s.Store.UpdateAsync(b =>
        {
            var now = s.Now;
            return TodoTools.GroupOf(b, change(b, now), now);
        }).ConfigureAwait(false);
        await s.SaveVersion().ConfigureAwait(false);
        return group;
    }

    /// <summary>A color as given (#rrggbb), or none.</summary>
    private static string? Color(string? text) =>
        text is null || text.Trim().Equals("none", StringComparison.OrdinalIgnoreCase) || text.Trim().Length == 0 ? null : text.Trim();

    private static async Task Timeline(CliSession s, CliArgs a)
    {
        a.Allow("limit");
        var limit = Limit(a, 50);
        var reference = a.Words.Count > 0 ? a.WordsFrom(0, "which task") : null;
        var entries = await s.Read(b => Wire.Timeline(b, reference is null ? null : TaskResolver.Resolve(b, reference).Id, limit)).ConfigureAwait(false);
        await s.Print(entries, (o, e) => o.Timeline(e)).ConfigureAwait(false);
    }

    private static int Limit(CliArgs a, int fallback) =>
        a.Value("limit") is not { } text ? fallback
        : int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var n) && n is >= 1 and <= 1000 ? n
        : throw new CliUsageException("--limit is a number from 1 to 1000.");
}
