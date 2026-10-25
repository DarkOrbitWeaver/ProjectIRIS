# Iris product direction

This is the decision document for turning Project Iris from a promising local assistant
into a trustworthy desktop companion. It supersedes any accidental implication that a
feature is complete merely because its class, screen or button exists.

## The product promise

Iris is a local-first companion with one continuous identity. She should feel calm when
idle, present when needed, and honest when a service, device or permission is unavailable.
Her interface is not a dashboard of speculative features: every visible control must act on
real state, and every real capability must be discoverable and controllable.

The correct default is the small floating pill. Contextual surfaces are earned by live work:
music creates a small transport card while it is playing; a timer creates a timer card while
it is active; a permission creates a decision card until it expires. Each surface must be
independently dismissible, never steal focus, never lie about its state and disappear when its
underlying activity ends.

## Completion standard

Do not label a subsystem complete unless all five are true:

1. It has a real implementation, not a visual placeholder.
2. Its unavailable, slow, permission-denied, unplugged and restart states are safe and plainly
   explained.
3. The user can discover, invoke, stop and inspect it from both Iris' language and the UI.
4. It has a regression test for its highest-risk state transition and a manually checked
   interaction checklist at 100%, 125% and 150% DPI.
5. It has an owner, a small contract and a documented upgrade path; extending it cannot require
   rewriting unrelated parts of Iris.

## Working architecture rules

- **One source of truth.** A module owns its state model. Main chat, overlay and settings bind
  to that state rather than maintaining copies.
- **State before animation.** The orb, cards, notices and transport controls only animate after
  a published state transition. No decorative activity indicators.
- **Degrade without breaking.** No microphone, no speaker, missing model, offline TTS, missing
  music folder, device removal and a denied permission are ordinary operating conditions.
- **Contextual overlays.** The pill is always quiet by default. Activity cards are temporary,
  focus-safe, stack predictably and have a visible minimize action plus a matching voice/tool
  command.
- **External code is explicit.** Folder plugins are not built-in modules. Their identity,
  transport, permission tier and next-launch enabled state are visible; they never silently
  appear or execute.
- **No false autonomy.** Memory writes, filesystem access, launching programs and external
  processes pass through their declared policy. "I found it" means a real result exists.

## Delivery sequence

### Gate 1 — Make the existing body trustworthy

- A passive Setup Readiness card now checks brain weights, microphone/recognizer, the last
  verified voice-engine state, speaker route, accessible music folders and local memory without
  waking a model, opening a microphone, playing audio or making a network call. It is the first
  slice of onboarding diagnostics, not a substitute for real-device tests.
- Complete hardware/service diagnostics for microphone, recognizer, voice engine, speaker,
  brain server and music output, with actionable recovery text and explicit active tests.
- Add an onboarding verification flow: choose voice, test output, test microphone, verify the
  brain, add one music folder, create one memory. Each step is skippable and resumable.
- Turn the present manual visual checks into a scripted matrix covering minimum window size,
  scaling, resize, keyboard navigation, overlay anchor/drag, sleep/wake and device removal.
- Correct stale claims in documentation as evidence changes; "foundation done" is not "AAA
  done."

### Gate 2 — Finish the companion surfaces

- Music overlay: now playing, pause/resume, previous/next/stop, minimize, show again by voice,
  correct speaker routing and device-loss recovery.
- Timer overlay: countdown, label, cancel/snooze and automatic removal when complete.
- Permission overlay: request, exact impact, allow once/always/deny, timeout and audit link.
- Notification discipline: no empty panels, no persistent success toast, no card without a
  live action behind it.

### Gate 3 — Deepen cognition without faking AGI

- Memory must show why a fact was recalled, confidence/recency, provenance and an editable
  correction/forget path. Consolidation and decay must never silently change a user assertion.
- Tool planning must report real progress for multi-step work, preserve cancellation and recover
  from a partial failure with a next action.
- Add scenario tests for interrupted turns, duplicate tool calls, offline brain/voice, slow
  filesystem scans, corrupt settings, crash recovery and plugin failure.

### Gate 4 — Grow safely

- Add a capability manifest for every built-in module and a user-facing permission/grant view.
- Define contracts for future integrations before implementation: lifecycle, data boundary,
  overlay surface, failure behavior, test matrix and removal path.
- Only then introduce avatar, richer voice, global hotkeys and third-party integrations.

## Current truth

The shell, local model route, graph memory, module host and basic perception are useful
foundations. They are not the completed companion. The immediate blockers to a daily-driver
claim are real-device voice verification, onboarding, visual interaction coverage, richer
memory explainability and a complete lifecycle for contextual overlays. Work should proceed
through the gates above, closing tested vertical slices rather than adding more isolated cards.

Manual Gaming Mode is now a first-class resource hand-off: it persists through unfamiliar game
launchers until the owner resumes Iris, while auto-detection remains available for known games.
