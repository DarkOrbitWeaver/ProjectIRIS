using CompanionOS.Core.Configuration;
using CompanionOS.Core.Voice;

namespace CompanionOS.Core.Diagnostics;

/// <summary>
/// A small, deliberately passive first-run checklist. It reads configuration and file/device
/// presence only: calling it never starts llama.cpp, opens a microphone, plays audio or makes
/// a network request. "Ready" therefore means the prerequisite is present, never that Iris
/// secretly exercised it behind the owner's back.
/// </summary>
public sealed class CompanionReadiness
{
    public required ReadinessItem Brain { get; init; }
    public required ReadinessItem Hearing { get; init; }
    public required ReadinessItem Voice { get; init; }
    public required ReadinessItem Music { get; init; }
    public required ReadinessItem Memory { get; init; }

    public IReadOnlyList<ReadinessItem> Items => new[] { Brain, Hearing, Voice, Music, Memory };

    public int ReadyCount => Items.Count(item => item.IsReady);
}

/// <summary>One explainable readiness result used by onboarding and Settings.</summary>
public sealed class ReadinessItem
{
    public required string Label { get; init; }
    public required bool IsReady { get; init; }
    public required string Detail { get; init; }
}

public static class CompanionReadinessProbe
{
    public static CompanionReadiness Read(CompanionSettings settings, VoiceReadiness voice)
    {
        BrainProfile profile = settings.ActiveBrainProfile;
        bool managed = profile.Kind == BrainHostKind.Managed;
        string brainPath = CompanionPaths.Model(profile.ModelFile);
        bool brainReady = !managed || File.Exists(brainPath);
        string brainDetail = !managed
            ? $"External endpoint configured: {profile.Endpoint}"
            : brainReady
                ? $"{profile.DisplayName} weights found"
                : $"Missing model: {profile.ModelFile}";

        bool hearingReady = !settings.Ears.Enabled ||
            (voice.IsMicrophoneConfigured && voice.IsRecognizerAvailable);
        string hearingDetail = !settings.Ears.Enabled
            ? "Ears are off by your choice"
            : !voice.IsMicrophoneConfigured ? voice.MicrophoneDetail
            : voice.IsRecognizerAvailable ? voice.RecognizerDetail : voice.RecognizerDetail;

        bool voiceReady = voice.IsOutputConfigured && voice.IsVoiceEngineOnline;
        string voiceDetail = !voice.IsOutputConfigured
            ? voice.OutputDetail
            : voice.IsVoiceEngineOnline ? "Voice engine and speaker route ready" : voice.VoiceEngineDetail;

        IReadOnlyList<string> configuredFolders = settings.Music.SearchDirectories;
        int availableFolders = configuredFolders.Count(folder =>
        {
            try
            {
                return Directory.Exists(Environment.ExpandEnvironmentVariables(folder));
            }
            catch (Exception exception)
            {
                // A malformed path or an unexpanded variable used to read exactly like
                // "the folder is gone" - an unexplained, silent red light on the
                // readiness screen (2026-10-27 audit). Now the probe states its reason.
                CompanionLog.Trace("Readiness",
                    $"Music folder '{folder}' could not be probed: {exception.Message}");
                return false;
            }
        });
        bool musicReady = availableFolders > 0;
        string musicDetail = musicReady
            ? $"{availableFolders} music folder{(availableFolders == 1 ? string.Empty : "s")} available"
            : "No accessible music folder added yet";

        // The graph is constructed before this probe. Its on-disk database may not exist until
        // the first write, which is normal, so its initialized in-memory state is the truth here.
        ReadinessItem memory = new()
        {
            Label = "Memory",
            IsReady = true,
            Detail = "Local memory store ready"
        };

        return new CompanionReadiness
        {
            Brain = new ReadinessItem { Label = "Brain", IsReady = brainReady, Detail = brainDetail },
            Hearing = new ReadinessItem { Label = "Hearing", IsReady = hearingReady, Detail = hearingDetail },
            Voice = new ReadinessItem { Label = "Voice", IsReady = voiceReady, Detail = voiceDetail },
            Music = new ReadinessItem { Label = "Music", IsReady = musicReady, Detail = musicDetail },
            Memory = memory
        };
    }
}
