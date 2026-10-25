namespace CompanionOS.Core.Voice;

/// <summary>
/// A cheap, side-effect-free picture of the audio chain. It deliberately does not
/// open a microphone, load Whisper, render speech, or wake a server: it is safe to
/// show in Settings while a game is using the machine.
/// </summary>
public sealed class VoiceReadiness
{
    public required bool EarsEnabled { get; init; }
    public required bool IsMicrophoneConfigured { get; init; }
    public required string MicrophoneDetail { get; init; }
    public required bool IsRecognizerAvailable { get; init; }
    public required string RecognizerDetail { get; init; }
    public required bool IsVoiceEngineOnline { get; init; }
    public required string VoiceEngineDetail { get; init; }
    public required bool IsOutputConfigured { get; init; }
    public required string OutputDetail { get; init; }

    /// <summary>One compact, human-readable truth line for Settings and onboarding.</summary>
    public string Summary
    {
        get
        {
            List<string> parts = new();
            parts.Add(EarsEnabled
                ? (IsMicrophoneConfigured ? "mic ready" : "mic unavailable")
                : "ears disabled");
            parts.Add(IsRecognizerAvailable ? "speech recognition ready" : "speech recognition unavailable");
            parts.Add(IsVoiceEngineOnline ? "voice engine connected" : "voice engine offline");
            parts.Add(IsOutputConfigured ? "speaker route ready" : "speaker route unavailable");
            return string.Join(" · ", parts);
        }
    }
}

/// <summary>Outcome of an explicit hardware or service check initiated by the owner.</summary>
public sealed class VoiceDiagnosticResult
{
    public required bool Succeeded { get; init; }
    public required string Detail { get; init; }
}
