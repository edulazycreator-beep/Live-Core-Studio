using System.Collections.Concurrent;
using LiveCoreStudio.Core.Diagnostics;

namespace LiveCoreStudio.Infrastructure.Diagnostics;

/// <summary>
/// Thread-safe log that appends to a daily file and keeps the most recent
/// entries in memory for the diagnostics view. File writes happen on a
/// background task so the UI thread never blocks on disk I/O.
/// </summary>
public sealed class FileAppLog : IAppLog, IAsyncDisposable
{
    private readonly string _directory;
    private readonly int _capacity;
    private readonly ConcurrentQueue<AppLogEntry> _recent = new();
    private readonly BlockingCollection<AppLogEntry> _pending = new(new ConcurrentQueue<AppLogEntry>(), 10_000);
    private readonly Task _writer;

    public FileAppLog(string directory, int recentCapacity = 2000)
    {
        _directory = directory;
        _capacity = recentCapacity;
        Directory.CreateDirectory(directory);
        _writer = Task.Factory.StartNew(WriterLoop, TaskCreationOptions.LongRunning);
    }

    public event Action<AppLogEntry>? EntryWritten;

    public IReadOnlyCollection<AppLogEntry> Recent => _recent.ToArray();

    public void Write(AppLogLevel level, string source, string message)
    {
        var entry = new AppLogEntry(DateTimeOffset.Now, level, source, message);
        _recent.Enqueue(entry);
        while (_recent.Count > _capacity && _recent.TryDequeue(out _)) { }
        if (!_pending.IsAddingCompleted) _pending.TryAdd(entry);  // drop rather than block if disk is stuck
        EntryWritten?.Invoke(entry);
    }

    private void WriterLoop()
    {
        StreamWriter? writer = null;
        DateOnly day = default;
        try
        {
            foreach (var e in _pending.GetConsumingEnumerable())
            {
                var today = DateOnly.FromDateTime(e.Timestamp.LocalDateTime);
                if (writer is null || today != day)
                {
                    writer?.Dispose();
                    day = today;
                    writer = new StreamWriter(Path.Combine(_directory, $"livecore-{day:yyyyMMdd}.log"), append: true) { AutoFlush = true };
                }
                writer.WriteLine($"{e.Timestamp:yyyy-MM-dd HH:mm:ss.fff} [{e.Level,-7}] {e.Source}: {e.Message}");
            }
        }
        catch (IOException)
        {
            // Logging must never take the application down.
        }
        finally
        {
            writer?.Dispose();
        }
    }

    public async ValueTask DisposeAsync()
    {
        _pending.CompleteAdding();
        await _writer.ConfigureAwait(false);
        _pending.Dispose();
    }
}
