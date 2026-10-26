using CompanionOS.Core.Configuration;
using CompanionOS.Core.Diagnostics;
using CompanionOS.Core.Events;
using CompanionOS.Core.Memory;
using CompanionOS.Core.Mind;
using CompanionOS.Core.Modules;
using CompanionOS.Core.Perception;
using CompanionOS.Core.Plugins;
using CompanionOS.Core.Recovery;
using CompanionOS.Core.Voice;

namespace CompanionOS.Core.Brain;

/// <summary>
/// The single place where her parts are built and wired together.
///
/// Nothing else in the framework creates its own dependencies. That rule keeps the whole
/// system readable and means the answer to "how does X know about Y?" is always the same:
/// it was handed to it here, once, in a sensible order.
/// </summary>
public sealed class CompanionRuntime : IAsyncDisposable
{
    /// <summary>How often the status strip gets a fresh VRAM/RAM sample while she is awake.</summary>
    private const int TelemetryIntervalMilliseconds = 4000;

    private readonly GpuMemoryProbe _gpuProbe = new();
    private readonly IDisposable _stateSubscription;
    private readonly IDisposable _modeSubscription;
    private readonly SemaphoreSlim _externalModuleRefreshGate = new(1, 1);
    private Timer? _telemetryTimer;
    private WakeHotKey? _wakeHotKey;
    private double _lastVramTotalMegabytes;
    private bool _isDisposed;

    private CompanionRuntime(SettingsStore settings, EventBus events, MindStateMachine mind,
        PersonaStore persona, MemoryGraph graph, SynapticMemoryEngine memory, SandboxGuard sandbox,
        ModuleHost modules, PerceptionEngine perception, VoicePipeline voice, LlamaServerHost serverHost,
        ModelClient modelClient, TurnJournal journal, AssistantBrain brain, PluginHost plugins)
    {
        Settings = settings;
        Events = events;
        Mind = mind;
        Persona = persona;
        Graph = graph;
        Memory = memory;
        Sandbox = sandbox;
        Modules = modules;
        Perception = perception;
        Voice = voice;
        ServerHost = serverHost;
        ModelClient = modelClient;
        Journal = journal;
        Brain = brain;
        Plugins = plugins;

        // Mirror the mind machine onto the event bus so every screen can simply subscribe.
        _stateSubscription = SubscribeToMindState();
        _modeSubscription = SubscribeToOperatingMode();

        // The telemetry timer is the only part of her that periodically starts a child
        // process (nvidia-smi), so it is born switched off and only ever runs while she is
        // awake. See HandleOperatingMode for why that matters.
        _telemetryTimer = new Timer(_ => PublishTelemetry(), null, Timeout.Infinite, Timeout.Infinite);
        HandleOperatingMode(Mind.CurrentMode);
    }

    public SettingsStore Settings { get; }
    public EventBus Events { get; }
    public MindStateMachine Mind { get; }
    public PersonaStore Persona { get; }
    public MemoryGraph Graph { get; }
    public SynapticMemoryEngine Memory { get; }
    public SandboxGuard Sandbox { get; }
    public ModuleHost Modules { get; }
    public PerceptionEngine Perception { get; }
    public VoicePipeline Voice { get; }
    public LlamaServerHost ServerHost { get; }
    public ModelClient ModelClient { get; }
    public TurnJournal Journal { get; }
    public AssistantBrain Brain { get; }

    /// <summary>
    /// The plugin spine: her tools live in the user's folder. This is how the OS grows
    /// without her source changing — new shapes of capability arrive as plugin folders.
    /// </summary>
    public PluginHost Plugins { get; }

    /// <summary>
    /// Reads first-run prerequisites without starting a model, opening a microphone, producing
    /// sound or issuing a network request. Settings uses this to guide setup safely in Gaming
    /// Mode as well as during a normal desktop session.
    /// </summary>
    public CompanionReadiness GetReadiness() =>
        CompanionReadinessProbe.Read(Settings.Current, Voice.GetReadiness());

    /// <summary>Builds every part. Nothing is listening and no model is loaded yet.</summary>
    public static CompanionRuntime Create()
    {
        CompanionPaths.EnsureCreated();
        CompanionLog.Configure(CompanionPaths.LogsDirectory);

        SettingsStore settings = new();
        settings.Load();

        if (!string.IsNullOrWhiteSpace(settings.LoadWarning))
        {
            CompanionLog.Warning("Settings", settings.LoadWarning);
        }

        CompanionLog.SetMinimumLevel(settings.Current.System.LogLevel.ToLowerInvariant() switch
        {
            "trace" => LogLevel.Trace,
            "warning" => LogLevel.Warning,
            "error" => LogLevel.Error,
            _ => LogLevel.Info
        });

        EventBus events = new();
        MindStateMachine mind = new();
        PersonaStore persona = new(CompanionPaths.PersonaFile);
        persona.Load();

        MemoryGraph graph = new(CompanionPaths.BrainDatabaseFile);
        SynapticMemoryEngine memory = new(graph, settings, events);
        FileAuditTrail auditTrail = FileAuditTrail.CreateDefault();
        PermissionGrantStore grants = PermissionGrantStore.CreateDefault();
        SandboxGuard sandbox = new(events, auditTrail, grants);
        ModuleHost modules = new(settings, events, sandbox, memory);

        PerceptionEngine perception = new(settings, events);
        VoicePipeline voice = new(settings, events, persona);
        LlamaServerHost serverHost = new();
        ModelClient modelClient = new();
        TurnJournal journal = TurnJournal.CreateDefault();

        // The plugin spine: her tools live in the user's folder, so they can be developed
        // and updated without touching her code. Scanning is cheap; MCP connections are
        // made lazily on first call.
        PluginHost plugins = new(events);
        plugins.Load();

        AssistantBrain brain = new(settings, events, persona, memory, modules, serverHost, modelClient,
            perception, voice, journal, mind);

        // The brain owns the modules; the modules call back into the brain through the
        // narrow IAssistantHostServices interface. One line, one direction, no circular mess.
        modules.Host = brain;

        return new CompanionRuntime(settings, events, mind, persona, graph, memory, sandbox, modules,
            perception, voice, serverHost, modelClient, journal, brain, plugins);
    }

    /// <summary>
    /// Starts everything: modules, senses, ears, then the model.
    /// Built-in modules are supplied by the application so this assembly stays generic.
    /// </summary>
    public async Task StartAsync(IReadOnlyList<IAssistantModule> builtInModules,
        CancellationToken cancellationToken)
    {
        foreach (IAssistantModule module in builtInModules)
        {
            await Modules.RegisterAsync(module, cancellationToken).ConfigureAwait(false);
        }

        await Plugins.PrepareEnabledAsync(cancellationToken,
            plugin => Modules.IsEnabledSetting($"plugin:{plugin.Id}")).ConfigureAwait(false);

        // Folder plugins are real modules, not just cards in the Body screen. Registering
        // them here gives the router, sandbox and model the same contract as built-ins.
        foreach (PluginManifest plugin in Plugins.Loaded.Where(plugin => !plugin.Disabled))
        {
            await Modules.RegisterAsync(new PluginModuleAdapter(Plugins, plugin), cancellationToken)
                .ConfigureAwait(false);
        }

        CompanionLog.Info("Modules", $"Loaded {Modules.Modules.Count} built-in modules.");
        IrisEvent.Log("Modules", "modules.registered", LogLevel.Info,
            data: new Dictionary<string, string?>
            {
                ["count"] = Modules.Modules.Count.ToString(),
                ["modules"] = string.Join(",", Modules.Modules.Select(module => module.Id))
            });

        Perception.Start();
        IrisEvent.Log("Senses", "senses.started", LogLevel.Info);
        await Voice.StartAsync(cancellationToken).ConfigureAwait(false);

        // The system-wide invoke switch, Siri-style: her name is now the second door,
        // not the only one.
        _wakeHotKey = WakeHotKey.Create(Settings.Current.Voice, () => _ = InvokeWakeSwitchAsync());
        if (_wakeHotKey is null)
        {
            CompanionLog.Info("Voice", "The invoke switch is off (Voice:WakeHotkey is empty).");
        }
        else if (!_wakeHotKey.IsRegistered)
        {
            Events.PublishNotice(
                "The invoke hotkey could not be registered. Her wake phrase still works; " +
                "set Voice:WakeHotkey in her settings to a free combo to try again.",
                NoticeSeverity.Warning);
        }

        await Brain.InitializeAsync(cancellationToken).ConfigureAwait(false);

        // Her brain gets its facts straight before the first turn: episodes that stated
        // an action without an outcome are removed (idempotent — none remain after the
        // first repair).
        int repairedEpisodes = await Memory.ForgetUnverifiedRequestEpisodesAsync(cancellationToken)
            .ConfigureAwait(false);
        if (repairedEpisodes > 0)
        {
            CompanionLog.Info("Memory", $"Brain repair removed {repairedEpisodes} unverified episodes at boot.");
        }
        // The count is a receipt even at zero: "she checks her memories at boot and
        // found nothing fictional this time" has to be visible in the day's records.
        IrisEvent.Log("Memory", "boot.memory_repair", LogLevel.Info,
            data: new Dictionary<string, string?>
            {
                ["removed"] = repairedEpisodes.ToString(),
                ["checked"] = "request episodes without outcomes"
            });
        ReportComponentTruth();
        IrisEvent.Log("Runtime", "boot.done", LogLevel.Info);
        // The one signal the shell gates typed input on: until this lands, sending a
        // message would only hit the cortex while it is still loading.
        Events.Publish(new RuntimeReadyEvent());
    }

    /// <summary>Re-reads external module folders and replaces their router registrations.</summary>
    public async Task RefreshExternalModulesAsync(CancellationToken cancellationToken)
    {
        await _externalModuleRefreshGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            foreach (string moduleId in Plugins.Loaded.Select(plugin => $"plugin:{plugin.Id}").ToArray())
            {
                await Modules.UnregisterAsync(moduleId).ConfigureAwait(false);
            }

            await Plugins.ReloadAsync(cancellationToken,
                plugin => Modules.IsEnabledSetting($"plugin:{plugin.Id}")).ConfigureAwait(false);

            foreach (PluginManifest plugin in Plugins.Loaded.Where(plugin => !plugin.Disabled))
            {
                await Modules.RegisterAsync(new PluginModuleAdapter(Plugins, plugin), cancellationToken)
                    .ConfigureAwait(false);
            }

            IrisEvent.Log("Plugins", "plugins.refreshed", LogLevel.Info,
                data: new Dictionary<string, string?>
                {
                    ["loaded"] = Plugins.Loaded.Count.ToString(),
                    ["rejected"] = Plugins.Rejected.Count.ToString()
                });
        }
        finally
        {
            _externalModuleRefreshGate.Release();
        }
    }

    /// <summary>
    /// One boot-time report about her three faculties: ears, cortex and voice, stated
    /// as they actually are. A faculty that is down is named down with the reason —
    /// she never pretends to a sense she does not have. The voice probe finishes off
    /// the boot path and announces its own verdict the moment the answer lands.
    /// </summary>
    private void ReportComponentTruth()
    {
        List<string> parts = new(3);

        if (Settings.Current.Ears.Enabled && Voice.Recognizer.IsAvailable)
        {
            parts.Add("ears: open, listening on the local speech model");
        }
        else if (Settings.Current.Ears.Enabled)
        {
            parts.Add("ears: deaf — the speech model is missing, so the microphone stays closed");
        }
        else
        {
            parts.Add("ears: disabled in settings");
        }

        if (ServerHost.IsRunning)
        {
            parts.Add("cortex: loaded and reasoning locally");
        }
        else
        {
            parts.Add("cortex: down — the model did not start this session");
        }

        VoiceSettings voice = Settings.Current.Voice;
        string voiceEndpoint = voice.Engine.Equals("pocket", StringComparison.OrdinalIgnoreCase)
            ? voice.PocketEndpoint
            : voice.Engine.Equals("kokoro", StringComparison.OrdinalIgnoreCase)
                ? voice.KokoroEndpoint
                : voice.HttpEndpoint;
        string voiceName = voice.Engine.Equals("kokoro", StringComparison.OrdinalIgnoreCase)
            ? $" (kokoro, voice {(string.IsNullOrWhiteSpace(voice.VoiceId) ? "af_heart" : voice.VoiceId)})"
            : voice.Engine.Equals("pocket", StringComparison.OrdinalIgnoreCase)
                ? $" (pocket, voice {voice.VoiceId})"
                : string.Empty;
        parts.Add($"voice: probing the local neural engine at {voiceEndpoint}{voiceName}");

        Events.PublishNotice("Booted — " + string.Join("; ", parts) + ".", NoticeSeverity.Info);
    }

    /// <summary>
    /// Pushes a RAM and VRAM sample onto the bus for the status strip.
    ///
    /// Skipped unless she is awake, because the sample is not free: <c>nvidia-smi</c> is a
    /// separate process, and spawning one costs roughly 10–20 ms of CPU. That is invisible
    /// while answering a question and very visible inside a 6.9 ms frame (144 fps) of
    /// somebody's game. When she sleeps or hands the GPU to a game there is nothing to
    /// decide with the number either, so it is not measured at all.
    /// </summary>
    /// <param name="force">Measure even when asleep. Used by diagnostics on purpose.</param>
    public void PublishTelemetry(bool force = false)
    {
        if (!force && Mind.CurrentMode != OperatingMode.Awake)
        {
            return;
        }

        try
        {
            (double usedVram, double totalVram) = _gpuProbe.Read();
            _lastVramTotalMegabytes = totalVram;
            double processRam = Environment.WorkingSet / 1024.0 / 1024.0;

            Events.Publish(new TelemetryEvent
            {
                UsedVramMegabytes = usedVram,
                TotalVramMegabytes = totalVram,
                ProcessRamMegabytes = processRam
            });
        }
        catch (Exception exception)
        {
            CompanionLog.Trace("Runtime", $"Telemetry sample failed: {exception.Message}");
        }
    }

    /// <summary>
    /// Tells the status strip what she is using *without* touching the GPU: her own RAM is
    /// a property read, and VRAM is reported as zero because it is genuinely free now.
    /// </summary>
    private void PublishReleasedTelemetry()
    {
        Events.Publish(new TelemetryEvent
        {
            UsedVramMegabytes = 0,
            TotalVramMegabytes = _lastVramTotalMegabytes,
            ProcessRamMegabytes = Environment.WorkingSet / 1024.0 / 1024.0
        });
    }

    public async ValueTask DisposeAsync()
    {
        if (_isDisposed)
        {
            return;
        }

        _isDisposed = true;
        IrisEvent.Log("Runtime", "shutdown.begin", LogLevel.Info);
        _telemetryTimer?.Dispose();
        _telemetryTimer = null;
        _wakeHotKey?.Dispose();
        _stateSubscription.Dispose();
        _modeSubscription.Dispose();

        await Voice.DisposeAsync().ConfigureAwait(false);
        Perception.Dispose();
        await Brain.DisposeAsync().ConfigureAwait(false);
        await Modules.DisposeAsync().ConfigureAwait(false);
        Plugins.Dispose();
        ModelClient.Dispose();
        await Graph.DisposeAsync().ConfigureAwait(false);
        IrisEvent.Log("Runtime", "shutdown.done", LogLevel.Info);
    }

    /// <summary>
    /// The invoke switch, pressed how Siri's side button is pressed: the first press
    /// opens her ears and answers with a quick "yes" cue, the second closes them. When
    /// she was resting or in a game, the same press wakes her first so the switch
    /// always answers.
    /// </summary>
    private async Task InvokeWakeSwitchAsync()
    {
        try
        {
            if (Mind.CurrentMode != OperatingMode.Awake)
            {
                await Brain.SetOperatingModeAsync(OperatingMode.Awake, CancellationToken.None)
                    .ConfigureAwait(false);
            }

            bool opening = !Voice.IsListening;
            Voice.SetListening(opening);

            // A switch that answers nothing feels broken on the day the headset mangles
            // her name; the press therefore speaks, on the shell and in her chatter line.
            string earsLine = opening
                ? "ears open on purpose — go ahead, Boss"
                : "ears closed on purpose";
            Events.Publish(new EarsEvent { State = opening ? "open" : "closed", Text = earsLine });
            Events.Publish(new NoticeEvent
            {
                Severity = NoticeSeverity.Info,
                Message = opening ? "Ears open — go ahead." : "Her ears are closed again (Ctrl+K to open)."
            });

            IrisEvent.Log("Voice", "wake.hotkey", LogLevel.Info,
                data: new Dictionary<string, string?>
                {
                    ["listening"] = opening.ToString(),
                    ["opened"] = Voice.IsListening.ToString()
                });
        }
        catch (Exception exception)
        {
            CompanionLog.Warning("Voice", $"The invoke switch failed: {exception.Message}");
        }
    }

    private IDisposable SubscribeToMindState()
    {
        void Handler(MindState previous, MindState current, string reason) =>
            Events.Publish(new MindStateChangedEvent(previous, current, reason));

        Mind.StateChanged += Handler;
        return new Unsubscriber(() => Mind.StateChanged -= Handler);
    }

    private IDisposable SubscribeToOperatingMode()
    {
        void Handler(OperatingMode previous, OperatingMode current, string reason)
        {
            Events.Publish(new OperatingModeChangedEvent(previous, current, reason));
            HandleOperatingMode(current);
        }

        Mind.ModeChanged += Handler;
        return new Unsubscriber(() => Mind.ModeChanged -= Handler);
    }

    /// <summary>
    /// Starts or stops the telemetry timer to match her mode, and sends one immediate
    /// sample so the status strip is right the instant the mode changes instead of up to
    /// four seconds late.
    ///
    /// Perception keeps running in every mode on purpose: reading the foreground window is
    /// how she notices a game closing and how she knows when the user has been idle. That
    /// poll touches no GPU and starts no process, so it is cheap enough to leave alone.
    /// </summary>
    private void HandleOperatingMode(OperatingMode current)
    {
        try
        {
            if (current == OperatingMode.Awake)
            {
                PublishTelemetry();
                _telemetryTimer?.Change(TelemetryIntervalMilliseconds, TelemetryIntervalMilliseconds);
                return;
            }

            _telemetryTimer?.Change(Timeout.Infinite, Timeout.Infinite);
            PublishReleasedTelemetry();
        }
        catch (Exception exception)
        {
            CompanionLog.Trace("Runtime", $"Mode change telemetry failed: {exception.Message}");
        }
    }

    /// <summary>Minimal unsubscribe handle for plain .NET events.</summary>
    private sealed class Unsubscriber : IDisposable
    {
        private Action? _unsubscribe;

        public Unsubscriber(Action unsubscribe) => _unsubscribe = unsubscribe;

        public void Dispose()
        {
            Action? action = _unsubscribe;
            _unsubscribe = null;
            action?.Invoke();
        }
    }
}
