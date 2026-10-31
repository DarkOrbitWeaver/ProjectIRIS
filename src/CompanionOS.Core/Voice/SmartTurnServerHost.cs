using System.Diagnostics;
using System.Net.Http;
using System.Text.Json;
using CompanionOS.Core.Configuration;
using CompanionOS.Core.Diagnostics;

namespace CompanionOS.Core.Voice;

/// <summary>Runs the CPU-only Smart Turn model beside the existing Parakeet recognizer.</summary>
public sealed class SmartTurnServerHost : IAsyncDisposable
{
    private const int Port = 18767;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private Process? _process;
    private int _disposeStarted;

    private static string Endpoint => $"http://127.0.0.1:{Port}";

    public bool IsAvailable =>
        File.Exists(CompanionPaths.Runtime(Path.Combine("smart-turn", "venv", "Scripts", "python.exe"))) &&
        File.Exists(CompanionPaths.Runtime(Path.Combine("smart-turn", "smart-turn-server.py"))) &&
        File.Exists(CompanionPaths.Model("smart-turn-v3.2-cpu.onnx"));

    public Task<bool> PrepareAsync(CancellationToken cancellationToken) =>
        IsAvailable ? EnsureRunningAsync(cancellationToken) : Task.FromResult(false);

    public async Task<double?> PredictCompletionProbabilityAsync(
        float[] samples,
        CancellationToken cancellationToken)
    {
        if (!IsAvailable || !await EnsureRunningAsync(cancellationToken).ConfigureAwait(false))
        {
            return null;
        }

        byte[] audio = new byte[samples.Length * sizeof(float)];
        Buffer.BlockCopy(samples, 0, audio, 0, audio.Length);
        using ByteArrayContent content = new(audio);
        content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/octet-stream");
        using HttpRequestMessage request = new(HttpMethod.Post, Endpoint + "/predict")
        {
            Content = content
        };

        try
        {
            using HttpResponseMessage response = await SharedClient.SendAsync(request, cancellationToken)
                .ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            using JsonDocument payload = JsonDocument.Parse(
                await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false));
            if (!payload.RootElement.TryGetProperty("probability", out JsonElement probability) ||
                !probability.TryGetDouble(out double value) || !double.IsFinite(value) || value is < 0 or > 1)
            {
                CompanionLog.Warning("Voice", "Smart Turn returned an invalid completion probability.");
                return null;
            }

            return value;
        }
        catch (HttpRequestException exception)
        {
            CompanionLog.Warning("Voice", $"Smart Turn request failed: {exception.Message}");
            return null;
        }
        catch (OperationCanceledException exception) when (!cancellationToken.IsCancellationRequested)
        {
            CompanionLog.Warning("Voice", $"Smart Turn request timed out: {exception.Message}");
            return null;
        }
        catch (JsonException exception)
        {
            CompanionLog.Warning("Voice", $"Smart Turn returned invalid JSON: {exception.Message}");
            return null;
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposeStarted, 1) != 0)
        {
            return;
        }

        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            Process? process = _process;
            _process = null;
            if (process is not null)
            {
                await StopWorkerAsync(process).ConfigureAwait(false);
            }
        }
        finally
        {
            _gate.Release();
            _gate.Dispose();
        }
    }

    private async Task<bool> EnsureRunningAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_process is { HasExited: false })
            {
                if (await IsHealthyAsync(cancellationToken).ConfigureAwait(false))
                {
                    return true;
                }

                await StopWorkerAsync(_process).ConfigureAwait(false);
            }

            _process?.Dispose();
            _process = null;

            string python = CompanionPaths.Runtime(Path.Combine("smart-turn", "venv", "Scripts", "python.exe"));
            string script = CompanionPaths.Runtime(Path.Combine("smart-turn", "smart-turn-server.py"));
            string model = CompanionPaths.Model("smart-turn-v3.2-cpu.onnx");
            ProcessStartInfo startInfo = new(python)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            startInfo.ArgumentList.Add(script);
            startInfo.ArgumentList.Add("--model");
            startInfo.ArgumentList.Add(model);
            startInfo.ArgumentList.Add("--port");
            startInfo.ArgumentList.Add(Port.ToString(System.Globalization.CultureInfo.InvariantCulture));

            Process process = new() { StartInfo = startInfo, EnableRaisingEvents = true };
            process.OutputDataReceived += (_, args) => LogWorkerLine(args.Data);
            process.ErrorDataReceived += (_, args) => LogWorkerLine(args.Data);
            try
            {
                if (!process.Start())
                {
                    process.Dispose();
                    CompanionLog.Warning("Voice", "Smart Turn worker could not start.");
                    return false;
                }

                ChildProcessGuard.Adopt(process);
                _process = process;
                process.BeginOutputReadLine();
                process.BeginErrorReadLine();
            }
            catch (Exception exception) when (
                exception is System.ComponentModel.Win32Exception or InvalidOperationException)
            {
                await StopWorkerAsync(process).ConfigureAwait(false);
                CompanionLog.Warning("Voice", $"Smart Turn worker could not start: {exception.Message}");
                return false;
            }

            DateTimeOffset deadline = DateTimeOffset.UtcNow.AddSeconds(45);
            while (DateTimeOffset.UtcNow < deadline)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (process.HasExited)
                {
                    CompanionLog.Warning("Voice", $"Smart Turn worker exited during startup (code {process.ExitCode}).");
                    process.Dispose();
                    _process = null;
                    return false;
                }

                if (await IsHealthyAsync(cancellationToken).ConfigureAwait(false))
                {
                    CompanionLog.Info("Voice", "Smart Turn v3.2 is ready on CPU.");
                    return true;
                }

                await Task.Delay(200, cancellationToken).ConfigureAwait(false);
            }

            CompanionLog.Warning("Voice", "Smart Turn worker did not become ready within 45 seconds.");
            await StopWorkerAsync(process).ConfigureAwait(false);
            return false;
        }

        finally
        {
            _gate.Release();
        }
    }

    private async Task StopWorkerAsync(Process process)
    {
        if (ReferenceEquals(_process, process))
        {
            _process = null;
        }

        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync().ConfigureAwait(false);
            }
        }
        catch (Exception exception) when (
            exception is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            CompanionLog.Warning("Voice", $"Smart Turn worker shutdown failed: {exception.Message}");
        }
        finally
        {
            process.Dispose();
        }
    }

    private static async Task<bool> IsHealthyAsync(CancellationToken cancellationToken)
    {
        try
        {
            using HttpResponseMessage response = await SharedClient.GetAsync(Endpoint + "/health", cancellationToken)
                .ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                return false;
            }

            using JsonDocument payload = JsonDocument.Parse(
                await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false));
            return payload.RootElement.TryGetProperty("model", out JsonElement model) &&
                   model.ValueKind == JsonValueKind.String &&
                   model.GetString() == "smart-turn-v3.2-cpu";
        }
        catch (HttpRequestException)
        {
            return false;
        }
        catch (JsonException)
        {
            return false;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return false;
        }
    }

    private static void LogWorkerLine(string? line)
    {
        if (!string.IsNullOrWhiteSpace(line))
        {
            CompanionLog.Info("Voice", $"Smart Turn: {line}");
        }
    }

    private static readonly HttpClient SharedClient = new()
    {
        Timeout = TimeSpan.FromSeconds(10)
    };
}
