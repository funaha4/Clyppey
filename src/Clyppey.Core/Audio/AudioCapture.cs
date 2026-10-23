using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace Clyppey.Core.Audio;

/// <summary>A chunk of captured PCM, tagged with its absolute sample index.</summary>
internal sealed class AudioChunk
{
    public required byte[] Data { get; init; }

    public required long StartSample { get; init; }

    public long SampleCount { get; init; }
}

public enum AudioKind
{
    /// <summary>What the speakers play (WASAPI loopback).</summary>
    Desktop,

    /// <summary>A microphone / capture device.</summary>
    Microphone
}

/// <summary>
/// Captures one audio source into a ring buffer of PCM.
///
/// Desktop audio uses WASAPI loopback - the same mechanism OBS and the NVIDIA overlay use, and
/// the reason no "Stereo Mix" device has to be enabled. Microphones use a normal WASAPI capture
/// device. Either buffer is turned into a WAV only at save time, so gameplay never waits on disk.
/// </summary>
public sealed class AudioCapture : IDisposable
{
    private readonly object _gate = new();
    private readonly LinkedList<AudioChunk> _chunks = new();
    private readonly AudioKind _kind;

    private IWaveIn? _capture;
    private MMDevice? _device;
    private WaveFormat? _format;
    private WaveFormat? _sourceFormat;
    private bool _sourceIsFloat;
    private int _sourceBlockAlign = 4;
    private double _startClock;
    private long _nextSampleIndex;
    private double _retainSeconds = 75;
    private float _peak;
    private float _peakDecay = 0.85f;

    public AudioCapture(AudioKind kind)
    {
        _kind = kind;
    }

    public AudioKind Kind => _kind;

    public bool IsRunning { get; private set; }

    public string DeviceName { get; private set; } = string.Empty;

    /// <summary>Clock time of sample 0 (diagnostics only).</summary>
    public double StartClock
    {
        get { lock (_gate) { return _startClock; } }
    }

    public int SampleRate => _format?.SampleRate ?? 0;

    public int Channels => _format?.Channels ?? 0;

    /// <summary>Smoothed input peak (0..1) for the UI level meter.</summary>
    public float PeakLevel
    {
        get
        {
            lock (_gate)
            {
                return _peak;
            }
        }
    }

    /// <summary>How many seconds of audio are currently buffered.</summary>
    public double BufferedSeconds
    {
        get
        {
            lock (_gate)
            {
                if (_format is null)
                {
                    return 0;
                }

                var first = _chunks.First?.Value.StartSample ?? _nextSampleIndex;
                return (_nextSampleIndex - first) / (double)_format.SampleRate;
            }
        }
    }

    public static IReadOnlyList<AudioDeviceInfo> GetDevices(AudioKind kind)
    {
        var list = new List<AudioDeviceInfo>();
        var flow = kind == AudioKind.Desktop ? DataFlow.Render : DataFlow.Capture;

        try
        {
            using var enumerator = new MMDeviceEnumerator();
            foreach (var device in enumerator.EnumerateAudioEndPoints(flow, DeviceState.Active))
            {
                try
                {
                    list.Add(new AudioDeviceInfo(device.ID, device.FriendlyName, kind));
                }
                finally
                {
                    device.Dispose();
                }
            }
        }
        catch (Exception ex)
        {
            Log.Warn($"オーディオデバイスの列挙に失敗 ({kind}): {ex.Message}");
        }

        return list;
    }

    public bool Start(string? deviceId, double retainSeconds)
    {
        if (IsRunning)
        {
            return true;
        }

        try
        {
            using var enumerator = new MMDeviceEnumerator();
            MMDevice? device = null;

            if (!string.IsNullOrWhiteSpace(deviceId))
            {
                try
                {
                    device = enumerator.GetDevice(deviceId);
                    if (device.State != DeviceState.Active)
                    {
                        device.Dispose();
                        device = null;
                    }
                }
                catch (Exception)
                {
                    device = null;
                }
            }

            device ??= _kind == AudioKind.Desktop
                ? enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia)
                : enumerator.GetDefaultAudioEndpoint(DataFlow.Capture, Role.Communications);

            _device = device;
            _retainSeconds = Math.Max(30, retainSeconds + 15);

            _capture = _kind == AudioKind.Desktop
                ? new WasapiLoopbackCapture(device)
                : new WasapiCapture(device);

            // Capture devices hand out 32-bit float; keeping that in the ring costs twice the RAM
            // of 16-bit PCM with no audible benefit, so it is converted on the way in.
            _sourceFormat = _capture.WaveFormat;
            _sourceIsFloat = _sourceFormat.Encoding == WaveFormatEncoding.IeeeFloat &&
                             _sourceFormat.BitsPerSample == 32;
            _sourceBlockAlign = Math.Max(1, _sourceFormat.BlockAlign);
            _format = _sourceIsFloat
                ? new WaveFormat(_sourceFormat.SampleRate, 16, _sourceFormat.Channels)
                : _sourceFormat;

            DeviceName = device.FriendlyName;
            _capture.DataAvailable += OnDataAvailable;
            _capture.RecordingStopped += OnRecordingStopped;
            _startClock = 0;
            _capture.StartRecording();
            IsRunning = true;

            Log.Info($"{( _kind == AudioKind.Desktop ? "デスクトップ音声" : "マイク")}を取得: " +
                     $"{DeviceName} ({_format.SampleRate}Hz {_format.Channels}ch {_format.BitsPerSample}bit)");
            return true;
        }
        catch (Exception ex)
        {
            Log.Warn($"{(_kind == AudioKind.Desktop ? "デスクトップ音声" : "マイク")}を開始できませんでした: {ex.Message}");
            IsRunning = false;
            Dispose();
            return false;
        }
    }

    private void OnDataAvailable(object? sender, WaveInEventArgs e)
    {
        if (e.BytesRecorded <= 0 || _format is null)
        {
            return;
        }

        lock (_gate)
        {
            var blockAlign = Math.Max(1, _sourceBlockAlign);
            var samples = e.BytesRecorded / blockAlign;
            var chunkSeconds = samples / (double)Math.Max(1, _format.SampleRate);

            if (_startClock == 0)
            {
                // The buffer we just received was captured BEFORE this callback fired, so
                // sample 0 belongs to (now - chunkSeconds). Anchoring to "now" would offset the
                // whole track by one buffer length - the classic cause of audible A/V drift.
                _startClock = Clock.Now - chunkSeconds;
            }
            var data = _sourceIsFloat
                ? ConvertToPcm16(e.Buffer, samples)
                : CopySamples(e.Buffer, samples * blockAlign);

            // Level meter: track the loudest sample, then let it fall away smoothly.
            var peak = ComputePeak(data, _format);
            _peak = Math.Max(_peak * _peakDecay, peak);

            _chunks.AddLast(new AudioChunk
            {
                Data = data,
                StartSample = _nextSampleIndex,
                SampleCount = samples
            });
            _nextSampleIndex += samples;

            TrimLocked();
        }
    }

    private static byte[] CopySamples(byte[] source, int byteCount)
    {
        var copy = new byte[byteCount];
        Buffer.BlockCopy(source, 0, copy, 0, byteCount);
        return copy;
    }

    /// <summary>32-bit float interleaved -> 16-bit PCM interleaved (same frame count).</summary>
    private byte[] ConvertToPcm16(byte[] source, int sampleFrames)
    {
        var channels = Math.Max(1, _format!.Channels);
        var result = new byte[sampleFrames * _format.BlockAlign];
        var src = 0;
        var dst = 0;
        for (var frame = 0; frame < sampleFrames; frame++)
        {
            for (var channel = 0; channel < channels; channel++)
            {
                var value = BitConverter.ToSingle(source, src);
                src += 4;
                var scaled = (short)Math.Clamp(value * 32767f, -32768f, 32767f);
                result[dst++] = (byte)scaled;
                result[dst++] = (byte)(scaled >> 8);
            }
        }

        return result;
    }

    private static float ComputePeak(byte[] data, WaveFormat format)
    {
        var peak = 0f;
        if (format.Encoding == WaveFormatEncoding.IeeeFloat && format.BitsPerSample == 32)
        {
            for (var i = 0; i + 4 <= data.Length; i += 4)
            {
                var value = Math.Abs(BitConverter.ToSingle(data, i));
                if (value > peak)
                {
                    peak = value;
                }
            }
        }
        else if (format.BitsPerSample == 16)
        {
            for (var i = 0; i + 2 <= data.Length; i += 2)
            {
                var value = Math.Abs(BitConverter.ToInt16(data, i) / 32768f);
                if (value > peak)
                {
                    peak = value;
                }
            }
        }

        return Math.Clamp(peak, 0f, 1f);
    }

    private void TrimLocked()
    {
        if (_format is null)
        {
            return;
        }

        var maxSamples = (long)(_retainSeconds * _format.SampleRate);
        while (_chunks.Count > 1 && _nextSampleIndex - _chunks.First!.Value.StartSample > maxSamples)
        {
            _chunks.RemoveFirst();
        }
    }

    private void OnRecordingStopped(object? sender, StoppedEventArgs e)
    {
        if (e.Exception is not null)
        {
            Log.Warn($"音声キャプチャが停止しました ({_kind}): {e.Exception.Message}");
        }

        IsRunning = false;
    }

    /// <summary>
    /// Extracts the audio covering the given wall-clock window into a WAV file.
    /// Returns null when nothing usable is buffered for that window.
    /// </summary>
    public string? WriteWindowWav(double startWall, double endWall, string path, int syncOffsetMs)
    {
        if (_format is null)
        {
            return null;
        }

        startWall += syncOffsetMs / 1000.0;
        endWall += syncOffsetMs / 1000.0;

        byte[] pcm;
        WaveFormat format;

        lock (_gate)
        {
            if (_chunks.Count == 0 || _startClock == 0)
            {
                return null;
            }

            // The capture device usually starts a little AFTER ffmpeg produced its first frame.
            // Clamping the window to 0 would slide the whole track later by that gap (and put the
            // two audio sources out of step with each other). Pad with silence instead so the
            // audio keeps its position on the timeline.
            var leadSeconds = Math.Max(0, _startClock - startWall);
            var fromSample = Math.Max(0, (long)((startWall - _startClock) * _format.SampleRate));
            var toSample = Math.Min(_nextSampleIndex, (long)((endWall - _startClock) * _format.SampleRate));
            if (toSample - fromSample < _format.SampleRate / 4)
            {
                return null; // Less than 250 ms - not worth muxing.
            }

            pcm = ReadRangeLocked(fromSample, toSample);
            format = _format;

            var leadBytes = (int)(leadSeconds * format.SampleRate) * Math.Max(1, format.BlockAlign);
            if (leadBytes > 0)
            {
                var padded = new byte[leadBytes + pcm.Length];
                Buffer.BlockCopy(pcm, 0, padded, leadBytes, pcm.Length);
                pcm = padded;
            }
        }

        if (pcm.Length == 0)
        {
            return null;
        }

        try
        {
            var directory = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            using var stream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.Read);
            WriteWavHeader(stream, pcm.Length, format);
            stream.Write(pcm, 0, pcm.Length);
            return path;
        }
        catch (Exception ex)
        {
            Log.Warn($"音声ファイルの書き出しに失敗 ({_kind}): {ex.Message}");
            return null;
        }
    }

    private byte[] ReadRangeLocked(long fromSample, long toSample)
    {
        var format = _format!;
        var blockAlign = format.BlockAlign;
        var totalBytes = (int)Math.Min(int.MaxValue - 4096, (toSample - fromSample) * blockAlign);
        if (totalBytes <= 0)
        {
            return Array.Empty<byte>();
        }

        var result = new byte[totalBytes];
        var written = 0;

        foreach (var chunk in _chunks)
        {
            var chunkEnd = chunk.StartSample + chunk.SampleCount;
            if (chunkEnd <= fromSample || written >= totalBytes)
            {
                continue;
            }

            var overlapStart = Math.Max(fromSample, chunk.StartSample);
            var overlapEnd = Math.Min(toSample, chunkEnd);
            if (overlapEnd <= overlapStart)
            {
                continue;
            }

            var byteOffset = (int)((overlapStart - chunk.StartSample) * blockAlign);
            var byteCount = (int)((overlapEnd - overlapStart) * blockAlign);
            byteCount = Math.Min(byteCount, totalBytes - written);
            if (byteCount <= 0)
            {
                break;
            }

            Buffer.BlockCopy(chunk.Data, byteOffset, result, written, byteCount);
            written += byteCount;
        }

        if (written == totalBytes)
        {
            return result;
        }

        var trimmed = new byte[written];
        Buffer.BlockCopy(result, 0, trimmed, 0, written);
        return trimmed;
    }

    private static void WriteWavHeader(Stream stream, int dataLength, WaveFormat format)
    {
        var isFloat = format.Encoding == WaveFormatEncoding.IeeeFloat;
        var formatTag = (short)(isFloat ? 3 : 1);

        using var writer = new BinaryWriter(stream, System.Text.Encoding.ASCII, leaveOpen: true);
        writer.Write("RIFF"u8.ToArray());
        writer.Write(36 + dataLength);
        writer.Write("WAVE"u8.ToArray());
        writer.Write("fmt "u8.ToArray());
        writer.Write(16);
        writer.Write(formatTag);
        writer.Write((short)format.Channels);
        writer.Write(format.SampleRate);
        writer.Write(format.AverageBytesPerSecond);
        writer.Write((short)format.BlockAlign);
        writer.Write((short)format.BitsPerSample);
        writer.Write("data"u8.ToArray());
        writer.Write(dataLength);
        writer.Flush();
    }

    public void Stop()
    {
        try
        {
            _capture?.StopRecording();
        }
        catch (Exception)
        {
            // Ignore.
        }

        IsRunning = false;
    }

    public void Clear()
    {
        lock (_gate)
        {
            _chunks.Clear();
            _nextSampleIndex = 0;
            _startClock = 0;
            _peak = 0;
        }
    }

    public void Dispose()
    {
        IsRunning = false;
        if (_capture is not null)
        {
            try
            {
                _capture.DataAvailable -= OnDataAvailable;
                _capture.RecordingStopped -= OnRecordingStopped;
                _capture.Dispose();
            }
            catch (Exception)
            {
                // Ignore.
            }

            _capture = null;
        }

        try
        {
            _device?.Dispose();
        }
        catch (Exception)
        {
            // Ignore.
        }

        _device = null;
        Clear();
    }
}

public sealed record AudioDeviceInfo(string Id, string Name, AudioKind Kind)
{
    /// <summary>WPF combo boxes fall back to ToString for the closed-state text.</summary>
    public override string ToString() => Name;
}
