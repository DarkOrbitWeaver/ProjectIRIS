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

The YouTube finder and the drop-in plugin loader were deleted on purpose: she will not fake
a service she does not have an account for, and nothing found on her user's disk is executed
as a module. Discovery belongs to real integrations built and shipped in the box.

## M7 — The body · next

A separate VRM avatar process driven over a local socket: expression and gesture commands
from her control tags, lip sync from the TTS amplitude envelope already published on the bus,
procedural breathing and blinking on the CPU. Deliberately not inside the main process, so a
body crash never touches her mind.

## M8 — Extensible module platform · long-range

Build a stable, documented scripting API and module authoring kit so new capabilities are easy
to create, install, inspect, and use. Modules should declare tools, permissions, lifecycle and
resource needs, and may contribute declarative UI surfaces or independently controlled
overlays. First-party modules should dogfood the same public contracts where practical. External
scripts must run behind an explicit install/review step with bounded execution, cancellation,
versioning, diagnostics, and revocable permissions; dropping an arbitrary file into a folder
must never silently execute it. This is an intentional future platform investment, not a blocker
for the current companion foundation.

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
