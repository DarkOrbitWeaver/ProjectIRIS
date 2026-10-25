using CompanionOS.Core.Configuration;
using CompanionOS.Core.Diagnostics;

namespace CompanionOS.Core.Voice;

/// <summary>What the gate decided about one finished utterance.</summary>
public enum WakeGateVerdict
{
    /// <summary>Worth the CPU: hand it to the recogniser.</summary>
    Transcribe,

    /// <summary>Cannot be for her. Throw it away without transcribing anything.</summary>
    Skip
}

/// <summary>
/// The cheap question asked before the expensive one.
///
/// Transcribing every sound the microphone hears is the single most expensive thing
/// her brainstem does. A phone assistant answers this differently: a tiny always-on
/// keyword spotter listens for "hey google" and only then does anything bigger wake up.
///
/// This interface is that seam. The gate runs after voice activity detection and before
/// speech recognition, on the raw samples, and its answer decides whether the recogniser
/// is touched at all.
///
/// Shipping a model-backed gate (Porcupine, openWakeWord) later means writing one class
/// that implements this interface and returning it from VoicePipeline.CreateWakeGate.
/// Nothing else in the pipeline changes, which is the whole reason the seam exists.
/// </summary>
public interface IWakeGate : IDisposable
{
    /// <summary>Short name used in logs and the settings screen.</summary>
    string Name { get; }

    /// <summary>False when the gate's model is missing, so the pipeline falls back.</summary>
    bool IsAvailable { get; }

    /// <summary>
    /// Decides whether an utterance is worth transcribing. Must be fast and must never
    /// throw: this runs on the audio callback path.
    /// </summary>
    WakeGateVerdict Check(float[] samples, CancellationToken cancellationToken);
}

/// <summary>
/// The default gate, and it needs no model at all.
///
/// Two facts decide everything it can decide for free:
///   * her name takes time to say, so a 200 ms blip is never "hey iris";
///   * silence after the recording did not end as speech, so a flat segment is noise.
///
/// That removes coughs, keyboard clicks, door slams and chair scrapes from the
/// recogniser's workload. It cannot tell "hey iris" from any other sentence — that is
/// what speech recognition is for — but it costs a few microseconds instead of a few
/// hundred milliseconds, and on a busy game that difference is a frame.
/// </summary>
public sealed class UtteranceShapeWakeGate : IWakeGate
{
    private const int SampleRate = 16000;

    private readonly SettingsStore _settings;

    public UtteranceShapeWakeGate(SettingsStore settings) => _settings = settings;

    public string Name => "utterance-shape";

    public bool IsAvailable => true;

    public WakeGateVerdict Check(float[] samples, CancellationToken cancellationToken)
    {
        try
        {
            if (samples.Length == 0)
            {
                return WakeGateVerdict.Skip;
            }

            EarsSettings ears = _settings.Current.Ears;
            int minimumSamples = Math.Max(1, SampleRate * Math.Clamp(ears.WakeGateMinimumMilliseconds, 0, 5000) / 1000);
            if (samples.Length < minimumSamples)
            {
                return WakeGateVerdict.Skip;
            }

            // Peak amplitude, not average: a sentence recorded with the tail of a
            // silence still has loud peaks, while hiss does not. The threshold is far
            // below speech, because the point is to reject silence, not quiet talking.
            float peak = 0;
            for (int index = 0; index < samples.Length; index++)
            {
                float magnitude = Math.Abs(samples[index]);
                if (magnitude > peak)
                {
                    peak = magnitude;
                }
            }

            return peak < 0.012f ? WakeGateVerdict.Skip : WakeGateVerdict.Transcribe;
        }
        catch (Exception exception)
        {
            // A gate that can break the audio path is worse than no gate.
            CompanionLog.Trace("Voice", $"Wake gate failed open: {exception.Message}");
            return WakeGateVerdict.Transcribe;
        }
    }

    public void Dispose()
    {
    }
}

/// <summary>
/// The gate that never says no. Used when Ears.RequireWakePhrase is off, and useful when
/// diagnosing whether the gate or the recogniser is dropping a sentence.
/// </summary>
public sealed class PassThroughWakeGate : IWakeGate
{
    public string Name => "pass-through";

    public bool IsAvailable => true;

    public WakeGateVerdict Check(float[] samples, CancellationToken cancellationToken) =>
        WakeGateVerdict.Transcribe;

    public void Dispose()
    {
    }
}
