# CompanionOS — Project Iris

A local-first AI companion for Windows. She hears you, sees your screen when asked,
remembers what matters in a typed synaptic graph, speaks in one locked neural voice,
plays music from your real folders, and finds files anywhere on your PC.

Built for an **RTX 3050 8 GB / 16 GB RAM** desktop, but nothing in the code assumes that
hardware: the brain is any OpenAI-compatible endpoint, local or remote.

> **Design rule:** the interface is her visible nervous system. Every pixel reflects a real
> event, never a decoration.

---

## Quick start

```powershell
# 1. Download the brain, vision projector, ears and llama.cpp runtime
powershell -ExecutionPolicy Bypass -File scripts/setup.ps1

# 2. Build and start her
powershell -ExecutionPolicy Bypass -File scripts/run.ps1
```

Requirements: **Windows 10/11**, **.NET 9 SDK**, an NVIDIA GPU with a recent driver
(nvidia-smi is used for the VRAM meter; everything else works without it).

Spoken output uses the managed **Pocket TTS** CPU sidecar. Iris starts it locally during startup,
caps it at two logical processors with below-normal priority, and stops it when the app exits.
The 18M English model and selected voice profiles live under `%LOCALAPPDATA%\CompanionOS\models`;
speech synthesis uses no VRAM. A user-configured OpenAI-compatible server remains available by
setting `Voice.Engine` to `http` in `settings.json`.

---

## What is implemented today

| Area | State |
|---|---|
| Avalonia shell, chat, status orb, overlay, permission prompt | working |
| Brain: OpenAI-compatible streaming, tool calls, vision, sleep/wake/gaming | working |
| Sleep: process exit to a tray icon — no microphone, no speech model, no timers | working |
| First-run voice picker: hear six real Pocket TTS profiles and lock one in | working |
| Synaptic memory: SQLite WAL, 4 layers, 15 typed links, spreading activation, decay, consolidation | working |
| Modules: dynamic routing, 3-tier sandbox, audit trail, built-in music / timers / notes / file search | working |
| Voice: Whisper (offline) or HTTP transcription; Pocket TTS CPU speech, capped to two logical processors, with barge-in and cached reflexes; no OS-voice fallback | working |
| Senses: foreground window, media sessions, CPU/RAM/battery/idle, dHash-gated screen capture | working |
| Crash recovery: WAL database, append-only turn journal, atomic settings | working |
| 3D avatar, MCP bridge, guided onboarding tour, true always-on keyword spotting | planned (see `docs/ROADMAP.md`) |

The honest short version: the capabilities above have code behind them, but that does not mean
every real-world path has been exercised on this machine. Seventeen blueprint areas use a
simpler engine; `docs/PLACEHOLDERS.md` explains those tradeoffs, and `docs/ROADMAP.md` lists open
features. The latest audit records what has and has not been verified.

---

## Repository layout

```
CompanionOS.sln
scripts/            setup.ps1, run.ps1, models.manifest.json
config/             explains where her real config lives
docs/               blueprint, architecture, build guide, module authoring, roadmap
src/
  CompanionOS.Core/      her body without pixels: brain, memory, modules, senses, voice
  CompanionOS.Modules/   built-in body parts: core senses, music, system control
  CompanionOS.App/       the interface (Avalonia, C#-only views)
```

## Documentation

| Document | What it is for |
|---|---|
| [`docs/BLUEPRINT.md`](docs/BLUEPRINT.md) | The locked specification: hardware budget, memory model, module contract, UI tokens |
| [`docs/ARCHITECTURE.md`](docs/ARCHITECTURE.md) | How the code is organised and how one turn flows through it |
| [`docs/BUILD-AND-RUN.md`](docs/BUILD-AND-RUN.md) | Building, running, publishing, and the three places a first build may need a nudge |
| [`docs/DECISIONS.md`](docs/DECISIONS.md) | Every "why is it built this way" with the alternative that was rejected |
| [`docs/PLACEHOLDERS.md`](docs/PLACEHOLDERS.md) | Every deviation from the blueprint, its cost and its upgrade path |
| [`docs/TROUBLESHOOTING.md`](docs/TROUBLESHOOTING.md) | What to check when she cannot hear you, think, or speak |
| [`docs/ROADMAP.md`](docs/ROADMAP.md) | What comes next, in milestones |
| [`docs/PRODUCT-DIRECTION.md`](docs/PRODUCT-DIRECTION.md) | The completion standard, contextual-overlay rules, and delivery gates |

Read `ARCHITECTURE.md` first if you want to change something, then add a module in
`src/CompanionOS.Modules/` and list it in `AppModules` — growing her is a build decision,
by design.

---

## Her data

Everything she knows lives in **one folder**, never inside the repository:

* Windows default: `%LOCALAPPDATA%\CompanionOS`
* override with the `COMPANIONOS_HOME` environment variable

No telemetry. No network calls except to your own local endpoints — the model, the ears
and the voice are all your machine. There are no external plugins, no drop-in scripts and
no self-installing modules: what ships in the box is what runs.

---

## License and intent

Personal companion framework. Model weights are downloaded separately and keep their own
licenses. The code is written to be maintained by people and by AI assistants: small
cohesive files, self-reading names, and comments that explain *why*.
