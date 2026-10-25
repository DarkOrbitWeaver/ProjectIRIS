# Placeholders, deviations and upgrade paths

This file exists because the honest answer to "is it all done?" is *almost*.

Every item below is a place where the shipped code deliberately does something simpler
than the blueprint describes. None of them is a stub that throws "not implemented", and
none of them silently pretends to work: each one either degrades in a visible way, says so
in the interface, or is a documented seam with a real implementation on the other side of it.

This list covers deliberate simplifications. It is not a claim of end-to-end readiness;
open product features and verification gaps are tracked in `docs/ROADMAP.md` and the audit.

---

## 1. Speech output: Pocket TTS sidecar instead of in-process Kokoro

| | |
|---|---|
| **Blueprint** | Kokoro-82M through KokoroSharp, in-process, `af_heart`, 0 VRAM |
| **Shipped** | Managed Pocket TTS CPU worker with six fixed voices (`alba`, `anna`, `eve`, `mary`, `vera`, `estelle`); optional OpenAI-compatible HTTP server remains available |
| **Why** | A fixed speaker embedding gives a consistent timbre without loading another GPU model or relying on Windows voices. The managed worker starts and stops with Iris |
| **Cost** | One standalone local runtime and a model cache; CPU use is capped at two threads and BelowNormal priority. Voice quality and timbre still need a subjective listening pass |
| **Memory / VRAM** | CPU only; the roughly 106.6 MiB core model and voice assets stay on disk/RAM, with no VRAM allocation |
| **Upgrade path** | Keep `VoiceProfile` as the stable identity. Consider a replacement only after repeatable latency, quality, CPU and disk comparisons on this machine |

Where it lives: `Core/Voice/PocketTts.cs`, `Core/Voice/SpeechSynthesis.cs`, `Core/Voice/VoiceProfile.cs`.
Setting: `Voice.Engine` = `pocket` or explicitly configured `http`.

## 2. Speech input: Whisper instead of SenseVoice

| | |
|---|---|
| **Blueprint** | SenseVoice-small through sherpa-onnx (also detects emotion and audio events) |
| **Shipped** | `WhisperNetRecognizer` (whisper.cpp, ggml-base model, CPU) and `HttpSpeechRecognizer` (any `/v1/audio/transcriptions` endpoint) |
| **Why** | A managed, restorable, cross-checkable package beats a native-only binding we cannot test here. Whisper base is ~150 MB and 5–10× faster than real time on a mid-range CPU |
| **Cost** | No emotion or event tags from the audio; language must be given or guessed |
| **Upgrade path** | Add `SenseVoiceRecognizer : ISpeechRecognizer` next to the existing two. The pipeline only needs `RecognizeAsync(pcm) → text` |

Where it lives: `Core/Voice/SpeechRecognition.cs`. Setting: `Ears.Recognizer`.

## 3. Voice activity detection: adaptive energy, not Silero

| | |
|---|---|
| **Blueprint** | Silero VAD (ONNX) |
| **Shipped** | `MicrophoneCapture` + `VoicePipeline` measure energy and zero-crossing rate, learn the room's noise floor, and use hysteresis plus a silence timer |
| **Why** | No ONNX runtime dependency, no 2 MB model to download, starts instantly, and on a desktop microphone it is genuinely good enough |
| **Cost** | A loud fan or a TV can hold the floor open; very quiet speech can be missed. Both thresholds are settings |
| **Upgrade path** | Replace the decision inside `VoicePipeline.OnMicrophoneSamples` with a Silero session; the surrounding state machine does not change |

Settings: `Ears.VoiceThreshold`, `Ears.SilenceMilliseconds`, `Ears.MinimumSpeechMilliseconds`.

## 4. Wake word: recognised, not spotted

| | |
|---|---|
| **Blueprint** | openWakeWord / Porcupine, always-on keyword spotter |
| **Shipped** | The ears open on voice activity, transcribe the utterance, and `WakePhraseMatcher` decides whether she was addressed — "hey iris", "wsp iris", "yo iris", "iris are you there" and about twenty more variants |
| **Why** | It removes a model, an API key and a native dependency, and it understands far more greetings than a fixed spotter. It also makes the *whole* utterance available, so "hey iris, play something" is one turn |
| **Cost** | One short transcription per utterance while the mic is open, so a mid-range CPU spends a few hundred milliseconds on non-requests. That is why the microphone can be closed with one key (`Ctrl+M`) and why `Ears.RequireWakePhrase` can be turned off once she is in a conversation |
| **Upgrade path** | A keyword spotter becomes a cheap pre-filter in front of the same pipeline: only open the recogniser when the spotter fires |

Where it lives: `Core/Voice/WakePhraseMatcher.cs`, `Core/Voice/VoicePipeline.cs`.

## 5. Vector search: cosine in process, not sqlite-vec

| | |
|---|---|
| **Blueprint** | sqlite-vec virtual tables |
| **Shipped** | Node vectors are stored as float blobs in the same SQLite file and compared in memory (normalised dot product) |
| **Why** | No native extension to load, no platform-specific binary, identical results, and the whole graph is small |
| **Cost** | O(n) per recall. Measured against the real budget: 20 000 nodes of 384 floats is 30 MB of floats and a few milliseconds per query — fine. At 200 000 nodes it would start to hurt |
| **Upgrade path** | `MemoryGraph.SearchByVector` is the only method that does this. Load `vec0` and turn it into a `MATCH` query, or keep a memory-mapped index file |

## 6. Embeddings: hashed lexical vectors, not MiniLM

| | |
|---|---|
| **Blueprint** | all-MiniLM-L6-v2 ONNX embeddings |
| **Shipped** | `TextVectorizer` — lower-casing with punctuation stripping, stop-word removal, feature hashing into 384 dimensions with a stable sign, a 0.6 down-weight on word pairs, then L2 normalisation. There is no stemming and no sublinear term scaling |
| **Why** | Deterministic, zero downloads, instant, and good at the thing that matters here: "the same words" and "the same idea with different words" overlap enough to find the right seed nodes. The typed synapse graph then does the semantic work that a bag of words cannot |
| **Cost** | Paraphrase recall is weaker than a real sentence embedder |
| **Upgrade path** | Drop in an ONNX session in `TextVectorizer.Vectorize` and keep the same 384 dimensions; every stored node keeps working |

## 7. MCP bridge: contract only

| | |
|---|---|
| **Blueprint** | Official MCP C# SDK, so any MCP server can become a body part |
| **Shipped** | Nothing. Modules can already be external processes (`plugin.json` + stdio JSON lines), which is the same shape MCP uses |
| **Why** | An unverifiable dependency in the most failure-prone area (transport, capability negotiation, auth) is worse than a documented seam |
| **Upgrade path** | One `McpModule : IAssistantModule` that connects, lists tools, and maps each MCP tool to a `ModuleTool` with the tier you choose. `ExternalCommandModule` is the working blueprint for the lifecycle |

## 8. Interface: no 3D avatar, no guided tour

| | |
|---|---|
| **Blueprint** | Later milestone: rigged 3D figure, onboarding tour with voice |
| **Shipped** | The status orb (nine real states), the full shell, the overlay pill. First run explains itself in the chat instead of a multi-step tour |
| **Why** | A 3D body is milestone 7 and would have delayed a working companion. The orb is driven by the same state machine an avatar would use, so the swap is local |
| **Upgrade path** | Replace `Controls/StatusOrb.cs` with an avatar control; `ShellModel.State`, `SpeechLevel` and the event bus already carry everything it needs |

## 9. Screen capture: GDI copy, not Desktop Duplication

| | |
|---|---|
| **Blueprint** | Desktop Duplication API + damage rectangles |
| **Shipped** | `Graphics.CopyFromScreen` into a 32-bit bitmap, downscale to 896 px, dHash to detect change, JPEG encode |
| **Why** | Twenty lines instead of a DXGI pipeline, no COM interop, no adapter resets. The dHash comparison already prevents needless captures |
| **Cost** | Cannot capture exclusive-fullscreen games (Gaming Mode is off then anyway), no cursor, slightly slower |
| **Upgrade path** | `ScreenCapture.Capture` is the whole surface: swap its body for a DXGI implementation and the perceptual layer stays |

## 10. GPU numbers: nvidia-smi, not NVML

`GpuMemoryProbe` runs `nvidia-smi --query-gpu` once every four seconds. No NVML binding, no
driver dependency at compile time. Without nvidia-smi the status strip simply shows no VRAM
figure, and nothing else changes.

## 11. Consolidation is arithmetic, not a second model call

`MemoryGraph.ConsolidateAsync` merges near-duplicate titles, fades weak links and prunes
orphans. The blueprint's version asks the brain to summarise clusters. Arithmetic is
predictable, free, and cannot invent a memory that was never there. A summarising pass can
be added later as a background job that *proposes* merges, with the same consolidation code
applying them.

## 12. Interface motion

There are no XAML animations or transitions: every moving element (orb, microphone meter,
graph drift) is drawn per frame by its own control at 30–60 fps, and everything respects
`Interface.ReduceMotion`. The blueprint's spring physics (300/25) is approximated by easing
in those render loops. This keeps all styling in C#, which is checkable without a design-time
XAML toolchain.

## 13. Global hotkeys

`Ctrl+M` (ears), `Ctrl+Shift+S` (screen), `Ctrl+1`–`Ctrl+4` (screens), `Esc` (interrupt)
work while a CompanionOS window has focus. System-wide registration (`RegisterHotKey` + a
WndProc hook) is not wired: it needs a platform hook that a first build should not depend
on. The overlay window is the always-available path in the meantime.

## 14. First-run downloads are not hash-pinned

`scripts/setup.ps1` resolves the newest matching artifact instead of pinning a version and a
SHA-256. That keeps the script working as quantisations are renamed, but it means the bytes
you get are whatever the repository serves that day. Add `sha256` fields to
`scripts/models.manifest.json` and verify them in `Save-Download` if you want pinning.

---

## 15. Sleep: the process exits, and a tray icon is what is left

| | |
|---|---|
| **Blueprint** | Sleep unloads the model; ears, reflexes and perception keep running so the wake word still works |
| **Shipped** | `MainWindow.SleepAsync()` asks the application to create the notification-area icon **first**, then disposes the runtime, closes both windows and leaves nothing running but that icon |
| **Why** | The requirement changed: *sleep must switch off every one of her functions*, not just unload the brain. Half-asleep is the worst of both worlds — the microphone is open, the transcription threads exist, and the user still cannot see her |
| **What is off** | The llama-server child process, the Whisper model and its threads, the microphone stream, the VAD timer, the ambient poll, the 4-second `nvidia-smi` sample, the TTS engine, the SQLite connection pool (WAL is checkpointed on close), every timer in the interface. `CompanionRuntime.DisposeAsync()` is the single list |
| **What is on** | One `TrayIcon` — an icon, a tooltip and a three-item menu |
| **Cost** | **No wake word while asleep.** Coming back takes a relaunch, about 1.5 s on an SSD, and it is a new process, not a revived one: audio devices and model hosts are not resurrected by design |
| **Why a new process** | A resumed process would have to re-open the microphone, re-spawn `llama-server`, re-scan modules and re-verify the graph — that is the whole of `CompanionRuntime.Create()` anyway, minus the risk of a half-closed handle. Relaunching is strictly more reliable |
| **Failure path** | If the tray icon cannot be created (no shell, no `Assets/iris.ico`), she refuses to sleep and instead uses the old *rest* path with a visible notice. She is never left running with nothing on screen |
| **Upgrade path** | Wake-on-voice while asleep needs a tiny always-on keyword spotter (<30 MB, CPU) outside this process. That is a deliberate later feature, not an oversight |

## 16. Gaming Mode with her ears off

| | |
|---|---|
| **Blueprint** | Gaming Mode frees VRAM and keeps reflexes available |
| **Shipped** | `System.GamingEarsEnabled` (default `false`). When off, entering Gaming Mode calls `VoicePipeline.SuspendListening()`, which swaps the recogniser for `SuspendedRecognizer` and closes the microphone |
| **Why** | A running game is the one time the machine's CPU belongs to something else. Transcription defaults to two threads (up to four if explicitly configured); even that is not a hard real-time guarantee for every game |
| **Cost** | She cannot be spoken to during a game unless the setting is turned on. The overlay and hotkeys still work |
| **Learning** | Game detection itself keeps running: it is a foreground-window read every few seconds, which starts no process and touches no GPU |

## 17. The voice picker does not pre-flight every voice

`VoiceGallery.DescribeCandidates()` lists what *should* exist and costs nothing. Audio is only
rendered when a candidate is clicked, and a candidate this machine cannot produce says so on
the card rather than disappearing. Rendering all six up front would mean minutes of synthesis
on first run and would hide the one voice the user actually wanted behind a spinner.

---

## Things that are genuinely not there yet

These are *feature gaps*, not deviations, and they are listed in `docs/ROADMAP.md`:

* 3D avatar body (milestone 7)
* MCP bridge (see 7 above)
* Guided onboarding tour (see 8 above)
* Wake word while asleep — sleeping is process exit by design (see 15 above), and the tray
  icon is her always-available form
* Multi-monitor capture beyond the primary screen
* A single omni model (vision + roleplay + voice in and out). See `docs/DECISIONS.md` §31:
  this is a **hardware** question, not a missing-code question

This list is not a complete feature checklist. Use `docs/ROADMAP.md` for the remaining product
work and verify each feature on the target machine before treating it as ready for daily use.
