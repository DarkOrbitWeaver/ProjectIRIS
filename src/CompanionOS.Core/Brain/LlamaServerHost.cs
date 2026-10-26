using System.Diagnostics;
using CompanionOS.Core.Configuration;
using CompanionOS.Core.Diagnostics;

namespace CompanionOS.Core.Brain;

/// <summary>
/// Owns the llama-server process that provides the cortex.
///
/// Why a child process instead of an in-process library: unloading a model in
/// process never returns every byte of VRAM, and Sleep / Gaming Mode depend on
/// VRAM going back to exactly zero. Killing a process is the only honest unload.
///
/// The host also captures the server's console output, because the first thing
/// anyone needs when a model will not load is the last twenty lines it printed.
/// </summary>
public sealed class LlamaServerHost : IAsyncDisposable
{
    private const int RememberedOutputLines = 250;

    private readonly object _stateLock = new();
    private readonly Queue<string> _recentOutput = new();
    private Process? _serverProcess;
    private CancellationTokenSource? _outputPumpSource;

    /// <summary>Raised for every line the server prints (stdout and stderr merged).</summary>
    public event Action<string>? ServerOutput;

    /// <summary>True while the child process is alive.</summary>
    public bool IsRunning
    {
        get
        {
            lock (_stateLock)
            {
                return _serverProcess is { HasExited: false };
            }
        }
    }

    private volatile bool _weightsReady;

    /// <summary>
    /// True only once the /health wait finished with the model actually loaded. A live
    /// process that is still paging weights into VRAM answers 503 — the boot race that
    /// 503'd the owner's first typed message. Ready and running are different truths.
    /// </summary>
    public bool IsReady => _weightsReady && IsRunning;

    /// <summary>The last lines the server printed, newest last.</summary>
    public IReadOnlyList<string> RecentOutput
    {
        get
        {
            lock (_stateLock)
            {
                return _recentOutput.ToArray();
            }
        }
    }

    /// <summary>
    /// Starts llama-server for the given profile and waits until it answers /health.
    /// Returns immediately for external profiles because something else is hosting them.
    /// </summary>
    public async Task StartAsync(BrainProfile profile, CompanionSettings settings,
        CancellationToken cancellationToken)
    {
        if (profile.Kind == BrainHostKind.External)
        {
            CompanionLog.Info("Runtime", $"Using external endpoint {profile.Endpoint} (no process to start).");
            return;
        }

        await StopAsync().ConfigureAwait(false);

        // Ownership sweep: a force-killed earlier session (a crash, or the owner closing
        // the app mid-turn) leaves its llama-server running with the whole model awake
        // in RAM and VRAM. Iris knows no process by its handle, but the machine she
        // runs on has exactly one brain — hers — and every leftover not started by
        // this session is a resource thief and is culled right before the fresh spawn
        // below.
        DemolishUnownedServers();

        string executablePath = ResolveExecutablePath(profile, settings.Brain.ServerExecutablePath);
        if (!File.Exists(executablePath))
        {
            throw new FileNotFoundException(
                "llama-server was not found. Run scripts/setup.ps1 to download it, or set " +
                "Brain.ServerExecutablePath in settings.json.", executablePath);
        }

        string modelPath = CompanionPaths.Model(profile.ModelFile);
        if (!File.Exists(modelPath))
        {
            throw new FileNotFoundException(
                $"The brain file '{profile.ModelFile}' is missing from the models folder. " +
                "Run scripts/setup.ps1 to download it.", modelPath);
        }

        ProcessStartInfo startInfo = new()
        {
            FileName = executablePath,
            WorkingDirectory = Path.GetDirectoryName(executablePath)!,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = false
        };

        foreach (string argument in BuildArguments(profile, modelPath))
        {
            startInfo.ArgumentList.Add(argument);
        }

        CompanionLog.Info("Runtime", $"Starting brain '{profile.DisplayName}' on port {ResolvePort(profile)}.");
        long bootStarted = System.Diagnostics.Stopwatch.GetTimestamp();

        Process process = new() { StartInfo = startInfo, EnableRaisingEvents = true };
        process.OutputDataReceived += (_, eventArgs) => CaptureOutput(eventArgs.Data);
        process.ErrorDataReceived += (_, eventArgs) => CaptureOutput(eventArgs.Data);

        if (!process.Start())
        {
            IrisEvent.Log("Runtime", "cortex.start_fail", LogLevel.Error, "llama-server refused to start.");
            throw new InvalidOperationException("llama-server refused to start.");
        }

        // First thing the brain does is join the family that outlives nothing: if Iris
        // exits for any reason, the operating system ends this server with her.
        ChildProcessGuard.Adopt(process);

        IrisEvent.Log("Runtime", "cortex.process_started", LogLevel.Info,
            data: new Dictionary<string, string?>
            {
                ["profile"] = profile.DisplayName,
                ["pid"] = process.Id.ToString(),
                ["port"] = ResolvePort(profile).ToString(),
                ["model"] = profile.ModelFile
            });

        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        lock (_stateLock)
        {
            _serverProcess = process;
        }

        _outputPumpSource = new CancellationTokenSource();

        try
        {
            await WaitUntilHealthyAsync(profile, settings.Brain.StartupTimeoutSeconds, cancellationToken)
                .ConfigureAwait(false);
            _weightsReady = true;
            CompanionLog.Info("Runtime", "Brain is answering. Cortex is awake.");
            IrisEvent.Log("Runtime", "cortex.ready", LogLevel.Info,
                durationMs: System.Diagnostics.Stopwatch.GetElapsedTime(bootStarted).TotalMilliseconds,
                data: new Dictionary<string, string?>
                {
                    ["profile"] = profile.DisplayName,
                    ["context"] = profile.ContextTokens.ToString()
                });
        }
        catch
        {
            await StopAsync().ConfigureAwait(false);
            throw;
        }
    }

    /// <summary>Kills the server and releases the GPU. Safe to call when already stopped.</summary>
    public async Task StopAsync()
    {
        _weightsReady = false;
        Process? process;
        lock (_stateLock)
        {
            process = _serverProcess;
            _serverProcess = null;
        }

        _outputPumpSource?.Cancel();
        _outputPumpSource?.Dispose();
        _outputPumpSource = null;

        if (process is null)
        {
            return;
        }

        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                using CancellationTokenSource waitTimeout = new(TimeSpan.FromSeconds(5));
                try
                {
                    await process.WaitForExitAsync(waitTimeout.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    CompanionLog.Warning("Runtime", "The brain process did not exit within five seconds.");
                }
            }
        }
        catch (Exception exception)
        {
            CompanionLog.Warning("Runtime", $"Server did not stop cleanly: {exception.Message}");
        }
        finally
        {
            process.Dispose();
        }

        CompanionLog.Info("Runtime", "Brain unloaded. VRAM released.");
        IrisEvent.Log("Runtime", "cortex.stopped", LogLevel.Info);
        return;
    }

    /// <summary>
    /// Kills every llama-server.exe this session does not own, so the fresh worker
    /// below starts without competing for VRAM and RAM. Only runs for managed brain
    /// profiles — an external endpoint is somebody else's to keep or kill.
    /// </summary>
    private static void DemolishUnownedServers()
    {
        foreach (Process straggler in Process.GetProcessesByName("llama-server"))
        {
            try
            {
                CompanionLog.Info("Runtime",
                    $"Removing a leftover llama-server (pid {straggler.Id}) from an earlier session.");
                straggler.Kill(entireProcessTree: true);
                straggler.WaitForExit(5000);
            }
            catch (Exception exception)
            {
                CompanionLog.Trace("Runtime", $"llama-server straggler shutdown: {exception.Message}");
            }
            finally
            {
                straggler.Dispose();
            }
        }
    }

    /// <summary>
    /// Finds llama-server.exe: an explicit setting wins, then the downloaded runtimes
    /// folder, then the system PATH. No path is ever hard-coded in source.
    /// </summary>
    public string ResolveExecutablePath(BrainProfile profile, string configuredPath)
    {
        if (!string.IsNullOrWhiteSpace(configuredPath) && File.Exists(configuredPath))
        {
            return configuredPath;
        }

        string runtimesRoot = CompanionPaths.RuntimesDirectory;
        if (Directory.Exists(runtimesRoot))
        {
            try
            {
                string? found = Directory
                    .EnumerateFiles(runtimesRoot, "llama-server.exe", SearchOption.AllDirectories)
                    .FirstOrDefault();
                if (found is not null)
                {
                    return found;
                }
            }
            catch (Exception exception)
            {
                CompanionLog.Warning("Runtime", $"Could not search the runtimes folder: {exception.Message}");
            }
        }

        return "llama-server.exe";
    }

    public async ValueTask DisposeAsync() => await StopAsync().ConfigureAwait(false);

    private async Task WaitUntilHealthyAsync(BrainProfile profile, int timeoutSeconds,
        CancellationToken cancellationToken)
    {
        using HttpClient probeClient = new() { Timeout = TimeSpan.FromSeconds(3) };
        string healthUrl = ComposeHealthUrl(profile.Endpoint);
        DateTimeOffset deadline = DateTimeOffset.Now.AddSeconds(Math.Max(10, timeoutSeconds));

        while (DateTimeOffset.Now < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();

            lock (_stateLock)
            {
                if (_serverProcess is { HasExited: true })
                {
                    throw new InvalidOperationException(
                        "llama-server exited while loading. Last output: " +
                        string.Join(" | ", _recentOutput.TakeLast(6)));
                }
            }

            try
            {
                using HttpResponseMessage response = await probeClient.GetAsync(healthUrl, cancellationToken)
                    .ConfigureAwait(false);
                if (response.IsSuccessStatusCode)
                {
                    return;
                }
            }
            catch (HttpRequestException)
            {
                // Still starting; that is expected for the first few seconds.
            }

            await Task.Delay(500, cancellationToken).ConfigureAwait(false);
        }

        throw new TimeoutException(
            $"The brain did not become ready within {timeoutSeconds} seconds. Last output: " +
            string.Join(" | ", _recentOutput.TakeLast(6)));
    }

    private static string ComposeHealthUrl(string endpoint)
    {
        if (!Uri.TryCreate(endpoint, UriKind.Absolute, out Uri? uri))
        {
            return endpoint.TrimEnd('/') + "/health";
        }

        return $"{uri.Scheme}://{uri.Host}:{uri.Port}/health";
    }

    private static int ResolvePort(BrainProfile profile)
    {
        if (Uri.TryCreate(profile.Endpoint, UriKind.Absolute, out Uri? uri) && uri.Port > 0)
        {
            return uri.Port;
        }

        return 8747;
    }

    private static IEnumerable<string> BuildArguments(BrainProfile profile, string modelPath)
    {
        List<string> arguments = new()
        {
            "--model", modelPath,
        };

        // Context: an explicit token count is passed through; a zero means "auto", so
        // llama-server sizes the context from the model itself — exactly as the
        // settings screen's Auto option promises.
        if (profile.ContextTokens > 0)
        {
            arguments.Add("--ctx-size");
            arguments.Add(profile.ContextTokens.ToString());
        }

        arguments.Add("--n-gpu-layers");
        arguments.Add(profile.GpuLayers.ToString());
        arguments.Add("--threads");
        arguments.Add(profile.Threads.ToString());
        arguments.Add("--host");
        arguments.Add("127.0.0.1");
        arguments.Add("--port");
        arguments.Add(ResolvePort(profile).ToString());
        arguments.Add("--jinja");

        if (profile.SupportsVision)
        {
            string projectorPath = CompanionPaths.Model(profile.ProjectorFile);
            if (File.Exists(projectorPath))
            {
                arguments.Add("--mmproj");
                arguments.Add(projectorPath);
            }
            else
            {
                CompanionLog.Warning("Runtime",
                    $"Vision projector '{profile.ProjectorFile}' is missing, so this brain starts text-only.");
            }
        }

        arguments.AddRange(SplitArguments(profile.ExtraServerArguments));
        return arguments;
    }

    /// <summary>Splits a command line string into arguments, respecting double quotes.</summary>
    public static List<string> SplitArguments(string? commandLine)
    {
        List<string> arguments = new();
        if (string.IsNullOrWhiteSpace(commandLine))
        {
            return arguments;
        }

        System.Text.StringBuilder current = new();
        bool insideQuotes = false;

        foreach (char character in commandLine)
        {
            if (character == '"')
            {
                insideQuotes = !insideQuotes;
                continue;
            }

            if (char.IsWhiteSpace(character) && !insideQuotes)
            {
                if (current.Length > 0)
                {
                    arguments.Add(current.ToString());
                    current.Clear();
                }

                continue;
            }

            current.Append(character);
        }

        if (current.Length > 0)
        {
            arguments.Add(current.ToString());
        }

        return arguments;
    }

    private void CaptureOutput(string? line)
    {
        if (string.IsNullOrWhiteSpace(line))
        {
            return;
        }

        lock (_stateLock)
        {
            _recentOutput.Enqueue(line);
            while (_recentOutput.Count > RememberedOutputLines)
            {
                _recentOutput.Dequeue();
            }
        }

        CompanionLog.Trace("llama-server", line);

        try
        {
            ServerOutput?.Invoke(line);
        }
        catch
        {
            // Diagnostics subscribers must never break the server pump.
        }
    }
}
