using System.IO;
using Clyppey.Core;
using Clyppey.Core.Audio;

namespace Clyppey.App.Platform;

/// <summary>
/// Plays the "a save just started" cue.
///
/// It exists to answer one question the completion chime cannot: <em>did my hotkey register?</em>
/// Assembling a clip takes a moment (the encoder still has to hand over its newest fragment), so
/// without a sound at the start the user has no feedback until the file is written - and none at
/// all when the press was ignored.
/// </summary>
internal static class SaveCue
{
    // SoundPlayer decodes from the stream it was handed for as long as it lives, so neither the
    // wav bytes nor the stream may be collected between plays. One instance for the process.
    private static readonly MemoryStream Wav = new(CueTone.BuildStartCue(), writable: false);

    private static readonly System.Media.SoundPlayer Player = new(Wav);

    private static bool _reportedFailure;

    /// <summary>Starts the cue and returns at once - the save must never wait on the sound card.</summary>
    public static void PlayStart()
    {
        try
        {
            Player.Play();
        }
        catch (Exception ex)
        {
            // No audio endpoint, or a sound scheme the player refuses: saving still has to work.
            if (_reportedFailure)
            {
                return;
            }

            _reportedFailure = true;
            Log.Warn($"保存開始の効果音を再生できません: {ex.Message}");
        }
    }
}
