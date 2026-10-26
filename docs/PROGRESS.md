# Project Iris progress snapshot

**Updated:** 2026-10-26

**State:** Active development; not release-ready or complete.
**Scope:** This snapshot tracks the whole companion, including the core, voice, awareness,
memory, integrations, interface, hardware behavior, and verification.

There is no defensible single percentage for the end goal yet. The repository has substantial
working foundations, but the goal includes cross-component behavior and target-PC verification
that a build or feature count cannot measure. Milestones below distinguish code that exists from
behavior that has been exercised.

## Working foundations in the repository

- **Conversation core:** typed and recognized speech requests reach the same turn coordinator.
  It includes available machine context, recalled memory, and request-relevant tools; it sends
  tool results back into the conversation and can make bounded follow-up calls. Its system
  instructions now distinguish ambient metadata from actual screen images.
- **Voice:** microphone capture, speech segmentation, Whisper or configured HTTP recognition,
  wake matching, interruption, one locked voice profile, cached reflex phrases, and the managed
  local Pocket TTS default are implemented. Missing voice output is meant to fail honestly.
- **Awareness:** low-cost Windows foreground, media, idle, CPU/RAM, and battery observations;
  optional screen capture; game detection; and sleep/gaming resource handoff are implemented.
  Screen vision is on demand or explicitly shared, not constant video; disabling vision now
  blocks even a forced-capture request. Prompt context labels ambient observation as unknown,
  current, stopped, stale, or disabled instead of presenting an old sample as live. Enabled
  foreground, CPU, RAM, power, and idle probes report unavailable results separately; media
  reports no session, unavailable, stale, or its last successfully read track. The Diagnostics
  screen exposes per-source status and latest evidence without increasing the sampling cadence.
- **Memory:** a local graph with layers, typed/polarized links, activation, persistence,
  consolidation, visualization, and exact forget paths exists.
- **Capabilities:** built-in music and system tools use the shared module router and approval
  boundary. External command/MCP modules can discover tools, request scoped consent, use encrypted
  credentials, expose bounded native controls and overlays, and report progress. Folder refresh
  and immediate enable/disable are implemented.
- **Shared state:** typed events connect conversation, mind state, voice, memory recall, tools,
  perception, notices, telemetry, and the interface. Local diagnostic logs and a turn journal
  exist.

These are implementation findings from source inspection. They do not prove the complete workflow
works on the target PC under real microphone, speaker, model, display, game, and device conditions.

## Biggest remaining work

1. **Connect and explain the whole loop.** Correlate source and freshness information with each
   turn; distinguish "configured" from "available" and "actually observed"; connect voice,
   memory, tools, overlays, cancellation, and recovery under one inspectable task/turn identity.
2. **Make voice dependable on this machine.** Verify every offered profile through the real
   engine, repair voice preview and selected-voice failures, and measure warm/cold synthesis,
   recognition time, CPU/RAM, interruption, missing-device recovery, and no-mic behavior. Keep
   the runtime and disk budget small.
3. **Make awareness trustworthy.** Make it clear what Iris can currently sense, what was captured
   for this request, when information is stale or unavailable, and what needs permission. Verify
   media ownership and that screen capture stops when requested.
4. **Deepen memory and reasoning feedback.** Show why a memory was recalled and where it came
   from; support correction and safe forgetting; test conflicts and long sessions. Make multi-step
   work verify results, ask when ambiguity matters, and report partial completion truthfully.
5. **Finish the scalable module platform.** Complete the stable authoring SDK, lifecycle, guided
   review/update/removal, diagnostics, typed UI, independently controlled overlays, shared
   activity states, persistent UI state, compatibility, and stronger isolation. Telegram and web
   search are examples for that common API, not features to hardcode separately.
6. **Polish the product surfaces.** Compare every page and state with the design references and
   improve them intentionally; fix layout and icon inconsistencies; complete compact call, music,
   timer, permission, and work overlays; verify keyboard, accessibility, scaling, and monitor
   behavior.
7. **Prove performance and resilience.** Measure idle, voice, brain, vision, music, and game
   modes on the Ryzen 5 5600X / RTX 3050 / 16 GB target. Exercise missing devices, offline
   services, model/tool failures, timeouts, denial, cancellation, corrupted settings, sleep/wake,
   and recovery without loading large models during code-only work.
8. **Close delivery gaps.** Finish resumable onboarding, setup/recovery instructions, reproducible
   scripts, logs useful for both Iris and Windows failures, automated interaction checks, and the
   final target-machine acceptance pass.

## Verification checkpoint

The current worktree builds with zero warnings/errors and the core suite passes 62/62 tests. New
regressions verify that `vision: off` blocks forced capture, that Iris does not claim passive
screen access, that ambient context is withheld until observed and when sensing has stopped, and
that a failed media probe never presents the previous track as current. Sensor-health checks
distinguish disabled, waiting, available, and unavailable sources. No model was loaded and
the app was not launched for this snapshot. The automated suite is still narrower than the product
acceptance matrix; real-device audio, visual/layout, multi-display, and sustained resource behavior
remain unverified here.

## Delivery promise

We will keep developing Iris as one connected companion, not a collection of disconnected demos.
Every feature must report real state, respect privacy and hardware limits, fail with a useful
recovery path, and connect cleanly to voice, memory, modules, and UI. We will grow the module
platform through a shared, versioned API, while continuing to improve the core awareness and
conversation loop. We will call a capability finished only after its normal, failure, recovery,
and resource paths pass automated checks and the appropriate target-machine verification. The
goal is a polished and dependable app with no known release-blocking issues, plus a disciplined
process to diagnose and fix what real use uncovers.

See [the full end-goal contract](END-GOAL.md), [the staged roadmap](ROADMAP.md), and
[the module SDK](MODULE-SDK.md) for detailed acceptance criteria.
