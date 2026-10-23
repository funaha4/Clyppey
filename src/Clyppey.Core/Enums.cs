namespace Clyppey.Core;

/// <summary>Video codecs the app knows how to encode.</summary>
public enum CodecKind
{
    H264,
    Hevc,
    Av1
}

/// <summary>Rate control mode.</summary>
public enum RateMode
{
    /// <summary>Constant bitrate - predictable clip size (the default for a replay buffer).</summary>
    Cbr,

    /// <summary>Variable bitrate - better quality per bit for a known target size.</summary>
    Vbr,

    /// <summary>Constant quality - fixed quantizer, bitrate floats.</summary>
    Cq
}

/// <summary>Which hardware encoder family to prefer.</summary>
public enum EncoderFamily
{
    Auto,
    Nvenc,
    Amf,
    Qsv,
    Software
}

/// <summary>
/// One dial for "how good should clips look", so the main screen does not need a bitrate
/// field. Custom falls back to the manual bitrate in the advanced section.
/// </summary>
public enum QualityPreset
{
    Low,
    Standard,
    High,
    Max,
    Custom
}
