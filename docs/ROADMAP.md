# Roadmap

Each milestone describes shipped implementation and remaining acceptance work. A class, screen, or green build alone does not mean the feature is ready for daily use; release readiness is defined by `docs/END-GOAL.md` and target-machine verification.

## M1 - Immortal skeleton: foundation shipped

Avalonia shell, chat with batched streaming, status orb, overlay, OpenAI-compatible brain,
llama-server lifecycle, sleep and wake, atomic settings, WAL memory, turn journal.

## M2 - Synaptic memory: foundation shipped

Four layers, fifteen link types with polarity, spreading activation, plain-English intuition
block, decay, consolidation, live graph view with inspector, export.

## M3 - Module engine: foundation shipped; platform growth continues in M8

`IAssistantModule`, dynamic routing with an eight-tool cap, three-tier sandbox with a
15-second auto-deny and permanent audit trail, built-in modules: core tools, music player
(library search across the user's real folders, playlists), system control (timers, notes,
app launch, machine status, file search). Built-ins are compiled into Iris; optional external modules run as separate processes under M8.

Music indexing now runs off the UI thread with cancellation and explicit limits (64 roots,
2,000 folders, 50,000 entries, 10,000 discovered tracks, depth 24, and a 4-second cooperative
time budget). It skips junctions/symlinks, keeps the previous playable index when a scan is
cancelled or partial, reports partial/unavailable/link-skipped results in the player rail and diagnostics,
and bounds folder browsing. This is a safety and responsiveness baseline; very large libraries
still need a resumable incremental index and real-device playback validation.

## M4 - Voice conversation: implementation shipped; acceptance remains

Voice enters the same conversation coordinator as typed chat through Whisper or configured HTTP
recognition. Default output is the managed local Pocket TTS worker behind a locked VoiceProfile;
a configured HTTP speech endpoint is also supported. Cached reflexes and barge-in are implemented.
There is no silent Windows voice fallback. Recognition and synthesis remain separate stages, not a
single native audio model. Output quality, latency, device recovery, and real-room behavior need
target-machine verification.

Still open: end-to-end microphone/speaker scenarios, noisy-room and barge-in tuning, measured warm
and cold latency, and energy-based VAD accuracy. Add another runtime only if measured target-
machine results justify its disk and resource cost.

## M5 - Screen vision and gaming mode: implementation shipped; acceptance remains

Ambient awareness (window, media, CPU, RAM, battery, idle), on-demand screen capture, live-share
capture gated by a 64-bit difference hash, 896 px JPEG frames, learned game exclusions, and GPU
release on Gaming Mode are implemented.

This is on-demand image context alongside speech recognition that produces text, not continuous
audiovisual perception or a unified multimodal model. Source freshness, capture limitations,
monitor/scaling edge cases, resource measurements, and target-machine transitions still need
verification.

## M6 — Discovery modules · replaced

The YouTube finder was removed rather than pretending to control a signed-in account. The
external module path has since been reintroduced as manifest-declared command and MCP
processes, routed through the same module host. Its current contract and remaining gaps are
tracked under M8 and in `docs/MODULE-SDK.md`.

## M7 — The body · next

A separate VRM avatar process driven over a local socket: expression and gesture commands
from her control tags, lip sync from the TTS amplitude envelope already published on the bus,
procedural breathing and blinking on the CPU. Deliberately not inside the main process, so a
body crash never touches her mind.

## M8 — Extensible module platform · active foundation

Make adding integrations a core product capability, not a list of features compiled into Iris.
The foundation now carries truthful MCP and command progress into shared activity UI; discovers
MCP and command tools at startup with bounded cost; routes relevant tools from large catalogs;
and lets external manifests contribute bounded sidebar forms, buttons, and up to three independent
overlays. Named capability scopes have separate saved grants and Settings revocation. A runnable
PowerShell starter module demonstrates discovery, native UI, and progress. The platform is not
complete until all stages below are shipped:

1. **Stable SDK and lifecycle:** both transports discover schemas at startup. The Modules screen
   can scan folders, refresh tools, and enable/disable modules immediately. Remaining work includes
   guided install/update/removal flows, publisher review, richer authoring tools, and actionable
   validation diagnostics. Refresh is explicit; there is no background folder watcher.
2. **Permission capabilities:** named grants and revocation work at Iris's tool-call boundary;
   tools can require fresh approval on every call. Remaining work includes applying that policy
   consistently to built-in actions and OS-level process isolation.
3. **Hosted module UI:** validated declarative surfaces, inputs, buttons, icons, and multiple
   independently controlled overlays are implemented. Persistent module state, richer typed forms,
   and explicit show/hide controls remain. Iris owns rendering and focus policy while modules
   provide data and actions.
4. **Activity contract:** correlated start, step, progress, wait, result, error, and cancel states
   shared by chat, the activity card, notifications, and diagnostics. Percentage remains optional.
5. **Safe scale:** bounded execution, cancellation, versions, resource declarations, failure
   isolation, audit trails, and routing that keeps irrelevant tools away from the small model.

First-party integrations should dogfood the same public contracts. External code must remain
process-isolated and explicitly reviewed; placing an arbitrary script or DLL in a folder must
never silently grant it access to Iris or the user's accounts.

## M9 - Integrated awareness and self-feedback: active

Iris must behave as one companion across sensing, conversation, memory, voice, tools, and UI.
The current code already routes spoken and typed requests through the same turn loop, includes
available ambient context and recalled memory in the prompt, returns tool results for follow-up
reasoning, and publishes typed events for UI and diagnostics. That is a connected foundation,
not yet complete multimodal awareness or reliable autonomous planning.

Remaining work:

1. Ambient prompt context now reports whether an observation is unknown, current, stopped, stale,
   or disabled, and hides old values when they are not current. Foreground, CPU, RAM, power, idle,
   and media sensing report unavailable states separately; failed media reads cannot leave a
   previous track looking current. Diagnostics now shows per-source configured/available/freshness
   status without adding polling. Each model turn captures matching prompt/source state and logs
   only source names, statuses, timestamps, and image attachment under its turn ID; private window
   and media text are omitted. Still connect voice-input confidence, memory provenance, tool
   outcomes, speech, cancellation, and recovery into that same inspectable turn record.
2. Recognized requests now pass their interaction ID explicitly into the turn coordinator;
   transcript, attention, and speech events carry the same ID. Media pause/resume transitions are
   serialized, ownership is cleared on a terminal decision, results are logged under the turn, and
   an unconfirmed resume shows a recovery notice. Still connect listening state and wake decisions
   into the same inspectable record; verify media behavior against real Windows players.
   Tool activity now has one start/finish pair and chat entries are associated with their turn;
   completed tools return the activity card to result review. Cancellation and failure close the
   associated module overlay as well as active chat/tool UI, and cancellation marks the journal
   turn intentionally cancelled so restart recovery does not mislabel it as a crash. The
   recognizer contract currently returns text only, so recognition confidence is unavailable and
   must not be implied in UI or prompts.
3. Show what memory informed a reply, with provenance and correction/forget controls; verify
   memory writes, consolidation, conflicts, and long-session bounds.
4. Make multi-step work inspectable and interruptible. Iris must verify each result, ask when
   ambiguity or impact warrants it, and explain partial failure without inventing progress.
5. Measure idle, voice, model, vision, music, and gaming resource use on target hardware; tune for
   responsiveness while Iris yields to foreground work.
6. Exercise cross-component failures: missing mic/speaker/model, stale or denied vision, offline
   voice service, model/tool timeout, permission refusal, device loss, sleep/gaming, and cancel.
   One unavailable sense must not take down unrelated capabilities.

The current design is a low-resource multimodal pipeline (recognized speech text plus occasional
captured images and structured machine context), not a claim of human awareness or a single
native audio/video model. Evaluate runtime changes against target hardware and the dependency and
storage budget.

## Also next, in order of value

1. **Guided onboarding tour** — the five resumable steps from the blueprint. Today the chat
   welcome card covers the same information, but not the voice/ears tests.
2. **Silero VAD ONNX** — replaces the energy detector.
3. **Global hotkeys** — push-to-talk and overlay toggle while another window is focused.
4. **Wake word while asleep** — needs a tiny always-on CPU keyword spotter outside the main
   process. Today sleeping means process exit (`docs/DECISIONS.md` §28) and the tray icon brings
   her back in about 1.5 s.
5. **Memory time travel** — the consolidation history slider from the blueprint.

Neural speech output is now Pocket TTS, a managed local CPU sidecar with six bundled
profiles. The old Qwen TTS prototype was removed; adding another speech runtime is not on the
roadmap until it demonstrates a clear quality or latency gain without increasing the user's
runtime and disk burden.
