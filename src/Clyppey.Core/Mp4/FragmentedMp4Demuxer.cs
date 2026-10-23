using System.Buffers.Binary;
using System.Text;

namespace Clyppey.Core.Mp4;

/// <summary>One keyframe-aligned chunk of encoded video, exactly as written by the muxer.</summary>
public sealed record FragmentChunk(byte[] Data, double StartSeconds, int SampleCount, double ArrivalSeconds);

/// <summary>
/// Incremental parser for the fragmented MP4 stream that ffmpeg writes to stdout.
///
/// Layout produced by "-movflags +frag_keyframe+empty_moov":
///     ftyp moov ( moof mdat )* ...
/// so the init segment is everything before the first moof, and every following moof starts
/// a new self-contained, keyframe-aligned fragment. That is precisely the unit the replay
/// buffer stores and the unit a clip is trimmed at, which is why saving never re-encodes.
/// </summary>
public sealed class FragmentedMp4Demuxer
{
    private const int MaxBoxBytes = 256 * 1024 * 1024;

    private byte[] _buffer = new byte[512 * 1024];
    private int _length;
    private int _position;

    private readonly MemoryStream _initStream = new();
    private byte[]? _initSegment;
    private bool _seenFirstFragment;
    private bool _initRaised;

    private MemoryStream? _fragmentStream;
    private double _fragmentStart;
    private int _fragmentSamples;

    /// <summary>Timescale from the track header (e.g. 15360 for 60 fps H.264).</summary>
    public int Timescale { get; private set; }

    public int VideoWidth { get; private set; }

    public int VideoHeight { get; private set; }

    public byte[]? InitSegment => _initSegment;

    /// <summary>Raised once, when ftyp+moov have been fully received.</summary>
    public event Action<byte[]>? InitSegmentReady;

    /// <summary>Raised for every complete fragment (moof+mdat group).</summary>
    public event Action<FragmentChunk>? FragmentReady;

    public void Push(ReadOnlySpan<byte> data)
    {
        if (data.IsEmpty)
        {
            return;
        }

        EnsureCapacity(_length + data.Length);
        data.CopyTo(_buffer.AsSpan(_length));
        _length += data.Length;

        ParseAvailable();
        Compact();
    }

    /// <summary>Flushes a trailing fragment (used when the capture stops).</summary>
    public void Flush()
    {
        FlushFragment();
    }

    public void Reset()
    {
        _buffer = new byte[512 * 1024];
        _length = 0;
        _position = 0;
        _initStream.SetLength(0);
        _initSegment = null;
        _seenFirstFragment = false;
        _initRaised = false;
        _fragmentStream = null;
        _fragmentStart = 0;
        _fragmentSamples = 0;
        Timescale = 0;
    }

    private void ParseAvailable()
    {
        while (true)
        {
            var available = _length - _position;
            if (available < 8)
            {
                return;
            }

            var span = _buffer.AsSpan(_position);
            var size32 = BinaryPrimitives.ReadUInt32BigEndian(span);
            var type = FourCc(span.Slice(4, 4));
            long size = size32;
            var headerSize = 8;

            if (size32 == 1)
            {
                if (available < 16)
                {
                    return;
                }

                size = (long)BinaryPrimitives.ReadUInt64BigEndian(span.Slice(8, 8));
                headerSize = 16;
            }
            else if (size32 == 0)
            {
                // "extends to end of file" - never expected on a live pipe, treat as corrupt.
                Log.Warn("MP4 ストリームに不正なボックスサイズ (0) を検出しました。再同期します。");
                _length = 0;
                _position = 0;
                return;
            }

            if (size < headerSize || size > MaxBoxBytes)
            {
                Log.Warn($"MP4 ストリームに不正なボックスサイズ ({size}) を検出しました。再同期します。");
                _length = 0;
                _position = 0;
                return;
            }

            if (size > available)
            {
                return; // Wait for the rest of this box.
            }

            var box = new byte[size];
            Buffer.BlockCopy(_buffer, _position, box, 0, (int)size);
            _position += (int)size;
            HandleBox(type, box, headerSize);
        }
    }

    private void HandleBox(string type, byte[] box, int headerSize)
    {
        switch (type)
        {
            case "ftyp":
            case "moov":
            case "free":
            case "skip":
            case "wide":
                if (!_seenFirstFragment)
                {
                    _initStream.Write(box, 0, box.Length);
                    if (type == "moov")
                    {
                        Timescale = ReadTimescale(box) ?? Timescale;
                        ReadDimensions(box);
                        PublishInitIfPossible();
                    }
                }

                break;

            case "moof":
                FlushFragment();
                _seenFirstFragment = true;
                PublishInitIfPossible();

                _fragmentStream = new MemoryStream(box.Length + 32);
                _fragmentStream.Write(box, 0, box.Length);
                _fragmentStart = ReadDecodeTime(box);
                _fragmentSamples = ReadSampleCount(box);
                break;

            default:
                if (_fragmentStream is not null)
                {
                    _fragmentStream.Write(box, 0, box.Length);
                }

                break;
        }
    }

    private void PublishInitIfPossible()
    {
        if (_initRaised || _initStream.Length == 0)
        {
            return;
        }

        // Wait until moov is present - a fragment stream is useless without it.
        var bytes = _initStream.ToArray();
        if (!ContainsFourCc(bytes, "moov"))
        {
            return;
        }

        _initSegment = bytes;
        _initRaised = true;
        InitSegmentReady?.Invoke(bytes);
    }

    private void FlushFragment()
    {
        var stream = _fragmentStream;
        _fragmentStream = null;
        if (stream is null || stream.Length == 0)
        {
            return;
        }

        var data = stream.ToArray();
        var samples = _fragmentSamples;
        var start = Timescale > 0 ? _fragmentStart / Timescale : 0;
        FragmentReady?.Invoke(new FragmentChunk(data, start, samples, Clock.Now));
    }

    private void ReadDimensions(byte[] moov)
    {
        // tkhd ends with width/height as 16.16 fixed point; audio tracks are zero-sized.
        var searchFrom = 0;
        while (true)
        {
            var index = IndexOfFourCc(moov.AsSpan(searchFrom), "tkhd");
            if (index < 0)
            {
                return;
            }

            index += searchFrom;
            if (index < 4 || index + 8 > moov.Length)
            {
                return;
            }

            var boxStart = index - 4;
            var size = (int)BinaryPrimitives.ReadUInt32BigEndian(moov.AsSpan(boxStart, 4));
            var boxEnd = boxStart + size;
            if (size >= 8 && boxEnd <= moov.Length && boxEnd - 8 >= boxStart)
            {
                var width = BinaryPrimitives.ReadUInt32BigEndian(moov.AsSpan(boxEnd - 8, 4)) >> 16;
                var height = BinaryPrimitives.ReadUInt32BigEndian(moov.AsSpan(boxEnd - 4, 4)) >> 16;
                if (width > 0 && height > 0)
                {
                    VideoWidth = (int)width;
                    VideoHeight = (int)height;
                    return;
                }
            }

            searchFrom = index + 4;
            if (searchFrom >= moov.Length)
            {
                return;
            }
        }
    }

    private int? ReadTimescale(byte[] moov)
    {
        // "IndexOfFourCc" yields the offset of the type field, i.e. box start + 4.
        var typeIndex = IndexOfFourCc(moov, "mdhd");
        if (typeIndex < 4)
        {
            return null;
        }

        var box = typeIndex - 4;
        var version = moov[box + 8];
        // v0: creation(4) modification(4) timescale(4) -> box+20
        // v1: creation(8) modification(8) timescale(4) -> box+28
        var offset = box + (version == 1 ? 28 : 20);
        if (offset + 4 > moov.Length)
        {
            return null;
        }

        var value = (int)BinaryPrimitives.ReadUInt32BigEndian(moov.AsSpan(offset, 4));
        return value > 0 ? value : null;
    }

    /// <summary>baseMediaDecodeTime from the first tfdt in the moof (raw, in timescale units).</summary>
    private double ReadDecodeTime(byte[] moof)
    {
        var typeIndex = IndexOfFourCc(moof, "tfdt");
        if (typeIndex < 4)
        {
            return 0;
        }

        var box = typeIndex - 4;
        var version = moof[box + 8];
        if (version == 1 && box + 20 <= moof.Length)
        {
            return (double)BinaryPrimitives.ReadUInt64BigEndian(moof.AsSpan(box + 12, 8));
        }

        return box + 16 <= moof.Length
            ? BinaryPrimitives.ReadUInt32BigEndian(moof.AsSpan(box + 12, 4))
            : 0;
    }

    private static int ReadSampleCount(byte[] moof)
    {
        var typeIndex = IndexOfFourCc(moof, "trun");
        if (typeIndex < 4)
        {
            return 0;
        }

        var box = typeIndex - 4;
        return box + 16 <= moof.Length
            ? (int)BinaryPrimitives.ReadUInt32BigEndian(moof.AsSpan(box + 12, 4))
            : 0;
    }

    internal static bool ContainsFourCc(byte[] data, string fourCc) => IndexOfFourCc(data, fourCc) >= 0;

    internal static int IndexOfFourCc(ReadOnlySpan<byte> data, string fourCc)
    {
        if (data.Length < 4)
        {
            return -1;
        }

        Span<byte> needle = stackalloc byte[4];
        Encoding.ASCII.GetBytes(fourCc, needle);
        return data.IndexOf(needle);
    }

    private static string FourCc(ReadOnlySpan<byte> span) => Encoding.ASCII.GetString(span);

    private void EnsureCapacity(int needed)
    {
        if (needed <= _buffer.Length)
        {
            return;
        }

        var size = _buffer.Length;
        while (size < needed)
        {
            size *= 2;
        }

        Array.Resize(ref _buffer, size);
    }

    private void Compact()
    {
        if (_position == 0)
        {
            return;
        }

        if (_position == _length)
        {
            _length = 0;
            _position = 0;
            return;
        }

        // Only compact when it is actually worth copying.
        if (_position < 512 * 1024 && _position < _length / 2)
        {
            return;
        }

        var remaining = _length - _position;
        Buffer.BlockCopy(_buffer, _position, _buffer, 0, remaining);
        _length = remaining;
        _position = 0;
    }
}
