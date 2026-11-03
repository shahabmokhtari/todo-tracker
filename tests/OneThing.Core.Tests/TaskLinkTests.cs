using OneThing.Core;

namespace OneThing.Core.Tests;

/// <summary>Deep links (onething://task/&lt;id&gt;) that open the app on a task.</summary>
public sealed class TaskLinkTests
{
    private static readonly Guid Id = Guid.Parse("4f9d1c2e-3b7a-4e10-9c55-0a1b2c3d4e5f");

    [Fact]
    public void A_task_link_round_trips_to_the_task_in_the_app()
    {
        var link = TaskLinks.ForTask(Id);

        Assert.Equal("onething://task/4f9d1c2e-3b7a-4e10-9c55-0a1b2c3d4e5f", link);
        Assert.Equal("/#/task/4f9d1c2e-3b7a-4e10-9c55-0a1b2c3d4e5f", TaskLinks.AppPath(link));
    }

    [Theory]
    [InlineData("OneThing://Task/4F9D1C2E-3B7A-4E10-9C55-0A1B2C3D4E5F")]
    [InlineData("onething://task/4f9d1c2e-3b7a-4e10-9c55-0a1b2c3d4e5f/")]
    [InlineData("onething://task/4f9d1c2e3b7a4e109c550a1b2c3d4e5f?from=teams")]
    [InlineData("onething:task/4f9d1c2e-3b7a-4e10-9c55-0a1b2c3d4e5f")]
    [InlineData("  onething://task/4f9d1c2e-3b7a-4e10-9c55-0a1b2c3d4e5f  ")]
    public void Links_pasted_or_typed_in_other_shapes_still_open_the_task(string link) =>
        Assert.Equal($"/#/task/{Id}", TaskLinks.AppPath(link));

    [Theory]
    [InlineData("onething://")]
    [InlineData("onething:")]
    [InlineData("onething://open")]
    public void The_bare_scheme_just_opens_the_app(string link) => Assert.Equal("/", TaskLinks.AppPath(link));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("https://example.com/#/task/4f9d1c2e-3b7a-4e10-9c55-0a1b2c3d4e5f")]
    [InlineData("onething://task/not-a-guid")]
    [InlineData("onething://task/")]
    [InlineData("onething://task/4f9d1c2e-3b7a-4e10-9c55-0a1b2c3d4e5f/../../settings")]
    [InlineData("onething://settings/delete-everything")]
    [InlineData("onething://task/{4f9d1c2e-3b7a-4e10-9c55-0a1b2c3d4e5f}")]
    [InlineData("--data")]
    public void Anything_else_is_not_a_link_the_app_follows(string? text) => Assert.Null(TaskLinks.AppPath(text));
}
