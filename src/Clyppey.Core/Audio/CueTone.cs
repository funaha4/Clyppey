using System.Buffers.Binary;
using System.Text;

namespace Clyppey.Core.Audio;

/// <summary>
/// Synthesizes the short PCM cue that marks "the save was accepted".
///
/// The completion sound is the Windows notification chime. Another <c>SystemSounds</c> member
/// would not tell the two apart: in the default scheme the info, warning and default-beep events
/// all resolve to the same wav (<c>Windows Background.wav</c>), so a "start" played with any of
/// them would be indistinguishable from the "done" chime. Synthesizing the start cue also means it
/// does not change when the user's sound scheme does.
///
/// The buffer is a complete RIFF/PCM wav, so callers can hand it straight to a player.
/// </summary>
public static class CueTone
{
    /// <summary>Sample rate of the generated cues. 44.1 kHz mono 16-bit keeps the wav tiny.</summary>
    public const int SampleRate = 44100;

    /// <summary>Both ends of a note are ramped over this long; a hard edge on a sine is a click.</summary>
    private const double FadeSeconds = 0.006;

    /// <summary>
    /// Rising two-note blip (D5 -> A5, ~160 ms) played the moment a save is accepted.
    /// Short and bright on purpose: the user has to be able to hear it under game audio and
    /// still tell it apart from the soft chime that follows.
    /// </summary>
    public static byte[] BuildStartCue() => BuildWav(
        new[] { (587.33, 0.050), (880.00, 0.070) },
        amplitude: 0.25,
        gapSeconds: 0.018);

    /// <summary>Renders <paramref name="notes"/> back to back into one mono 16-bit wav.</summary>
    /// <param name="notes">Frequency (Hz) and length (seconds) of each note, in order.</param>
    /// <param name="amplitude">Peak level, 0..1 of full scale.</param>
    /// <param name="gapSeconds">Silence inserted between notes.</param>
    public static byte[] BuildWav(
        IReadOnlyList<(double Frequency, double Seconds)> notes,
        double amplitude = 0.25,
        double gapSeconds = 0.018)
    {
        ArgumentNullException.ThrowIfNull(notes);
        if (notes.Count == 0)
        {
            throw new ArgumentException("at least one note is required", nameof(notes));
        }

        var samples = new List<short>();
        var gap = (int)Math.Round(Math.Max(0, gapSeconds) * SampleRate);

        for (var i = 0; i < notes.Count; i++)
        {
            if (i > 0 && gap > 0)
            {
                samples.AddRange(new short[gap]);
            }

            AppendNote(samples, notes[i].Frequency, notes[i].Seconds, amplitude);
        }

        return WrapPcm16(samples);
    }

    private static void AppendNote(List<short> samples, double frequency, double seconds, double amplitude)
    {
        var count = (int)Math.Round(seconds * SampleRate);
        if (count <= 0 || frequency <= 0)
        {
            return;
        }

        var fade = Math.Min((int)Math.Round(FadeSeconds * SampleRate), count / 2);
        var peak = Math.Clamp(amplitude, 0.0, 1.0) * short.MaxValue;

        for (var i = 0; i < count; i++)
        {
            var envelope = 1.0;
            if (fade > 0)
            {
                if (i < fade)
                {
                    envelope = 0.5 * (1 - Math.Cos(Math.PI * i / fade));
                }
                else if (i >= count - fade)
                {
                    envelope = 0.5 * (1 - Math.Cos(Math.PI * (count - 1 - i) / fade));
                }
            }

            var value = Math.Sin(2 * Math.PI * frequency * i / SampleRate) * envelope * peak;
            samples.Add((short)Math.Clamp(Math.Round(value), short.MinValue, short.MaxValue));
        }
    }

    private static byte[] WrapPcm16(List<short> samples)
    {
        const int HeaderBytes = 44;
        var dataBytes = samples.Count * 2;
        var wav = new byte[HeaderBytes + dataBytes];

        Encoding.ASCII.GetBytes("RIFF").CopyTo(wav, 0);
        BinaryPrimitives.WriteInt32LittleEndian(wav.AsSpan(4), 36 + dataBytes);
        Encoding.ASCII.GetBytes("WAVE").CopyTo(wav, 8);
        Encoding.ASCII.GetBytes("fmt ").CopyTo(wav, 12);
        BinaryPrimitives.WriteInt32LittleEndian(wav.AsSpan(16), 16); // fmt chunk size
        BinaryPrimitives.WriteInt16LittleEndian(wav.AsSpan(20), 1);  // PCM
        BinaryPrimitives.WriteInt16LittleEndian(wav.AsSpan(22), 1);  // mono
        BinaryPrimitives.WriteInt32LittleEndian(wav.AsSpan(24), SampleRate);
        BinaryPrimitives.WriteInt32LittleEndian(wav.AsSpan(28), SampleRate * 2); // byte rate
        BinaryPrimitives.WriteInt16LittleEndian(wav.AsSpan(32), 2);  // block align
        BinaryPrimitives.WriteInt16LittleEndian(wav.AsSpan(34), 16); // bits per sample
        Encoding.ASCII.GetBytes("data").CopyTo(wav, 36);
        BinaryPrimitives.WriteInt32LittleEndian(wav.AsSpan(40), dataBytes);

        for (var i = 0; i < samples.Count; i++)
        {
            BinaryPrimitives.WriteInt16LittleEndian(wav.AsSpan(HeaderBytes + (i * 2)), samples[i]);
        }

        return wav;
    }
}
