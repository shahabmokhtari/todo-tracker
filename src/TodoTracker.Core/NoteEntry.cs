namespace TodoTracker.Core;

public sealed record NoteEntry(DateTimeOffset CreatedAt, string Text);
