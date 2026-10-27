namespace CompanionOS.Core.Configuration;

/// <summary>
/// Every knob Iris has, in one serialisable object.
///
/// The rules that shaped this file:
///   * defaults must work on the target machine (RTX 3050, 8 GB VRAM, 16 GB RAM);
///   * no absolute paths and no secrets are written here by hand — the user's
///     settings file overrides the defaults;
///   * each group is a small class so the settings screen can bind to it directly.
/// </summary>
public sealed class CompanionSettings
{
    /// <summary>
    /// The schema generation this build writes. Files stored below it are migrated
    /// exactly once by the settings loader (see SettingsStore.Normalize) and then skip
    /// the legacy heuristics forever — instead of every boot re-deciding what a user's
    /// saved voices meant (audit: this property existed but nothing ever consumed it).
    /// </summary>
    public const int CurrentSchemaVersion = 2;

    /// <summary>
    /// The schema generation of files that predate version stamping. A file whose
    /// SchemaVersion is missing deserializes to this value, which is exactly what
    /// makes the legacy migration run once on an old file — defaulting to the CURRENT
    /// generation instead would silently skip migration for every pre-existing file.
    /// </summary>
    public const int LegacySchemaVersion = 1;

    /// <summary>The file's schema generation; bumped when a setting is renamed.</summary>
    public int SchemaVersion { get; set; } = LegacySchemaVersion;

    public BrainSettings Brain { get; set; } = new();
    public VoiceSettings Voice { get; set; } = new();
    public EarsSettings Ears { get; set; } = new();
    public SenseSettings Senses { get; set; } = new();
    public MemorySettings Memory { get; set; } = new();
    public MusicSettings Music { get; set; } = new();
    public InterfaceSettings Interface { get; set; } = new();
    public SystemSettings System { get; set; } = new();

    /// <summary>The profile whose brain is currently loaded.</summary>
    public BrainProfile ActiveBrainProfile =>
        Brain.Profiles.FirstOrDefault(profile => profile.Id == Brain.ActiveProfileId)
        ?? Brain.Profiles.FirstOrDefault()
        ?? BrainProfile.CreateVisionAgentDefault();
}

/// <summary>How the thinking layer is provided.</summary>
public enum BrainHostKind
{
    /// <summary>CompanionOS starts and stops llama-server itself (sleep and gaming need this).</summary>
    Managed,

    /// <summary>Something else already serves an OpenAI-compatible API (LM Studio, Ollama, a remote host).</summary>
    External
}

/// <summary>One swappable brain. Persona, voice and memory are never stored here.</summary>
public sealed class BrainProfile
{
    public string Id { get; set; } = "vision-agent";
    public string DisplayName { get; set; } = "Vision Agent";
    public BrainHostKind Kind { get; set; } = BrainHostKind.Managed;

    /// <summary>Base URL including the /v1 suffix.</summary>
    public string Endpoint { get; set; } = "http://127.0.0.1:8747/v1";

    /// <summary>Model name reported to the API. llama-server accepts any value; hosted APIs need the real one.</summary>
    public string ModelName { get; set; } = "qwen3-vl-4b";

    /// <summary>Optional bearer token for hosted endpoints. Stored locally, never logged.</summary>
    public string ApiKey { get; set; } = string.Empty;

    /// <summary>Weights file, relative to the models folder unless absolute.</summary>
    public string ModelFile { get; set; } = "brain.gguf";

    /// <summary>Vision projector produced by the same model family. Empty disables vision.</summary>
    public string ProjectorFile { get; set; } = "brain-mmproj.gguf";

    public int ContextTokens { get; set; } = 16384;
    public int GpuLayers { get; set; } = 99;
    public int Threads { get; set; } = 6;
    public double Temperature { get; set; } = 0.7;
    public double TopP { get; set; } = 0.9;

    /// <summary>
    /// 2048 default (was 1024): an agentic turn reasons, calls tools and reports in one
    /// emission, and a 900-1024 ceiling cut exactly the chains that made her feel lazy.
    /// The brain also continues from a "length" cut, so an old settings file can no
    /// longer lose work — it just pays more rounds.
    /// </summary>
    public int MaxResponseTokens { get; set; } = 2048;

    /// <summary>Free-form extra command line switches, e.g. "--flash-attn on".
    /// image-min-tokens comes from llama.cpp itself: Qwen-VL needs at least 1024 image
    /// tokens for grounded picture questions, and its own loader asks for this switch.</summary>
    public string ExtraServerArguments { get; set; } =
        "--flash-attn on --cache-type-k q8_0 --cache-type-v q8_0 --image-min-tokens 1024";

    /// <summary>True when this profile can understand images at all.</summary>
    public bool SupportsVision => !string.IsNullOrWhiteSpace(ProjectorFile);

    public bool HasFullUrl => !string.IsNullOrWhiteSpace(Endpoint);

    /// <summary>The default brain: 4B visual agent, fits the 8 GB budget with room spare.</summary>
    public static BrainProfile CreateVisionAgentDefault() => new();

    /// <summary>A copy with a new identity, used when the user duplicates a profile.</summary>
    public BrainProfile CloneWithNewId(string newId) => new()
    {
        Id = newId,
        DisplayName = DisplayName,
        Kind = Kind,
        Endpoint = Endpoint,
        ModelName = ModelName,
        ApiKey = ApiKey,
        ModelFile = ModelFile,
        ProjectorFile = ProjectorFile,
        ContextTokens = ContextTokens,
        GpuLayers = GpuLayers,
        Threads = Threads,
        Temperature = Temperature,
        TopP = TopP,
        MaxResponseTokens = MaxResponseTokens,
        ExtraServerArguments = ExtraServerArguments
    };
}

public sealed class BrainSettings
{
    public string ActiveProfileId { get; set; } = "vision-agent";

    public List<BrainProfile> Profiles { get; set; } = new()
    {
        BrainProfile.CreateVisionAgentDefault(),
        new BrainProfile
        {
            Id = "roleplay",
            DisplayName = "Roleplay Companion",
            Kind = BrainHostKind.Managed,
            ModelFile = "brain-roleplay.gguf",
            ProjectorFile = string.Empty,
            ContextTokens = 12288,
            Temperature = 0.9,
            MaxResponseTokens = 2048
        },
        new BrainProfile
        {
            Id = "speed",
            DisplayName = "Speed Runner",
            Kind = BrainHostKind.Managed,
            ModelFile = "brain-speed.gguf",
            ProjectorFile = string.Empty,
            ContextTokens = 8192,
            Temperature = 0.6,
            MaxResponseTokens = 1600
        },
        new BrainProfile
        {
            Id = "external",
            DisplayName = "External API",
            Kind = BrainHostKind.External,
            Endpoint = "http://127.0.0.1:1234/v1",
            ModelName = "local-model",
            ProjectorFile = string.Empty
        }
    };

    /// <summary>Unloads the brain after this many idle minutes. Zero keeps it awake.</summary>
    public int AutoSleepIdleMinutes { get; set; } = 20;

    /// <summary>Path to llama-server.exe. Empty means "look in the runtimes folder, then PATH".</summary>
    public string ServerExecutablePath { get; set; } = string.Empty;

    /// <summary>How long to wait for llama-server to answer /health before giving up.</summary>
    public int StartupTimeoutSeconds { get; set; } = 180;

    /// <summary>Sentences are spoken as they arrive; this is the minimum length before speaking.</summary>
    public int MinimumSpokenSentenceCharacters { get; set; } = 12;
}

public sealed class VoiceSettings
{
    /// <summary>Local CPU neural speech. "http" remains available for a user-configured server.</summary>
    public string Engine { get; set; } = "pocket";

    /// <summary>Locked timbre. With the HTTP engine this is the server's voice name.</summary>
    public string VoiceId { get; set; } = "vera";

    /// <summary>OpenAI-compatible speech endpoint used only when Engine is "http".</summary>
    public string HttpEndpoint { get; set; } = "http://127.0.0.1:8880/v1";
    public string HttpModel { get; set; } = "kokoro";
    public string ApiKey { get; set; } = string.Empty;

    /// <summary>Local Pocket TTS HTTP endpoint. Iris starts the server only for the Pocket engine.</summary>
    public string PocketEndpoint { get; set; } = "http://127.0.0.1:18765";

    /// <summary>Optional override; otherwise the bundled runtime under the data root is used.</summary>
    public string PocketExecutablePath { get; set; } = string.Empty;

    /// <summary>Hard thread ceiling for the local speech worker; kept low for games and foreground work.</summary>
    public int PocketMaxThreads { get; set; } = 2;

    /// <summary>CPU model. English 18M is selected for measured latency and bounded footprint.</summary>
    public string PocketLanguage { get; set; } = "english_18m_2026-10";

    /// <summary>
    /// Local Kokoro-82M HTTP endpoint (OpenAI-compatible /audio/speech). Iris starts
    /// the worker only for the Kokoro engine; like Pocket, it is loopback-only.
    /// </summary>
    public string KokoroEndpoint { get; set; } = "http://127.0.0.1:18766";

    /// <summary>Hard thread ceiling for the Kokoro worker's ORT session.</summary>
    public int KokoroMaxThreads { get; set; } = 2;

    /// <summary>
    /// Kokoro voice to fall back to when her profile does not name one. af_heart is
    /// the researched first choice; af_bella is the audition alternative.
    /// </summary>
    public string KokoroDefaultVoice { get; set; } = "af_heart";

    public double Rate { get; set; } = 1.0;
    public double Pitch { get; set; } = 0.0;
    public double Volume { get; set; } = 1.0;

    /// <summary>
    /// The endpoint the configured engine actually speaks through. One resolver for
    /// every caller — the pipeline, the voice picker, the boot status — so no screen
    /// can ever report a port for an engine that is not the one chosen.
    /// </summary>
    public string GetEffectiveEndpoint() => (Engine ?? "pocket").Trim().ToLowerInvariant() switch
    {
        "kokoro" => KokoroEndpoint,
        "http" => HttpEndpoint,
        _ => PocketEndpoint
    };

    /// <summary>Lower music automatically while she speaks.</summary>
    public double MusicDuckLevel { get; set; } = 0.25;

    /// <summary>Speak answers out loud without being asked.</summary>
    public bool SpeakReplies { get; set; } = true;

    /// <summary>
    /// Her one switch for spontaneous chatter: the boot greeting and mode-change
    /// announcements. The old per-action acks ("I'll check") were removed outright —
    /// she now speaks real answers only, unless this switch allows her the niceties.
    /// </summary>
    public bool ReflexPhrasesEnabled { get; set; } = true;

    /// <summary>
    /// The system-wide invoke switch, Siri-style: pressing it anywhere in Windows opens
    /// her ears without needing her wake phrase — press again to close them. "Ctrl+K"
    /// by default; set to empty to disable the switch and keep her wake phrase as the
    /// only voice door.
    /// </summary>
    public string WakeHotkey { get; set; } = "Ctrl+K";

    /// <summary>Stop talking the moment the user speaks.</summary>
    public bool BargeInEnabled { get; set; } = true;

    /// <summary>
    /// When the owner addresses Iris by voice, pause an active Windows media session so speech
    /// recognition is not fighting a video or song. Iris resumes only a session she paused.
    /// </summary>
    public bool PauseMediaWhenListening { get; set; } = true;

    /// <summary>Resume an Iris-paused Windows media session after a completed voice turn.</summary>
    public bool ResumeMediaAfterVoiceTurn { get; set; } = true;

    /// <summary>
    /// True once the user has picked her voice in the first-run step.
    /// While false, the picker is offered again on the next start; it is never
    /// offered silently after that, because a voice that changes by itself is
    /// exactly what makes an assistant feel fake.
    /// </summary>
    public bool ChosenByUser { get; set; }

    /// <summary>What she says in the voice picker, so the choice is made on sound, not labels.</summary>
    public string PreviewSentence { get; set; } =
        "Hey. I'm Iris. This is what I sound like when we talk.";

    /// <summary>
    /// Output device for her voice and her music previews. -1 is the system default;
    /// naming a device means she speaks through those speakers, not the default pair.
    /// Zero means the default WaveOut device slot; -1 is used when the user picks
    /// "Default speakers" so the OS, not the app, decides.
    /// </summary>
    public int OutputDeviceNumber { get; set; } = -1;

    /// <summary>
    /// Speaker names offered by the voice picker. Pocket names are the bundled defaults;
    /// custom HTTP engines may replace them with their own supported speaker ids.
    /// </summary>
    public List<string> CandidateVoices { get; set; } = new()
    {
        "anna", "mary", "vera"
    };
}

public sealed class EarsSettings
{
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// "auto", "whisper" (offline whisper.cpp model), "parakeet" (offline NVIDIA
    /// Parakeet through transcribe.cpp) or "http" (local transcription server).
    /// </summary>
    public string Recognizer { get; set; } = "auto";

    public string WhisperModelFile { get; set; } = "whisper-base.bin";

    /// <summary>
    /// The Parakeet GGUF file in models\ used when the recogniser is "parakeet".
    /// The name matches the download manifest, so a plain install needs no edit here.
    /// </summary>
    public string ParakeetModelFile { get; set; } = "parakeet-unified-en-0.6b-Q5_K_M.gguf";
    public string HttpEndpoint { get; set; } = "http://127.0.0.1:8000/v1";
    public string HttpModel { get; set; } = "whisper-1";
    public string ApiKey { get; set; } = string.Empty;
    public string Language { get; set; } = "auto";

    /// <summary>Microphone device number; -1 means "system default".</summary>
    public int InputDeviceNumber { get; set; } = -1;

    /// <summary>Silence needed before a sentence is considered finished.</summary>
    public int SilenceMilliseconds { get; set; } = 650;

    /// <summary>Ignore blips shorter than this.</summary>
    public int MinimumSpeechMilliseconds { get; set; } = 250;

    /// <summary>Higher values need louder speech before recording starts.</summary>
    public double VoiceThreshold { get; set; } = 0.025;

    /// <summary>Answer only when her name is heard, or when the mic was explicitly opened.</summary>
    public bool RequireWakePhrase { get; set; } = true;

    /// <summary>
    /// How many CPU threads transcription may use. The default is two; higher values
    /// are available up to four for machines where speed matters more than headroom.
    /// </summary>
    public int MaxRecognitionThreads { get; set; } = 2;

    /// <summary>
    /// Utterances shorter than this are never transcribed. A cough, a door or a single
    /// word cannot contain her name, so spending CPU on them is waste. This is the
    /// first, model-free stage of the wake gate.
    /// </summary>
    public int WakeGateMinimumMilliseconds { get; set; } = 420;
}

public sealed class SenseSettings
{
    /// <summary>"off", "ondemand" or "live".</summary>
    public string VisionMode { get; set; } = "ondemand";

    /// <summary>Longest edge of a captured frame. 896 keeps OCR readable and VRAM low.</summary>
    public int CaptureMaxDimension { get; set; } = 896;
    public int JpegQuality { get; set; } = 72;

    /// <summary>How many bits of the perceptual hash must change before a frame counts as new.</summary>
    public int ChangeSensitivity { get; set; } = 6;

    public bool WatchActiveWindow { get; set; } = true;
    public bool WatchMediaSession { get; set; } = true;
    public bool WatchSystemStats { get; set; } = true;
    public bool WatchIdleState { get; set; } = true;

    public int StatsIntervalMilliseconds { get; set; } = 3000;
}

public sealed class MemorySettings
{
    /// <summary>How many hops spreading activation walks.</summary>
    public int IntuitionDepth { get; set; } = 2;

    /// <summary>How many seed nodes a single turn may start from.</summary>
    public int SeedCount { get; set; } = 4;

    /// <summary>Strength lost per day by links that are never used. 1.0 disables decay.</summary>
    public double DailyDecay { get; set; } = 0.997;

    public bool ConsolidationEnabled { get; set; } = true;
    public int ConsolidationIdleMinutes { get; set; } = 25;

    /// <summary>Maximum intuition characters injected into a prompt.</summary>
    public int IntuitionCharacterBudget { get; set; } = 1400;

    /// <summary>Keep this many recent messages in the sliding conversation window.</summary>
    public int ConversationWindowMessages { get; set; } = 16;
}

public sealed class MusicSettings
{
    /// <summary>Where she looks for your music: your real Music folder plus any other
    /// directory you name here. Her drop-in workspace folder is always searched too,
    /// so both "my own collection" and "files I gave her" are first-class.</summary>
    public List<string> SearchDirectories { get; set; } = new()
    {
        Environment.GetFolderPath(Environment.SpecialFolder.MyMusic)
    };

    /// <summary>
    /// Time budget per library scan, in seconds. A hard-coded four seconds used to
    /// cap EVERY play/list/shuffle scan, so a big collection was permanently
    /// truncated and real tracks became invisible — "play X" failing while X existed
    /// was the flagship symptom (2026-10-27 audit). The budget is still enforced
    /// between filesystem entries and a scan that hits it reports its bound.
    /// </summary>
    public int ScanTimeBudgetSeconds { get; set; } = 15;
}

public sealed class InterfaceSettings
{
    public bool ReduceMotion { get; set; }
    public bool ShowOverlay { get; set; } = true;

    /// <summary>When music is active, show the small transport card beside Iris' pill.
    /// The pill is always present; this only controls the contextual player surface.</summary>
    public bool ShowMusicOverlay { get; set; } = true;

    /// <summary>When a countdown is active, show its compact controls near Iris' pill.</summary>
    public bool ShowTimerOverlay { get; set; } = true;
    public bool CloseToOverlay { get; set; } = true;

    /// <summary>Show the "recalled: ..." chips under her answers.</summary>
    public bool ShowRecallChips { get; set; } = true;

    /// <summary>
    /// Where her overlay pill sits: "bottom right", "top right", "bottom left" or
    /// "top left". The pill stays draggable; this is only where she starts.
    /// </summary>
    public string OverlayPosition { get; set; } = "bottom right";

    /// <summary>
    /// The pixel corner she was dragged to, remembered the way the board's caption says:
    /// "position persists". Empty means "use the corner words above". A remembered spot
    /// is only honoured when it hugs a screen edge — a stale coordinate from an older
    /// resolution must never leave the pill floating mid-screen.
    /// </summary>
    public string OverlayPositionPx { get; set; } = "";

    /// <summary>
    /// The music card's own remembered position from dragging, in the pill's "x,y"
    /// format but only trusted while it stays docked near the right screen edge.
    /// Empty means "spawn top right, about a fifth of the screen down".
    /// </summary>
    public string MusicOverlayPositionPx { get; set; } = "";

    /// <summary>When she games, the overlay pill dims to 40 % so it is noticed, not loud.</summary>
    public bool DimWhileGaming { get; set; } = true;
}

public sealed class SystemSettings
{
    public bool GamingModeEnabled { get; set; } = true;
    public bool GamingAutoDetect { get; set; } = true;

    /// <summary>
    /// Set only by an explicit user request (button or command). While true, automatic
    /// foreground detection must not wake Iris merely because an unfamiliar game is active.
    /// It is cleared when the user resumes Awake mode.
    /// </summary>
    public bool ManualGamingMode { get; set; }
    public string LogLevel { get; set; } = "info";

    /// <summary>Learned game executables (lower case, no folder), written by the game sensor.</summary>
    public List<string> KnownGameExecutables { get; set; } = new()
    {
        "cs2.exe", "valorant.exe", "league of legends.exe", "fortniteclient-win64-shipping.exe",
        "gta5.exe", "rdr2.exe", "eldenring.exe", "cyberpunk2077.exe", "minecraft.windows.exe",
        "javaw.exe", "dota2.exe", "apex_legends.exe", "rainbowsix.exe", "overwatch.exe"
    };

    /// <summary>Executables that must never trigger gaming mode.</summary>
    public List<string> GamingExclusions { get; set; } = new();

    /// <summary>
    /// Keep her ears open while a game is running. Off by default: a game is the one
    /// time the machine's CPU belongs to something else. With this off she holds no
    /// microphone, no speech model and no transcription threads until the game closes;
    /// only the foreground-window poll that notices the game ending keeps running.
    /// </summary>
    public bool GamingEarsEnabled { get; set; }

    /// <summary>
    /// When she is told to sleep, shut the process down to a tray icon instead of
    /// merely unloading the model. This is the only setting that means "everything off".
    /// </summary>
    public bool SleepToTray { get; set; } = true;

    /// <summary>
    /// Start her when this user signs in, done by a real registry Run entry. The
    /// switch and the registry never disagree: the switch is written only after the
    /// registry accepted the change, and it re-reads the truth on start-up.
    /// </summary>
    public bool StartWithWindows { get; set; }
}
