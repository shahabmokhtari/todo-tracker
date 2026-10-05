using TodoTracker.Core;

namespace TodoTracker.Core.Tests;

public class TaskOrganizationTests
{
    private static readonly DateTimeOffset T0 = new(2026, 1, 5, 9, 0, 0, TimeSpan.Zero);
    private readonly TaskBoard _board = new();

    // ---- Tags ---------------------------------------------------------------------------

    [Fact]
    public void Tags_are_normalized_deduplicated_and_logged()
    {
        var item = _board.AddTask(new NewTask("Ship"), Actor.User, T0);

        _board.SetTags(item.Id, ["#release", " infra/k8s ", "Release", "#infra/k8s"], Actor.User, T0);

        Assert.Equal(["release", "infra/k8s"], item.Tags);
        Assert.Contains(_board.Activity, a => a.Kind == ActivityKind.Updated && a.Summary.Contains("#release", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("has space")]
    [InlineData("2026")]
    [InlineData("a,b")]
    [InlineData("#")]
    public void Invalid_tags_are_rejected_without_changing_anything(string tag)
    {
        var item = _board.AddTask(new NewTask("Ship") { Tags = ["ok"] }, Actor.User, T0);

        Assert.Throws<ArgumentException>(() => _board.SetTags(item.Id, ["fine", tag], Actor.User, T0));
        Assert.Equal(["ok"], item.Tags);
    }

    [Fact]
    public void Too_many_tags_are_rejected()
    {
        var item = _board.AddTask(new NewTask("Ship"), Actor.User, T0);

        Assert.Throws<ArgumentException>(() => _board.SetTags(item.Id, Enumerable.Range(0, TaskBoard.MaxTags + 1).Select(i => $"t{i}"), Actor.User, T0));
    }

    [Fact]
    public void New_tasks_can_carry_tags_and_labels()
    {
        var item = _board.AddTask(new NewTask("Ship") { Tags = ["release"], Labels = ["Deep work"] }, Actor.User, T0);

        Assert.Equal(["release"], item.Tags);
        Assert.Equal(["Deep work"], item.Labels);
        Assert.Contains(_board.Labels, l => l.Name == "Deep work");
    }

    // ---- Labels -------------------------------------------------------------------------

    [Fact]
    public void Using_an_unknown_label_defines_it_with_a_palette_color()
    {
        var item = _board.AddTask(new NewTask("Ship"), Actor.User, T0);

        _board.SetLabels(item.Id, ["Quick win", "quick WIN", "Waiting on others"], Actor.User, T0);

        Assert.Equal(["Quick win", "Waiting on others"], item.Labels);
        Assert.All(_board.Labels, l => Assert.Matches("^#[0-9a-f]{6}$", l.Color));
        Assert.Equal(2, _board.Labels.Select(l => l.Color).Distinct().Count());
    }

    [Fact]
    public void Labels_match_existing_definitions_case_insensitively()
    {
        _board.DefineLabel("Deep work", "#7c3aed", Actor.User, T0);
        var item = _board.AddTask(new NewTask("Ship"), Actor.User, T0);

        _board.SetLabels(item.Id, ["deep WORK"], Actor.User, T0);

        Assert.Equal(["Deep work"], item.Labels);
        Assert.Single(_board.Labels);
    }

    [Fact]
    public void Renaming_a_label_updates_every_task()
    {
        var a = _board.AddTask(new NewTask("A") { Labels = ["Later"] }, Actor.User, T0);
        var b = _board.AddTask(new NewTask("B") { ParentId = a.Id, Labels = ["Later"] }, Actor.User, T0);

        _board.UpdateLabel("later", "Someday", "#64748b", Actor.User, T0);

        Assert.Equal(["Someday"], a.Labels);
        Assert.Equal(["Someday"], b.Labels);
        Assert.Equal("#64748b", Assert.Single(_board.Labels).Color);
    }

    [Fact]
    public void Deleting_a_label_removes_it_from_tasks()
    {
        var a = _board.AddTask(new NewTask("A") { Labels = ["Later", "Quick win"] }, Actor.User, T0);

        _board.DeleteLabel("Later", Actor.User, T0);

        Assert.Equal(["Quick win"], a.Labels);
        Assert.DoesNotContain(_board.Labels, l => l.Name == "Later");
    }

    [Theory]
    [InlineData("red")]
    [InlineData("#12345")]
    public void Label_colors_must_be_hex(string color)
    {
        Assert.Throws<ArgumentException>(() => _board.DefineLabel("X", color, Actor.User, T0));
    }

    // ---- Attachments --------------------------------------------------------------------

    [Fact]
    public void Attachments_can_be_added_to_tasks_and_subtasks()
    {
        var root = _board.AddTask(new NewTask("Ship"), Actor.User, T0);
        var step = _board.AddTask(new NewTask("Step") { ParentId = root.Id }, Actor.User, T0);

        var plan = _board.AddAttachment(root.Id, "plan.pdf", "_attachments/abc/plan.pdf", 1200, Actor.User, T0);
        var shot = _board.AddAttachment(step.Id, "screen.png", "_attachments/abc/screen.png", 50, Actor.Agent("copilot"), T0);

        Assert.Equal([plan], root.Attachments);
        Assert.Equal([shot], step.Attachments);
        Assert.Equal("screen.png", shot.FileName);
        Assert.Equal(ActorKind.Agent, shot.AddedBy.Kind);
        Assert.Contains(_board.Activity, a => a.Kind == ActivityKind.AttachmentAdded && a.ItemId == step.Id);
    }

    [Theory]
    [InlineData("../evil.txt")]
    [InlineData("dir/file.txt")]
    [InlineData("dir\\file.txt")]
    [InlineData("")]
    public void Attachment_names_cannot_contain_paths(string name)
    {
        var root = _board.AddTask(new NewTask("Ship"), Actor.User, T0);

        Assert.Throws<ArgumentException>(() => _board.AddAttachment(root.Id, name, "_attachments/x/y", 1, Actor.User, T0));
    }

    [Fact]
    public void Attachments_can_be_removed()
    {
        var root = _board.AddTask(new NewTask("Ship"), Actor.User, T0);
        var a = _board.AddAttachment(root.Id, "plan.pdf", "_attachments/abc/plan.pdf", 1, Actor.User, T0);

        _board.RemoveAttachment(root.Id, a.Id, Actor.User, T0);

        Assert.Empty(root.Attachments);
        Assert.Throws<TaskNotFoundException>(() => _board.RemoveAttachment(root.Id, a.Id, Actor.User, T0));
    }

    // ---- Hierarchy moves ----------------------------------------------------------------

    [Fact]
    public void A_task_can_move_under_another_task_at_a_position()
    {
        var project = _board.AddTask(new NewTask("Project"), Actor.User, T0);
        var first = _board.AddTask(new NewTask("First") { ParentId = project.Id }, Actor.User, T0);
        var loose = _board.AddTask(new NewTask("Loose idea"), Actor.User, T0);

        _board.Move(loose.Id, project.Id, index: 0, groupId: null, Actor.User, T0);

        Assert.Equal([loose, first], project.Children);
        Assert.Equal(project, loose.Parent);
        Assert.DoesNotContain(loose, _board.Items);
        Assert.Contains(_board.Activity, a => a.Kind == ActivityKind.Moved && a.ItemId == loose.Id);
    }

    [Fact]
    public void A_subtask_can_become_a_top_level_task_in_a_group()
    {
        var project = _board.AddTask(new NewTask("Project"), Actor.User, T0);
        var step = _board.AddTask(new NewTask("Step") { ParentId = project.Id }, Actor.User, T0);
        var personal = _board.Groups[1];

        _board.Move(step.Id, parentId: null, index: null, groupId: personal.Id, Actor.User, T0);

        Assert.Null(step.Parent);
        Assert.Contains(step, _board.Items);
        Assert.Equal(personal.Id, step.GroupId);
        Assert.Empty(project.Children);
    }

    [Fact]
    public void Siblings_can_be_reordered()
    {
        var project = _board.AddTask(new NewTask("Project"), Actor.User, T0);
        var a = _board.AddTask(new NewTask("A") { ParentId = project.Id }, Actor.User, T0);
        var b = _board.AddTask(new NewTask("B") { ParentId = project.Id }, Actor.User, T0);
        var c = _board.AddTask(new NewTask("C") { ParentId = project.Id }, Actor.User, T0);

        _board.Move(c.Id, project.Id, index: 0, groupId: null, Actor.User, T0);

        Assert.Equal([c, a, b], project.Children);
    }

    [Fact]
    public void A_task_cannot_move_into_itself_or_its_descendants()
    {
        var project = _board.AddTask(new NewTask("Project"), Actor.User, T0);
        var step = _board.AddTask(new NewTask("Step") { ParentId = project.Id }, Actor.User, T0);

        Assert.Throws<InvalidOperationException>(() => _board.Move(project.Id, step.Id, null, null, Actor.User, T0));
        Assert.Throws<InvalidOperationException>(() => _board.Move(project.Id, project.Id, null, null, Actor.User, T0));
        Assert.Equal([step], project.Children);
    }

    // ---- Query --------------------------------------------------------------------------

    [Fact]
    public void Queries_combine_text_tags_labels_groups_and_status()
    {
        var personal = _board.Groups[1];
        var release = _board.AddTask(new NewTask("Ship release 2.3") { Tags = ["infra"], Labels = ["Deep work"] }, Actor.User, T0);
        var canary = _board.AddTask(new NewTask("Deploy canary") { ParentId = release.Id }, Actor.User, T0);
        var dentist = _board.AddTask(new NewTask("Dentist") { GroupId = personal.Id, Tags = ["health"] }, Actor.User, T0);
        _board.Complete(dentist.Id, Actor.User, T0);

        Assert.Equal([release, canary], Search("#infra"));
        Assert.Equal([canary], Search("canary #infra"));
        Assert.Equal([release, canary], Search("label:\"deep work\""));
        Assert.Equal([dentist], Search("is:done"));
        Assert.Equal([dentist], Search("group:personal"));
        Assert.Empty(Search("#infra is:done"));
        Assert.Equal([release], Search("RELEASE"));
    }

    [Fact]
    public void Nested_tag_queries_match_child_tags()
    {
        var k8s = _board.AddTask(new NewTask("Upgrade cluster") { Tags = ["infra/k8s"] }, Actor.User, T0);
        _board.AddTask(new NewTask("Other") { Tags = ["infrastructure"] }, Actor.User, T0);

        Assert.Equal([k8s], Search("#infra"));
    }

    [Fact]
    public void Agenda_can_be_filtered_by_a_query()
    {
        var release = _board.AddTask(new NewTask("Ship") { Tags = ["infra"] }, Actor.User, T0);
        _board.AddTask(new NewTask("Groceries"), Actor.User, T0);

        var snapshot = Agenda.Build(_board, T0, filter: TaskQuery.Parse("#infra"));

        Assert.Equal([release.Id], snapshot.Now.Select(e => e.Item.Id));
    }

    private List<WorkItem> Search(string query) => TaskQuery.Parse(query).Apply(_board).ToList();

    [Fact]
    public void A_note_being_typed_is_updated_in_place_without_flooding_the_timeline()
    {
        var task = _board.AddTask(new NewTask("Ship"), Actor.User, T0);
        var note = _board.AddNote(task.Id, "deployed", Actor.User, T0);

        _board.UpdateNote(task.Id, note.Id, "deployed ring 0", Actor.User, T0.AddSeconds(5));
        _board.UpdateNote(task.Id, note.Id, "deployed ring 0 and 1", Actor.User, T0.AddSeconds(9));

        var saved = Assert.Single(task.Notes);
        Assert.Equal((note.Id, "deployed ring 0 and 1", T0), (saved.Id, saved.Text, saved.At));
        Assert.Single(_board.Activity, a => a.Kind == ActivityKind.NoteAdded);
        Assert.DoesNotContain(_board.Activity, a => a.Kind == ActivityKind.Updated);
    }

    [Fact]
    public void Editing_an_old_note_is_logged()
    {
        var task = _board.AddTask(new NewTask("Ship"), Actor.User, T0);
        var note = _board.AddNote(task.Id, "deployed", Actor.User, T0);

        _board.UpdateNote(task.Id, note.Id, "deployed (fixed typo)", Actor.Agent("copilot"), T0.AddHours(2));

        Assert.Contains(_board.Activity, a => a.Kind == ActivityKind.Updated && a.Actor.Kind == ActorKind.Agent);
    }

    [Fact]
    public void Notes_cannot_be_emptied()
    {
        var task = _board.AddTask(new NewTask("Ship"), Actor.User, T0);
        var note = _board.AddNote(task.Id, "deployed", Actor.User, T0);

        Assert.Throws<ArgumentException>(() => _board.UpdateNote(task.Id, note.Id, "  ", Actor.User, T0));
        Assert.Throws<TaskNotFoundException>(() => _board.UpdateNote(task.Id, Guid.NewGuid(), "x", Actor.User, T0));
    }

    [Fact]
    public void Tags_labels_and_attachments_survive_serialization()
    {
        var root = _board.AddTask(new NewTask("Ship") { Tags = ["release"], Labels = ["Deep work"] }, Actor.User, T0);
        var step = _board.AddTask(new NewTask("Step") { ParentId = root.Id }, Actor.User, T0);
        var file = _board.AddAttachment(step.Id, "plan.pdf", "_attachments/abc/plan.pdf", 42, Actor.Agent("copilot"), T0);

        var restored = BoardSerializer.Deserialize(BoardSerializer.Serialize(_board));

        var r = restored.Get(root.Id);
        Assert.Equal(["release"], r.Tags);
        Assert.Equal(["Deep work"], r.Labels);
        var label = Assert.Single(restored.Labels);
        Assert.Equal(_board.Labels[0].Color, label.Color);
        var a = Assert.Single(restored.Get(step.Id).Attachments);
        Assert.Equal((file.Id, "plan.pdf", "_attachments/abc/plan.pdf", 42L, T0, "copilot"), (a.Id, a.FileName, a.Path, a.Size, a.AddedAt, a.AddedBy.Name));
    }
}
