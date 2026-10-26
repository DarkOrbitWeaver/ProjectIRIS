# Architecture

This document is the map. The locked specification lives in `docs/BLUEPRINT.md`; this file
explains how the code in this repository implements it.

---

## 1. The two-speed mind

```
        CORTEX  (GPU, unloads on command)
        llama-server -> 4B multimodal model
        reasoning · vision · tools · roleplay
                    ▲
                    │  prompts and tool results
                    ▼
        BRAINSTEM  (CPU, never unloads)
        ears · wake matcher · voice · reflex cache
        memory database · ambient sensors · sandbox
```

The cortex is a **child process**. Sleep, gaming mode and crash recovery all depend on
being able to kill it and get every byte of VRAM back, which an in-process model can never
promise.

The brainstem stays alive, so when the model is unloaded she still hears her name, answers
with cached lines, keeps music playing, and can be woken in about two seconds.

Files: `Core/Brain/LlamaServerHost.cs`, `Core/Brain/ModelClient.cs`,
`Core/Brain/AssistantBrain.cs`.

---

## 2. One turn, in order

`AssistantBrain.RunTurnAsync` always does the same seven things:

1. **Low-power check** — if the cortex is unloaded, answer honestly from reflexes, or wake
   up when the user asks.
2. **Memory** — `SynapticMemoryEngine.ActivateAsync` finds seeds by similarity, walks
   outward/inward/across danger links, and renders a ~200 token plain-English block.
3. **Routing** — `ModuleHost.RouteTools` picks at most two modules plus the always-on core
   tools, so the model never sees more than eight tools.
4. **Prompt** — persona template + situation + intuition + capability map + recent history.
5. **Stream** — tokens are published as `ConversationEvent`s; speech can begin before the
   answer ends.
6. **Tools** — execute through the sandbox, feed results back, repeat up to four times.
7. **Speak and journal** — say it, then record that it happened.

Everything the interface shows comes from steps 2–7 as events.

---

## 3. The event bus

`Core/Events/EventBus.cs` — one in-process pub/sub. Publishing is synchronous and
allocation-free; a throwing subscriber is logged and skipped.

| Event | Meaning | Consumed by |
|---|---|---|
| `MindStateChangedEvent` | idle, listening, thinking, acting, speaking, sleeping, gaming | orb, header, overlay |
| `OperatingModeChangedEvent` | awake / sleeping / gaming | status strip, overlay |
| `ConversationEvent` | one phase of a turn (user, delta, tool, completed) | chat |
| `MemoryRecallEvent` | what memory contributed, for the chips | chat |
| `MemoryChangedEvent` | a node or link changed | graph view |
| `SpeechEvent`, `AudioLevelEvent` | her voice and both loudness meters | orb, overlay |
| `TranscriptEvent` | recognised user speech | chat, status |
| `ModelStatusEvent` | loading, ready, unloaded, failed | status strip |
| `PermissionRequestedEvent` | a tier-3 action is waiting | permission card |
| `TelemetryEvent` | VRAM, RAM, context fill | status strip |
| `NoticeEvent` | a toast | toasts |
| `ModuleRegistryChangedEvent` | a module was added, removed, toggled | rail, dock |
| `SensesEvent` | window/media/vision changes | diagnostics |

---

## 4. Memory

`Core/Memory/MemoryGraph.cs` — SQLite in WAL mode, `synchronous=FULL`, whole graph mirrored
in memory for fast recall. Two tables (`nodes`, `synapses`) plus `meta`.

* **Layers:** 0 core self, 1 episodic, 2 semantic, 3 procedural.
* **Link types:** `IS_A`, `BELONGS_TO`, `USED_FOR`, `CAUSES`, `COMBINES_WITH`,
  `CONFLICTS_WITH`, `DANGER_IF`, `CANNOT_BE`, `LIKES`, `DISLIKES`, `PREFERS`, `BEFORE`,
  `AFTER`, `DURING`, `RELATED_TO`.
* **Polarity:** inward (abstraction), outward (capability), inhibitory (danger),
  affective (preference), temporal (ordering). Inhibitory links deliberately carry the most
  activation, so warnings surface first.
* **Strength:** 0.05–1.00, grows when used, fades daily, never for pinned core nodes.
* **Consolidation:** while the user is idle, near-duplicate nodes in the same layer are
  merged and their links reattached.

`Core/Memory/TextVectorizer.cs` turns text into a 384-dimension fingerprint with the hashing
trick (words plus word pairs, L2-normalised). No model download, deterministic, and the only
replaceable piece if you ever want real embeddings.

---

## 5. Modules and the sandbox

* `IAssistantModule` — id, display name, purpose, capability text, tools, execute.
* `IModuleSurfaceProvider` — optional panels, exposed as data models the shell renders.
* `ModuleHost` — registry, routing, execution, enable/disable.
* `SandboxGuard` — tiers: reflex (instant), scoped (workspace only, logged), system-critical
  (always asks, 15 second auto-deny, recorded forever in `audit.jsonl`).

Built-in modules are registered from `AppModules.CreateBuiltInModules`. External modules
currently use a validated `plugin.json` manifest and a separate command or MCP process; they
are adapted into the same router and sandbox, and arbitrary DLLs are not loaded into Iris.
External tools all ask before running today. This is a basic extension path, not the finished
SDK: plugins cannot yet contribute hosted panels or overlays, declare enforceable capability
grants, or report progress through the shared activity event. See `MODULE-SDK.md` for the
current contract and the remaining platform work.

---

## 6. Senses and voice

`PerceptionEngine` polls cheap Win32 facts (foreground window, idle time, memory, CPU,
battery), reads media sessions through Windows SMTC, and gates screen capture with a 64-bit
difference hash so an unchanged screen costs nothing.

`VoicePipeline` owns the audio path: microphone → adaptive VAD → Whisper or HTTP
transcription → wake matcher → brain. On the way out: sentence splitter → HTTP or Windows
synthesis → NAudio playback with a 50 ms loudness envelope that drives the orb.

Her voice identity is `VoiceProfile` (engine + speaker + rate + pitch), stored in settings
and used to name the reflex cache, so an engine change rebuilds the cache instead of
replaying yesterday's timbre. `VoiceSetupView` is where that identity is decided: it renders
real previews through the same synthesizer and writes `Voice.ChosenByUser` when the user
picks one by ear. `VoiceGallery` builds that shortlist and can be used without any audio
device at all.

Three seams keep the microphone honest. `IWakeGate` (`UtteranceShapeWakeGate`: a minimum
duration and a peak level) drops door slams before they cost a transcription pass;
`WhisperNetRecognizer` caps itself at half the cores, clamped to 2–4, at `BelowNormal`
priority; and `SuspendedRecognizer` is what replaces the recogniser in Gaming Mode when
`System.GamingEarsEnabled` is off — it makes the microphone genuinely unmetered rather than
merely ignored.

---

## 7. Crash immunity

| Failure | What happens |
|---|---|
| Power loss mid-write | SQLite WAL replays committed transactions |
| Crash mid-turn | `journal.jsonl` still has the user's message; the next start reports it and nothing was lost |
| Torn settings file | `.bak` is restored, the damaged copy is quarantined |
| llama-server dies | `ModelStatusEvent.Failed`, a toast, and the turn fails cleanly instead of hanging |
| Audio device disappears | playback stops, listening recovers on the next device change |
| Plugin throws | logged, the tool returns a failure string, the conversation continues |

---
## 8. Where the files are, and why some classes have two of them

```
src/CompanionOS.Core/
  Configuration/   CompanionPaths, CompanionSettings, SettingsStore
  Diagnostics/     CompanionLog            one diary, plain text, nothing throws
  Common/          ObservableObject        the whole change-notification story
  Events/          CompanionEvents, EventBus
  Mind/            MindStates              state + mode + the machine that moves between them
  Memory/          MemoryModel, MemoryGraph (+ .Queries), SynapticMemoryEngine, TextVectorizer
  Brain/           Persona, ModelClient, LlamaServerHost, AssistantBrain (+ .Turn), CompanionRuntime
  Modules/         ModuleContracts, SandboxGuard, ModuleHost
  Perception/      WindowsNative, ScreenCapture, MediaSessionSensor, PerceptionEngine, GpuMemoryProbe
  Voice/           VoiceProfile, SpeechSynthesis, AudioPlayback, MicrophoneCapture,
                   SpeechRecognition (+ SuspendedRecognizer), WakePhraseMatcher, WakeGate,
                   SpeechTextSanitizer, ReflexBank, VoiceGallery, VoicePipeline
  Recovery/        TurnJournal
src/CompanionOS.Modules/
  CoreToolsModule, MusicPlayerModule, SystemControlModule
src/CompanionOS.App/
  Program, App.axaml(+.cs), AppModules
  Services/        TrayIconHost          the only thing left when she is asleep
  Assets/          iris.ico              her tray icon, embedded as an Avalonia resource
  Interface/       MainWindow (+ .Layout, .Dock), ShellModel (+ .Events), Ui, Theme/Palette
  Interface/Views/ ChatView, MemoryView, ModulesView, SettingsView, VoiceSetupView, OverlayWindow
  Interface/Controls/ StatusOrb, MemoryGraphCanvas
```

The house rule is a cohesive file of roughly 150–350 lines, and the four biggest classes
broke it. Rather than create a file per method, each was split with `partial` at a real
seam, and each part keeps its own header comment:

| Class | Parts | The seam |
|---|---|---|
| `MemoryGraph` | `.cs` / `.Queries.cs` | storing and mutating the graph / reading and reporting it |
| `AssistantBrain` | `.cs` / `.Turn.cs` | the body: modes, lifecycle, prompt building / the turn loop itself |
| `MainWindow` | `.cs` / `.Layout.cs` / `.Dock.cs` | window behaviour and header / rail, dock, status strip / the now-playing panel |
| `ShellModel` | `.cs` / `.Events.cs` | state the UI binds to / the event handlers that write it |

A partial class shares every private field, so nothing is duplicated and no visibility had to
be loosened to make the split work.
