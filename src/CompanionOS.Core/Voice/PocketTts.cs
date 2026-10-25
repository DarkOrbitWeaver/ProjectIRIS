using System.Diagnostics;
using System.Net.Http.Headers;
using System.Text;
using CompanionOS.Core.Configuration;
using CompanionOS.Core.Diagnostics;

namespace CompanionOS.Core.Voice;

/// <summary>
/// Starts the small CPU-only Pocket TTS sidecar and keeps its CPU budget bounded.
/// The model is read from the Hugging Face cache below Iris's models directory;
/// no credentials are stored or passed to the child process.
/// </summary>
public sealed class PocketTtsServerHost : IAsyncDisposable
{
    private readonly VoiceSettings _settings;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private Process? _process;

    public PocketTtsServerHost(VoiceSettings settings) => _settings = settings;

    public string Endpoint => _settings.PocketEndpoint.TrimEnd('/');

    public async Task<bool> StartAsync(CancellationToken cancellationToken = default)
    {
        if (!_settings.Engine.Equals("pocket", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (!Uri.TryCreate(Endpoint, UriKind.Absolute, out Uri? endpointUri) ||
            !endpointUri.IsLoopback || endpointUri.Port is < 1 or > 65535)
        {
            CompanionLog.Warning("Voice", "Pocket TTS requires a loopback HTTP endpoint; remote hosts are refused.");
            return false;
        }

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_process is { HasExited: false })
            {
                return await IsHealthyAsync(cancellationToken).ConfigureAwait(false);
            }

            _process?.Dispose();
            _process = null;

            string executable = string.IsNullOrWhiteSpace(_settings.PocketExecutablePath)
                ? CompanionPaths.Runtime(Path.Combine("pocket-tts", "pocket-tts.exe"))
                : _settings.PocketExecutablePath;
            if (!File.Exists(executable))
            {
                CompanionLog.Warning("Voice", $"Pocket TTS runtime is missing: {executable}");
                return false;
            }

            string cacheDirectory = Path.Combine(CompanionPaths.ModelsDirectory, "pocket-tts-cache");
            if (!Directory.Exists(cacheDirectory) ||
                !Directory.EnumerateFiles(cacheDirectory, "model.safetensors", SearchOption.AllDirectories).Any() ||
                !Directory.EnumerateFiles(cacheDirectory, "tokenizer.model", SearchOption.AllDirectories).Any())
            {
                CompanionLog.Warning("Voice", $"Pocket TTS model cache is missing or incomplete: {cacheDirectory}");
                return false;
            }

            ProcessStartInfo startInfo = new(executable)
            {
                WorkingDirectory = Path.GetDirectoryName(executable)!,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            startInfo.ArgumentList.Add("serve");
            startInfo.ArgumentList.Add("--host");
            startInfo.ArgumentList.Add("127.0.0.1");
            startInfo.ArgumentList.Add("--port");
            startInfo.ArgumentList.Add(endpointUri.Port.ToString());
            startInfo.ArgumentList.Add("--language");
            startInfo.ArgumentList.Add(_settings.PocketLanguage);
            startInfo.ArgumentList.Add("--default-voice");
            startInfo.ArgumentList.Add(string.IsNullOrWhiteSpace(_settings.VoiceId) ? "alba" : _settings.VoiceId);
            startInfo.Environment["HF_HOME"] = cacheDirectory;
            startInfo.Environment["RAYON_NUM_THREADS"] = Math.Clamp(_settings.PocketMaxThreads, 1, 2).ToString();

            Process process = new() { StartInfo = startInfo, EnableRaisingEvents = true };
            process.OutputDataReceived += (_, eventArgs) =>
            {
                if (!string.IsNullOrWhiteSpace(eventArgs.Data))
                {
                    CompanionLog.Trace("Voice", $"Pocket TTS: {eventArgs.Data}");
                }
            };
            process.ErrorDataReceived += (_, eventArgs) =>
            {
                if (!string.IsNullOrWhiteSpace(eventArgs.Data))
                {
                    CompanionLog.Trace("Voice", $"Pocket TTS: {eventArgs.Data}");
                }
            };

            try
            {
                if (!process.Start())
                {
                    process.Dispose();
                    return false;
                }

                _process = process;
                process.BeginOutputReadLine();
                process.BeginErrorReadLine();
                ApplyCpuLimits(process);
            }
            catch (Exception exception)
            {
                process.Dispose();
                CompanionLog.Warning("Voice", $"Pocket TTS could not start: {exception.Message}");
                return false;
            }

            DateTimeOffset deadline = DateTimeOffset.UtcNow.AddSeconds(45);
            while (DateTimeOffset.UtcNow < deadline)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (process.HasExited)
                {
                    CompanionLog.Warning("Voice", $"Pocket TTS exited during startup (code {process.ExitCode}).");
                    process.Dispose();
                    _process = null;
                    return false;
                }

                if (await IsHealthyAsync(cancellationToken).ConfigureAwait(false))
                {
                    CompanionLog.Info("Voice", "Pocket TTS is ready on the local CPU worker.");
                    return true;
                }

                await Task.Delay(250, cancellationToken).ConfigureAwait(false);
            }

            CompanionLog.Warning("Voice", "Pocket TTS did not become ready within 45 seconds.");
            await StopProcessAsync().ConfigureAwait(false);
            return false;
        }
        catch (OperationCanceledException)
        {
            await StopProcessAsync().ConfigureAwait(false);
            throw;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<bool> IsHealthyAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            using HttpClient client = new() { Timeout = TimeSpan.FromSeconds(1) };
            using HttpResponseMessage response = await client.GetAsync(Endpoint + "/health", cancellationToken)
                .ConfigureAwait(false);
            return response.IsSuccessStatusCode;
        }
        catch
        {
            return false;
        }
    }

    public async Task StopAsync()
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            await StopProcessAsync().ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync().ConfigureAwait(false);
        _gate.Dispose();
    }

    private async Task StopProcessAsync()
    {
        Process? process = _process;
        _process = null;
        if (process is null)
        {
            return;
        }

        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync().ConfigureAwait(false);
            }
        }
        catch (Exception exception)
        {
            CompanionLog.Trace("Voice", $"Pocket TTS shutdown: {exception.Message}");
        }
        finally
        {
            process.Dispose();
        }
    }

    private static void ApplyCpuLimits(Process process)
    {
        try
        {
            process.PriorityClass = ProcessPriorityClass.BelowNormal;
            if (OperatingSystem.IsWindows())
            {
                // The sidecar can occupy at most two of the machine's logical processors,
                // even if a library creates an unexpected worker thread.
                int logicalProcessors = Math.Max(1, Environment.ProcessorCount);
                int allowed = Math.Min(2, logicalProcessors);
                long mask = (1L << allowed) - 1;
                process.ProcessorAffinity = new IntPtr(mask);
            }
        }
        catch (Exception exception)
        {
            CompanionLog.Warning("Voice", $"Could not apply the Pocket TTS CPU cap: {exception.Message}");
        }
    }
}

/// <summary>HTTP adapter for Pocket's local multipart /tts endpoint.</summary>
public sealed class PocketTtsSpeechSynthesizer : ISpeechSynthesizer
{
    private readonly VoiceSettings _settings;
    private readonly HttpClient _client = new() { Timeout = TimeSpan.FromSeconds(45) };

    public PocketTtsSpeechSynthesizer(VoiceSettings settings) => _settings = settings;

    public string EngineName => "pocket";
    public bool IsAvailable => Uri.TryCreate(_settings.PocketEndpoint, UriKind.Absolute, out Uri? endpoint) &&
                               endpoint.IsLoopback;

    public async Task<SpeechAudio?> SynthesizeAsync(string text, VoiceProfile profile,
        CancellationToken cancellationToken)
    {
        using MultipartFormDataContent form = new();
        form.Add(new StringContent(text, Encoding.UTF8), "text");
        form.Add(new StringContent(profile.VoiceId, Encoding.UTF8), "voice_url");

        try
        {
            using HttpResponseMessage response = await _client.PostAsync(
                _settings.PocketEndpoint.TrimEnd('/') + "/tts", form, cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                CompanionLog.Warning("Voice", $"Pocket TTS answered {(int)response.StatusCode}.");
                return null;
            }

            byte[] bytes = await response.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);
            return bytes.Length == 0
                ? null
                : new SpeechAudio { Bytes = bytes, Format = "wav" };
        }
        catch (OperationCanceledException)
        {
            return null;
        }
        catch (Exception exception)
        {
            CompanionLog.Warning("Voice", $"Pocket TTS request failed: {exception.Message}");
            return null;
        }
    }

    public void Dispose() => _client.Dispose();
}

public static class SpeechSynthesizerFactory
{
    public static ISpeechSynthesizer Create(VoiceSettings settings) =>
        settings.Engine.Equals("pocket", StringComparison.OrdinalIgnoreCase)
            ? new PocketTtsSpeechSynthesizer(settings)
            : new HttpSpeechSynthesizer(settings);
}
