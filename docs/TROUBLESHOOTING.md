# When something is wrong

Her first move is always the same: **read the log**. It is a plain text file, newest at the
bottom, at `%LOCALAPPDATA%\CompanionOS\logs\companion.log` (or under `COMPANIONOS_HOME`).

The status strip in her window also tells the truth: brain state, VRAM, context fill, RAM and
whether the microphone is open.

---

## She starts, but says her thinking model is not loaded

1. `powershell -ExecutionPolicy Bypass -File scripts/setup.ps1 -VerifyOnly` — this lists what
   is present and what is missing.
2. If `brain.gguf` is missing, run the setup without `-VerifyOnly`, or point
   `Brain.Profiles[].ModelFile` at a GGUF you already have (an absolute path is allowed).
3. If the file exists, check the log for the last twenty lines llama-server printed. They are
   captured on purpose — the reason a model will not load is almost always in them.
4. If the model loads but responses are gibberish, the profile and the file have drifted apart:
   a vision projector (`brain-mmproj.gguf`) must come from the same family as the weights.
5. If the log shows llama-server exiting while loading CUDA ("cannot open cudart64_13.dll" or
   similar), the CUDA DLL pack did not land next to the binaries. Re-run
   `scripts/setup.ps1 -Component runtime`; both artifacts unpack into
   `runtimes\llama.cpp\` and its presence checks are content-based
   (`llama-server.exe`, `cudart*.dll`), so a partial unpack is reported as missing.
   A machine without any NVIDIA GPU is supported by the Vulkan fallback pair in the manifest.

## llama-server exits immediately with "unknown argument"

`Brain.Profiles[].ExtraServerArguments` defaults to:

```
--flash-attn on --cache-type-k q8_0 --cache-type-v q8_0
```

Very old llama.cpp builds do not accept `on` after `--flash-attn`. Open
`%LOCALAPPDATA%\CompanionOS\config\settings.json` and change that one line to `--flash-attn`,
then restart. Nothing else depends on it.

## She went to sleep and I cannot wake her with my voice

That is by design, not a bug. Sleep disposes the whole runtime — microphone closed, speech
model freed, every timer stopped — and leaves only the tray icon, so nothing is listening for
her name. Click the tray icon, or right-click it and choose **Wake Iris**; a fresh process
starts in about a second and a half.

If the tray icon is not there, she did not sleep: look for a notice saying the notification
area was unavailable. In that case she is *resting* (brain unloaded, ears still open) and her
name still works.

To go back to the old "rest instead of switch off" behaviour, turn off
**Settings → System → Sleep switches her off completely** (`System.SleepToTray`).

## The VRAM number did not go to zero when she went to sleep

1. Look at the status strip: "Resting" means the mode changed.
2. Check `nvidia-smi` in a terminal. If a `llama-server.exe` is still alive, the process was
   killed but not reaped — kill it once by hand; the next sleep should be clean.
3. If you pointed her at an external endpoint (LM Studio, Ollama), *that* application owns the
   model, not her. Closing it is your job; she cannot unload someone else's server.
4. After a full sleep the VRAM figure is *not measured at all* — she reports zero because
   nothing of hers is loaded, and deliberately skips the `nvidia-smi` call that would cost a
   few milliseconds every four seconds.

## She cannot hear me

* Windows **Settings → Privacy → Microphone → Let desktop apps access your microphone**.
* `Ears.InputDeviceNumber` is `-1` (system default) — try an explicit device number if the
  default is a webcam or a virtual cable.
* The microphone must be **open**: press `Ctrl+M`, or click the mic in the composer. The
  status strip says "Mic open" when it is listening.
* If the log says *no speech recogniser is available*, the Whisper model is missing — run
  `scripts/setup.ps1 -Component ears`, or set `Ears.Recognizer` to `http` and give
  `Ears.HttpEndpoint` a local `/v1/audio/transcriptions` server.

## She hears me but ignores me

She waits for her name. Try "hey Iris", "Iris", "yo Iris", "wsp iris". If background noise is
loud, lower `Ears.VoiceThreshold` (more sensitive) or raise `Ears.SilenceMilliseconds`.

If short remarks never register, check `Ears.WakeGateMinimumMilliseconds` (default 420): the
gate throws away anything shorter than that before spending CPU on transcription. It is the
first thing to lower if you speak in clipped two-word commands. Setting it to 0 disables that
stage entirely.

Setting `Ears.RequireWakePhrase` to `false` makes the open microphone answer everything — good
in a quiet room, noisy in a shared one.

## Her voice is silent or sounds wrong

Pocket TTS is the default neural voice. Check that
`%LOCALAPPDATA%\CompanionOS\runtimes\pocket-tts\pocket-tts.exe` and the
`models\pocket-tts-cache` folder exist, then use **Settings → Audio → Test voice**. Iris starts
the CPU worker during application startup; it is capped at two threads and
does not use VRAM. If the runtime reports a missing model, the Settings notice/log gives the
runtime path and startup error.

The six fixed profiles are `alba`, `anna`, `eve`, `mary`, `vera`, and `estelle`. Preview one in
**Persona → Voice**; previews render on demand. Listen to the previews before choosing, since
the profile names alone do not guarantee a particular accent or tone.

An OpenAI-compatible HTTP engine remains an explicit alternative: set `Voice.Engine` to `http`
and `Voice.HttpEndpoint` to the local server's `/v1` URL. Iris does not substitute a Windows
voice if either engine fails.

To change which voice she uses, open **Persona → Voice** and use the picker: it renders a real
line through the selected engine before you commit, and sets `Voice.ChosenByUser` so the
first-run prompt does not return.

## Her ears are off in a game and I want to talk to her

Gaming Mode closes the microphone and unloads the speech model by default, because transcription
uses CPU when it runs. It defaults to two threads and can be configured up to four. Turn on **Settings → System → Keep her ears on
during games** (`System.GamingEarsEnabled`) if you would rather talk to her while playing.

## Gaming Mode took over when I opened something that is not a game

Open **Settings → System → Gaming Mode**, or edit `System.GamingExclusions` and add the executable name
(`obs64.exe`, `javaw.exe`, …). You can also teach her: "this is not a game, it's my editor."
She stores the decision and stops guessing.

If a game is *not* detected, add its executable to `System.KnownGameExecutables`.

## The memory graph is empty

It is empty because she has not been told anything worth keeping yet. Try:

* "Remember that my graphics card is an RTX 3050 with 8 gigs."
* "Remember I hate being called sir — call me Boss."
* "Remember that coffee after 6 pm ruins my sleep."

Each of those becomes a node with typed links, and the graph view fills as soon as you open it.

## The database will not open / "database is locked"

* Close every CompanionOS window first — two instances sharing one brain database is not
  supported.
* A `brain.db-wal` file next to `brain.db` is normal and must not be deleted while she runs.
* If the file is genuinely corrupt: stop her, move `memory\brain.db*` into
  `memory\export\` as a backup, and start again. She recreates the database empty rather than
  refusing to run — that trade was made on purpose.

## She stays silent

She has exactly one voice: the neural engine at `Voice.HttpEndpoint` in `settings.json`.
If nothing answers there, she does not speak — there is no Windows-voice fallback by
design, and she says so in the panel. Start the speech server and she recovers
automatically within a minute; chat, memory and hearing are unaffected.

If she cannot hear you instead, see "Her ears do not understand" above.

## Build errors in Visual Studio

See the last section of `docs/BUILD-AND-RUN.md`: package API drift, Avalonia property renames
and the Windows media-session projection are the only three places where a first build can
fail, and each is one file.

## Starting completely fresh

```powershell
# keep a copy of the memory first if you care about it
Move-Item "$env:LOCALAPPDATA\CompanionOS\memory" "$env:LOCALAPPDATA\CompanionOS\memory-backup"
```

Delete `%LOCALAPPDATA%\CompanionOS\config\settings.json` to return every setting to its
default; delete the whole folder to forget everything (the downloaded models go too).
