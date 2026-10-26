# Roadmap

Each milestone ends at a state you can live in daily. "Done" means the code in this repository
does it; "next" means it does not yet.

## M1 — Immortal skeleton · done

Avalonia shell, chat with batched streaming, status orb, overlay, OpenAI-compatible brain,
llama-server lifecycle, sleep and wake, atomic settings, WAL memory, turn journal.

## M2 — Synaptic memory · done

Four layers, fifteen link types with polarity, spreading activation, plain-English intuition
block, decay, consolidation, live graph view with inspector, export.

## M3 — Module engine · done

`IAssistantModule`, dynamic routing with an eight-tool cap, three-tier sandbox with a
15-second auto-deny and permanent audit trail, built-in modules: core tools, music player
(library search across the user's real folders, playlists), system control (timers, notes,
app launch, machine status, file search). Nothing is ever loaded from disk at runtime.

Music indexing now runs off the UI thread with cancellation and explicit limits (64 roots,
2,000 folders, 50,000 entries, 10,000 discovered tracks, depth 24, and a 4-second cooperative
time budget). It skips junctions/symlinks, keeps the previous playable index when a scan is
cancelled or partial, reports partial/unavailable/link-skipped results in the player rail and diagnostics,
and bounds folder browsing. This is a safety and responsiveness baseline; very large libraries
still need a resumable incremental index and real-device playback validation.

## M4 — Real-time voice · done

Offline Whisper or HTTP transcription, adaptive voice activity detection, wake matcher with
tolerance for greetings and one-letter speech errors, one neural-only synthesis engine behind
a locked `VoiceProfile`, cached reflex phrases, barge-in that stops audio immediately. There
is no Windows-voice fallback: if the neural engine is not running, she does not speak and
she reports it.

**Still rough:** the current VAD is energy-based with a moving noise floor. A Silero VAD ONNX
pass would make barge-in and end-of-speech detection sharper in noisy rooms.

## M5 — Screen vision and gaming mode · done

Ambient awareness (window, media, CPU, RAM, battery, idle), on-demand and live-share capture
gated by a 64-bit difference hash, 896 px JPEG frames, game detection with learned
exclusions, and a full GPU release when a game starts.

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
