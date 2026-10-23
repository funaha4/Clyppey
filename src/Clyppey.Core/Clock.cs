using System.Diagnostics;

namespace Clyppey.Core;

/// <summary>Monotonic clock used to align video fragments with audio samples.</summary>
public static class Clock
{
    private static readonly double Frequency = Stopwatch.Frequency;

    /// <summary>Seconds since an arbitrary fixed point (boot). Monotonic.</summary>
    public static double Now => Stopwatch.GetTimestamp() / Frequency;

    public static long Ticks => Stopwatch.GetTimestamp();
}
