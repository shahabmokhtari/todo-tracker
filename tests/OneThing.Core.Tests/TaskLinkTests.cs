using System.Text.Json.Nodes;
using OneThing.Core;
using OneThing.Core.Tests.Fixtures;

namespace OneThing.Core.Tests;

/// <summary>
/// Deep links (onething://task/&lt;id&gt;) that open the app on a task. The cases are in tests/fixtures/links.json,
/// which Swift and Kotlin read too.
/// </summary>
public sealed class TaskLinkTests
{
    private static readonly JsonNode Fixture = FixtureFile.Read("links.json");

    private static Guid Id => Guid.Parse((string)Fixture["id"]!);

    private static IEnumerable<string?> Lines(string name) => Fixture[name]!.AsArray().Select(n => (string?)n);

    [Fact]
    public void A_task_link_round_trips_to_the_task_in_the_app()
    {
        var link = TaskLinks.ForTask(Id);

        Assert.Equal((string)Fixture["link"]!, link);
        Assert.Equal($"/#/task/{Id}", TaskLinks.AppPath(link));
    }

    [Fact]
    public void Links_pasted_or_typed_in_other_shapes_still_open_the_task()
    {
        Assert.NotEmpty(Lines("opensTheTask"));
        Assert.All(Lines("opensTheTask"), link => Assert.Equal($"/#/task/{Id}", TaskLinks.AppPath(link)));
    }

    [Fact]
    public void The_bare_scheme_just_opens_the_app()
    {
        Assert.NotEmpty(Lines("opensTheApp"));
        Assert.All(Lines("opensTheApp"), link => Assert.Equal("/", TaskLinks.AppPath(link)));
    }

    [Fact]
    public void Anything_else_is_not_a_link_the_app_follows()
    {
        Assert.Contains(null, Lines("ignored"));
        Assert.All(Lines("ignored"), text => Assert.Null(TaskLinks.AppPath(text)));
    }
}
