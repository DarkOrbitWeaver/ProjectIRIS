using System.Runtime.InteropServices;
using CompanionOS.Core.Configuration;
using CompanionOS.Core.Diagnostics;

namespace CompanionOS.Core.Voice;

/// <summary>
/// Offline speech recognition through the transcribe.cpp native library, running the
/// NVIDIA Parakeet Unified EN 0.6B model (`parakeet-unified-en-0.6b-Q5_K_M.gguf`).
///
/// Why this shape and not another Whisper size: Parakeet is materially more accurate
/// on conversational English than whisper-base — 3.2% versus 11.6% word error rate on
/// the published LibriSpeech test-other CPU benchmark — at 541 MB on disk, and its
/// RNN-T decoder stays faster than real time on the two threads the ears budget
/// allows. The engine is the official prebuilt `transcribe-native-0.4.1` package,
/// driven through its narrow versioned C ABI; the model is memory-mapped, so load is
/// instant and disposing the recognizer (Sleep, Gaming, settings change) hands the
/// memory straight back to the operating system.
///
/// The GPU promise is structural, not behavioural: `allowed_backends` carries only the
/// CPU bit, so the Vulkan helper DLLs that ship beside the library are never
/// initialized and never touch the RTX card, no matter what state the machine is in.
///
/// Known native limitation of 0.x: at most one transcription may run at a time per
/// model. The pipeline feeds the recogniser one finished segment at a time and this
/// adapter serialises everything behind one session lock, so the contract is met by
/// construction. Cancellation is sampled before a run; a segment is at most ~30 s of
/// audio and decodes in a couple of seconds, so no mid-run abort hook is wired.
///
/// License note: the model weights carry NVIDIA's Open Model License, which the owner
/// explicitly accepted when choosing this engine (see docs/DECISIONS.md). The runtime
/// itself is MIT and its licenses ship inside the runtime folder.
/// </summary>
public sealed class ParakeetRecognizer : ISpeechRecognizer
{
    private const string RuntimeFolderName = "transcribe-native-windows-x86_64-cpu-vulkan";
    private const string NativeLibraryFileName = "transcribe.dll";
    private const string DefaultModelFile = "parakeet-unified-en-0.6b-Q5_K_M.gguf";

    /// <summary>transcribe.h: TRANSCRIBE_OK == 0; every other value is a failure status.</summary>
    private const int TranscribeOk = 0;

    /// <summary>transcribe.h TRANSCRIBE_BACKEND_MASK_CPU — the only bit the backends registry may load.</summary>
    private const uint BackendMaskCpu = 0x00000001u;

    /// <summary>transcribe.h TRANSCRIBE_BACKEND_CPU — strict CPU, no GPU, no host accelerators.</summary>
    private const int BackendRequestCpu = 1;

    /// <summary>transcribe_abi_struct ids for the three structs this adapter builds.</summary>
    private const int AbiModelLoadParams = 0;
    private const int AbiSessionParams = 1;
    private const int AbiBackendInitParams = 15;

    /// <summary>transcribe_log_level values this adapter surfaces.</summary>
    private const int LogLevelInfo = 1;
    private const int LogLevelWarn = 2;
    private const int LogLevelError = 3;

    private static readonly object _nativeInitLock = new();
    private static bool _nativeReady;
    private static string? _nativeFailure;
    private static string _runtimeVersion = "unknown";

    /// <summary>Struct sizes the native library reported through transcribe_abi_struct_size.</summary>
    private static ulong _modelLoadStructSize;
    private static ulong _sessionStructSize;

    /// <summary>Rooted in a static so the garbage collector can never free a callback native code may still call.</summary>
    private static readonly TranscribeLogCallback _logCallback = OnNativeLog;

    private readonly string _modelPath;
    private readonly string _runtimeLibraryPath;
    private readonly int _threadCount;
    private readonly object _sessionLock = new();

    /// <summary>Native session, opened on first use. It owns the loaded model (the transcribe_open path).</summary>
    private IntPtr _session;
    private string? _sessionFailure;

    public ParakeetRecognizer(EarsSettings ears)
    {
        _modelPath = CompanionPaths.Model(string.IsNullOrWhiteSpace(ears.ParakeetModelFile)
            ? DefaultModelFile
            : ears.ParakeetModelFile);
        _runtimeLibraryPath = Path.Combine(CompanionPaths.RuntimesDirectory, RuntimeFolderName, NativeLibraryFileName);
        _threadCount = ResolveThreadCount(ears.MaxRecognitionThreads);
    }

    /// <summary>"parakeet", used in logs, diagnostics and decision events.</summary>
    public string EngineName => "parakeet";

    /// <summary>True only when both the model file and the native runtime are installed.</summary>
    public bool IsAvailable => File.Exists(_modelPath) && File.Exists(_runtimeLibraryPath);

    /// <summary>How many CPU threads transcription may touch — the same budget whisper runs under.</summary>
    public int ThreadCount => _threadCount;

    public async Task<SpeechRecognitionResult> TranscribeAsync(float[] samples, CancellationToken cancellationToken)
    {
        if (samples.Length == 0)
        {
            return new SpeechRecognitionResult(SpeechRecognitionOutcome.NoSpeech);
        }

        if (!IsAvailable)
        {
            // Name which half of the install is missing so a broken setup is one log
            // line away from obvious.
            string failure = !File.Exists(_modelPath)
                ? "model_missing"
                : "runtime_missing";
            return new SpeechRecognitionResult(SpeechRecognitionOutcome.Unavailable, FailureCode: failure);
        }

        try
        {
            return await Task.Run(() => RunTranscription(samples, cancellationToken), cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return new SpeechRecognitionResult(SpeechRecognitionOutcome.Cancelled);
        }
        catch (Exception exception)
        {
            CompanionLog.Warning("Voice", $"Parakeet could not transcribe ({exception.GetType().Name}).");
            return new SpeechRecognitionResult(SpeechRecognitionOutcome.Failed,
                FailureCode: exception.GetType().Name);
        }
    }

    public void Dispose()
    {
        lock (_sessionLock)
        {
            if (_session == IntPtr.Zero)
            {
                return;
            }

            // Session and model are freed in one call: transcribe_open made the session
            // own the model it loaded, exactly so callers need no second bookkeeping.
            transcribe_session_free(_session);
            _session = IntPtr.Zero;
        }
    }

    /// <summary>Loads the native runtime once per process, then opens the session once per recognizer.</summary>
    private SpeechRecognitionResult RunTranscription(float[] samples, CancellationToken cancellationToken)
    {
        if (!EnsureNativeReady())
        {
            return new SpeechRecognitionResult(SpeechRecognitionOutcome.Unavailable,
                FailureCode: _nativeFailure ?? "native_unavailable");
        }

        ThreadPriority previousPriority = ThreadPriority.Normal;
        bool priorityLowered = TryLowerPriority(ref previousPriority);
        GCHandle pinned = GCHandle.Alloc(samples, GCHandleType.Pinned);
        try
        {
            cancellationToken.ThrowIfCancellationRequested();

            lock (_sessionLock)
            {
                if (_session == IntPtr.Zero)
                {
                    OpenSessionLocked();
                }

                if (_session == IntPtr.Zero)
                {
                    return new SpeechRecognitionResult(SpeechRecognitionOutcome.Failed,
                        FailureCode: _sessionFailure ?? "session_open_failed");
                }

                // Run params NULL: the library's defaults — TRANSCRIBE task, auto
                // timestamps, no language hint (Parakeet is English-only), special
                // vocabulary tags stripped from the returned text.
                int status = transcribe_run(_session, pinned.AddrOfPinnedObject(), samples.Length, IntPtr.Zero);
                if (status != TranscribeOk)
                {
                    CompanionLog.Warning("Voice", $"Parakeet transcription failed: {DescribeStatus(status)}.");
                    return new SpeechRecognitionResult(SpeechRecognitionOutcome.Failed,
                        FailureCode: $"transcribe_status_{status}");
                }

                // The returned pointer is borrowed and valid only until the next run;
                // we hold the session lock, so the copy happens inside that guarantee.
                string text = ReadFullText();
                return string.IsNullOrWhiteSpace(text)
                    ? new SpeechRecognitionResult(SpeechRecognitionOutcome.NoSpeech)
                    : new SpeechRecognitionResult(SpeechRecognitionOutcome.Recognized, text);
            }
        }
        finally
        {
            pinned.Free();
            if (priorityLowered)
            {
                RestorePriority(previousPriority);
            }
        }
    }

    /// <summary>
    /// One-time per-process native bring-up: load transcribe.dll with its own folder on
    /// the loader search path, verify the ABI struct sizes this adapter was written
    /// against, mute nothing but surface native log lines into her own log, register
    /// backends restricted to the CPU mask, and record the runtime version.
    /// </summary>
    private bool EnsureNativeReady()
    {
        if (_nativeReady)
        {
            return true;
        }

        lock (_nativeInitLock)
        {
            if (_nativeReady)
            {
                return true;
            }

            if (_nativeFailure is not null)
            {
                return false;
            }

            try
            {
                // LOAD_LIBRARY_SEARCH_DLL_LOAD_DIR keeps the ggml helper DLLs resolvable
                // from the runtime folder no matter where the application started from.
                NativeLibrary.Load(_runtimeLibraryPath);

                _modelLoadStructSize = transcribe_abi_struct_size(AbiModelLoadParams);
                _sessionStructSize = transcribe_abi_struct_size(AbiSessionParams);
                ulong backendInitSize = transcribe_abi_struct_size(AbiBackendInitParams);

                // 0 means "struct unknown to this library" per the header; treating it
                // as an error beats silently passing a struct the library cannot read.
                bool sizesKnown = _modelLoadStructSize != 0 && _sessionStructSize != 0 && backendInitSize != 0;
                bool sizesMatch =
                    Marshal.SizeOf<ModelLoadParams>() == checked((int)_modelLoadStructSize) &&
                    Marshal.SizeOf<SessionParams>() == checked((int)_sessionStructSize);
                if (!sizesKnown || !sizesMatch)
                {
                    throw new InvalidOperationException(
                        "transcribe.dll does not report the struct sizes this adapter was written for; " +
                        $"expected model={Marshal.SizeOf<ModelLoadParams>()}, session={Marshal.SizeOf<SessionParams>()}, " +
                        $"got model={_modelLoadStructSize}, session={_sessionStructSize}, backend={backendInitSize}.");
                }

                transcribe_log_set(_logCallback, IntPtr.Zero);

                var backendInit = new BackendInitParams
                {
                    StructSize = backendInitSize,
                    // NULL: package-local — the folder containing transcribe.dll itself,
                    // which is where the ggml backend modules of this package live.
                    ArtifactDirectory = IntPtr.Zero,
                    AllowedBackends = BackendMaskCpu
                };
                int initStatus = transcribe_init_backends_ex(ref backendInit);
                if (initStatus != TranscribeOk)
                {
                    throw new InvalidOperationException($"backend registration failed: {DescribeStatus(initStatus)}");
                }

                if (!transcribe_backend_available(BackendRequestCpu))
                {
                    throw new InvalidOperationException("the CPU backend did not register.");
                }

                IntPtr version = transcribe_version();
                _runtimeVersion = version != IntPtr.Zero
                    ? Marshal.PtrToStringUTF8(version) ?? "unknown"
                    : "unknown";

                _nativeReady = true;
                CompanionLog.Info("Voice",
                    $"Parakeet runtime ready: transcribe {_runtimeVersion}, backend registry restricted to CPU " +
                    $"(mask {BackendMaskCpu}); the GPU was never touched.");
            }
            catch (Exception exception)
            {
                _nativeFailure = "native_unavailable";
                CompanionLog.Warning("Voice",
                    $"transcribe.cpp runtime could not load: {exception.GetType().Name} — {exception.Message}");
            }

            return _nativeReady;
        }
    }

    /// <summary>Opens the native session on first use. The caller holds _sessionLock.</summary>
    private void OpenSessionLocked()
    {
        // Backend = strict CPU even though the registry only contains CPU devices
        // already: the request itself is the durable statement of intent.
        var load = new ModelLoadParams
        {
            StructSize = _modelLoadStructSize,
            Backend = BackendRequestCpu,
            Device = IntPtr.Zero,
            NoMmap = 0
        };
        var session = new SessionParams
        {
            StructSize = _sessionStructSize,
            NThreads = _threadCount,
            KvType = 0, // AUTO
            NCtx = 0    // model maximum; chunked families ignore it anyway
        };

        IntPtr path = Marshal.StringToCoTaskMemUTF8(_modelPath);
        try
        {
            int status = transcribe_open(path, ref load, ref session, out IntPtr opened);
            if (status != TranscribeOk || opened == IntPtr.Zero)
            {
                _sessionFailure = DescribeStatus(status);
                CompanionLog.Warning("Voice", $"Parakeet model refused to load: {_sessionFailure}.");
                return;
            }

            _session = opened;
            CompanionLog.Info("Voice",
                $"Parakeet ears loaded: {Path.GetFileName(_modelPath)}, {_threadCount} threads, CPU backend " +
                $"(runtime {_runtimeVersion}).");
        }
        finally
        {
            Marshal.FreeCoTaskMem(path);
        }
    }

    private string ReadFullText()
    {
        IntPtr text = transcribe_full_text(_session);
        return text == IntPtr.Zero
            ? string.Empty
            : (Marshal.PtrToStringUTF8(text) ?? string.Empty).Trim();
    }

    private static string DescribeStatus(int status)
    {
        IntPtr description = transcribe_status_string(status);
        string text = description != IntPtr.Zero ? Marshal.PtrToStringUTF8(description) ?? string.Empty : string.Empty;
        return string.IsNullOrWhiteSpace(text) ? $"status {status}" : $"{status}: {text}";
    }

    private static int ResolveThreadCount(int configuredThreads)
    {
        int cores = Math.Max(2, Environment.ProcessorCount);
        return configuredThreads > 0
            ? Math.Clamp(configuredThreads, 1, Math.Min(4, cores))
            : Math.Clamp(2, 1, Math.Min(4, cores));
    }

    // The two helpers below mirror WhisperNetRecognizer's behaviour one file at a
    // time: the thread cap is the real guarantee, priority only decides who wins
    // when two capped jobs compete for the same core.

    private static bool TryLowerPriority(ref ThreadPriority previousPriority)
    {
        try
        {
            Thread currentThread = Thread.CurrentThread;
            previousPriority = currentThread.Priority;
            if (previousPriority <= ThreadPriority.BelowNormal)
            {
                return false;
            }

            currentThread.Priority = ThreadPriority.BelowNormal;
            return true;
        }
        catch (Exception exception)
        {
            CompanionLog.Trace("Voice", $"Could not lower the transcription thread priority: {exception.Message}");
            return false;
        }
    }

    private static void RestorePriority(ThreadPriority previousPriority)
    {
        try
        {
            Thread.CurrentThread.Priority = previousPriority;
        }
        catch (Exception exception)
        {
            CompanionLog.Trace("Voice", $"Could not restore the thread priority: {exception.Message}");
        }
    }

    private static void OnNativeLog(int level, IntPtr message, IntPtr userData)
    {
        if (message == IntPtr.Zero)
        {
            return;
        }

        string text = Marshal.PtrToStringUTF8(message) ?? string.Empty;
        if (string.IsNullOrWhiteSpace(text))
        {
            return;
        }

        switch (level)
        {
            case LogLevelInfo:
                CompanionLog.Info("Voice", $"transcribe: {text}");
                break;
            case LogLevelWarn:
            case LogLevelError:
                CompanionLog.Warning("Voice", $"transcribe: {text}");
                break;
            default:
                CompanionLog.Trace("Voice", $"transcribe: {text}");
                break;
        }
    }

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void TranscribeLogCallback(int level, IntPtr message, IntPtr userData);

    /// <summary>transcribe_backend_init_params — CPU-only backend registry request.</summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct BackendInitParams
    {
        public ulong StructSize;
        public IntPtr ArtifactDirectory;
        public uint AllowedBackends;
    }

    /// <summary>
    /// transcribe_model_load_params. `Device` is a borrowed native handle
    /// (transcribe_device_t is a pointer); zero keeps the library default.
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct ModelLoadParams
    {
        public ulong StructSize;
        public int Backend;
        public IntPtr Device;
        public byte NoMmap;
    }

    /// <summary>transcribe_session_params — n_threads is the only field this adapter sets.</summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct SessionParams
    {
        public ulong StructSize;
        public int NThreads;
        public int KvType;
        public int NCtx;
    }

    [DllImport("transcribe.dll", ExactSpelling = true, CallingConvention = CallingConvention.Cdecl)]
    private static extern ulong transcribe_abi_struct_size(int which);

    [DllImport("transcribe.dll", ExactSpelling = true, CallingConvention = CallingConvention.Cdecl)]
    private static extern void transcribe_log_set(TranscribeLogCallback callback, IntPtr userData);

    [DllImport("transcribe.dll", ExactSpelling = true, CallingConvention = CallingConvention.Cdecl)]
    private static extern int transcribe_init_backends_ex(ref BackendInitParams parameters);

    [DllImport("transcribe.dll", ExactSpelling = true, CallingConvention = CallingConvention.Cdecl)]
    [return: MarshalAs(UnmanagedType.U1)]
    private static extern bool transcribe_backend_available(int kind);

    [DllImport("transcribe.dll", ExactSpelling = true, CallingConvention = CallingConvention.Cdecl)]
    private static extern IntPtr transcribe_version();

    [DllImport("transcribe.dll", ExactSpelling = true, CallingConvention = CallingConvention.Cdecl)]
    private static extern IntPtr transcribe_status_string(int status);

    [DllImport("transcribe.dll", ExactSpelling = true, CallingConvention = CallingConvention.Cdecl)]
    private static extern int transcribe_open(
        IntPtr path,
        ref ModelLoadParams loadParams,
        ref SessionParams sessionParams,
        out IntPtr session);

    [DllImport("transcribe.dll", ExactSpelling = true, CallingConvention = CallingConvention.Cdecl)]
    private static extern int transcribe_run(IntPtr session, IntPtr pcm, int nSamples, IntPtr runParams);

    [DllImport("transcribe.dll", ExactSpelling = true, CallingConvention = CallingConvention.Cdecl)]
    private static extern IntPtr transcribe_full_text(IntPtr session);

    [DllImport("transcribe.dll", ExactSpelling = true, CallingConvention = CallingConvention.Cdecl)]
    private static extern void transcribe_session_free(IntPtr session);
}
