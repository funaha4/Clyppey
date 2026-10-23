using System.Text.Json;

namespace Clyppey.Core.Storage;

public sealed record ClipEntry(
    string Path,
    string Mode,
    double DurationSeconds,
    long Bytes,
    DateTimeOffset CreatedAt)
{
    public string FileName => System.IO.Path.GetFileName(Path);

    public string SizeText => Bytes >= 1_048_576
        ? $"{Bytes / 1048576.0:F1} MB"
        : $"{Bytes / 1024.0:F0} KB";

    public string ModeLabel => Mode switch
    {
        "replay" => "インスタントリプレイ",
        "record" => "録画",
        _ => Mode
    };
}

/// <summary>Keeps a small history of saved clips so the UI can show and reopen them.</summary>
public sealed class ClipLibrary
{
    private const int MaxEntries = 200;
    private readonly List<ClipEntry> _entries = new();
    private readonly object _gate = new();

    public IReadOnlyList<ClipEntry> All
    {
        get
        {
            lock (_gate)
            {
                return _entries.ToArray();
            }
        }
    }

    public void Load()
    {
        try
        {
            if (!File.Exists(AppPaths.ClipLibraryFile))
            {
                return;
            }

            var json = File.ReadAllText(AppPaths.ClipLibraryFile);
            var loaded = JsonSerializer.Deserialize<List<ClipEntry>>(json);
            if (loaded is null)
            {
                return;
            }

            lock (_gate)
            {
                _entries.Clear();
                // Drop entries whose file has since been deleted or moved.
                _entries.AddRange(loaded.Where(e => File.Exists(e.Path)));
            }
        }
        catch (Exception ex)
        {
            Log.Warn($"クリップ履歴の読み込みに失敗: {ex.Message}");
        }
    }

    public void Add(ClipEntry entry)
    {
        lock (_gate)
        {
            _entries.RemoveAll(e => string.Equals(e.Path, entry.Path, StringComparison.OrdinalIgnoreCase));
            _entries.Insert(0, entry);
            if (_entries.Count > MaxEntries)
            {
                _entries.RemoveRange(MaxEntries, _entries.Count - MaxEntries);
            }
        }

        Save();
    }

    public void Remove(string path)
    {
        lock (_gate)
        {
            _entries.RemoveAll(e => string.Equals(e.Path, path, StringComparison.OrdinalIgnoreCase));
        }

        Save();
    }

    private void Save()
    {
        try
        {
            AppPaths.EnsureDirectories();
            ClipEntry[] snapshot;
            lock (_gate)
            {
                snapshot = _entries.ToArray();
            }

            var json = JsonSerializer.Serialize(snapshot, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(AppPaths.ClipLibraryFile, json);
        }
        catch (Exception ex)
        {
            Log.Warn($"クリップ履歴の保存に失敗: {ex.Message}");
        }
    }
}
