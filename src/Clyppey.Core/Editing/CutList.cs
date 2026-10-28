namespace Clyppey.Core.Editing;

public readonly record struct TimeRange(double Start, double End)
{
    public double Duration => End - Start;

    public bool IsEmpty => End - Start < 0.01;

    public static TimeRange Normalized(double a, double b) => a <= b ? new(a, b) : new(b, a);
}

/// <summary>
/// The ranges to delete, kept as "removed" rather than "kept" because that is how the user
/// thinks about it: mark the dead time between kills, everything else survives.
/// </summary>
public sealed class CutList
{
    private const double MergeGapSeconds = 0.05;
    private readonly List<TimeRange> _removed = new();

    public IReadOnlyList<TimeRange> Removed
    {
        get
        {
            lock (_removed)
            {
                return _removed.ToArray();
            }
        }
    }

    public int Count
    {
        get
        {
            lock (_removed)
            {
                return _removed.Count;
            }
        }
    }

    /// <summary>Adds a range, merging anything that overlaps or nearly touches it.</summary>
    public void Add(TimeRange range)
    {
        var r = TimeRange.Normalized(
            Math.Max(0, range.Start),
            Math.Max(0, range.End));
        if (r.IsEmpty)
        {
            return;
        }

        lock (_removed)
        {
            var merged = new List<TimeRange>();
            var start = r.Start;
            var end = r.End;

            foreach (var existing in _removed)
            {
                if (existing.End < start - MergeGapSeconds || existing.Start > end + MergeGapSeconds)
                {
                    merged.Add(existing);
                    continue;
                }

                start = Math.Min(start, existing.Start);
                end = Math.Max(end, existing.End);
            }

            merged.Add(new TimeRange(start, end));
            merged.Sort((a, b) => a.Start.CompareTo(b.Start));

            _removed.Clear();
            _removed.AddRange(merged);
        }
    }

    public bool RemoveLast()
    {
        lock (_removed)
        {
            if (_removed.Count == 0)
            {
                return false;
            }

            _removed.RemoveAt(_removed.Count - 1);
            return true;
        }
    }

    public void Clear()
    {
        lock (_removed)
        {
            _removed.Clear();
        }
    }

    /// <summary>Complement of <see cref="Removed"/> inside the clip.</summary>
    public IReadOnlyList<TimeRange> Keeps(double duration)
    {
        if (duration <= 0)
        {
            return Array.Empty<TimeRange>();
        }

        TimeRange[] removed;
        lock (_removed)
        {
            removed = _removed.ToArray();
        }

        var keeps = new List<TimeRange>();
        var cursor = 0.0;
        foreach (var r in removed)
        {
            var start = Math.Clamp(r.Start, 0, duration);
            var end = Math.Clamp(r.End, 0, duration);
            if (start > cursor + 0.005)
            {
                keeps.Add(new TimeRange(cursor, start));
            }

            cursor = Math.Max(cursor, end);
        }

        if (duration > cursor + 0.005)
        {
            keeps.Add(new TimeRange(cursor, duration));
        }

        return keeps;
    }

    public double KeptSeconds(double duration)
    {
        var total = 0.0;
        foreach (var keep in Keeps(duration))
        {
            total += keep.Duration;
        }

        return total;
    }
}
