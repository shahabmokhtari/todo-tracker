namespace TodoTracker.Core.Vault;

/// <summary>Attachments (<c>_attachments/&lt;task&gt;/</c>) and rich HTML versions (<c>Task.html</c> next to <c>Task.md</c>).</summary>
public sealed partial class VaultBoardStore
{
    private readonly Dictionary<string, string> _wikiLinkCache = new(StringComparer.OrdinalIgnoreCase);

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

        // Claim a unique file atomically (CreateNew), so two uploads named the same never touch each other's file.
        FileStream? file = null;
        var full = string.Empty;
        var unique = stem;
        for (var n = 1; file is null; n++)
        {
            unique = n == 1 ? stem : $"{stem} {n}";
            full = Path.Combine(dir, unique + extension);
            try
            {
                file = new FileStream(full, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, useAsync: true);
            }
            catch (IOException) when (File.Exists(full) && n < 1000)
            {
                // Taken: try the next name.
            }
        }

        long size = 0;
        try
        {
            await using (file)
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

    /// <summary>The attachment an embed (<c>![[name]]</c>) means: the task's, else its nearest parent's, by file name.</summary>
    public async Task<(string FullPath, string FileName)?> FindEmbedAsync(Guid itemId, string name, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(name);
        var wanted = name.Replace('\\', '/').Split('/')[^1];
        var attachment = await ReadAsync(
            b =>
            {
                for (var item = b.Get(itemId); item is not null; item = item.Parent)
                {
                    if (item.Attachments.FirstOrDefault(a => string.Equals(a.FileName, wanted, StringComparison.OrdinalIgnoreCase)
                        || string.Equals(Path.GetFileName(a.Path), wanted, StringComparison.OrdinalIgnoreCase)) is { } found)
                    {
                        return found;
                    }
                }

                return null;
            },
            cancellationToken).ConfigureAwait(false);
        return attachment is null ? null : (ResolveAttachment(attachment.Path), attachment.FileName);
    }

    /// <summary>The attachment's file on disk. Links that point outside the vault are refused.</summary>
    public async Task<(string FullPath, string FileName)> GetAttachmentFileAsync(Guid itemId, Guid attachmentId, CancellationToken cancellationToken = default)
    {
        var attachment = await ReadAsync(b => FindAttachment(b, itemId, attachmentId), cancellationToken).ConfigureAwait(false);
        return (ResolveAttachment(attachment.Path), attachment.FileName);
    }

    public Task RemoveAttachmentAsync(Guid itemId, Guid attachmentId, Actor actor, CancellationToken cancellationToken = default) =>
        UpdateAsync(
            b =>
            {
                var a = FindAttachment(b, itemId, attachmentId);
                b.RemoveAttachment(itemId, attachmentId, actor, Now);
                return (a.Path, StillUsed: b.AllItems().Any(i => i.Attachments.Any(x => x.Path == a.Path)));
            },
            removed =>
            {
                // Only a file the app stored under _attachments, and that no other task links, goes to the trash.
                var full = VaultFiles.Full(_root, removed.Path);
                var store = Path.Combine(_root, VaultFiles.AttachmentsFolder) + Path.DirectorySeparatorChar;
                if (!removed.StillUsed && full.StartsWith(store, StringComparison.OrdinalIgnoreCase) && File.Exists(full))
                {
                    File.Move(full, TrashPath(Path.GetFileName(full)));
                }
            },
            cancellationToken);

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
                RichPath(b, rootId);
                b.NoteRichContentChanged(rootId, html is null, actor, Now);
                return rootId;
            },
            id =>
            {
                // After the save, so the task has its file (and path) before the HTML lands next to it.
                var full = VaultFiles.Full(_root, Path.ChangeExtension(_caches.Paths[id], ".html"));
                if (html is not null)
                {
                    VaultFiles.WriteAtomic(full, html);
                }
                else if (File.Exists(full))
                {
                    File.Move(full, TrashPath(Path.GetFileName(full)));
                }
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

        return Path.ChangeExtension(_caches.Paths.GetValueOrDefault(rootId) ?? throw TaskNotFoundException.ForTask(rootId), ".html");
    }

    private static Attachment FindAttachment(TaskBoard board, Guid itemId, Guid attachmentId) =>
        board.Get(itemId).Attachments.FirstOrDefault(a => a.Id == attachmentId) ?? throw new TaskNotFoundException($"Attachment {attachmentId} was not found.");

    private static bool IsInside(string path, string root) =>
        path.StartsWith(root.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);

    /// <summary>The Obsidian vault the tasks folder is part of (a parent folder with <c>.obsidian</c>), if any.</summary>
    private string? EnclosingObsidianVault()
    {
        for (var dir = Directory.GetParent(_root); dir is not null; dir = dir.Parent)
        {
            if (Directory.Exists(Path.Combine(dir.FullName, ".obsidian")))
            {
                return dir.FullName;
            }
        }

        return null;
    }

    private string ResolveAttachment(string relative)
    {
        var full = VaultFiles.Full(_root, relative);
        var obsidian = EnclosingObsidianVault();
        if (!IsInside(full, _root) && !(obsidian is not null && IsInside(full, obsidian)))
        {
            throw new UnauthorizedAccessException("That attachment points outside the vault.");
        }

        if (File.Exists(full) || relative.Contains('/', StringComparison.Ordinal) || relative.IndexOfAny(['*', '?']) >= 0)
        {
            return full;
        }

        // An Obsidian wiki link ([[file.png]]) names a file anywhere in the vault (hidden folders excluded).
        lock (_wikiLinkCache)
        {
            if (_wikiLinkCache.TryGetValue(relative, out var cached) && File.Exists(cached))
            {
                return cached;
            }

            var options = new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, MatchCasing = MatchCasing.CaseInsensitive };
            string? Find(string root) => Directory.EnumerateFiles(root, relative, options)
                .FirstOrDefault(f => !VaultFiles.Relative(root, f).Split('/').Any(s => s.StartsWith('.')));

            // When the tasks folder sits inside an Obsidian vault, Obsidian keeps pasted images elsewhere in that vault.
            var match = Find(_root) ?? (obsidian is not null ? Find(obsidian) : null);
            if (match is not null)
            {
                _wikiLinkCache[relative] = match;
            }

            return match ?? full;
        }
    }
}
