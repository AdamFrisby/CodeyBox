namespace CodeyBox.Orchestrator;

/// <summary>
/// Shared in-process write gate for a SQLite database file. SQLite WAL still
/// permits only one writer at a time, so all stores that point at the same file
/// must coordinate before issuing write commands on their separate connections.
/// </summary>
internal sealed class SqliteDatabaseWriteGate : IDisposable
{
    private static readonly object Sync = new();
    private static readonly Dictionary<string, Entry> Entries = new(StringComparer.Ordinal);

    private readonly string _path;
    private Entry? _entry;

    private SqliteDatabaseWriteGate(string path, Entry entry)
    {
        _path = path;
        _entry = entry;
    }

    public static SqliteDatabaseWriteGate ForPath(string path)
    {
        var fullPath = Path.GetFullPath(path);
        lock (Sync)
        {
            if (!Entries.TryGetValue(fullPath, out var entry))
            {
                entry = new Entry();
                Entries.Add(fullPath, entry);
            }

            entry.RefCount++;
            return new SqliteDatabaseWriteGate(fullPath, entry);
        }
    }

    public void Wait(CancellationToken ct = default) => Current.Semaphore.Wait(ct);

    public Task WaitAsync(CancellationToken ct = default) => Current.Semaphore.WaitAsync(ct);

    public void Release() => Current.Semaphore.Release();

    public void Dispose()
    {
        Entry? disposeEntry = null;

        lock (Sync)
        {
            var entry = _entry;
            if (entry is null)
                return;

            _entry = null;
            entry.RefCount--;
            if (entry.RefCount == 0 && Entries.TryGetValue(_path, out var current) && ReferenceEquals(current, entry))
            {
                Entries.Remove(_path);
                disposeEntry = entry;
            }
        }

        disposeEntry?.Semaphore.Dispose();
    }

    private Entry Current => _entry ?? throw new ObjectDisposedException(nameof(SqliteDatabaseWriteGate));

    private sealed class Entry
    {
        public readonly SemaphoreSlim Semaphore = new(1, 1);
        public int RefCount;
    }
}
