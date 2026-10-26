# Iris handoff: machine evidence and next actions

**Prepared:** 2026-10-26  
**Repository:** `C:\DarkOrbitWeaver\ProjectIRIS`  
**Important:** Iris was not launched and no tests or model inference were run for this handoff. Findings below come from the current source, existing local logs, current settings, and your reports in chat. “Playback accepted” means Windows accepted a playback request; it does not prove you heard sound.

This file is a short, machine-specific pickup point. The wider project status is in `docs/PROGRESS.md`; operating instructions are in `docs/BUILD-AND-RUN.md` and `docs/TROUBLESHOOTING.md`; the complete product goal is in `docs/END-GOAL.md`.

## 1. Microphone capture-loss finding

There are exactly two `microphone.capture.lost` events in the retained Iris event files. Both are immediately preceded by orderly shutdown and report `intentional_close:false`. The current source explains this: `VoicePipeline.DisposeAsync()` directly calls `_microphone.Dispose()` (`src/CompanionOS.Core/Voice/VoicePipeline.cs:1040-1072`); that reaches `MicrophoneCapture.Stop()` and `CleanUpInput()` (`src/CompanionOS.Core/Voice/MicrophoneCapture.cs:114-132,207-243`). Unlike `VoicePipeline.StopMicrophone()` (`VoicePipeline.cs:380-395`), disposal does not set `_microphoneStopRequested` before the capture-state callback. The callback consequently labels this normal close “lost” (`VoicePipeline.cs:77-100`). The two saved records therefore strongly indicate a shutdown-classification bug; by themselves, they do not prove that the headset disconnected.

The exact event records are:

```text
C:\Users\weaksou\AppData\Local\CompanionOS\logs\iris-20261026.jsonl:23: {"ts":"2026-10-26T12:31:57.218+00:00","level":"warning","area":"Voice","event":"microphone.capture.lost","correlation_id":"82b66c4c","data":{"capture_open":"False","intentional_close":"False"}}
C:\Users\weaksou\AppData\Local\CompanionOS\logs\iris-20261026.jsonl:109: {"ts":"2026-10-26T12:40:52.116+00:00","level":"warning","area":"Voice","event":"microphone.capture.lost","correlation_id":"ac6d3ac7","data":{"capture_open":"False","intentional_close":"False"}}
```

The event timestamps are UTC (`+00:00`). Here are the available surrounding lines; the second excerpt reaches the end of the file, so there are only two following records to show:

```text
iris-20261026.jsonl lines 13-33 (event at line 23):
13: {"ts":"2026-10-26T12:27:43.090+00:00","level":"trace","area":"Voice","event":"voice.segment.decision","correlation_id":"82b66c4c","ms":10.2,"data":{"outcome":"no_speech","stage":"transcription","requires_wake_phrase":"True","conversation_was_open":"False","wake_gate_available":"True","recognizer":"whisper","recognizer_available":"True","recognition_attempted":"True","capture_session_id":"82b66c4c","failure_code":"","recognized_chars":"0"}}
14: {"ts":"2026-10-26T12:27:49.210+00:00","level":"trace","area":"Voice","event":"voice.segment.decision","correlation_id":"82b66c4c","ms":1569.6,"data":{"outcome":"wake_miss","stage":"addressing","requires_wake_phrase":"True","conversation_was_open":"False","wake_gate_available":"True","recognizer":"whisper","recognizer_available":"True","recognition_attempted":"True","capture_session_id":"82b66c4c","failure_code":"","recognized_chars":"12"}}
15: {"ts":"2026-10-26T12:27:49.511+00:00","level":"trace","area":"Voice","event":"voice.segment.decision","correlation_id":"82b66c4c","ms":10.1,"data":{"outcome":"no_speech","stage":"transcription","requires_wake_phrase":"True","conversation_was_open":"False","wake_gate_available":"True","recognizer":"whisper","recognizer_available":"True","recognition_attempted":"True","capture_session_id":"82b66c4c","failure_code":"","recognized_chars":"0"}}
16: {"ts":"2026-10-26T12:27:52.327+00:00","level":"trace","area":"Sandbox","event":"permission.auto","data":{"tool":"play_music","module":"music","tier":"reflex"}}
17: {"ts":"2026-10-26T12:27:52.334+00:00","level":"info","area":"Music","event":"library.scan.completed","ms":0.5,"data":{"roots":"2","directories":"3","entries":"3","tracks":"0","unavailable_items":"0","links_skipped":"0","limited":"False"}}
18: {"ts":"2026-10-26T12:27:52.335+00:00","level":"warning","area":"Modules","event":"tool.refused","msg":"I found no audio files. Name the folders that hold your music in settings (MusicSearchDirectories), or drop files into my workspace Music folder.","ms":3.1,"data":{"tool":"play_music","module":"music","tier":"Reflex","turn":"f39fac42"}}
19: {"ts":"2026-10-26T12:27:54.341+00:00","level":"trace","area":"Voice","event":"voice.segment.decision","correlation_id":"82b66c4c","ms":11.4,"data":{"outcome":"no_speech","stage":"transcription","requires_wake_phrase":"True","conversation_was_open":"False","wake_gate_available":"True","recognizer":"whisper","recognizer_available":"True","recognition_attempted":"True","capture_session_id":"82b66c4c","failure_code":"","recognized_chars":"0"}}
20: {"ts":"2026-10-26T12:27:56.351+00:00","level":"trace","area":"Voice","event":"voice.segment.decision","correlation_id":"82b66c4c","ms":10.2,"data":{"outcome":"no_speech","stage":"transcription","requires_wake_phrase":"True","conversation_was_open":"False","wake_gate_available":"True","recognizer":"whisper","recognizer_available":"True","recognition_attempted":"True","capture_session_id":"82b66c4c","failure_code":"","recognized_chars":"0"}}
21: {"ts":"2026-10-26T12:29:38.352+00:00","level":"trace","area":"Voice","event":"voice.segment.decision","correlation_id":"82b66c4c","ms":10.5,"data":{"outcome":"no_speech","stage":"transcription","requires_wake_phrase":"True","conversation_was_open":"False","wake_gate_available":"True","recognizer":"whisper","recognizer_available":"True","recognition_attempted":"True","capture_session_id":"82b66c4c","failure_code":"","recognized_chars":"0"}}
22: {"ts":"2026-10-26T12:31:57.203+00:00","level":"info","area":"Runtime","event":"shutdown.begin"}
23: {"ts":"2026-10-26T12:31:57.218+00:00","level":"warning","area":"Voice","event":"microphone.capture.lost","correlation_id":"82b66c4c","data":{"capture_open":"False","intentional_close":"False"}}
24: {"ts":"2026-10-26T12:31:57.286+00:00","level":"info","area":"Runtime","event":"cortex.stopped"}
25: {"ts":"2026-10-26T12:31:57.291+00:00","level":"info","area":"Runtime","event":"shutdown.done"}
26: {"ts":"2026-10-26T12:35:43.186+00:00","level":"info","area":"Modules","event":"modules.registered","data":{"count":"4","modules":"core,background_tasks,music,system"}}
27: {"ts":"2026-10-26T12:35:43.187+00:00","level":"info","area":"Music","event":"library.scan.completed","ms":1.5,"data":{"roots":"2","directories":"3","entries":"3","tracks":"0","unavailable_items":"0","links_skipped":"0","limited":"False"}}
28: {"ts":"2026-10-26T12:35:43.240+00:00","level":"info","area":"Senses","event":"senses.started"}
29: {"ts":"2026-10-26T12:35:43.292+00:00","level":"info","area":"Voice","event":"microphone.capture.opened","correlation_id":"38b9729b","data":{"capture_open":"True","intentional_close":"False"}}
30: {"ts":"2026-10-26T12:35:43.608+00:00","level":"info","area":"Runtime","event":"cortex.process_started","data":{"profile":"Vision Agent","pid":"17316","port":"8747","model":"brain.gguf"}}
31: {"ts":"2026-10-26T12:35:43.853+00:00","level":"info","area":"Voice","event":"voice.online","data":{"endpoint":"http://127.0.0.1:18765"}}
32: {"ts":"2026-10-26T12:35:46.656+00:00","level":"info","area":"Runtime","event":"cortex.ready","ms":3053.2,"data":{"profile":"Vision Agent","context":"16384"}}
33: {"ts":"2026-10-26T12:35:46.657+00:00","level":"info","area":"Runtime","event":"boot.done"}

iris-20261026.jsonl lines 99-111 (event at line 109; file ends at 111):
99: {"ts":"2026-10-26T12:38:17.189+00:00","level":"info","area":"Brain","event":"turn.report","correlation_id":"976d4a11","data":{"origin":"text","image_attached":"False","memories_activated":"0","tools":"","round_limit_hit":"False"}}
100: {"ts":"2026-10-26T12:38:18.231+00:00","level":"trace","area":"Voice","event":"speech.reflex.playback_accepted","correlation_id":"976d4a11","ms":2167.1,"data":{"category":"acknowledge"}}
101: {"ts":"2026-10-26T12:38:18.468+00:00","level":"debug","area":"Voice","event":"synth.done","correlation_id":"976d4a11","data":{"chars":"38","bytes":"132524","format":"wav"}}
102: {"ts":"2026-10-26T12:38:21.416+00:00","level":"trace","area":"Voice","event":"speech.playback_accepted","correlation_id":"976d4a11","data":{"bytes":"132524"}}
103: {"ts":"2026-10-26T12:38:21.559+00:00","level":"debug","area":"Voice","event":"synth.done","correlation_id":"976d4a11","data":{"chars":"20","bytes":"94124","format":"wav"}}
104: {"ts":"2026-10-26T12:38:23.746+00:00","level":"trace","area":"Voice","event":"speech.playback_accepted","correlation_id":"976d4a11","data":{"bytes":"94124"}}
105: {"ts":"2026-10-26T12:38:23.950+00:00","level":"debug","area":"Voice","event":"synth.done","correlation_id":"976d4a11","data":{"chars":"37","bytes":"117164","format":"wav"}}
106: {"ts":"2026-10-26T12:38:26.577+00:00","level":"trace","area":"Voice","event":"speech.playback_accepted","correlation_id":"976d4a11","data":{"bytes":"117164"}}
107: {"ts":"2026-10-26T12:38:26.579+00:00","level":"debug","area":"Brain","event":"turn.done","correlation_id":"976d4a11","ms":10518,"data":{"origin":"text","chars_in":"20","chars_out":"99","had_image":"False"}}
108: {"ts":"2026-10-26T12:40:52.103+00:00","level":"info","area":"Runtime","event":"shutdown.begin"}
109: {"ts":"2026-10-26T12:40:52.116+00:00","level":"warning","area":"Voice","event":"microphone.capture.lost","correlation_id":"ac6d3ac7","data":{"capture_open":"False","intentional_close":"False"}}
110: {"ts":"2026-10-26T12:40:52.187+00:00","level":"info","area":"Runtime","event":"cortex.stopped"}
111: {"ts":"2026-10-26T12:40:52.192+00:00","level":"info","area":"Runtime","event":"shutdown.done"}
```

All source paths found that can emit the lost event:

1. `src/CompanionOS.Core/Voice/VoicePipeline.cs:77-100` is the sole event-name emitter. It emits `microphone.capture.lost` only when `CaptureStateChanged(false)` arrives while `_microphoneStopRequested` is zero.
2. `src/CompanionOS.Core/Voice/MicrophoneCapture.cs:166-204` handles the WinMM/NAudio `RecordingStopped` callback. If the device had been capturing, it invokes `CaptureStateChanged(false)`. An unplug, driver stop, device disable, or other capture termination can reach this branch; the code logs a driver exception at lines 168-171 if present.
3. `src/CompanionOS.Core/Voice/MicrophoneCapture.cs:207-243` invokes the same callback during its own cleanup if it had been capturing. It is normally wrapped by `VoicePipeline.StopMicrophone()` (`VoicePipeline.cs:380-395`), which marks the close intentional. `VoicePipeline.DisposeAsync()` (`VoicePipeline.cs:1040-1072`) instead calls `_microphone.Dispose()` directly at line 1065, exposing the false “lost” label during clean shutdown.

Reproduction attempts you can safely do tonight, in order:

1. Start Iris with the microphone open, then use Iris’s normal Close/Quit action. The current code path is expected to reproduce the false lost event on clean shutdown; confirm that `shutdown.begin` is immediately before it and `shutdown.done` follows. This is the most direct reproduction supported by the saved evidence.
2. Start with the USB headset mic selected and open, then unplug the headset while Iris is listening. Reconnect it afterward. This exercises the device-stopped callback. Do not unplug while Iris is writing or installing anything; no such work should be needed for this test.
3. With Iris listening, disable the selected recording device in Windows Sound settings, then re-enable it. The exact result depends on the driver; I never tested this on your PC.
4. With Iris listening, change Windows’ default recording device or its privacy permission, then speak once. The current source uses the configured device index and NAudio/WinMM (`MicrophoneCapture.cs:69-99`); whether Windows terminates the existing handle is driver-dependent. I never tested this on your PC.

After each attempt, inspect `iris-YYYYMMDD.jsonl` for `microphone.capture.opened`, `.closed`, `.lost`, or `.open_failed`, and inspect `companion.log` for `Recording stopped unexpectedly:`. The latter message is only present if NAudio supplies an exception (`MicrophoneCapture.cs:166-171`).

## 2. Log locations and retention

The app’s active data root is `%LOCALAPPDATA%\CompanionOS` unless `COMPANIONOS_HOME` overrides it (`src/CompanionOS.Core/Configuration/CompanionPaths.cs:17-50,79-95`). The current logs physically found on this PC are under `C:\Users\weaksou\AppData\Local\CompanionOS\logs\`.

| Record | Exact current path | Retention / size behavior | Exit behavior |
|---|---|---|---|
| Readable app log | `C:\Users\weaksou\AppData\Local\CompanionOS\logs\companion.log` | Active file max 8 MiB plus one previous file, `companion.previous.log`, max 8 MiB (`CompanionLog.cs:43-44,65-78,158-184`; `docs/BUILD-AND-RUN.md:98-105`). This is size-based, not days-based. | Not deleted on normal exit. It is appended across sessions and only rotated when the size limit is reached. |
| Structured event JSONL | `C:\Users\weaksou\AppData\Local\CompanionOS\logs\iris-YYYYMMDD.jsonl` | Daily files, each max 8 MiB plus a same-day previous segment if it rolls; files older than seven calendar days are pruned when a new day’s event is appended (`IrisEvent.cs:19-20,30-38`; `RollingJsonLinesWriter.cs:11,39-82,86-101`). | Not deleted on normal exit. |
| Turn journal | `C:\Users\weaksou\AppData\Local\CompanionOS\memory\journal.jsonl` | No day-based retention. Rolls when the active file reaches 8 MiB to a timestamped `.old` archive (`TurnJournal.cs:60-72,249-270`). I did not find a cleanup policy for those archives. | Not deleted on normal exit; appended and flushed to disk (`TurnJournal.cs:249-260`). |
| System watch | `C:\Users\weaksou\AppData\Local\CompanionOS\logs\system-YYYYMMDD.jsonl` | Daily files, max 8 MiB per active segment, seven calendar days (`SystemWatch.cs:72`; `RollingJsonLinesWriter.cs:11,39-82,86-101`; `docs/BUILD-AND-RUN.md:104-105`). | Not deleted on normal exit. Each record is flushed by the rolling writer; clean exit writes a lifecycle record (`SystemWatch.cs:22-31,140-155`; `Program.cs:46`). |

The app log and turn journal have no number-of-days guarantee. The event/system seven-day deletion occurs when a new daily record is appended, not necessarily at process exit. No log is intentionally wiped just because Iris exits. The UI’s in-memory views are bounded separately: app log 600 entries (`CompanionLog.cs:43,125-128`), Iris events 1,000 (`IrisEvent.cs:19,163-169`), and system watch 600 (`SystemWatch.cs:Recent` and `Trace`).

## 3. User-reported test-session issues

The JSONL does not persist transcripts or microphone samples (`VoicePipeline.cs:1196-1199`); therefore it cannot prove which exact words you said or what you heard. The statuses below distinguish your report from machine-verifiable evidence. “Unconfirmed” means the report is real but the retained evidence does not reproduce or close it. No item is marked fixed based only on code edits.

1. **Voice preview initially failed / selected voices sounded wrong.** You said the app claimed it could not render selected voices; later you heard a male voice identified as Alba and said Estelle sounded robotic. Evidence from the older endpoint-based run in `companion.log` includes `10:54:24.696 Info Ui Notice queued: Warning · She cannot speak: nothing answers at http://127.0.0.1:8880/v1...` and `10:54:24.696 Warning Voice Voice engine offline at http://127.0.0.1:8880/v1...`. Current source/config now select Pocket and Vera (`settings.json`: `Voice.Engine=pocket`, `Voice.VoiceId=vera`; `VoicePipeline.cs:62-68`; `docs/TROUBLESHOOTING.md:89-109`). Current event log has `12:25:43.708 ... voice.online` and `12:25:48.579 ... Reflex bank ready with 22 instant phrases` (`iris-20261026.jsonl:4,9`). That proves the worker answered, not that Vera sounds natural or that the output was audible. **Status: unconfirmed on current build; previous configured HTTP engine failure is evidenced.**
2. **Changing to Vira appeared to stop replies; calling “Hey Iris / Iris / Yo Iris” sometimes did nothing.** Current retained log has wake misses: `2026-10-26T12:27:49.210+00:00 ... "outcome":"wake_miss" ... "recognized_chars":"12"` (`iris-20261026.jsonl:14`) and `2026-10-26T12:37:09.326+00:00 ... "outcome":"wake_miss" ... "recognized_chars":"23"` (`iris-20261026.jsonl:60`). Earlier logs also contain `wake.miss` at `iris-20261025.jsonl:212,215,241`. A later turn was accepted in an open conversation: `12:37:18.509 ... "outcome":"request_in_open_conversation"` (`iris-20261026.jsonl:65`). These records confirm wake misses occurred but do not establish which preset change caused them. Current profile is Vera, not Vira. **Status: unconfirmed root cause; wake misses evidenced; not verified fixed.**
3. **Recognition felt unreliable and behind modern assistants.** You reported repeated calls not engaging and poor transcription. The current log includes no-speech decisions (`iris-20261026.jsonl:13,15,19-21,58-59,64`) and the wake misses above. The retained app log also has old misrecognitions such as `16:06:16.835 Info Voice Heard: NSMG.`, `16:06:35.418 Info Voice Heard: [LAUGHTER]`, and `17:51:16.993 Info Voice Heard: - Where is?` (`companion.log`; timestamps are local). Those old lines are direct evidence of recognizer output quality in that session; they are not evidence of Pocket TTS. **Status: reproducible in historical evidence; current accuracy not tested.**
4. **Speech felt slow / robotic / inconsistent, and the user wanted conversational, interruptible voice.** Your listening judgment is not captured by logs. The current log shows 1.4–2.6 second acknowledgement/reflex durations and generated reply audio chunk events, e.g. `12:37:21.134 ... speech.reflex.playback_accepted ... "ms":2621.2`, `12:37:21.488 ... synth.done`, then `12:37:25.468 ... speech.playback_accepted` (`iris-20261026.jsonl:70-72`). `turn.done` includes total turn time but not physical speech onset or first-audio-output time (`AssistantBrain.cs:189`; `VoicePipeline.cs:874-941`). **Status: subjective quality/latency unconfirmed on current Pocket/Vera; no listening test performed here.**
5. **Media control was claimed or expected, but a YouTube video did not pause; you said Iris hallucinated the action.** The event log directly records `2026-10-26T12:37:18.509+00:00 ... "event":"media.pause_unconfirmed"` (`iris-20261026.jsonl:66`), after a voice request event (`:65`). This means the OS media pause was not confirmed; it does not contain the model’s exact claim. **Status: unconfirmed as hallucinated wording; media pause failure is evidenced.**
6. **Music folder/play action did not find music.** The log says `library.scan.completed ... "tracks":"0"` followed by `tool.refused` with `I found no audio files...` (`iris-20261026.jsonl:17-18`; similar events at `iris-20261025.jsonl:18,163`). **Status: reproducible for the currently indexed roots; not fixed until a real folder is indexed and playback is heard.**
7. **Notifications appeared stuck; UI items/icons looked misaligned or generic; brain status sometimes said unavailable.** You supplied screenshots showing uneven spacing/centering, mic control, labels such as `LOCAL ONLY`, a stuck notice, and an unavailable-brain status. The current `companion.log` includes notice-add/remove diagnostics, for example `10:54:28.620 Info Ui Notices changed: Remove...` and `10:54:30.619 Info Ui Notices changed: Remove...`, so at least some notices are removed in that old run; this does not prove the screenshot’s notice lifecycle or current layout. The event logs do not contain screenshot geometry. **Status: unconfirmed; requires your UI test and/or a recorded reproduction.**
8. **No-microphone behavior must remain recoverable.** You asked whether Iris breaks when no mic is attached. `MicrophoneCapture.Start()` explicitly returns false for no devices or open failure (`MicrophoneCapture.cs:58-110`), and voice start leaves text usable and presents a notice (`VoicePipeline.cs:407-427,617-646`). The current machine log has successful microphone opens, not a no-device test. **Status: code path exists; I never tested the no-mic scenario on this PC.**
9. **Browser/YouTube behavior learning, music download/conversion, and broad PC control were requested as capabilities.** They are not tests you reported completing. The current browser module and host implementation have not been validated in real Chrome in this handoff; existing logs shown here do not prove browser behavior. **Status: not done / not tested, not a reproduced bug.**

## 4. Build and launch in a normal window

The project targets `net9.0-windows10.0.19041.0` (`Directory.Build.props:8`) and the app is a Windows GUI executable (`src/CompanionOS.App/CompanionOS.App.csproj:1-10`). The current working tree already contains this Debug executable:

`C:\DarkOrbitWeaver\ProjectIRIS\src\CompanionOS.App\bin\Debug\net9.0-windows10.0.19041.0\CompanionOS.exe`

To build and run normally from PowerShell:

```powershell
Set-Location C:\DarkOrbitWeaver\ProjectIRIS
dotnet build src/CompanionOS.App/CompanionOS.App.csproj -c Debug
dotnet run --project src/CompanionOS.App/CompanionOS.App.csproj -c Debug --no-build
```

Or use the repository wrapper, which builds and launches Debug by default:

```powershell
Set-Location C:\DarkOrbitWeaver\ProjectIRIS
powershell -ExecutionPolicy Bypass -File scripts/run.ps1
```

The wrapper’s `scripts/run.ps1` uses `dotnet build`, then `dotnet run --no-build`; `-Release` builds/runs Release and `-BuildOnly` compiles without starting Iris. The Release executable path, after a Release build, is:

`C:\DarkOrbitWeaver\ProjectIRIS\src\CompanionOS.App\bin\Release\net9.0-windows10.0.19041.0\CompanionOS.exe`

I verified the Debug executable exists on disk; I did not run it or verify this binary’s runtime behavior. A normal launch starts the configured local brain/TTS services and may load the brain into VRAM (`docs/BUILD-AND-RUN.md:14-36,98-120`; `AssistantBrain.Turn.cs:135-177`).

First-run setup is documented in `docs/BUILD-AND-RUN.md:14-36,65-76`: if the assets are missing, run `powershell -ExecutionPolicy Bypass -File scripts/setup.ps1` to install the app’s configured brain, Whisper, runtime and Pocket TTS. This can download several GB; setup does not itself start the TTS worker or load model weights (`BUILD-AND-RUN.md:24-36`). To install just individual components, the script supports `-Component brain|ears|runtime|voice` (`scripts/setup.ps1:24-37`). I did not run setup.

For Windows audio access, ensure Windows **Settings → Privacy & security → Microphone** permits microphone access and access for desktop apps, then check **Settings → Ears** in Iris and choose the attached headset mic. The current settings file says `Ears.Enabled=True`, `Ears.InputDeviceNumber=0`, `Ears.Recognizer=auto`, and `Ears.WhisperModelFile=whisper-base.bin`. `MicrophoneCapture.ListInputDevices()` provides the enumerated input list (`MicrophoneCapture.cs:33-50`); a stale selected index will not silently choose a different mic (`:76-88`). Current `Voice.OutputDeviceNumber=-1`, which means use the Windows default output (`VoicePipeline.cs:155-161`). Set Windows’ default output to the headphones before launching, or select a specific output in **Settings → Voice**. I never tested Windows permission prompts or headphone routing on this machine.

## 5. Voice latency timestamps

The requested four physical timestamps are not currently all logged. The event log has recognition decision duration (`VoicePipeline.cs:1194-...`, `LogSegmentDecision` around line 1331), end-to-end `turn.done` duration (`AssistantBrain.cs:189`), synthesis completion (`VoicePipeline.cs:904`), and a playback-accepted event (`VoicePipeline.cs:930`). It does **not** currently mark microphone speech onset, the VAD speech-end instant, or the first sample actually rendered by the output device. `speech.playback_accepted` is not the same as sound reaching your headphones.

For useful stamps, change:

- `src/CompanionOS.Core/Voice/VoicePipeline.cs`: stamp the first VAD-positive frame in `OnMicrophoneSamples` and the VAD end when silence closes a segment (the segmentation block is around lines 1130-1175); also log time recognition starts and finishes in `ProcessSegmentAsync` (around 1194 onwards).
- `src/CompanionOS.Core/Voice/SpeechRecognition.cs`: log recognizer request start/end separately from segmentation and wake matching.
- `src/CompanionOS.Core/Brain/AssistantBrain.cs` or `AssistantBrain.Turn.cs`: carry the interaction ID and stamp the first assistant text/token and first sentence scheduled for speech. `turn.done` is too late to isolate those stages.
- `src/CompanionOS.Core/Voice/SpeechSynthesis.cs` / `VoicePipeline.cs`: stamp TTS request start and first returned audio chunk, not only `synth.done` after the full WAV is ready.
- `src/CompanionOS.Core/Voice/AudioPlayback.cs`: expose and log the actual output-start callback from NAudio (`PlaybackStopped` exists in the player lifecycle; this file owns output-device playback). Use a monotonic stopwatch for durations and UTC wall timestamps for cross-file ordering.

The four numbers should be: first VAD speech frame; VAD end/segment finalized; first assistant response token or spoken response start (define which one and log both if possible); first audio buffer accepted by the output device. Existing `correlation_id` should link them. Do not record audio or transcript for these timings.

## 6. Ten-minute Pocket/Vera voice test

Use only the currently selected local Pocket TTS voice, Vera. Do not install another engine. Before starting, confirm **Persona → Voice** shows Vera, **Settings → Ears** shows the intended mic, **Settings → Voice** says Pocket, and Windows output is the headphones. The current settings file already records Pocket/Vera and headphones-as-Windows-default (`settings.json`: `Voice.Engine=pocket`, `Voice.VoiceId=vera`, `Voice.OutputDeviceNumber=-1`).

1. **Minute 0-1 — readiness.** Open Iris normally, wait until the UI reports ears and voice online. Confirm the mic indicator changes when you speak. If it does not, stop and note the displayed status; do not interpret silence as a TTS test.
2. **Minute 1-2 — sentence 1:** “Hey Iris, say hello and tell me you’re listening.” Listen for a quick acknowledgement, clean consonants, no clipped first word, and whether Vera is consistent. Log should show `voice.segment.decision` with a request outcome, a correlated turn, then `synth.done` and `speech.playback_accepted` (`VoicePipeline.cs:1331,904,930`; `AssistantBrain.cs:189`).
3. **Minute 2-3 — sentence 2:** “Iris, what time is it? Answer in one short sentence.” Listen for recognition of the whole request and a concise answer. Check for `voice.segment.decision`, `turn.report`, `turn.done`, and corresponding speech events. If the clock tool is disabled/unavailable, note that separately from voice quality.
4. **Minute 3-4 — sentence 3:** “Iris, listen carefully: my test phrase is blue lantern, seven quiet waves. Repeat it exactly.” Compare the visible recognized text with what you said, then compare the spoken reply to the phrase. `voice.segment.decision` records only character count/outcome, not the transcript; the UI conversation is needed to judge words.
5. **Minute 4-5 — sentence 4:** “Iris, answer in one short sentence and say ‘voice check complete.’” Listen for natural pauses, stable pronunciation and speed. Use `synth.done` and `speech.playback_accepted` to verify rendering and handoff only; they do not prove audibility.
6. **Minute 5-7 — sentence 5:** “Iris, tell me a short two-sentence story about a rainy train station.” This creates a longer spoken answer. Listen for prosody, breathless pacing, repeated sentence-boundary glitches, changes in timbre between chunks, and dead air between chunks. Expect one turn ID and one or more `synth.done` / `speech.playback_accepted` pairs.
7. **Minute 7-8 — mid-answer interruption:** While the story is audibly playing, say “Stop. Say only: interrupted successfully.” Listen for immediate stop, whether the mic hears you over her audio, whether the new request is recognized, and whether she speaks only the short correction. Check for the original turn cancellation/interruption and a new correlated voice turn. If she continues speaking over you, record the time and check for `voice.segment.decision`, `speech.output_failed` or cancellation events; current playback logs do not stamp audible interruption latency.
8. **Minute 8-10 — repeat sentence 1 and write notes.** Repeat the greeting once to compare warm-path timing and consistency. In a note, record perceived start-to-response delay, any missed/incorrect words, loudness, pronunciation, pacing, interruptions, and whether the selected headphone output was used. Copy the exact UTC timestamps and correlation IDs from the JSONL.

In the event log, useful events include `microphone.capture.opened/closed/lost`, `voice.segment.decision` (including `no_speech` and `wake_miss`), `turn.report`, `turn.done`, `synth.done`, `speech.playback_accepted`, `speech.output_failed`, and any cancellation event. `turn.done` measures turn duration; `synth.done` marks completed generated audio. Neither alone measures first-audio latency. The available current log shows long output split into chunks; e.g. a text turn at `12:37:40Z` took `7667.4 ms` and emitted playback accepted at `12:37:40.612Z` (`iris-20261026.jsonl:34-40`); this is historical evidence, not a fresh baseline and not one of these test prompts.

## 7. Current uncommitted changes

`git status --short` shows 26 modified tracked files and no untracked files. `git diff --stat` reports **659 insertions and 68 deletions**. HEAD is `8683351` (`Fix startup, notices, and UI alignment`). Nothing has been staged or committed by this handoff. The inspected diff is not a complete code review, and I did not build or test it; “safe” below means lower expected regression risk, not proven safe.

| Area | Files | Risk |
|---|---|---|
| Runtime/UI boot, startup state, notices and browser-install prompt | `src/CompanionOS.App/Interface/MainWindow.cs`, `ShellModel.cs`, `ShellModel.Events.cs`, `Views/PersonaView.cs` | **Risky:** touches startup/runtime UI flow and notice state; requires live launch and UI checks. |
| Voice pipeline, playback, recognition, Pocket configuration/profile gallery | `src/CompanionOS.Core/Voice/AudioPlayback.cs`, `PocketTts.cs`, `SpeechRecognition.cs`, `SpeechSynthesis.cs`, `VoicePipeline.cs`, `VoiceGallery.cs`, `VoiceProfile.cs` | **Risky:** affects capture, cancellation, buffering, selected voice and actual output timing. Needs build plus device testing. |
| Brain, persona, settings, memory/tool feedback and perception | `AssistantBrain.cs`, `AssistantBrain.Turn.cs`, `Persona.cs`, `CompanionSettings.cs`, `SettingsStore.cs`, `ModuleContracts.cs`, `PerceptionEngine.cs`, `src/CompanionOS.Modules/CoreToolsModule.cs` | **Risky:** changes turn behavior, migration/default settings, tool routing and media state. Tests and behavior checks needed. |
| Regression tests | `tests/CompanionOS.Core.Tests/CoreRegressionTests.cs` | **Lower implementation risk, but unverified:** contains 45 added lines; run the suite after code changes settle. |
| User docs | `docs/BUILD-AND-RUN.md`, `PLACEHOLDERS.md`, `ROADMAP.md`, `TROUBLESHOOTING.md` | **Lower risk:** text-only, but documentation must match the actual scripts and behavior. |
| Browser module docs/manifest | `iris-browser/README.md`, `iris-browser/plugin.json` | **Medium risk:** mostly install and metadata changes, but plugin manifest edits can change host discovery/enablement. No Chrome validation was done. |

Suggested clean commits, after reviewing and running checks:

1. `fix(voice): harden Pocket playback and interruption lifecycle` — voice files plus voice-specific regressions in `CoreRegressionTests.cs`.
2. `fix(core): improve turn feedback and perception reliability` — brain, persona, settings, module contracts, perception, `CoreToolsModule.cs`, and their tests.
3. `fix(ui): clarify startup state and browser setup prompts` — app interface files; validate the first-run notice and each affected UI state before committing.
4. `docs: update setup, diagnostics, and troubleshooting guidance` — `docs/BUILD-AND-RUN.md`, `PLACEHOLDERS.md`, `ROADMAP.md`, `TROUBLESHOOTING.md`.
5. `docs(browser): document module install and pairing` — `iris-browser/README.md` and `plugin.json`; keep the manifest change separate so its host behavior can be reviewed independently.

These are proposals only. No files are staged and no commit was created.

## 8. Immediate next three steps

1. Fix the false microphone-loss report by routing pipeline disposal through an intentional microphone stop (or equivalent close-reason state), then add a regression asserting normal shutdown records `microphone.capture.closed`, not `.lost`. The exact suspicious path is `VoicePipeline.cs:1040-1072` plus `MicrophoneCapture.cs:207-243`.
2. Build and run the Pocket/Vera ten-minute test, preserving the event log and your short listening notes. Add first-VAD-frame, VAD-end, first-response, and real playback-start stamps in the files listed in section 5 before comparing latency claims.
3. Reproduce the reported media and wake failures with one known active Windows media session and the headset mic. Record your spoken prompt in your notes (the logs intentionally omit transcripts), the visible response, event correlation IDs, and whether the media action was actually confirmed.

## Evidence and things not verified

- The exact local log files and sizes were enumerated from `%LOCALAPPDATA%\CompanionOS\logs` on 2026-10-26. The two loss events are at `iris-20261026.jsonl:23,109`.
- Current settings values above were read from `%LOCALAPPDATA%\CompanionOS\config\settings.json` on 2026-10-26. They are user-editable and may change.
- I did not launch Iris, access the microphone, play audio, run setup, run a build, run tests, validate Windows microphone privacy, inspect screenshots beyond your supplied reports, or test Chrome. `docs/PROGRESS.md:119-141` also states the last documented suite/build result is not a target-PC audio/layout acceptance pass.
- The old app log contains endpoint failures from an earlier configuration; current event logs show Pocket worker readiness. Do not treat those two periods as the same voice-engine state.

---

## 9. Session record — 2026-10-26 (evening)

Shipped, built clean (0 warnings) with 84/84 tests green:

1. **New ears live:** NVIDIA Parakeet Unified EN 0.6B Q5_K_M (541 MB, sha-pinned) through the
   transcribe.cpp 0.4.1 C API — `ParakeetRecognizer`, strict CPU backend, 2 threads, offline.
   Smoke test on noise-only audio returned `NoSpeech` (anti-hallucination at the engine level).
   `Ears.Recognizer = "parakeet"`; `whisper-base.bin` removed per the owner's call (§23b).
2. **Dead-capture watchdog** (`microphone.capture.restart`): ~10 s of pure digital silence
   auto-reopens the capture stream, max 2 attempts then one honest warning. Root cause of
   "yo iris → nothing": the H510-PRO headset streams silence after boot until reopened.
   The mic test also showed rms ≈ 0.0100 vs the 0.025 speech threshold — input level too low.
3. **Pocket worker ownership rule:** every voice start removes unowned pocket-tts workers
   first (`DemolishUnownedWorkers` in `PocketTts.cs` — a wedged orphan answered /health
   while /tts was dead).
4. **open_application made actually usable:** allow-list launcher (calculator, notepad, paint,
   chrome, edge, explorer, task manager, terminal, cmd), tier Scoped (runs on command),
   honest refusal for anything outside the list. The hallucinated "Calculator is open now."
   (she called the *media* tool and lied) is addressed by two new persona rules: on-voice
   brevity ("Done. / On it. / Alright Boss.") and no-success-without-tool-proof. Rules are in
   both the code defaults and the live config/persona.json.
5. **Voice picker:** Eve removed everywhere (bad English per the owner); picker is anna, mary,
   vera; anyone on Eve migrates to Vera. Tests updated to the new set.
6. **Chat/scroll UI:** Enter sends (Shift+Enter newline) — the composer had swallowed Enter;
   text padding added; mic/send glyphs at 20 px so they centre at every display scale;
   visible scrollbars in chat, memory rail, settings, modules and diagnostics; Diagnostics
   page layout fixed (stretching tabs, inner scroll, square outer frame).
7. **Memory map navigation:** the canvas only hit-tested painted pixels, so pans/zooms were
   mostly dead — now `ICustomHitTest` admits the whole surface; wheel zooms toward the
   cursor (0.15×–6×), right/middle drag pans anywhere, left click selects, left drag pans.
   Temporarily missing pan/zoom affordances; consider a fit-view button when nodes grow.
8. **Browser extension setup:** the button always works from the module card; after launching
   the installer the UI now shows the manual Chrome steps (Chrome 137+ requires Developer
   mode → Load unpacked, the folder lands on the clipboard).

Not yet done / queued: hard max-token cap for voice turns, Vera-style rate/pitch wiring,
garbage-transcript classifier, `look_at_screen` latency, housekeeping commits (~40 files
uncommitted, planned in 5 chunks), system-wide wake hotkey, and the TTS engine research (the
owner wants a more human voice; Pocket voices read as robotic to him).
