using System.Diagnostics;
using CompanionOS.Core.Configuration;
using CompanionOS.Core.Diagnostics;

namespace CompanionOS.Core.Voice;

/// <summary>
/// Starts her Kokoro-82M voice worker: a pinned python environment under the
/// runtimes directory that speaks the OpenAI <c>/audio/speech</c> protocol her HTTP
/// synthesizer already speaks. The worker therefore never gets its own request
/// code paths in the pipeline — only its own process ownership.
/// </summary>
public sealed class KokoroTtsServerHost : IAsyncDisposable
{
    /// <summary>Endpoints and thread budgets are read live (2026-10-27 audit), the same
    /// contract as the Pocket TTS host: a settings change must reach the next worker
    /// start instead of waiting for her restart.</summary>
    private readonly Func<VoiceSettings> _settings;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private Process? _process;

    public KokoroTtsServerHost(Func<VoiceSettings> settings) => _settings = settings;

    private VoiceSettings Options => _settings();

    public string Endpoint => Options.KokoroEndpoint.TrimEnd('/');

    public async Task<bool> StartAsync(CancellationToken cancellationToken = default)
    {
        if (!Options.Engine.Equals("kokoro", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (!Uri.TryCreate(Endpoint, UriKind.Absolute, out Uri? endpointUri) ||
            !endpointUri.IsLoopback || endpointUri.Port is < 1 or > 65535)
        {
            CompanionLog.Warning("Voice", "Kokoro requires a loopback HTTP endpoint; remote hosts are refused.");
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

            // Same family contract as the other workers: the only voice worker allowed
            // on Iris's port is one Iris own-venv-launched. A leftover python from a
            // crashed session answers /health while unable to speak — replace it
            // instead of negotiating with it.
            DemolishUnownedWorkers();

            string python = CompanionPaths.Runtime(Path.Combine("kokoro-tts", "venv", "Scripts", "python.exe"));
            string script = CompanionPaths.Runtime(Path.Combine("kokoro-tts", "kokoro-server.py"));
            if (!File.Exists(python) || !File.Exists(script))
            {
                CompanionLog.Warning("Voice",
                    $"Kokoro runtime is missing: {python} / {script}. Run scripts/setup.ps1 -Component voice.");
                return false;
            }

            string modelDirectory = CompanionPaths.ModelsDirectory;
            modelDirectory = Path.Combine(modelDirectory, "kokoro-cache");
            if (!File.Exists(Path.Combine(modelDirectory, "kokoro-v1.0.onnx")) ||
                !File.Exists(Path.Combine(modelDirectory, "voices-v1.0.bin")))
            {
                CompanionLog.Warning("Voice",
                    $"Kokoro weights are missing from {modelDirectory}. Run scripts/setup.ps1 -Component voice.");
                return false;
            }

            ProcessStartInfo startInfo = new(python)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            startInfo.ArgumentList.Add(script);
            startInfo.ArgumentList.Add("--host");
            startInfo.ArgumentList.Add("127.0.0.1");
            startInfo.ArgumentList.Add("--port");
            startInfo.ArgumentList.Add(endpointUri.Port.ToString());
            startInfo.ArgumentList.Add("--model-dir");
            startInfo.ArgumentList.Add(modelDirectory);
            startInfo.ArgumentList.Add("--threads");
            startInfo.ArgumentList.Add(Math.Clamp(Options.KokoroMaxThreads, 1, 4).ToString());
            startInfo.ArgumentList.Add("--default-voice");
            startInfo.ArgumentList.Add(string.IsNullOrWhiteSpace(Options.KokoroDefaultVoice)
                ? "af_heart"
                : Options.KokoroDefaultVoice);

            Process process = new() { StartInfo = startInfo, EnableRaisingEvents = true };
            process.OutputDataReceived += (_, eventArgs) => OnWorkerLine(eventArgs.Data, "out");
            process.ErrorDataReceived += (_, eventArgs) => OnWorkerLine(eventArgs.Data, "err");

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
                ApplyCpuLimits(process);
            }
            catch (Exception exception)
            {
                process.Dispose();
                CompanionLog.Warning("Voice", $"Kokoro could not start: {exception.Message}");
                return false;
            }

            DateTimeOffset deadline = DateTimeOffset.UtcNow.AddSeconds(90);
            while (DateTimeOffset.UtcNow < deadline)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (process.HasExited)
                {
                    CompanionLog.Warning("Voice", $"Kokoro exited during startup (code {process.ExitCode}).");
                    process.Dispose();
                    _process = null;
                    return false;
                }

                if (await IsHealthyAsync(cancellationToken).ConfigureAwait(false))
                {
                    CompanionLog.Info("Voice", "Kokoro is ready on the local CPU worker.");
                    return true;
                }

                await Task.Delay(250, cancellationToken).ConfigureAwait(false);
            }

            CompanionLog.Warning("Voice", "Kokoro did not become ready within 90 seconds.");
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
    /// Kills only python processes started from <em>her own</em> kokoro venv, so a
    /// leftover worker from a crashed session cannot squat her port. Every other
    /// python.exe on this machine is explicitly left alone; processes whose command
    /// line cannot be inspected are skipped honestly rather than guessed at.
    /// </summary>
    private static void DemolishUnownedWorkers()
    {
        string[] markerPaths =
        {
            CompanionPaths.Runtime(Path.Combine("kokoro-tts", "venv", "Scripts", "python.exe")),
            CompanionPaths.Runtime(Path.Combine("kokoro-tts", "venv", "Scripts", "pythonw.exe"))
        };

        foreach (string marker in markerPaths)
        {
            string processName = Path.GetFileNameWithoutExtension(marker);
            foreach (Process candidate in Process.GetProcessesByName(processName))
            {
                try
                {
                    string? image = null;
                    try
                    {
                        image = candidate.MainModule?.FileName;
                    }
                    catch
                    {
                        // Not ours to inspect (different elevation/user) — skip, never kill.
                        continue;
                    }

                    if (image is null || !markerPaths.Contains(image, StringComparer.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    CompanionLog.Info("Voice",
                        $"Kokoro: replacing a leftover worker (pid {candidate.Id}) from an earlier session.");
                    candidate.Kill(entireProcessTree: true);
                    candidate.WaitForExit(5000);
                }
                catch (Exception exception)
                {
                    CompanionLog.Trace("Voice", $"Kokoro straggler shutdown: {exception.Message}");
                }
                finally
                {
                    candidate.Dispose();
                }
            }
        }
    }

    /// <summary>Worker receipts become her permanent voice receipts — Info, never Trace.</summary>
    private static void OnWorkerLine(string? line, string stream)
    {
        if (string.IsNullOrWhiteSpace(line))
        {
            return;
        }

        bool isReceipt = line.StartsWith("receipt ", StringComparison.OrdinalIgnoreCase) ||
                         line.StartsWith("listening", StringComparison.OrdinalIgnoreCase) ||
                         line.StartsWith("voice catalogue", StringComparison.OrdinalIgnoreCase);
        if (isReceipt)
        {
            CompanionLog.Info("Voice", $"Kokoro: {line}");
        }
        else
        {
            CompanionLog.Trace("Voice", $"Kokoro {stream}: {line}");
        }
    }

    public async Task<bool> IsHealthyAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            // One shared client per host: a fresh HttpClient on every health poll churned
            // sockets on the senses hot path (2026-10-27 audit).
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
            CompanionLog.Trace("Voice", $"Kokoro shutdown: {exception.Message}");
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
                // Her voice may use at most two of the machine's logical processors,
                // mirrored from the Pocket worker's cap.
                int logicalProcessors = Math.Max(1, Environment.ProcessorCount);
                int allowed = Math.Min(2, logicalProcessors);
                long mask = (1L << allowed) - 1;
                process.ProcessorAffinity = new IntPtr(mask);
            }
        }
        catch (Exception exception)
        {
            CompanionLog.Warning("Voice", $"Could not apply the Kokoro CPU cap: {exception.Message}");
        }
    }
}
