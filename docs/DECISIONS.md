# Decision log

Every entry here answers the same question: *why is it built this way and not the obvious
other way?* Nothing in this file is a preference — each decision had a cost that was
accepted on purpose.

Format: **decision — alternative that was rejected — why — what it costs us.**

---

## The two-speed mind

### 1. The model runs in a child process, never in-process
**Rejected:** LLamaSharp / in-process bindings.
**Why:** Sleep Mode and Gaming Mode promise *zero* VRAM. Unloading an in-process model does
not reliably return every allocation to the driver; killing a process always does. The same
process boundary also means a model crash is a restart, not an app crash.
**Cost:** one extra executable to download, and a startup health check to wait on.

### 2. Everything model-facing speaks the OpenAI HTTP contract
**Rejected:** a bespoke protocol between brain, voice and ears.
**Why:** llama.cpp, LM Studio, Ollama, vLLM, Kokoro-FastAPI, Speaches and most future servers
already speak it. It means the "local" requirement is a configuration detail, not an
architectural one — swap one URL and she runs on a different machine's GPU.
**Cost:** a little JSON overhead per call, and the quirks of a de-facto standard.

### 3. Ears stay on the CPU while the cortex sleeps
**Why:** the promise is that she is still *there* when the GPU is off — she can hear her name,
answer with a reflex, and wake the brain. That only works if every voice component is CPU-only.
**Cost:** transcription latency that a GPU whisper build would not have.

### 4. Auto-sleep and auto-wake instead of a manual "load model" button
**Why:** the user asked for a companion, not a launcher. She goes quiet after
`Brain.AutoSleepIdleMinutes` of inactivity and wakes on the first thing you say.
**Cost:** the first turn after a nap takes a few seconds — which is exactly when the reflex
bank speaks ("one sec") so the silence has a shape.

---

## Memory

### 5. One SQLite file with WAL, not a document store
**Why:** a power cut in the middle of a write must not lose the graph, and WAL with
`synchronous=FULL` gives that with one pragma. It is also a single file the user can copy.
**Cost:** schema changes need migration code (see `MemoryGraph.InitializeAsync`).

### 6. Relations are typed and *signed*
**Why:** "coffee CONFLICTS_WITH sleep" has to *subtract* activation, not add another edge of
the same weight. Polarity is part of the synapse type (`SynapseTypes.PolarityOf`), not a
separate column, so an inhibitor cannot be accidentally written as an exciter.
**Cost:** adding a new relation type means touching the enum and the word mapping together.

### 7. Recall is rendered as plain English before the model sees it
**Rejected:** dumping JSON nodes into the prompt.
**Why:** a 4B model does not reason well over graph syntax; it reasons well over sentences.
The intuition block is capped (`Memory.IntuitionCharacterBudget`) and puts warnings first.
**Cost:** the model cannot ask follow-up graph queries; it gets one summary per turn.

### 8. Layer 0 is read-only to personalities
**Why:** her core self (name, voice, boundaries, the user's hard rules) must not be rewritten
by a roleplay profile or by a model having a strange day.
**Cost:** changing her core self means editing memory on purpose, never by accident.

### 9. Decay is slow and visible
`Memory.DailyDecay` defaults to 0.997 per day, and every node shows its strength in the graph
view. A memory fades because it stopped mattering, not because a counter hit zero.
**Cost:** the graph grows; consolidation has to prune it.

---

## Modules

### 10. Tools are routed, not all offered
**Why:** eight tools is roughly where a 4B model starts picking the wrong one. `ModuleHost`
scores modules against the sentence, always includes `AlwaysAvailable` core tools, and caps
the list.
**Cost:** a badly worded request can miss a tool; the router keeps keywords in the manifest
for exactly that reason.

### 11. Three tiers, and red always asks
**Why:** a permission prompt on "play music" is noise; a silent `shutdown` is unacceptable.
Tiers are declared with the tool, enforced centrally in `SandboxGuard`, and every decision
lands in an audit file.
**Cost:** red tools interrupt flow — that is the point.

### 12. External plugins were removed (reversing the original plan)
**Rejected:** the original design shipped `plugin.json` drop-in modules and runtime DLL
loading. Removed: nothing found on the user's disk is executed on faith, and a body that
growls itself by dropping folders is a scripting system nobody approved.
**Why now:** a companion OS must be exactly what it ships. New abilities are reviewed C#
modules listed in `AppModules` — a build decision, visible in source control.

### 13. Built-in modules live in their own assembly
**Why:** it keeps the Core honest. If the built-ins had privileged access, the module contract
would never be tested by the people who maintain it.
**Cost:** one more project in the solution.

---

## Interface

### 14. All views are C#, not XAML
**Why:** this code had to be written without a design-time Avalonia toolchain. C# views are
compiler-checked and greppable; XAML failures are runtime-only and invisible until the window
opens. `App.axaml` keeps only the theme.
**Cost:** no designer preview, and more verbose layout code.

### 15. Design tokens in one file
**Why:** a whole-look change should be one diff in `Interface/Theme/Palette.cs`, not a hunt
through sixteen files.
**Cost:** a stray hard-coded colour is always a bug; reviewers can grep for `Color.Parse`
outside the palette.

### 16. The interface never asks the brain for a value
**Why:** the brain publishes every state change as an event; the shell model holds the latest
value, and a batching timer paints it. Nothing walks up to the model host and interrogates it,
and no pixel ever sits waiting on a getter.
**Cost:** more event types than a direct-getter design would need, plus small per-window
timers that copy buffered model state onto controls.

### 17. Streaming is batched at 30 fps
**Why:** a fast model emits token deltas faster than a UI can lay out; the shell buffers them
and flushes on a 33 ms timer. This is the difference between smooth and unresponsive.
**Cost:** up to 33 ms of extra latency on the first visible token — invisible to a human.

### 18. No MVVM framework
**Why:** one forty-line `ObservableObject` covers everything this app needs. A framework would
add attributes, source generators and a second way to do the same thing.
**Cost:** no built-in commands or validation helpers; we write the two lines ourselves.

---

## Durability

### 19. An append-only journal, separate from the database
**Why:** "did the last turn finish?" is a question about *intent*, and intent belongs in a log.
Turning it into rows would make every write part of the same transaction we are trying to
survive.
**Cost:** a second file to keep tidy (it is truncated on clean start).

### 20. Settings are written atomically, with a `.bak`
**Why:** settings are the one file a user edits by hand. A half-written file must never be
able to stop her from starting; if the JSON is broken it is quarantined and defaults are used
with a warning in the UI.
**Cost:** a stale `.bak` may briefly exist after a save.

---

## Voice

### 21. The voice is an identity object, not a setting
**Why:** AAA assistants are recognisable. `VoiceProfile` pins engine, speaker and parameters,
is fingerprinted, and names the cached reflex files. Change it and the cache is rebuilt rather
than mixing two voices in one conversation.
**Cost:** changing her voice is a deliberate act, not a slider you graze.

### 22. Reflex phrases are pre-rendered
**Why:** the moment after the user stops talking is when a companion feels alive. Playing a
150 ms cached "On it" beats a 900 ms synthesis delay.
**Cost:** a few hundred kilobytes of WAV in the cache folder, rebuilt when the profile changes.

### 23. Barge-in stops speech, not thinking
**Why:** interrupting her mid-answer is usually "stop talking", not "forget the question".
The turn keeps running and the text still arrives.
**Cost:** if the user really did want to cancel, `Esc` cancels the turn too.

### 23a. One voice engine, no fallbacks
**Why:** a voice is who she is. A second, cheaper mouth (Windows SAPI) means she silently
borrows a stranger's voice whenever the real one is down — dishonest about her body. If the
neural engine is not up, she does not speak, one notice says so exactly, and a probe every
minute at most lets her recover mid-session when the engine starts.
**Cost:** an offline engine means silence, not a degraded voice. That is the point.

---

## Process

### 24. One composition root
Every object is built in `CompanionRuntime.Create()` and handed its dependencies. No service
locator, no static god object, no hidden construction.
**Cost:** a long constructor call — which doubles as the map of the system.

### 25. Self-reading names, no abbreviations
`microphoneLevel`, not `lvl`; `remainingSilenceMilliseconds`, not `rsm`. The people who
maintain this are a user, a future AI, and maybe a teenager learning C#.
**Cost:** longer lines and wider signatures.

### 26. Windows-only, and proud of it
**Why:** media sessions, screen capture and gaming detection are Windows features.
Pretending to be cross-platform would mean three half-implementations.
**Cost:** no Linux build — the Core would port if the perception layer were swapped.

### 27. No telemetry, no accounts, no cloud fallbacks
**Why:** she knows what is on your screen. That fact only stays comfortable if there is
nowhere for it to go.
**Cost:** every diagnostic has to be visible locally, which is why the log is a text file and
the audit trail is readable JSON lines.

---

## Sleep, power and gaming

### 28. Sleep switches her off; a tray icon is what stays
**Why:** "asleep" used to mean "brain unloaded, everything else listening". That is not what
the word means to a person — a sleeping assistant that still holds the microphone is a
camera light nobody asked for. Sleep now disposes the runtime, releases the microphone, frees
the ASR weights, stops every timer, checkpoints the database and exits everything except a
`TrayIcon`. Waking is a relaunch.
**Cost:** no wake word while asleep, and about 1.5 s to come back. Waking by name always needs
*something* running; the honest options are a microphone that is open while she is asleep or a
click. We chose the click, because it is the only one that frees the CPU and the GPU entirely.
**Ordering matters:** the tray icon is created *before* the teardown, and if it cannot be
created she refuses to sleep. There must be no state in which a process runs and nothing is
visible.

### 29. Gaming Mode takes her ears too, unless you say otherwise
**Why:** entering Gaming Mode frees VRAM and leaves the CPU alone. Transcription is a
300–700 ms burst on up to four threads — a dropped frame at 144 Hz is 6.9 ms, so that burst is
not something to run silently under a game. `System.GamingEarsEnabled` is therefore `false`
by default, and turning it on is a one-click decision for people who talk to her while playing.
**Cost:** she cannot hear you during a game by default.

### 30. A voice is chosen by ear, once
**Why:** "consistent voice" is not a property of an engine, it is a decision that has to be
*recorded*. `Voice.ChosenByUser` is the record. The first-run picker renders real previews
through the same synthesizer that will speak her answers, so what the user approves is what
they get, and a candidate the machine cannot produce says so instead of being substituted.
**Cost:** one screen of onboarding, and a preview costs a synthesis round-trip.
**Why it is worth it:** without it, whichever engine loads first decides what she sounds like,
and a later engine change silently changes her identity.

---

## Models and hardware

### 31. No omni model in the 8 GB build — and that is hardware, not code
The one change that would justify swapping her brain is a *single* model that does vision,
roleplay and realtime speech in both directions while keeping one consistent voice: no separate
TTS voice to drift, no ASR plumbing to tune. On an RTX 3050 with 8 GB, that model does not
exist yet. The numbers, from the projects themselves:

| Candidate | Reality |
|---|---|
| **Qwen3-Omni-30B-A3B** | Talker (speech out) is **not implemented in mainline llama.cpp**; `talker` + code2wav add ~3.3 GB on top of a ~19 GB model. 24 GB floor |
| **MiniCPM-o 4.5 (9B)** | Real omni at ~9–11 GB VRAM (text 5, +image 6, +voice-in 6.5, full omni 9), **≥12 GB recommended**, and needs the `llama.cpp-omni` fork, not upstream. Supports zero-shot cloning from a reference clip |
| **Qwen3.5-4B + Kokoro + Whisper** (shipped) | Vision + roleplay at 4–5 GB with headroom left, voice in/out on the CPU. Two models, one pinned `VoiceProfile` |

So the split — brain in VRAM, ears and mouth on the CPU — is not laziness, it is what fits.
The upgrade is a one-line config change when a 12 GB card and a working omni runtime exist:
`BrainProfile` already carries endpoint, model name and context, and `VoiceProfile` is already
an identity object rather than a scattering of settings. At 12 GB+ the omni model *replaces*
the brain, the ears and the mouth at once, and most of this split machinery becomes dead weight
that can be deleted without touching the memory, modules or interface layers.
**Cost of waiting:** her voice is a separate component today, which is exactly why it is pinned
and cached per profile.
