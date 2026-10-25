using System.Security.Cryptography;
using System.Text;
using CompanionOS.Core.Configuration;

namespace CompanionOS.Core.Voice;

/// <summary>
/// Her voice identity, locked for life.
///
/// A VoiceProfile is not a preference; it is who she sounds like. It pins the engine,
/// the exact speaker, and every synthesis parameter, and it is stored next to her
/// persona so a restart, a model swap or an engine upgrade cannot change it by accident.
///
/// The signature is used to name cached reflex phrases. If the profile ever changes,
/// the cache is rebuilt instead of replaying yesterday's voice.
/// </summary>
public sealed class VoiceProfile
{
    public required string Engine { get; init; }
    public required string VoiceId { get; init; }
    public double Rate { get; init; } = 1.0;
    public double Pitch { get; init; } = 0.0;
    public double Volume { get; init; } = 1.0;

    /// <summary>Short stable fingerprint of the identity, used for cache file names.</summary>
    public string Signature
    {
        get
        {
            string material = $"{Engine}|{VoiceId}|{Rate:0.###}|{Pitch:0.###}";
            byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(material));
            return Convert.ToHexString(hash)[..12].ToLowerInvariant();
        }
    }

    /// <summary>Human description shown in the voice settings panel.</summary>
    public string Describe() => $"{Engine} · {VoiceId}" +
                                (Math.Abs(Rate - 1.0) > 0.001 ? $" · {Rate:0.00}x" : string.Empty);

    /// <summary>Builds the locked profile from settings. This is the only place it is derived.
    /// The engine is pinned to the neural server on purpose: there is exactly one mouth.</summary>
    public static VoiceProfile FromSettings(CompanionSettings settings)
    {
        VoiceSettings voice = settings.Voice;
        return new VoiceProfile
        {
            Engine = voice.Engine,
            VoiceId = string.IsNullOrWhiteSpace(voice.VoiceId)
                ? voice.Engine.Equals("pocket", StringComparison.OrdinalIgnoreCase) ? "alba" : "af_heart"
                : voice.VoiceId.Trim(),
            Rate = Math.Clamp(voice.Rate, 0.5, 1.8),
            Pitch = Math.Clamp(voice.Pitch, -0.5, 0.5),
            Volume = Math.Clamp(voice.Volume, 0.0, 1.0)
        };
    }
}
