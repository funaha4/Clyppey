using System.Collections.Concurrent;

namespace Clyppey.Core.Mp4;

/// <summary>
/// Holds the replay buffer on disk instead of in RAM.
///
/// NVIDIA's Instant Replay keeps its buffer off the managed heap, which is why it sits at a few
/// hundred megabytes while a long replay would otherwise cost gigabytes. Each encoded fragment is
/// written to its own small file and deleted when it falls out of the ring; saving streams the
/// files back out, so RAM use stays flat no matter how long the buffer is.
///
/// Fragments are written on a background task: writing 3 MB synchronously inside the stdout read
/// loop throttled the capture pipeline and showed up as occasional held frames.
/// </summary>
public sealed class DiskFragmentStore : IDisposable
{
    private const long MaxPendingBytes = 256L * 1024 * 1024;

    private readonly string _directory;
    private readonly BlockingCollection<(string Path, byte[] Data)> _queue = new();
    private readonly ConcurrentDictionary<string, byte> _pending = new();
    private readonly ConcurrentDictionary<string, byte> _deferredDeletes = new();
    private readonly Task _writer;
    private long _bytesWritten;
    private long _pendingBytes;
    private int _sequence;
    private bool _disposed;

    public DiskFragmentStore(string? parentDirectory = null)
    {
        var root = parentDirectory ?? Core.AppPaths.TempDir;
        _directory = Path.Combine(root, "ring_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_directory);
        _writer = Task.Run(WriteLoop);
    }

    public string Folder => _directory;

    public long BytesOnDisk => Interlocked.Read(ref _bytesWritten);

    /// <summary>Queues one fragment and returns the file it will land in.</summary>
    public string Write(byte[] data)
    {
        var path = Path.Combine(_directory, $"f{Interlocked.Increment(ref _sequence):D6}.bin");
        Interlocked.Add(ref _bytesWritten, data.Length);

        // If the disk falls behind, apply back-pressure instead of hoarding the buffer in RAM.
        if (Interlocked.Read(ref _pendingBytes) + data.Length > MaxPendingBytes)
        {
            WriteFile(path, data);
            return path;
        }

        _pending[path] = 0;
        Interlocked.Add(ref _pendingBytes, data.Length);
        _queue.Add((path, data));
        return path;
    }

    private void WriteLoop()
    {
        foreach (var (path, data) in _queue.GetConsumingEnumerable())
        {
            WriteFile(path, data);
            Interlocked.Add(ref _pendingBytes, -data.Length);
            _pending.TryRemove(path, out _);

            if (_deferredDeletes.TryRemove(path, out _))
            {
                Delete(path);
            }
        }
    }

    private void WriteFile(string path, byte[] data)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.Read,
                64 * 1024, FileOptions.SequentialScan);
            stream.Write(data);
        }
        catch (Exception ex)
        {
            Log.Debug($"リングファイルを書き込めません: {path} ({ex.Message})");
        }
    }

    /// <summary>Blocks until every queued fragment is on disk (called before building a clip).</summary>
    public void WaitForPending(int timeoutMilliseconds = 8000)
    {
        var deadline = Environment.TickCount64 + timeoutMilliseconds;
        while (!_pending.IsEmpty && Environment.TickCount64 < deadline)
        {
            Thread.Sleep(2);
        }
    }

    public void Delete(string? path)
    {
        if (string.IsNullOrEmpty(path))
        {
            return;
        }

        // Still queued: let the writer finish, then remove it.
        if (_pending.ContainsKey(path))
        {
            _deferredDeletes[path] = 0;
            return;
        }

        try
        {
            var info = new FileInfo(path);
            var length = info.Exists ? info.Length : 0;
            info.Delete();
            Interlocked.Add(ref _bytesWritten, -length);
        }
        catch (Exception ex)
        {
            Log.Debug($"リングファイルを削除できません: {path} ({ex.Message})");
        }
    }

    /// <summary>
    /// Removes ring folders left behind by a previous run that never shut down cleanly (crash,
    /// power loss, task manager). A live session writes every second, so anything older than a
    /// few minutes is dead weight.
    /// </summary>
    public static void RemoveStale(string? parentDirectory = null, TimeSpan? minimumAge = null)
    {
        var root = parentDirectory ?? Core.AppPaths.TempDir;
        var age = minimumAge ?? TimeSpan.FromMinutes(10);
        try
        {
            if (!System.IO.Directory.Exists(root))
            {
                return;
            }

            var removed = 0;
            long freed = 0;
            foreach (var directory in System.IO.Directory.GetDirectories(root, "ring_*"))
            {
                try
                {
                    var last = System.IO.Directory.GetLastWriteTimeUtc(directory);
                    if (DateTime.UtcNow - last < age)
                    {
                        continue; // possibly a live session
                    }

                    var size = System.IO.Directory.GetFiles(directory)
                        .Sum(f => new FileInfo(f).Length);
                    System.IO.Directory.Delete(directory, recursive: true);
                    removed++;
                    freed += size;
                }
                catch (Exception ex)
                {
                    Log.Debug($"古いリングを削除できません: {directory} ({ex.Message})");
                }
            }

            if (removed > 0)
            {
                Log.Warn($"前回の残留バッファを削除しました: {removed} 件 / {freed / 1024.0 / 1024.0:F0} MB");
            }
        }
        catch (Exception ex)
        {
            Log.Debug($"残留リングの掃除に失敗: {ex.Message}");
        }
    }

    /// <summary>Bytes per second the buffer will write at this bitrate.</summary>
    public static long EstimateBytesPerSecond(int bitrateMbps) => bitrateMbps * 1_000_000L / 8;

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        try
        {
            _queue.CompleteAdding();
            _writer.Wait(TimeSpan.FromSeconds(3));
        }
        catch (Exception ex)
        {
            Log.Debug($"リング書き込みの終了に失敗: {ex.Message}");
        }

        try
        {
            if (System.IO.Directory.Exists(_directory))
            {
                System.IO.Directory.Delete(_directory, recursive: true);
            }
        }
        catch (Exception ex)
        {
            Log.Debug($"リングフォルダを削除できません: {_directory} ({ex.Message})");
        }

        _queue.Dispose();
    }
}
