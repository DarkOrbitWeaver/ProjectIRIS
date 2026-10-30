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
    /// <summary>Endpoints and thread budgets are read live (2026-10-27 audit):
    /// SettingsStore.Update replaces Current wholesale, and the worker must follow
    /// the new endpoint instead of pinning whatever existed at boot.</summary>
    private readonly Func<VoiceSettings> _settings;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private Process? _process;

    public PocketTtsServerHost(Func<VoiceSettings> settings) => _settings = settings;

    private VoiceSettings Options => _settings();

    public string Endpoint => Options.PocketEndpoint.TrimEnd('/');

    public async Task<bool> StartAsync(CancellationToken cancellationToken = default)
    {
        if (!Options.Engine.Equals("pocket", StringComparison.OrdinalIgnoreCase))
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

            // Ownership rule: the only voice worker allowed on Iris's port is the one
            // she spawned. A worker orphaned by a crashed or force-stopped earlier
            // session can still answer /health while breaking every synthesis request,
            // so a healthy ping proves nothing about speech — replace the straggler
            // instead of negotiating with it. Skipped when the user points the endpoint
            // at an explicitly configured worker they manage themselves.
            if (string.IsNullOrWhiteSpace(Options.PocketExecutablePath))
            {
                DemolishUnownedWorkers();
            }

            string executable = string.IsNullOrWhiteSpace(Options.PocketExecutablePath)
                ? CompanionPaths.Runtime(Path.Combine("pocket-tts", "pocket-tts.exe"))
                : Options.PocketExecutablePath;
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
                CompanionLog.Warning("Voice",
                    $"Pocket TTS weights are missing from {cacheDirectory}. Run scripts/setup.ps1 -Component voice to install the public preset-voice model.");
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
            startInfo.ArgumentList.Add(Options.PocketLanguage);
            startInfo.ArgumentList.Add("--default-voice");
            startInfo.ArgumentList.Add(string.IsNullOrWhiteSpace(Options.VoiceId) ? "vera" : Options.VoiceId);
            startInfo.Environment["HF_HOME"] = cacheDirectory;
            startInfo.Environment["HF_HUB_DISABLE_IMPLICIT_TOKEN"] = "1";
            startInfo.Environment["RAYON_NUM_THREADS"] = Math.Clamp(Options.PocketMaxThreads, 1, 4).ToString();

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

                // Same family contract as the brain and plugins: no voice worker
                // outlives Iris's process.
                ChildProcessGuard.Adopt(process);

                _process = process;
                process.BeginOutputReadLine();
                process.BeginErrorReadLine();
                ApplyCpuLimits(process, Math.Clamp(Options.PocketMaxThreads, 1, 4));
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

    /// <summary>
    /// Kills any pocket-tts.exe Iris does not own, so the fresh worker below can claim
    /// the port. Only called when Iris manages the worker path herself.
    /// </summary>
    private static void DemolishUnownedWorkers()
    {
        foreach (Process straggler in Process.GetProcessesByName("pocket-tts"))
        {
            try
            {
                CompanionLog.Info("Voice",
                    $"Pocket TTS: replacing a leftover worker (pid {straggler.Id}) from an earlier session.");
                straggler.Kill(entireProcessTree: true);
                straggler.WaitForExit(5000);
            }
            catch (Exception exception)
            {
                CompanionLog.Trace("Voice", $"Pocket TTS straggler shutdown: {exception.Message}");
            }
            finally
            {
                straggler.Dispose();
            }
        }
    }

    public async Task<bool> IsHealthyAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            // One shared health client per host: a new HttpClient every 250 ms poll
            // churned sockets on the senses hot path (2026-10-27 audit).
            using HttpRequestMessage request = new(HttpMethod.Get, Endpoint + "/health");
            using HttpResponseMessage response = await SharedHealthClient.SendAsync(request, cancellationToken)
                .ConfigureAwait(false);
            return response.IsSuccessStatusCode;
        }
        catch
        {
            return false;
        }
    }

    private static readonly HttpClient SharedHealthClient = new()
    {
        Timeout = TimeSpan.FromSeconds(1)
    };

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

    private static void ApplyCpuLimits(Process process, int threads)
    {
        try
        {
            process.PriorityClass = ProcessPriorityClass.Normal;
            if (OperatingSystem.IsWindows())
            {
                int logicalProcessors = Math.Clamp(Environment.ProcessorCount, 1, IntPtr.Size * 8);
                int allowed = Math.Clamp(threads, 1, Math.Min(logicalProcessors, 63));
                int firstProcessor = logicalProcessors - allowed;
                ulong mask = ((1UL << allowed) - 1) << firstProcessor;
                process.ProcessorAffinity = new IntPtr(unchecked((long)mask));
            }
        }
        catch (Exception exception)
        {
            CompanionLog.Warning("Voice", $"Could not apply the voice CPU plan: {exception.Message}");
        }
    }
}

/// <summary>HTTP adapter for Pocket's local multipart /tts endpoint.</summary>
public sealed class PocketTtsSpeechSynthesizer : IStreamingSpeechSynthesizer
{
    /// <summary>Endpoint read live (2026-10-27 audit): a settings change must reach
    /// the next synthesis request, not the one after a restart.</summary>
    private readonly Func<VoiceSettings> _settings;
    private readonly HttpClient _client = new() { Timeout = TimeSpan.FromSeconds(45) };

    public PocketTtsSpeechSynthesizer(Func<VoiceSettings> settings) => _settings = settings;

    public string EngineName => "pocket";
    public bool IsAvailable => Uri.TryCreate(_settings().PocketEndpoint, UriKind.Absolute, out Uri? endpoint) &&
                               endpoint.IsLoopback;

    public async Task<SpeechAudioStream?> OpenSpeechStreamAsync(string text, VoiceProfile profile,
        CancellationToken cancellationToken)
    {
        using MultipartFormDataContent form = new();
        form.Add(new StringContent(text, Encoding.UTF8), "text");
        form.Add(new StringContent(profile.VoiceId, Encoding.UTF8), "voice_url");

        try
        {
            using HttpRequestMessage request = new(HttpMethod.Post,
                _settings().PocketEndpoint.TrimEnd('/') + "/tts") { Content = form };
            HttpResponseMessage response = await _client.SendAsync(request,
                HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                CompanionLog.Warning("Voice", $"Pocket TTS answered {(int)response.StatusCode}.");
                response.Dispose();
                return null;
            }

            Stream content = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            return new SpeechAudioStream(content, response);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            CompanionLog.Warning("Voice", $"Pocket TTS stream request failed ({exception.GetType().Name}).");
            return null;
        }
    }

    public async Task<SpeechAudio?> SynthesizeAsync(string text, VoiceProfile profile,
        CancellationToken cancellationToken)
    {
        using MultipartFormDataContent form = new();
        form.Add(new StringContent(text, Encoding.UTF8), "text");
        form.Add(new StringContent(profile.VoiceId, Encoding.UTF8), "voice_url");

        try
        {
            using HttpResponseMessage response = await _client.PostAsync(
                _settings().PocketEndpoint.TrimEnd('/') + "/tts", form, cancellationToken).ConfigureAwait(false);
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
    /// <summary>Builds her synthesizer against a LIVE settings source: engine,
    /// endpoint and voice changes from settings now reach the next spoken answer
    /// instead of waiting for a restart (2026-10-27 audit).</summary>
    public static ISpeechSynthesizer Create(Func<VoiceSettings> settings)
    {
        if (settings().Engine.Equals("pocket", StringComparison.OrdinalIgnoreCase))
        {
            return new PocketTtsSpeechSynthesizer(settings);
        }

        if (settings().Engine.Equals("kokoro", StringComparison.OrdinalIgnoreCase))
        {
            return new HttpSpeechSynthesizer(settings,
                endpointOverride: () => settings().KokoroEndpoint, engineName: "kokoro");
        }

        return new HttpSpeechSynthesizer(settings);
    }
}
