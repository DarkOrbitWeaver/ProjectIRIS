# Build and run

## What you need

| Requirement | Notes |
|---|---|
| Windows 10 or 11 (x64) | The senses use Win32 and Windows media sessions |
| .NET 9 SDK | `dotnet --version` should print 9.x |
| NVIDIA driver with `nvidia-smi` | Only used for the VRAM meter |
| ~8 GB free disk for the default full setup | Brain ~2.5 GB, projector ~0.9 GB, ears ~150 MB, llama runtime, and CPU-only Pocket TTS dependencies/model |

## First run

```powershell
powershell -ExecutionPolicy Bypass -File scripts/setup.ps1
powershell -ExecutionPolicy Bypass -File scripts/run.ps1
```

`setup.ps1` reads `scripts/models.manifest.json`. It discovers the exact GGUF file inside the
Hugging Face repo through the API, so a repo that renames its quantisation does not break the
setup. The llama.cpp runtime comes from the newest per-build release that actually carries
binaries (the bare version tags ship none), and the CUDA runtime DLLs arrive as a second,
overlaying artifact so `llama-server.exe` and its `cudart64_*.dll` unpack into the same folder.
The voice setup keeps an existing compatible Pocket runtime. On a clean install it installs the
pinned `uv` tool and Pocket TTS 4.0.1 in an isolated Python 3.12 environment, using CPU-only
Windows wheels. It fetches only the public, no-cloning English 18M checkpoint and six selected
preset-voice embeddings. No CUDA PyTorch package, voice-cloning weights, unrelated language
weights, or Hugging Face credentials are needed.

Use `-Component brain`, `-Component ears`, `-Component runtime` or `-Component voice` to fetch
one piece, and `-VerifyOnly` to see what is missing. Setup does not start the TTS engine or load
model weights into memory.

## Where things go

```
%LOCALAPPDATA%\CompanionOS\        (or COMPANIONOS_HOME)
  models\brain.gguf
  models\brain-mmproj.gguf
  models\whisper-base.bin
  models\pocket-tts-cache\hub\models--kyutai--pocket-tts-without-voice-cloning\snapshots\...
  runtimes\llama.cpp\llama-server.exe
  runtimes\pocket-tts\pocket-tts.exe
  runtimes\pocket-uv\...\uv.exe
  runtimes\python\...\python.exe
  memory\brain.db
  workspace\Music\                <-- drop your songs here
```

## Choosing a brain

Open **Settings → Brain** and pick a profile:

| Profile | Use |
|---|---|
| Vision Agent | default, 4B visual agent, 16K context |
| Roleplay Companion | abliterated / uncensored build, no vision, warmer sampling |
| Speed Runner | fastest small model for long text work |
| External API | anything serving `/v1` — LM Studio, Ollama, a remote host |

Profiles are just data in `settings.json`. A new model folder only needs a new profile.

## Voice

Pocket TTS is the default local voice. `scripts/setup.ps1 -Component voice` installs the
supported runtime and downloads the selected model/profile set; it is not present until this
step succeeds. The English 18M model runs on CPU with a two-logical-processor ceiling and
below-normal priority. No GPU or Windows voice fallback is used. If Hugging Face access was not
completed, setup reports that and the app remains usable through text while voice is offline.
The chosen voice is stored as `Voice.VoiceId`; the six bundled IDs are `alba`, `anna`, `eve`,
`mary`, `vera`, and `estelle`.

An explicitly configured OpenAI-compatible server remains supported by setting
`Voice.Engine` to `http` and `Voice.HttpEndpoint` to its `/v1` base URL. If the selected engine is
unavailable, Iris stays silent and reports the problem. Voice previews and spoken replies require
successful Pocket setup or an explicitly configured HTTP engine.

Pocket TTS is by Kyutai Labs. Its runtime code is MIT-licensed. This setup uses the public,
no-cloning checkpoint and its [CC BY 4.0 model card](https://huggingface.co/kyutai/pocket-tts-without-voice-cloning).
The weights are kept in the local data directory, not bundled into the source tree.

The voice used for her reflex phrases is rendered once and cached per voice signature, so
"On it." is instant forever.

## If something goes wrong

| Symptom | Check |
|---|---|
| "Her brain did not load" | `models/brain.gguf` exists, and `runtimes/.../llama-server.exe` runs with `-h` |
| VRAM never drops after Rest | another instance of llama-server is still running: Task Manager → `llama-server` |
| She cannot hear you | Settings → Ears: recogniser `whisper`, and `models/whisper-base.bin` exists |
| She hears but ignores you | say her name (`"Hey Iris, ..."`), or press **Ctrl+M** to open the mic |
| No sound | Settings → Voice → Speak answers; also check Windows default output device |
| She cannot speak | check that the Pocket runtime and model cache above exist, or verify the configured HTTP endpoint; Iris never falls back to a different voice |
| Graph view empty | talk to her once, or switch away from and back to **Memory** |
| Everything is slow | lower the context window to 8K, or switch to the Speed Runner profile |

Logs live under `<data>\logs\` (the data root defaults to `%LOCALAPPDATA%\CompanionOS`, or
`COMPANIONOS_HOME` when configured):

| File | What it records |
|---|---|
| `companion.log` | Readable app, voice, module, memory, and recovery messages; rotates at 8 MB and keeps one previous segment. |
| `iris-YYYYMMDD.jsonl` | Structured behavior events and durations, suitable for filtering or replay-oriented debugging; bounded daily segments are retained for seven days. |
| `system-YYYYMMDD.jsonl` | Process lifecycle, UI heartbeat and stalls, plus a lightweight CPU/RAM/process snapshot at startup and every 30 seconds; bounded daily segments are retained for seven days. |

Use **Diagnostics → Behavior**, **App diary**, and **OS watch** to inspect the live tails, or
choose **Open logs folder** to collect the files. Host snapshots use in-process Windows and
.NET counters; they do not launch a GPU probe. If the data drive is unavailable at startup,
Iris remains usable and the in-memory OS watch records that persistent system logging could
not be opened.

## Building by hand

```powershell
dotnet build CompanionOS.sln -c Release
dotnet run --project src/CompanionOS.App/CompanionOS.App.csproj
dotnet test tests/CompanionOS.Core.Tests/CompanionOS.Core.Tests.csproj
```

Package versions are pinned in the three `.csproj` files. If a package moves its API, the
file that needs touching is named at the top of each comment block, and every third-party
dependency is isolated in exactly one file:

| Dependency | Only file that uses it |
|---|---|
| `Microsoft.Data.Sqlite` | `Core/Memory/MemoryGraph.cs` |
| `NAudio` | `Core/Voice/AudioPlayback.cs`, `MicrophoneCapture.cs`, `Modules/MusicPlayerModule.cs` |
| `System.Drawing.Common` | `Core/Perception/ScreenCapture.cs` |
| `Whisper.net` | `Core/Voice/SpeechRecognition.cs` |
| `Windows.Media.Control` (SMTC) | `Core/Perception/MediaSessionSensor.cs` |
| Avalonia | `src/CompanionOS.App/**` |

## First build: the three places that may need a nudge

This code was written without a compiler in the authoring environment, so if the very first
`dotnet build` reports errors, they will be in one of these three places, and each is one file:

1. **Package API drift.** Versions are pinned, but if a package has moved an API since this
   was written, the fix is local: `Whisper.net` is only used in
   `Core/Voice/SpeechRecognition.cs`, `NAudio` in `AudioPlayback.cs` / `MicrophoneCapture.cs` /
   `MusicPlayerModule.cs`, `System.Drawing` in `ScreenCapture.cs`.
2. **Avalonia theme property names.** All views are plain C#, so a renamed property shows up as
   a single compiler error in the view that uses it. The design values live in
   `Interface/Theme/Palette.cs` and the helpers in `Interface/Ui.cs`.
3. **Windows media session projection.** `Core/Perception/MediaSessionSensor.cs` uses
   `Windows.Media.Control`, which needs the `net9.0-windows10.0.19041.0` target (already set in
   `Directory.Build.props`). If that projection is ever unavailable, delete the body of that one
   class and everything else keeps working — she simply stops knowing what is playing.

Nothing else in the repository reaches outside its own assembly, which is why the blast radius
of any single fix stays small.
