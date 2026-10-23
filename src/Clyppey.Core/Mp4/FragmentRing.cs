namespace Clyppey.Core.Mp4;

/// <summary>A keyframe-aligned chunk held in RAM.</summary>
public sealed class VideoFragment
{
    /// <summary>Encoded bytes, empty when the fragment lives on disk.</summary>
    public byte[] Data { get; init; } = Array.Empty<byte>();

    /// <summary>Set when this fragment is backed by a ring file instead of RAM.</summary>
    public string? FilePath { get; init; }

    /// <summary>Encoded size in bytes (valid for both storage modes).</summary>
    public long SizeBytes { get; init; }

    /// <summary>Presentation start on the video timeline, in seconds.</summary>
    public double Start { get; init; }

    /// <summary>Presentation duration in seconds (refined when the next fragment arrives).</summary>
    public double Duration { get; set; }

    public int SampleCount { get; init; }

    public double ArrivalSeconds { get; init; }

    public double End => Start + Duration;

    public long Bytes => SizeBytes > 0 ? SizeBytes : Data.Length;
}

/// <summary>An immutable view of what a save operation will write.</summary>
public sealed record RingSnapshot(
    byte[]? InitSegment,
    IReadOnlyList<VideoFragment> Fragments,
    double StartSeconds,
    double EndSeconds)
{
    public double Duration => Math.Max(0, EndSeconds - StartSeconds);

    public long TotalBytes => (InitSegment?.Length ?? 0) + Fragments.Sum(f => f.Bytes);

    public bool IsEmpty => Fragments.Count == 0;

    /// <summary>
    /// Wall-clock time the clip starts at. A fragment is delivered once its last frame is
    /// encoded, so its arrival time is its END - the start is one fragment duration earlier.
    /// </summary>
    public double WallStart =>
        Fragments.Count == 0 ? 0 : Fragments[0].ArrivalSeconds - Fragments[0].Duration;

    /// <summary>Wall-clock time the clip ends at (= arrival of the newest fragment).</summary>
    public double WallEnd => Fragments.Count == 0 ? 0 : Fragments[^1].ArrivalSeconds;
}

/// <summary>
/// The replay buffer: the init segment plus the most recent keyframe-aligned fragments.
/// Nothing is written to disk while gaming; eviction is by time and by a hard RAM cap.
/// </summary>
public sealed class FragmentRing
{
    private readonly LinkedList<VideoFragment> _fragments = new();
    private long _bytes;

    public byte[]? InitSegment { get; private set; }

    /// <summary>How many seconds to retain (the replay length).</summary>
    public double MaxSeconds { get; set; } = 60;

    /// <summary>Hard cap so a misconfiguration can never eat all system memory.</summary>
    public long MaxBytes { get; set; } = 3072L * 1024 * 1024;

    public int Count => _fragments.Count;

    public long Bytes => _bytes + (InitSegment?.Length ?? 0);

    public double BufferedSeconds =>
        _fragments.Count == 0 ? 0 : Math.Max(0, EndSeconds - _fragments.First!.Value.Start);

    public double StartSeconds => _fragments.Count == 0 ? 0 : _fragments.First!.Value.Start;

    public double EndSeconds => _fragments.Count == 0 ? 0 : _fragments.Last!.Value.End;

    /// <summary>
    /// Wall-clock time where the newest *buffered* content ends. ffmpeg holds a couple of
    /// fragments before writing them, so this always trails "now" by a second or two - which is
    /// why a save has to wait for the tail to arrive.
    /// </summary>
    public double NewestContentEndWall =>
        _fragments.Count == 0 ? 0 : _fragments.Last!.Value.ArrivalSeconds - _fragments.Last!.Value.Duration;

    /// <summary>Fragments dropped because of the RAM cap (tells the user their buffer shrank).</summary>
    public long EvictedForMemory { get; private set; }

    public void SetInitSegment(byte[] init)
    {
        InitSegment = init;
        Trim();
    }

    public void Add(VideoFragment fragment)
    {
        // Refine the previous fragment's duration now that we know where the next one starts.
        if (_fragments.Last is { } last)
        {
            var delta = fragment.Start - last.Value.Start;
            if (delta > 0)
            {
                last.Value.Duration = delta;
            }
        }

        _fragments.AddLast(fragment);
        _bytes += fragment.Bytes;
        Trim();
    }

    /// <summary>Raised for every fragment leaving the ring (disk mode deletes its file).</summary>
    public event Action<VideoFragment>? Evicted;

    public void Clear()
    {
        foreach (var fragment in _fragments)
        {
            Evicted?.Invoke(fragment);
        }

        _fragments.Clear();
        _bytes = 0;
        EvictedForMemory = 0;
    }

    /// <summary>
    /// Takes the last <paramref name="seconds"/> of buffered video. The clip always begins on a
    /// keyframe (fragment boundary) so it decodes standalone.
    /// </summary>
    public RingSnapshot Take(double seconds)
    {
        if (_fragments.Count == 0)
        {
            return new RingSnapshot(InitSegment, Array.Empty<VideoFragment>(), 0, 0);
        }

        var list = _fragments.ToList();
        var end = EndSeconds;
        var target = Math.Max(list[0].Start, end - Math.Max(0.5, seconds));

        // Walk forward to the last fragment that still starts at or before the target time:
        // that fragment covers the requested start point.
        var index = 0;
        while (index + 1 < list.Count && list[index + 1].Start <= target)
        {
            index++;
        }

        var kept = list.GetRange(index, list.Count - index);
        return new RingSnapshot(InitSegment, kept, kept[0].Start, end);
    }

    private void Trim()
    {
        while (_fragments.Count > 1)
        {
            var overTime = EndSeconds - _fragments.First!.Value.Start > MaxSeconds;
            var overMemory = _bytes > MaxBytes;
            if (!overTime && !overMemory)
            {
                break;
            }

            if (overMemory)
            {
                EvictedForMemory++;
            }

            var removed = _fragments.First!.Value;
            _bytes -= removed.Bytes;
            _fragments.RemoveFirst();
            Evicted?.Invoke(removed);
        }
    }
}
