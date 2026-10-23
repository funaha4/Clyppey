using Clyppey.Core.Ffmpeg;

namespace Clyppey.Core.Capture;

/// <summary>Grabs a single JPEG frame from a display output (used by the monitor picker).</summary>
public static class CapturePreview
{
    public static Task<byte[]?> GrabAsync(
        string ffmpegPath, int outputIndex, CancellationToken cancellationToken = default)
    {
        return ProcessRunner.RunForBytesAsync(
            ffmpegPath,
            FfmpegCommandBuilder.BuildSnapshotArgs(outputIndex),
            timeoutMs: 15_000,
            cancellationToken);
    }
}
