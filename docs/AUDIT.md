# Iris Capability & Correctness Audit — Oct 27 2026 (read-only round)

Mission from Boss: identify all terrible logic/handling so Iris becomes a *superior agentic AI* —
"smaller models on other assistants do way more; find every reason ours underperforms."
All findings verified against code (self-read + two independent sweeps + spot-checks).
**We fix all of these after. Nothing touched during the read-only round.**

Three root mechanisms explain "she does less":
1. **Routing sits IN FRONT of the model** — she only ever holds a fraction of her hands per turn.
2. **Evidence dies at turn borders** — tool results, receipts and recall never survive into the next context.
3. **Hard caps + silent failure paths** — timeouts, aborted scans, swallowed errors make real work vanish.
Everything below is an instance of one of those three.

---

## P0 — CRITICAL (capability blockers + correctness lies)

### Axle 1: routing in front of the model
- [x] **ModuleHost.cs:267-271 — router is a hard keyword/cosine gate.** Score ≥0.08 only, `Take(2)` modules, 4-of-11 core budget. Below the floor the tool definition never reaches the model: she *cannot* call it. Multi-intent sentences structurally can't be served. (Comment at 353-358 claims "not a hard gate" — false.)
- [x] **ModuleHost.cs:346-349 — routing evidence is bidirectional substring matching.** "my" ⊆ "media", "me" ⊆ "remem…": inflated hit counts crowd real matches out of the 2-module / 4-core budgets.
- [x] **ModuleHost.cs:587-590 + CoreToolsModule.cs:298-308 — capability self-knowledge is broken.** `DescribeCapabilities()` returns one generic sentence; `search_capabilities` (her ability-discovery tool) greps that sentence and can never answer "can you do X".
- [x] **AssistantBrain.cs:34/701-706 — `MaximumToolRounds = 4`, then a canned permission-ask fallback** ("Tell me if you want me to continue."). Multi-step work (scan → filter → act → verify) dies mid-chain and requires a re-ask that restarts a whole turn with tool context gone.
- [ ] **AssistantBrain.cs:553-554 + SpeechPlanner — `IsActionRequest` keyword decides who gets the receipt-check nudge.** Broad noun list (song/video/game/app/file…) — informational questions containing media nouns can trigger "call the matching tool" nagging.
- [x] **AssistantBrain.cs:624-631 — invalid tool args get a canned string, not the schema/evidence.** A 4B re-emits the same bad args and burns a round with nothing corrective to reason from.

### Axle 2: evidence dies at borders
- [x] **AssistantBrain.Turn.cs:276-289 — tool results vanish from history between turns.** `_history` keeps user/assistant text only; what failed, what real playlist names exist, what she launched — all gone next turn. The evidence-refusal philosophy stops working across turns.
- [x] **CoreToolsModule.cs:153-154 — `recall_memory` returns titles only.** The model learns node *names*, never the facts. Memory capability pinned at titles-length.
- [x] **SynapticMemoryEngine.cs:83-86 — seed truncation before task-log filter.** Top-4 taken first, diary filtered after: four recent task-log matches = a full turn with **zero** recalled memory while real facts sit below the cutoff. (Verified: filter order is exactly that.)
- [x] **CoreToolsModule.cs:186-193 — Windows media success detected by string prefix** (`StartsWith("Windows confirmed")`): wording drift flips confirmed actions to Fail. Canonical stringly-typed control flow.
- [ ] **ModuleHost.cs:454-478 — every `BackgroundCapable` call becomes fire-and-forget.** Model gets "started in background" instead of the result; follow-up status tools may not even be routed; 5-round cap leaves no room to check outcomes.
- [x] **ModuleHost.cs:541-544 + MainWindow/others — late faults after timeout are observed and silently discarded** (`_ = task.Exception;`): outcome stays "unknown" forever even when the finished result was clean gold for the next round.
- [ ] **PluginHost.cs:471-475 + PluginModuleAdapter.cs:121-124 — plugin stdout dropped on failure** (model gets stderr only); failed 10s discovery leaves a plugin registered with **zero tools**, silent to user and model (PluginHost.cs:117-125).

### Axle 3: caps, silent deaths, corruption
- [x] **MusicLibraryScanner.cs:31 + MusicPlayerModule.cs:1244 — library scan has a 4-second budget.** Every play/list/shuffle call scans with it; big folder = permanently truncated index → "play X" fails when X exists. The flagship symptom.
- [x] **ModelClient.cs:277 + 183-239 — `max_tokens` 900-1200 always sent; `finish_reason` never checked.** Truncation mid-tool-call → fragmenting JSON silently vanishes (`Where(name not empty)`) → the turn reads as "she chose not to act". Direct cause of lazy-looking agents.
- [ ] **ModuleHost.cs:32/522 — 45s foreground tool timeout.** Longer legit work becomes `Uncertain` while the job keeps running (uncancelled — see P1 find_files / MCP entries). Companion caps: 30s default MCP `tools/call`, 5min background (PluginHost.cs:1469-1495).
- [x] **MusicPlayerModule.cs:1145-1174 — track start destroys the playing player *before* validating the new file.** Bad file = current song dies, no rollback, model told playback "could not" — never that music stopped.
- [x] **MusicPlayerModule.cs:286/358-359 — volume is per-reader and lost every track change; duck-restore hard-sets 1.0.** "Quieter" lasts one song; user's chosen level erased, model reports success.
- [x] **SandboxGuard.cs:72/163-167 + ModuleHost.cs:447-449 — 15s permission timeout auto-denies and is misreported as "The user did not allow that action."** Model can't distinguish no / AFK / missed prompt; audit trail records both as plain "denied".
- [x] **MemoryGraph.cs:292-300 — node inserted into live cache BEFORE DB persist; no rollback on failure.** Phantom memory visible to recall/consolidation this session, vanished next boot.
- [x] **MemoryGraph.cs:312-351 — `LinkAsync` check-then-insert race duplicates synapses;** the duplicate's later UPDATE matches 0 rows (fresh Guid never inserted) and affected-rows is never checked — bookkeeping writes lost silently.
- [x] **MemoryGraph.Queries.cs:92-134 — consolidation iterates a stale snapshot;** a merged-away node can still be picked as `keep` later → `PersistNodeAsync` upsert **resurrects deleted rows with duplicated content**. Long-term memory corruption on exactly the runs consolidation exists for.
- [ ] **SettingsStore.cs:56-74 — one malformed property (single typo'd enum) wipes the whole settings file to defaults.** Backup read also; then quarantine + `new CompanionSettings()`. User's profiles/hotkeys/endpoints destroyed by one bad line.
- [x] **CoreToolsModule.cs:197-213 — `set_operating_mode` maps unknown input → Awake and unconditionally clears manual gaming.** Garbage/other modes wake her and drop the game hand-off as a side effect.
- [x] **MusicPlayerModule.cs:1385-1389 — a corrupt file mid-playlist silently ends the whole queue.** No skip, no notice, no model feedback — silence is her only symptom.
- [ ] **ModuleHost.cs:96-132 — a module whose `InitializeAsync` failed stays installed, enabled, and routable.** First call = "Object reference not set…" to the model.

## P1 — HIGH (bad handling worth fixing in the same passes)

- [x] **MusicPlayerModule.cs:1394-1401/1179 — Stop vs auto-next TOCTOU race.** Generation checked outside the player lock; commanded stop can still lose and music keeps playing.
- [ ] **SystemControlModule.cs:185-231 — `find_files` is a synchronous `SearchOption.AllDirectories` sweep with no CancellationToken**, run on the ModuleHost caller thread → 45s-cap + keeps walking after "stopped waiting"; per-root catch hides skipped half-trees.
- [ ] **SystemControlModule.cs:265/394 — `double.TryParse` without InvariantCulture.** "5.5 minutes" → **55 minutes** on comma-decimal locales; unparsable → silently becomes a 5-minute timer, announced confidently.
- [x] **MusicPlayerModule.cs:1077-1085/976-980 — corrupt playlist JSON swallowed as empty list, re-reported as "no playable tracks left on disk"** (points the investigation at disk, not data); `SavePlaylist` has no atomic write.
- [x] **MusicPlayerModule.cs:135/735-737 — "play anything" while paused replays the current track** while its description promises a library pick — the vague-ask path lies.
- [x] **MusicPlayerModule.cs:1120-1126 — track matching scores file names only.** Artists in folder names/tag data unfindable; "play Iron Maiden" fails unless the filename says so. (Her-IQ-evidence philosophy: match should return *candidates* for her to reason over, not fail empty.)
- [ ] **AssistantBrain.cs:634-641 — `uncertainTools` blocks retries by tool NAME for the whole turn** even for a legitimately different target.
- [ ] **AssistantBrain.Turn.cs:37-39 — bare word "think" wakes the cortex from sleep.** "I think that song is nice" loads 7-8B VRAM on chatter.
- [ ] **LlamaServerHost.cs:229-249 — startup kills every `llama-server.exe` on the machine by name.** Collateral damage on a dev machine running other local servers.
- [ ] **AssistantBrain.cs:133-143 + LlamaServerHost — cold wake waits max 15s for weights; slow load → canned "not loaded" bounce.** First request after a deep sleep can fail outright.
- [ ] **SettingsStore.cs:156-169 — voice "normalization" overwrites the user's saved voice list and resets `ChosenByUser=false`** (one Kosoro id in list → whole list replaced + picker nags again).
- [ ] **SettingsStore.cs:78-79 — settings.json rewritten on EVERY boot; user comments silently stripped.**
- [ ] **MainWindow.Layout.cs:174-187 + App.axaml.cs:31 — closing the window with CloseToOverlay=false leaves an invisible fully-running process** (no tray, no overlay, VRAM occupied, "quit" believed).
- [x] **MemoryGraph.Queries.cs:36-58 — daily decay does a full fsync per node in a loop on the boot path, no transaction.** Tens of thousands of nodes = boot stall blocking every memory write.
- [ ] **ShellModel.Events.cs:292-294 + ShellModel.cs:36 — recall chips assigned without change notification → silently never render** when the recall event lands after AssistantStarted.
- [ ] **CompanionSettings.cs:15 — `SchemaVersion` is dead;** migrations key on magic strings, unknown-vintage files get heuristics.
- [ ] **PluginHost.cs:976-997 — one global MCP semaphore serializes every MCP call AND the process handshake.** One slow server stalls all other plugins' calls.
- [ ] **ModuleHost.cs:96-132 / RemoveWorkspacePath — `ResolveWorkspacePath` (SandboxGuard.cs:216-234) has zero callers; Scoped tier path-confinement is unenforced.**
- [ ] **SynapticMemoryEngine.cs:549-581 — boot repair deletes episodes by content prefix "Heard: "/"Requested: ".** Genuine dictation starting with those words is purged permanently at next boot.
- [ ] **MemoryGraph.cs:269-272 — episodic re-record demotes a Semantic fact to Episodic** silently (`if (layer < existing.Layer) existing.Layer = layer;`).
- [ ] **MemoryGraph.cs:369-381/145-149 — strength/usecount mutations race without locks;** TouchNode racing ForgetNode can persist a just-deleted node (resurrection #2).
- [ ] **MemoryGraph.Queries.cs:106-113 — consolidation can merge "night drive" and "night drive 2"** (title-substring + cosine 0.93) — distinct facts folded into one while user is away.

## P2 — MEDIUM (smells/debt, fix opportunistically)

- [x] MusicPlayerModule.cs:297-299 — `list_music` shows 20 of many thousands; no music-search tool exists (evidence gap).
- [ ] ModuleHost.cs:29 — `MaximumToolsPerTurn = 24` is generous but unused since core budget 4 + Take(2) modules bind first.
- [ ] ModuleHost.cs:522/541 — timeout keeps work running uncancelled even for cancellable modules (not just late faults).
- [x] CoreToolsModule.cs:62-65 — `forget_memory` description forces "ask user to open Memory" — in-tool permission-stall; should return candidates for her to decide (evidence pattern like MissingPlaylistResult).
- [ ] SystemControlModule.cs:332 — timer-completion speech fire-and-forget, silent failure.
- [ ] CompanionLog.cs:137-143 — empty catches: once the log file fails (disk full/locked), logging dies silently forever; no once-flag, no notice.
- [ ] CompanionLog.cs:48 + CompanionRuntime.cs:110-116 — `LogLevel` "debug" maps to Info (no case); null LogLevel string NREs app at startup; log-level change does nothing until restart (no SettingsChangedEvent listener).
- [ ] TurnJournal.cs:269 — `.old` journal archives accumulate forever (rotation writes, nothing deletes).
- [ ] Audit JSONL (SandboxGuard.cs:288-305) — unbounded forever-append.
- [ ] MemoryGraph.Queries.cs:480-484 — corrupt timestamps parse as "right now" (inflates IsRecent, erases corruption evidence).
- [ ] MemoryGraph.cs:145-149 — daily backup copies main+WAL without a checkpoint (torn backup after power cut, silently trusted).
- [ ] SettingsStore — `SchemaVersion` bump path + migrations unversioned (see P1).
- [ ] ShellModel.cs:410-419 — `IsTurnInProgress = false` mutated on worker thread (ConfigureAwait resumed thread), racing UI-thread writes.
- [ ] SystemWatch.cs:94-98 — UI-thread exceptions marked Handled, only file-logged: half-updated view state + no toast.
- [ ] MainWindow.Dock.cs:140-143 — `Rebind(model)` ignores its argument (API trap: stuck "Loading player…").
- [ ] MainWindow.Layout.cs:239-254 — `QuitAsync` has no failure guard (unlike SleepAsync): teardown exception = app never quits, `_isShuttingDown` already true.
- [ ] CompanionReadiness.cs:66-69 — bare `catch { return false; }` on music-folder probe (bad env var == "folder deleted").
- [ ] AutoStart.cs — both registry ops bare `catch { return false; }`, silent.
- [ ] IrisEvent.cs:31-44 — `Configure` swaps writer without disposing old (single-call today; landmine).
- [ ] Shared settings mutation: modules/`OnSituationUpdated` mutate `_settings.Current` and save from arbitrary threads (SettingsStore has no lock).
- [ ] ModuleUi.cs:157-232 — task-card `_title`/`_detail` mutated cross-thread without sync (comment claims "Thread-safe.").
- [ ] BackgroundTaskManager.cs:265-286 — "give up after 5s" path leaks CTSs + `_slots`; tasks outlive shutdown.
- [ ] ModuleHost.cs:616-630 — module disposal nonexistent (`IAssistantModule`/`Dispose` never awaited; SemaphoreSlim/CTS leak at shutdown only).
- [ ] BuildReceiptAnswer fallback (AssistantBrain.Turn.cs:374) ends "Want me to try another way?" — a permission-ask in the OS-owned truth (inconsistent with act-first contract).
- [ ] FirstSentenceOf (AssistantBrain.Turn.cs:387) cuts at first '.' anywhere — tears URLs/abbrevs ("youtu.be…").
- [ ] MainWindow.Layout.cs:47-57 — `SyncFromModel` rebuilds labels + brush 12.5×/sec even when nothing changed.
- [ ] SystemWatch.cs:379 — "ts" without timezone/offset, mixed with offset-stamped IrisEvent lines in the same logs dir.

## P3 — LOW (note & sweep when in the file, never block release)

- [ ] Contents under-cap/edge-cases: `TextVectorizer` thresholds tuning (title-substring +0.45 bonus, forward direction lacks min-length guard — MemoryGraph.cs:504-507).
- [ ] Tests file (2248 lines) predates several behavior changes listed above; expect spec updates as we fix.
- [ ] Style: `_ = Task.Run` fire-and-forgets in brain callbacks — each without exception observer (SpeakReflexInBackgroundAsync covers its own; BeginVoiceAttention timer path OK; others unobserved).

---

## Do-NOT-touch (actually fine; fixes must not regress these)

- **ToolSchemaValidator** — best file in the codebase (fail-closed, caps, honest per-path errors).
- **MusicLibraryScanner mechanics** — junction refusal, per-entry cancellation checkpoints, honest partial results WITH limit reason. (Only its callers' 4s budget is the bug.)
- **ModuleSecretStore** — DPAPI hygiene done right.
- **PluginManifest validation** + McpConnection plumbing — thorough contract checks, id-matched calls, real redaction.
- **`ToolResult.Uncertain` + brain's no-repeat-of-uncertain concept** — good pattern; the NAME-based dedupe (P1) is the part to reshape, not the concept.
- **BackgroundTaskManager bounds & honesty; receipt-check retry concept; TurnJournal append design; RollingJsonLinesWriter retention; EventBus per-handler isolation; settings crash-safety (temp+replace+bak); SQLite WAL config; shell `Post` to UI thread discipline.**

---

## Work log

- 2026-10-27: audit created. Core lane self-read (brain turn loop, ModelClient, ModuleHost router+execute, CoreToolsModule, CompanionRuntime, LlamaServerHost). Sweep A (tool machinery + modules + plugins) merged. Sweep B (memory/infra/app shell) merged. All CRITICAL claims spot-verified against source (SandboxGuard 15s deny, volume-per-reader, 4s scan budget, phantom-node order, consolidation resurrection, settings single-enum wipe, seed-filter order).
- 2026-10-27 (later): Sweep C (voice/perception senses) landed and merged into the appendix above. Its two headline criticals (unbounded open speech segment VoicePipeline.cs:1361-1399/_speechSegment.AddRange 1365/1375 with no max duration anywhere; self-triggering barge-in at 2×30ms frames 1323/1330) and the 140-char hard floor (SpeechPlanner.cs:29/43, verified) spot-checked true.
- ALL THREE SWEEPS COMPLETE. Fix-all pass next, P0 first, Boss re-ranks before any edit.
- 2026-10-27 (fix round): Batch 1 (agentic core: router manifest, rounds, length-continue, receipts-in-history, recall content, token stocks), Batch 2 (sandbox truth: AuthorizationOutcome + PermissionStop, timeout hygiene with logged late faults, typed media result, plugin stdout evidence, mode rejection), Batch 3 (memory durability: persist-before-cache, LinkAsync under one lock + zero-row rewrite, consolidation live re-fetch, decay in one transaction, seed filter-then-take, forget evidence candidates). Each batch committed with the suite green (125/125).
- 2026-10-27 (fix round, cont.): Batch 4 (music: scan budget becomes a setting, track start validates before destroying the playing player, remembered volume, playback fault skips with a notice, queue advance under the player lock, honest corrupt-playlist errors + write-then-move saves, folder-aware matching, list_music limit). Batch 5 (voice/senses: 20 s open-segment ceiling, barge-in = 8 frames + self-echo floor, barge-in cancels the whole turn, 2-worker/2-deep segment backlog instead of drop, 8 s stream-stall watchdog, envelope decode outside the playback lock, nvidia-smi deadlock fix + watchdog kill, Whisper hallucination set, every TTS/recognizer settings source made live).
- 2026-10-27 (judgment ruling on appendix C line 135): the boss-approved 140-character spoken economy STAYS — it is a TTS-latency decision, and speech remains priced to be heard. The SpeechBudgetCharacters=240 setting was REMOVED rather than honored: the planner clamped every budget to 140 anyway, so the setting only ever lied about allowing more. The constant is now SpeechPlanner.SpeechEconomyCharacters. The silent dropping of list-shaped detail answers was NOT extended — pacing guidance, not a permission to monologue, remains the trade the boss chose.
- 2026-10-27 (fix round, deliberately NOT fixed): ParakeetRecognizer decode lock (C-140 — native ABI restructure), MicrophoneCapture 90 ms ring (C-148 — capture-thread redesign), stage-specific failure receipts for the catch-all segment catch (C-144 — needs teardown of one error path into per-stage notices), plus the listed C-MEDIUMs. Each is larger than the risk window between commits; they stay explicitly open rather than half-fixed.

## Appendix C — Sweep C findings (voice + perception senses)

### C-CRITICAL
- [x] **VoicePipeline.cs:1361-1399 — an open speech segment grows forever (no max utterance duration).** VAD only closes on 650 ms silence; music/applause/her own TTS leaking in holds the threshold forever → `_speechSegment` ≈ 64 KB/s unbounded → one giant transcription shipped at the brain. (Verified: AddRange at 1365/1375, no cap in the file.)
- [x] **SpeechPlanner.cs:29/43 — spoken voice hard-floored at 140 chars, list sentences dropped entirely;** `SpeechBudgetCharacters=240` in settings can never take effect. NOTE: the ≤140/one-verdict economy was a boss-approved decision for TTS latency — but the setting lying and the silent dropping of detail answers need a boss re-ruling (pacing hint vs hard wall).
- [x] **VoicePipeline.cs:1318-1343 — barge-in fires on her own voice** (2×30 ms loud frames, no suppression of self-echo): speakers-near-mic → she interrupts herself; same energy opens VAD segments of her own words and re-submits them as user requests. (Verified thresholds.)
- [x] **AssistantBrain.Turn.cs:406-410 + VoicePipeline arbiter — barge-in stops the current clip but not the turn.** Mid-stream queued sentences chain on the turn token and swap to the new arbiter epoch → she keeps talking right after being silenced. A barge-in answered by more speech is worse than none.

### C-HIGH
- [ ] ParakeetRecognizer.cs:160-204 — native decode inside `lock (_sessionLock)`; SuspendListening/Dispose (gaming/sleep transition, UI thread) blocks until an in-flight decode finishes (seconds of UI freeze).
- [x] AudioPlayback.cs:199-211/276 + PocketTts.cs:289 — streamed TTS body reads have no deadline beyond response-headers: one wedged voice-worker socket stalls the entire speech queue (until Escape).
- [x] VoicePipeline.cs:72-77 + SettingsStore — TTS/recognizer hosts freeze a settings snapshot at construction; `SettingsStore.Update` replaces `Current` wholesale → endpoint/thread changes silently do nothing; live probe checks the NEW endpoint while the worker binds the OLD one.
- [x] VoicePipeline.cs:1388-1397 — third finished utterance in flight is thrown away permanently (`segment_dropped_backlog`, Trace-only) — words reaching the mic during her generation are lost, not queued.
- [ ] VoicePipeline.cs:1567-1577 — one catch-all collapses wake-check+transcribe+addressing failures into a warning + silent drop: mic works, transcript dies invisibly.
- [x] GpuMemoryProbe.cs:81-97 — nvidia-smi launched with stderr captured but never drained → pipe-fill deadlock holding `_probeLock` forever; no kill after `WaitForExit(1500)` fails (zombie child per telemetry tick).
- [x] SpeechRecognition.cs:138-147 — Whisper hallucination filter is ONE string (`[BLANK_AUDIO]`). Music/noise → "Thank you for watching" becomes a fake Recognized turn into the brain (phantom requests).
- [x] AudioPlayback.cs:59-93/297-307 — full-clip envelope decode runs inside `_playbackLock` → barge-in Stop() waits behind a whole-clip decode: interrupt latency scales with sentence length.
- [ ] MicrophoneCapture.cs:89-95 — 90 ms WinMM ring (3×30ms) fed from a callback thread that synchronously publishes events (AudioLevelEvent at 20 Hz, UserInterrupted taking the playback lock): slow subscriber/GC → ring overflow → Windows silently drops audio ("she ignored her name").

### C-MEDIUM
- [ ] VoicePipeline.cs:1432 vs VoiceAddressingPolicy.cs:11 — `conversationWasOpen` sampled at processing time, not capture time: ambient speech recorded before her reply becomes a "follow-up" inside the open window.
- [ ] VoicePipeline.cs:1495-1510 — an armed wake-training sample hijacks any transcript, including the user's real question in an open conversation; no notice.
- [ ] PerceptionEngine.cs:216-217 — ambient interval read once at Start; changing `StatsIntervalMilliseconds` silently does nothing until restart.
- [ ] VoicePipeline.cs:1378-1399 — 650 ms pause hard-splits hesitant speech: "Iris… so I was wondering" fires a half-question turn, remainder arrives as a second queued request.
- [ ] VoicePipeline.cs:938-948 — streamed-synth open failure silently `continue`s. Hole in the spoken answer.
- [ ] ReflexBank.cs:29/53 — per-signature reflex cache folders never evicted (every voice/rate change accumulates WAVs forever).
- [ ] PerceptionEngine.cs:244-249 — LookAsync captions screenshots with a possibly ~9 s stale `ActiveWindowTitle` (wrong app on the evidence); primary screen only.
- [ ] PerceptionEngine.cs:204/294-298 — `ForegroundGame` written by timer thread, read by turn threads unsynchronized (stale "a game is running" fact into the prompt).
- [ ] MediaSessionSensor.cs:46-55 — `_manager` connect is unsynchronized (two connectors, one orphaned); `ControlMediaAsync` bypasses the ownership gate.
- [ ] WakeGate.cs:114-129 vs VoicePipeline.cs:1764 — `PassThroughWakeGate` documented "used when RequireWakePhrase off" but never wired; the shape gate always runs and only works by an accidental trailing-silence invariant.
- [ ] SpeechOutputArbiter.cs:42-52/84-88 — Stop() disposes the epoch CTS that a linked token may reference microseconds later (ObjectDisposedException into a queued speech turn).
- [ ] PocketTts.cs:206-246 + VoicePipeline.cs:1669-1690 — brand-new HttpClient per 250 ms health poll (socket churn on the senses hot path).
- [ ] VoicePipeline.cs:464-486 — dead-capture restart uses Thread.Sleep(400) unsynchronized against SetListening/TestMicrophone transitions → clobbered session ids, false "lost microphone".
- [ ] VoicePipeline.cs:1228-1330 — DisposeAsync disposes recognizer mid-decode and `_recognitionGate.Dispose()` can fault pending waiters (shutdown ODE into unobserved tasks).
- [ ] WakeHotKey.cs:78-81 — `IsRegistered` blocks up to 2 s on the boot path (properties that wait).
- [ ] AudioPlayback.cs:343-355 vs 434-478 — `WaitForPlaybackAsync` reads position outside the lock while Stop() disposes under it (ODE from the frame-pump, mislabeled as playback failure).

### Sweep C "actually fine" (protect during fixes)
- Culture discipline clean repo-wide in scope; stale-fact governance (staleness annotations, clock stamps) largely done right; segment accounting leak-free; playback generation checks correct; mic-loss watchdog honest; media ownership no-phantom-resume; ASR thread caps protect games; both TTS sidecars are sibling-owned (no collateral kills); queued second request is FIFO-brain, not lost (bounded by 16-message history).
