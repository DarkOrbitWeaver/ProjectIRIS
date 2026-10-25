using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using CompanionOS.Core.Configuration;
using CompanionOS.Core.Diagnostics;

namespace CompanionOS.Core.Voice;

/// <summary>Finished audio, ready to play. WAV or MP3 bytes plus their format.</summary>
public sealed class SpeechAudio
{
    public required byte[] Bytes { get; init; }

    /// <summary>"wav" or "mp3".</summary>
    public required string Format { get; init; }

    public TimeSpan ApproximateDuration { get; init; }
}

/// <summary>Anything that can turn text into audio.</summary>
public interface ISpeechSynthesizer : IDisposable
{
    /// <summary>"neural" — the only engine she has.</summary>
    string EngineName { get; }

    /// <summary>False when the engine cannot run on this machine right now.</summary>
    bool IsAvailable { get; }

    /// <summary>Produces audio, or null when synthesis failed.</summary>
    Task<SpeechAudio?> SynthesizeAsync(string text, VoiceProfile profile, CancellationToken cancellationToken);
}

/// <summary>
/// Speaks through a local OpenAI-compatible speech server (Kokoro, Qwen3-TTS, Piper
/// gateways and similar). This is the recommended engine: the voice is defined by the
/// server's speaker embedding, so it is identical on every call, every day.
///
/// Nothing here is hard-coded to one product: point Voice.HttpEndpoint at whatever
/// implements POST /v1/audio/speech and keep VoiceId as the speaker name.
/// </summary>
public sealed class HttpSpeechSynthesizer : ISpeechSynthesizer
{
    private readonly HttpClient _httpClient;
    private readonly VoiceSettings _settings;

    public HttpSpeechSynthesizer(VoiceSettings settings)
    {
        _settings = settings;
        _httpClient = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
    }

    public string EngineName => "http";

    public bool IsAvailable => !string.IsNullOrWhiteSpace(_settings.HttpEndpoint);

    public async Task<SpeechAudio?> SynthesizeAsync(string text, VoiceProfile profile,
        CancellationToken cancellationToken)
    {
        JsonObject body = new()
        {
            ["model"] = _settings.HttpModel,
            ["input"] = text,
            ["voice"] = profile.VoiceId,
            ["response_format"] = "wav",
            ["speed"] = profile.Rate
        };

        try
        {
            using HttpRequestMessage request = new(HttpMethod.Post,
                _settings.HttpEndpoint.TrimEnd('/') + "/audio/speech");
            request.Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json");
            if (!string.IsNullOrWhiteSpace(_settings.ApiKey))
            {
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _settings.ApiKey);
            }

            using HttpResponseMessage response = await _httpClient
                .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
                .ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
            {
                CompanionLog.Warning("Voice", $"Speech server answered {(int)response.StatusCode}.");
                return null;
            }

            byte[] audioBytes = await response.Content.ReadAsByteArrayAsync(cancellationToken)
                .ConfigureAwait(false);
            if (audioBytes.Length == 0)
            {
                return null;
            }

            string contentType = response.Content.Headers.ContentType?.MediaType ?? string.Empty;
            bool isMp3 = contentType.Contains("mpeg", StringComparison.OrdinalIgnoreCase) ||
                         contentType.Contains("mp3", StringComparison.OrdinalIgnoreCase) ||
                         LooksLikeMp3(audioBytes);

            return new SpeechAudio
            {
                Bytes = audioBytes,
                Format = isMp3 ? "mp3" : "wav"
            };
        }
        catch (OperationCanceledException)
        {
            return null;
        }
        catch (Exception exception)
        {
            CompanionLog.Warning("Voice", $"Speech request failed: {exception.Message}");
            return null;
        }
    }

    public void Dispose() => _httpClient.Dispose();

    private static bool LooksLikeMp3(byte[] bytes)
    {
        // A WAV file starts with "RIFF"; anything else with an ID3 or frame header is MP3.
        if (bytes.Length < 4)
        {
            return false;
        }

        bool isRiff = bytes[0] == 'R' && bytes[1] == 'I' && bytes[2] == 'F' && bytes[3] == 'F';
        return !isRiff;
    }
}
