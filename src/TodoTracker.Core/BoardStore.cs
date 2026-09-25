namespace TodoTracker.Core;

/// <summary>
/// Serialized access to the board. All reads and writes run under one lock, so callers must project
/// what they need inside the delegate and not keep references to domain objects afterwards.
/// </summary>
public interface IBoardStore
{
    event EventHandler? Changed;

    Task<T> ReadAsync<T>(Func<TaskBoard, T> read, CancellationToken cancellationToken = default);

    /// <summary>Applies a mutation atomically: if it throws, the board is rolled back to the last saved state.</summary>
    Task<T> UpdateAsync<T>(Func<TaskBoard, T> mutate, CancellationToken cancellationToken = default);
}

public static class BoardStoreExtensions
{
    public static Task UpdateAsync(this IBoardStore store, Action<TaskBoard> mutate, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(mutate);
        return store.UpdateAsync(b =>
        {
            mutate(b);
            return true;
        }, cancellationToken);
    }
}

public abstract class BoardStoreBase : IBoardStore, IDisposable
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private TaskBoard _board;
    private string _lastSaved;
    private bool _disposed;

    protected BoardStoreBase(TaskBoard board)
    {
        _board = board;
        _lastSaved = BoardSerializer.Serialize(board);
    }

    public event EventHandler? Changed;

    public async Task<T> ReadAsync<T>(Func<TaskBoard, T> read, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(read);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return read(_board);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<T> UpdateAsync<T>(Func<TaskBoard, T> mutate, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(mutate);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        T result;
        var changed = false;
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            try
            {
                result = mutate(_board);
                var json = BoardSerializer.Serialize(_board);
                if (json != _lastSaved)
                {
                    Persist(json);
                    _lastSaved = json;
                    changed = true;
                }
            }
            catch
            {
                _board = BoardSerializer.Deserialize(_lastSaved);
                throw;
            }
        }
        finally
        {
            _gate.Release();
        }

        if (changed)
        {
            Changed?.Invoke(this, EventArgs.Empty);
        }

        return result;
    }

    protected abstract void Persist(string json);

    public void Dispose()
    {
        Dispose(disposing: true);
        GC.SuppressFinalize(this);
    }

    protected virtual void Dispose(bool disposing)
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        if (disposing)
        {
            _gate.Dispose();
        }
    }
}

public sealed class InMemoryBoardStore(TaskBoard? board = null) : BoardStoreBase(board ?? new TaskBoard())
{
    protected override void Persist(string json)
    {
    }
}

public sealed class StoreLockedException(string message, Exception inner) : IOException(message, inner);

/// <summary>
/// Local-first JSON store. A process-lifetime exclusive lock file guarantees a single writer (the OS releases
/// it if the process dies). Saves are atomic (write temp file, then replace) and keep a <c>.bak</c> copy.
/// </summary>
public sealed class FileBoardStore : BoardStoreBase
{
    private readonly string _path;
    private readonly FileStream _lock;

    private FileBoardStore(string path, FileStream lockHandle, TaskBoard board)
        : base(board)
    {
        _path = path;
        _lock = lockHandle;
    }

    public string FilePath => _path;

    public static FileBoardStore Open(string path)
    {
        var fullPath = System.IO.Path.GetFullPath(path);
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(fullPath)!);
        FileStream lockHandle;
        try
        {
            lockHandle = new FileStream(fullPath + ".lock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None, 1, FileOptions.DeleteOnClose);
        }
        catch (IOException ex)
        {
            throw new StoreLockedException($"Another Todo Tracker instance is already using {fullPath}.", ex);
        }

        try
        {
            return new FileBoardStore(fullPath, lockHandle, Load(fullPath));
        }
        catch
        {
            lockHandle.Dispose();
            throw;
        }
    }

    private static TaskBoard Load(string path)
    {
        if (!File.Exists(path))
        {
            return new TaskBoard();
        }

        try
        {
            return BoardSerializer.Deserialize(File.ReadAllText(path));
        }
        catch (InvalidDataException) when (File.Exists(path + ".bak"))
        {
            var board = BoardSerializer.Deserialize(File.ReadAllText(path + ".bak"));
            File.Move(path, $"{path}.corrupt-{DateTime.UtcNow:yyyyMMddHHmmss}");
            File.Copy(path + ".bak", path);
            return board;
        }
    }

    protected override void Persist(string json)
    {
        var temp = _path + ".tmp";
        File.WriteAllText(temp, json);
        if (File.Exists(_path))
        {
            File.Replace(temp, _path, _path + ".bak", ignoreMetadataErrors: true);
        }
        else
        {
            File.Move(temp, _path);
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _lock.Dispose();
        }

        base.Dispose(disposing);
    }
}
