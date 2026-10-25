# Build and run

## What you need

| Requirement | Notes |
|---|---|
| Windows 10 or 11 (x64) | The senses use Win32 and Windows media sessions |
| .NET 9 SDK | `dotnet --version` should print 9.x |
| NVIDIA driver with `nvidia-smi` | Only used for the VRAM meter |
| ~7 GB free disk | Brain ~2.5 GB, projector ~0.9 GB, ears ~150 MB, runtime + CUDA DLLs ~0.6 GB |

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
Use `-Component brain`, `-Component ears` or `-Component runtime`
to fetch one piece, and `-VerifyOnly` to see what is missing.

## Where things go

```
%LOCALAPPDATA%\CompanionOS\        (or COMPANIONOS_HOME)
  models\brain.gguf
  models\brain-mmproj.gguf
  models\whisper-base.bin
  runtimes\llama.cpp\llama-server.exe
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

Pocket TTS is the default local voice. The standalone CPU runtime is expected at
`%LOCALAPPDATA%\CompanionOS\runtimes\pocket-tts\pocket-tts.exe`; its Hugging Face cache is
under `%LOCALAPPDATA%\CompanionOS\models\pocket-tts-cache`. The English 18M model is selected
for this machine, and its server is limited to two logical processors at below-normal priority.
No GPU or Windows voice fallback is used. Pocket's weights require accepting Kyutai's Hugging Face
conditions once; the model is cached locally afterward. The chosen voice is stored as
`Voice.VoiceId`; the six bundled profile IDs are `alba`, `anna`, `eve`, `mary`, `vera`, and
`estelle`.

An explicitly configured OpenAI-compatible server remains supported by setting
`Voice.Engine` to `http` and `Voice.HttpEndpoint` to its `/v1` base URL. If the selected engine is
unavailable, Iris stays silent and reports the problem.

Pocket TTS is by Kyutai Labs. Its runtime code is MIT-licensed and its model weights are
[CC BY 4.0](https://creativecommons.org/licenses/by/4.0/) with the model's [use conditions](https://huggingface.co/kyutai/pocket-tts).
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

Logs: `<data>\logs\companion.log`, rotated to `companion.previous.log` once it passes 8 MB. The last 600 lines are also kept in memory so a
diagnostics panel can show them without reading the disk.

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
