namespace TodoTracker.Core.Vault;

/// <summary>Attachments (<c>_attachments/&lt;task&gt;/</c>) and rich HTML versions (<c>Task.html</c> next to <c>Task.md</c>).</summary>
public sealed partial class VaultBoardStore
{
    public async Task<Attachment> AddAttachmentAsync(Guid itemId, string fileName, Stream content, Actor actor, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(content);
        var name = TaskBoard.ValidateFileName(fileName);
        var rootId = await ReadAsync(b => b.Get(itemId).Root.Id, cancellationToken).ConfigureAwait(false);
        var folder = $"{VaultFiles.AttachmentsFolder}/{rootId.ToString("N")[..8]}";
        var dir = VaultFiles.Full(_root, folder);
        Directory.CreateDirectory(dir);
        var stem = VaultFiles.SafeName(Path.GetFileNameWithoutExtension(name), 100);
        var extension = Path.GetExtension(name) is { Length: > 1 } ext ? "." + VaultFiles.SafeName(ext[1..], 20) : string.Empty;
        var unique = VaultFiles.UniqueStem(stem, s => File.Exists(Path.Combine(dir, s + extension)));
        var full = Path.Combine(dir, unique + extension);
        long size = 0;
        try
        {
            await using (var file = new FileStream(full, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, useAsync: true))
            {
                var buffer = new byte[81920];
                int read;
                while ((read = await content.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
                {
                    size += read;
                    if (size > _options.MaxAttachmentBytes)
                    {
                        throw new ArgumentException($"Attachments can be at most {_options.MaxAttachmentBytes / (1024 * 1024)} MB.", nameof(content));
                    }

                    await file.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                }
            }

            var rel = $"{folder}/{unique}{extension}";
            return await UpdateAsync(b => b.AddAttachment(itemId, name, rel, size, actor, Now), cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            File.Delete(full);
            throw;
        }
    }

    /// <summary>The attachment's file on disk. Links that point outside the vault are refused.</summary>
    public async Task<(string FullPath, string FileName)> GetAttachmentFileAsync(Guid itemId, Guid attachmentId, CancellationToken cancellationToken = default)
    {
        var attachment = await ReadAsync(b => FindAttachment(b, itemId, attachmentId), cancellationToken).ConfigureAwait(false);
        return (ResolveAttachment(attachment.Path), attachment.FileName);
    }

    public async Task RemoveAttachmentAsync(Guid itemId, Guid attachmentId, Actor actor, CancellationToken cancellationToken = default)
    {
        var attachment = await UpdateAsync(
            b =>
            {
                var a = FindAttachment(b, itemId, attachmentId);
                b.RemoveAttachment(itemId, attachmentId, actor, Now);
                return a;
            },
            cancellationToken).ConfigureAwait(false);

        // Only files the app stored are removed (to the trash); files linked from elsewhere in the vault stay.
        if (attachment.Path.StartsWith(VaultFiles.AttachmentsFolder + "/", StringComparison.OrdinalIgnoreCase))
        {
            var full = ResolveAttachment(attachment.Path);
            if (File.Exists(full))
            {
                File.Move(full, TrashPath(Path.GetFileName(full)));
            }
        }
    }

    /// <summary>The task's rich (HTML) version, or null. Only top-level tasks have one.</summary>
    public async Task<string?> ReadRichAsync(Guid rootId, CancellationToken cancellationToken = default)
    {
        var rel = await ReadAsync(b => RichPath(b, rootId), cancellationToken).ConfigureAwait(false);
        var full = VaultFiles.Full(_root, rel);
        return File.Exists(full) ? await File.ReadAllTextAsync(full, cancellationToken).ConfigureAwait(false) : null;
    }

    /// <summary>Saves (or with null, removes) the rich HTML version next to the task's markdown file.</summary>
    public Task WriteRichAsync(Guid rootId, string? html, Actor actor, CancellationToken cancellationToken = default)
    {
        if (html is not null && System.Text.Encoding.UTF8.GetByteCount(html) > _options.MaxRichBytes)
        {
            throw new ArgumentException($"The rich version can be at most {_options.MaxRichBytes / (1024 * 1024)} MB.", nameof(html));
        }

        return UpdateAsync(
            b =>
            {
                var full = VaultFiles.Full(_root, RichPath(b, rootId));
                if (html is null)
                {
                    if (File.Exists(full))
                    {
                        File.Move(full, TrashPath(Path.GetFileName(full)));
                    }
                }
                else
                {
                    VaultFiles.WriteAtomic(full, html);
                }

                b.NoteRichContentChanged(rootId, html is null, actor, Now);
                return true;
            },
            cancellationToken);
    }

    private string RichPath(TaskBoard board, Guid rootId)
    {
        var item = board.Get(rootId);
        if (item.Parent is not null)
        {
            throw new InvalidOperationException("Only top-level tasks have a rich version; open the task it belongs to.");
        }

        lock (_paths)
        {
            return Path.ChangeExtension(_paths.GetValueOrDefault(rootId) ?? throw TaskNotFoundException.ForTask(rootId), ".html");
        }
    }

    private static Attachment FindAttachment(TaskBoard board, Guid itemId, Guid attachmentId) =>
        board.Get(itemId).Attachments.FirstOrDefault(a => a.Id == attachmentId) ?? throw new TaskNotFoundException($"Attachment {attachmentId} was not found.");

    private string ResolveAttachment(string relative)
    {
        var full = VaultFiles.Full(_root, relative);
        if (!full.StartsWith(_root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
        {
            throw new UnauthorizedAccessException("That attachment points outside the vault.");
        }

        if (!File.Exists(full) && !relative.Contains('/', StringComparison.Ordinal))
        {
            // An Obsidian wiki link ([[file.png]]) names a file anywhere in the vault.
            var match = Directory.EnumerateFiles(_root, relative, SearchOption.AllDirectories)
                .FirstOrDefault(f => !VaultFiles.Relative(_root, f).StartsWith(VaultFiles.MetaFolder, StringComparison.Ordinal));
            if (match is not null)
            {
                return match;
            }
        }

        return full;
    }
}
