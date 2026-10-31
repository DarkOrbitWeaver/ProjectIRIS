P  R  O  J  E  C  T     I  R  I  S 

# **CompanionOS** 

Master Architecture Blueprint for a Local AI Companion 

Version 1.0 - Locked Specification Target: .NET 9 / Avalonia UI / RTX 3050 8 GB October 2026 

CompanionOS - Project Iris - October 2026 

## **Table of Contents** 

|**1. Executive Summary...........................................................................................................**|
|---|
|**1. Executive Summary.............................................................................................................1**|
|**2. Vision and Product Principles.............................................................................................2**|
|2.1 What Iris Is.....................................................................................................................2|
|2.2 The Three Laws of the Interface....................................................................................2|
|2.3 The Body-Part Mental Model.........................................................................................3|
|2.4 Explicitly Out of Scope..................................................................................................3|
|**3. System Architecture - The Two-Speed Mind.....................................................................3**|
|3.1 Cortex and Brainstem.....................................................................................................3|
|3.2 The Two-Speed Turn......................................................................................................4|
|3.3 The EventBus Nervous System......................................................................................4|
|3.4 Crash Immunity and Recovery.......................................................................................5|
|**4. Technology Stack - Verified Selections..............................................................................5**|
|4.1 Why Not a Single Native Omni Model..........................................................................5|
|4.2 Model Profiles................................................................................................................6|
|**5. Hardware Resource Budgets...............................................................................................6**|
|5.1 The Three Operating Modes...........................................................................................6|
|5.2 System RAM Budget......................................................................................................7|
|5.3 Context Window Strategy..............................................................................................7|
|**6. The Voice System - Consistent Identity..............................................................................8**|
|6.1 The VoiceProfile Contract..............................................................................................8|
|6.2 The Reflex Phrase Bank.................................................................................................9|
|6.3 Emotional Awareness and Expression...........................................................................9|
|6.4 Voice Etiquette Rules.....................................................................................................9|
|**7. Gaming Mode - Full Specification......................................................................................9**|
|7.1 Purpose and Promise....................................................................................................10|
|7.2 Detection......................................................................................................................10|



I 

|7.3 Engagement Sequence..................................................................................................10|
|---|
|7.4 What Survives During Gaming....................................................................................10|
|7.5 Resume, Edge Cases, and Controls..............................................................................11|
|**8. Perception and Senses........................................................................................................11**|
|8.1 Ambient Awareness at Zero Cost.................................................................................11|
|8.2 Screen Vision: Three Modes........................................................................................12|
|8.3 The Situational Snapshot..............................................................................................12|
|**9. Synaptic Memory Architecture.........................................................................................12**|
|9.1 Design Intent................................................................................................................12|
|9.2 Memory Layers............................................................................................................13|
|9.3 Typed Synapses with Polarity......................................................................................13|
|9.4 Spreading Activation and Intuition Injection...............................................................14|
|9.5 Consolidation, Sleep, and Crash Proofing....................................................................14|
|**10. Module and Plugin System..............................................................................................15**|
|10.1 The Contract...............................................................................................................15|
|10.2 Dynamic Skill Routing...............................................................................................17|
|10.3 The Three-Tier Sandbox............................................................................................17|
|10.4 The MCP Bridge........................................................................................................17|
|10.5 Install Flow and Manifest...........................................................................................18|
|**11. User Interface Specification............................................................................................18**|
|11.1 Design Language: Calm Instrument...........................................................................18|
|11.2 Window Architecture.................................................................................................18|
|11.3 The StatusOrb.............................................................................................................19|
|11.4 Screens........................................................................................................................19|
|Onboarding...................................................................................................................19|
|Chat..............................................................................................................................20|
|Memory........................................................................................................................ 20|
|Modules, Settings, and System.....................................................................................20|
|Overlay, Permission, and Command Palette................................................................20|



II 

|11.5 Micro-Interactions and Sound....................................................................................21|
|---|
|11.6 Input Map and State Machine....................................................................................21|
|**12. Codebase Structure and Engineering Standards..........................................................22**|
|12.1 Repository Layout......................................................................................................22|
|12.2 Coding Standards, Locked.........................................................................................25|
|12.3 Why This Is AI-Maintainable.....................................................................................26|
|**13. Persona - Iris.....................................................................................................................26**|
|**14. Development Roadmap....................................................................................................27**|
|**15. Risk Register and Mitigations.........................................................................................27**|
|**16. Appendix - Research Decision Log.................................................................................28**|



_Note: This Table of Contents is generated via field codes. To ensure page number accuracy after editing, please rightclick the TOC and select "Update Field."_ 

III 

## **1. Executive Summary** 

CompanionOS is a local-first AI companion framework for Windows, designed to run a persistent, voice-capable, screen-aware assistant named Iris on a single consumer-grade machine: an NVIDIA RTX 3050 with 8 GB of VRAM, 16 GB of system RAM, and a midrange CPU. The system is not a chatbot with a skin. It is a framework whose interface acts as the visible surface of a complete, always-on nervous system: a cortical language model that reasons, sees, and speaks, paired with a brainstem of lightweight CPU services that keep her senses alive even while the model is unloaded from the GPU. 

The architecture solves the central conflict of small-hardware AI: a 4-billion-parameter model cannot natively provide realtime vision, hearing, speech, memory, and tool use at once without exhausting VRAM or adding seconds of latency. CompanionOS resolves this by splitting the system into two speeds. Fast reflexes (wake word detection, speech recognition, voice playback, pre-cached spoken phrases, ambient PC awareness) run entirely on the CPU at negligible cost. Deliberate thought (reasoning, screen understanding, tool orchestration, roleplay) runs on the GPU through an OpenAI-compatible local server. The user experiences a single continuous personality with effectively zero perceived latency, because the reflex layer speaks while the thinking layer works.

Three properties define the project's non-negotiables. First, consistency: Iris has one persistent voice identity, one persona, and one memory, and none of them drift between sessions, model swaps, or engine upgrades. Second, immortality: all state lives in a writeahead-logged SQLite database and an append-only journal, so crashes, restarts, and power loss never corrupt or lose her memory. Third, infinite growth: a slot-based module system with progressive tool discovery allows hundreds of plugins (music, browser control, PC control, custom players, MCP servers) without attaching every JSON schema to every model request.

This document is the locked specification. It records every verified technology decision, every resource budget, every screen, every state, and every interaction so that future development, whether by humans or AI coding assistants, cannot deviate from the agreed design. 

**Table 1 - Project fact sheet** 

|**Aspect**|**Locked decision**|
|---|---|
|**Codename**|Project Iris|
|**Framework name**|CompanionOS|
|**Platform**|Windows 10/11, .NET 9, C#|
|**UI framework**|Avalonia UI with SkiaSharp custom controls|



1 

|**Aspect**|**Locked decision**|
|---|---|
|**Language brain**|Qwen3-VL-4B-Instruct, Q4_K_M GGUF, via llama.cpp llama-server|
|**Voice identity**|Persistent VoiceProfile; Kokoro af_heart at launch, Qwen3-TTS 0.6B upgrade<br>path|
|**Ears**|SenseVoice-small via sherpa-onnx C# API, CPU only|
|**Memory**|SQLite WAL + sqlite-vec typed synaptic graph|
|**Plugin protocol**|IAssistantModule native contract + official MCP C# SDK bridge|
|**Target hardware**|RTX 3050 8 GB VRAM, 16 GB RAM|
|**Awake VRAM**<br>**budget**|4.4 GB of 6.5 GB usable, 2 GB headroom|
|**Sleep / Gaming**<br>**VRAM**|0 GB (brainstem runs on CPU)|



## **2. Vision and Product Principles** 

### **2.1 What Iris Is** 

Iris is a permanent resident of the user's PC, not a website with a text box. The product goal is a companion that feels like a body part: always present, aware of the machine's state, reachable by voice at any moment, and able to act through an ever-growing set of capabilities. The user should be able to press one toggle to open her microphone, say her name, and ask for anything from a YouTube recommendation to a music playlist to an explanation of an on-screen error, and receive a spoken, useful answer that begins immediately. 

The reference experiences are the best commercial AI voice modes and screen-sharing assistants, but the entire stack runs locally. Nothing the user says, shows, or types ever leaves the machine, and the system must state this plainly in its settings interface rather than burying it in a policy document. 

### **2.2 The Three Laws of the Interface** 

Because the product must feel like a premium instrument rather than a developer tool, three laws govern every interface decision. Law one: one glance equals full truth. Her state and system health are always visible through the StatusOrb and the status strip, never hidden behind menus or transient spinners. Law two: the 100-millisecond rule. Every input, whether a click, a keystroke, or a spoken word, must receive visible acknowledgment within 100 milliseconds; there are no dead interactions anywhere in the product. Law three: calm over loud. The visual language is inspired by high-end audio equipment and restrained sciencefiction terminals; motion exists only to communicate state changes, never as decoration. 

2 

### **2.3 The Body-Part Mental Model** 

Every capability of the system is conceived as a body part that plugs into one shared body. Modules are hands (they act), the memory graph is her long-term mind, the perception engine is her senses, and the voice pipeline is her mouth and ears. The shell never knows about specific plugins; it only exposes slots where module panels may appear. This is the mechanism that makes infinite growth safe: new modules extend the body without surgery on the core, and the brain never has to hold more than a handful of tools in view at once, because a two-millisecond routing step selects the relevant hands for each turn. 

### **2.4 Explicitly Out of Scope** 

The project does not train or fine-tune language models from scratch, and it does not implement reinforcement learning. Model behavior is shaped exclusively through prompting, memory injection, and tool design. This is a deliberate constraint: the value of CompanionOS is the surrounding intelligence architecture, which remains valid no matter which model, local or remote, small or large, powers the cortex in any given month. The ModelClient speaks the OpenAI-compatible API, so swapping between a local 4B model, a future sub-8GB omni model, or a hosted frontier model is a configuration change measured in seconds, not a rewrite. 

## **3. System Architecture - The Two-Speed Mind** 

### **3.1 Cortex and Brainstem** 

The system is organized around a biological insight: humans do not keep hearing in the cortex. The brainstem keeps sensory reflexes alive even while the cortex rests, which is why a spoken name wakes a sleeping person. CompanionOS copies this structure directly. The cortex is the GPU-resident language model that performs reasoning, vision understanding, roleplay, and tool orchestration. The brainstem is a set of CPU-only services that never unload: wake word detection, speech recognition, speech synthesis, the reflex phrase bank, ambient PC awareness, and the memory graph store. 

**Table 2 - The two layers of the system** 

|**Property**|**Cortex (thinking)**|**Brainstem (reflexes)**|
|---|---|---|
|**Runs on**|GPU (RTX 3050)|CPU only|
|**Components**|llama-server hosting Qwen3-VL-4B|Wake word, SenseVoice STT, Kokoro<br>TTS, Silero VAD, reflex bank, context<br>stream, memory store|
|**Latency class**|300 ms to 3 s per thought|2 to 300 ms per reflex|
|**Sleeps?**|Yes, on command, on idle, or in<br>Gaming Mode|Never, except that a full Sleep<br>switches the whole process off (§28)|



3 

|**Property**|**Cortex (thinking)**|**Brainstem (reflexes)**|
|---|---|---|
|**VRAM when active**|About 4.4 GB|0 GB|
|**Survives crashes**|State journaled before each turn|State persisted with SQLite WAL|



The split is what makes the sleep and gaming features honest. When the model unloads, the user does not lose the assistant; they lose only her slow thinking. She can still hear her name, answer with pre-cached reflex phrases, control music playback, and take notes, all on the CPU. Waking her reloads the model file from the NVMe SSD in roughly 1.5 to 2 seconds. 

### **3.2 The Two-Speed Turn** 

Every user turn is executed at two speeds simultaneously. The reflex layer reacts within 150 milliseconds: Silero VAD detects that the user has finished speaking, the skill router attaches the relevant module tools, and the voice pipeline plays a pre-cached spoken acknowledgment such as a brief confirmation that she is on the task. In parallel, the cortex performs the actual reasoning: memory recall, tool invocation, and response generation. The voice engine streams sentence by sentence, so she begins speaking the first sentence while later sentences are still being generated. The user therefore never faces silence during processing, which is the single most important factor in perceived intelligence. 

### **3.3 The EventBus Nervous System** 

Every subsystem communicates through one in-process publish-subscribe event bus, and every user interface element subscribes to it. This is the mechanism that makes the interface the visible brain: the memory graph view pulses when nodes are recalled, the status strip's VRAM meter moves when the model loads or unloads, and the orb breathes in sync with her audio output. No pixel on screen is decorative; each reflects a real event emitted by a real subsystem. The catalog of core events is fixed and versioned so modules can depend on it. 

**Table 3 - Core EventBus event catalog** 

|**Event**|**Emitted when**|**Primary consumers**|
|---|---|---|
|**StateChanged**|Mind state transitions (idle, listening,<br>thinking, acting, speaking)|StatusOrb, titlebar, overlay|
|**MemoryWritten**|A node or synapse is created or<br>strengthened|Memory graph view,<br>consolidation worker|
|**MemoryRecalled**|Spreading activation returns active<br>nodes|Chat memory chips, graph<br>replay|
|**ToolInvoked**|A module tool begins or completes|Tool capsule cards, audit log,<br>orb acting state|
|**PermissionRequested**|A tier-3 action requires approval|Permission prompt, orb<br>amber state|



4 

|**Event**|**Emitted when**|**Primary consumers**|
|---|---|---|
|**VisionFrameProcessed**|A screen frame was analyzed or skipped<br>by dHash|Vision status chip, overlay<br>watch mode|
|**SpeechStarted /**<br>**SpeechEnded**|TTS output begins or ends per utterance|Orb speaking state, avatar lip<br>sync|
|**SleepEntered / SleepExited**|Model unload or reload completes|Status strip, orb ember state|
|**GamingModeEntered /**<br>**Exited**|Game detection state changes|Status strip, overlay, module<br>panels|
|**ModelSwapped**|A different brain profile becomes active|Settings, status strip,<br>capability map|
|**DeviceChanged**|Audio input or output device changes|Voice pipeline, settings<br>meters|
|**ModuleInstalled**|A module registers after manifest<br>verification|Module grid, rail, permission<br>center|



### **3.4 Crash Immunity and Recovery** 

The persistence design assumes crashes will happen. All memory and configuration state lives in a single SQLite database in WAL mode, which is safe against power loss mid-write because the write-ahead log replays committed transactions on the next open. On top of the database, an append-only journal records every turn boundary: the user's input, the active mind state, the in-flight tool calls, and the partial response at the moment of any interruption. On restart, the recovery guard replays the journal, restores the exact mind state, and resumes or cleanly rolls back the interrupted turn. The user's experience is that Iris restarts exactly where she was, and the design goal is zero lost memories and zero half-finished visible actions. The avatar process, being a separate operating-system process, can crash without affecting the mind; the mind can crash without affecting the always-on ears. 

## **4. Technology Stack - Verified Selections** 

Every selection below was verified against current releases as of October 2026. The guiding criteria were: fits the 8 GB VRAM budget with headroom, exposes a C# or HTTP integration path suitable for a clean Avalonia application, has a permissive license, and can be replaced without touching the core if a better option appears next year. 

5 

**Table 4 - The locked component stack** 

|**Layer**<br>**Language**<br>**brain**|**Selection**<br>Qwen3-VL-4B-Instruct,<br>Q4_K_M GGUF with<br>mmproj vision projector|**Runs on**<br>GPU|**Why it won**<br>Trained as a visual GUI agent (top<br>OSWorld-class results for its size), 256K<br>native context, native function calling, 32-<br>language OCR, Apache 2.0; strongest small<br>model for reading screens and calling tools|
|---|---|---|---|
|**Model**<br>**runtime**|llama.cpp llama-server as a<br>managed child process|GPU|Lowest overhead, OpenAI-compatible<br>HTTP API, full lifecycle control for sleep,<br>wake, and gaming unload|
|**Ears (STT)**|SenseVoice-small via<br>sherpa-onnx C# API|CPU|Realtime multilingual recognition with<br>speech emotion recognition and audio event<br>detection; roughly 230 MB RAM and zero<br>VRAM|
|**Wake word**|sherpa-onnx keyword<br>spotting (openWakeWord<br>ONNX alternative)|CPU|Always-on, custom phrase, no GPU cost|
|**Mouth (TTS)**|Kokoro-82M via<br>KokoroSharp.CPU NuGet|CPU|Pure C# NuGet, plug and play, streaming<br>output, 54 bundled voices, near-commercial<br>naturalness at 82M parameters|
|**Voice upgrade**|Qwen3-TTS 12Hz 0.6B<br>CustomVoice sidecar<br>(phase 2)|CPU or<br>GPU|97 ms streaming first-packet latency,<br>natural-language emotion control, ten<br>languages, warm female timbres|
|**Voice activity**|Silero VAD v5 ONNX|CPU|Sub-2 ms detection, drives barge-in<br>interruption|
|**Embeddings**|all-MiniLM-L6-v2 ONNX|CPU|384-dimension vectors in about 5 ms for<br>skill routing and memory seeds|
|**Memory store**|SQLite in WAL mode with<br>sqlite-vec extension|CPU|One file, power-loss safe, vector search and<br>relational graph in the same database|
|**Plugin**<br>**protocol**|IAssistantModule native<br>contract,<br>ModelContextProtocol C#<br>SDK 2.3 bridge|CPU|Native modules stay first-class; external<br>MCP servers become indistinguishable<br>hands|
|**Web media**|yt-dlp invoked with<br>ytsearch for metadata|CPU|No API keys, no browser dependency,<br>returns titles, durations, and thumbnails in<br>about 2 s|
|**UI framework**|Avalonia UI 11 on .NET 9<br>with SkiaSharp|GPU<br>(tiny) or<br>CPU|MIT licensed, cross-platform, lookless<br>controls for the custom design system,<br>software-rendering fallback frees VRAM if<br>ever needed|



6 

### **4.1 Why Not a Single Native Omni Model** 

A single 4B model with built-in speech in and speech out would simplify the diagram, and the research effort examined every shipping candidate. The conclusion, documented fully in the appendix decision log, is that no current omni model under 8 GB delivers the combination the product requires. The Qwen3-Omni and Nemotron Omni families start at 30B parameters and roughly 17 GB; small full-duplex models trade voice quality for footprint; audio-capable 4B models such as Gemma 4 E4B have incomplete llama.cpp support and failed community tone and audio understanding tests. Additionally, abliterated (uncensored) builds of omni models frequently damage the audio weights, and baking the ears into the GPU model means unloading the model makes her deaf, which contradicts the sleep and gaming requirements. 

The brainstem architecture delivers the omni experience the user actually wants, one seamless personality with realtime voice, without any of those compromises. The ModelClient abstraction guarantees that when a suitable sub-8GB omni model matures, adopting it is a configuration change: point the profile at a new model file and the ears and mouth move into the cortex. Nothing else in the framework changes. 

### **4.2 Model Profiles** 

The system ships with three swap-ready brain profiles and a custom slot. Vision Agent is the default Qwen3-VL-4B profile for everyday assistant use. Roleplay Companion is an abliterated Gemma-3-4B or equivalent community build for uncensored roleplay sessions, where vision is unnecessary and persona consistency matters most. Speed Runner is NVIDIA Nemotron-3-Nano-4B, the fastest tested small model at roughly 80 tokens per second on an 8 GB card, for long text-heavy work sessions. A profile switch is a voice command away, and the persona, memory, and voice identity are profile-independent, which is what keeps her the same Iris regardless of which brain she is wearing. 

## **5. Hardware Resource Budgets** 

### **5.1 The Three Operating Modes** 

The RTX 3050 provides 8 GB of VRAM, of which Windows, the desktop compositor, and the Avalonia UI reserve roughly 1.3 GB, leaving about 6.5 GB usable. The system defines three operating modes. Awake is full capability with the model loaded. Sleep unloads the brain for idle periods while keeping the senses alive. Gaming Mode unloads everything GPU-resident so the user's games receive the full card. The budgets below are enforced with hard limits; if a configuration would exceed the awake budget, the settings interface refuses it with a live calculation. 

7 

**Table 5 - VRAM budget, Awake mode** 

|**Item**|**Cost**|
|---|---|
|**Qwen3-VL-4B weights, Q4_K_M**|2.5 GB|
|**Vision projector (mmproj, active only when she looks)**|0.84 GB|
|**KV cache, 16K context at q8_0 quantization**|0.7 GB|
|**Compute and scratch buffers**|0.3 GB|
|**Total with about 2 GB of headroom for spikes and game-adjacent**<br>**apps**|4.4 GB|



**Table 6 - VRAM budget, Sleep and Gaming modes** 

|**Item**|**Sleep**|**Gaming Mode**|
|---|---|---|
|**Language model**|0 GB|0 GB|
|**Vision pipeline**|Off|Off|
|**Ears, wake word, voice, memory,**<br>**context stream**|0 GB (CPU)|0 GB (CPU)|
|**Overlay window**|0 GB (CPU-rendered)|0 GB (CPU-rendered)|
|**Wake trigger**|Tray click or relaunch, in 1.5<br>to 2 s (see §11 amendment)|Tier-1 reflexes only; brain wake<br>requires explicit ask|



Text generation in Awake mode streams at roughly 50 to 65 tokens per second on this hardware, which reads as instant for spoken-length sentences. A vision query adds roughly 0.5 to 1 second of projector and prompt processing. Context can be raised to 32K for approximately 0.7 GB of additional KV cache while remaining inside the budget. 

### **5.2 System RAM Budget** 

With 16 GB of system RAM, the steady-state allocation is approximately 4.5 GB for Windows, 0.5 GB for the Avalonia application and overlay, 0.8 GB for llama-server host memory, and 0.6 GB combined for the STT, TTS, VAD, and embedding models. That leaves roughly 9.5 GB free for the user's browser, games, and other applications. The CPU reflex models together hold under 1 GB of RAM, which is the reason the brainstem can stay alive in every mode without imposing on the user's other work. 

### **5.3 Context Window Strategy** 

A 4B model cannot brute-force its way through a long history, so the framework never carries raw transcript. The context window is treated as a scarce stage, assembled fresh each turn, while the durable knowledge lives in the memory graph. Old conversations are compressed into graph nodes; only the active intuition slice and a sliding recent window enter the prompt. 

8 

**Table 7 - Per-turn context budget, 16K window** 

|**Block**|**Typical tokens**|
|---|---|
|**Persona and core system prompt**|500|
|**Active intuition from memory graph**|150 to 300|
|**Recent conversation, sliding window**|1,500 to 2,500|
|**Compressed episodic summaries of older turns**|300 to 500|
|**Tool definitions for the routed module only**|300 to 500|
|**Screen frame, only when she looks**|800 to 1,300|
|**Total used, leaving ample generation room**|3,500 to 5,500|



This design is what allows a small model to behave like a much larger one: her effective memory is not the context window, it is the graph, and the graph is unbounded. The same assembly strategy means a future hosted model with a million-token window changes nothing about the memory architecture; the intuition slice simply grows if the user opts in. 

## **6. The Voice System - Consistent Identity** 

### **6.1 The VoiceProfile Contract** 

The user requirement is explicit: a single, consistent voice, comparable to a high-quality commercial AI voice call, never a different voice between sessions or components. The framework therefore treats voice identity as a first-class persistent object. A VoiceProfile record stores the engine identifier, the fixed speaker or timbre selection, and all locked synthesis parameters such as speed and pitch offsets. It is persisted in Layer 0 of the memory graph, alongside her persona, so it survives restarts by construction and is versioned like any other core memory. 

At launch the VoiceProfile pins Kokoro's af_heart voice, a warm natural female timbre, with synthesis running on the CPU through KokoroSharp. Because Kokoro uses a fixed speaker embedding, the output is deterministic: the same voice on every call, on every day, in every feature, including the interface's own chimes, which are rendered through the same profile so that even the UI sounds like her. When the Qwen3-TTS sidecar is adopted in phase two, the migration procedure is an audition step that maps the existing profile to the closest matching warm female timbre, locks it, and re-registers the profile; the user hears the same character with improved expressiveness, never a stranger. 

**Table 8 - Voice pipeline stages and latency** 

|**Stage**|**Component**|**Budget**|
|---|---|---|
|**Microphone gate**|Push-to-talk, toggle, or wake word|0 ms once triggered|



9 

|**Stage**|**Component**|**Budget**|
|---|---|---|
|**Voice activity detection**|Silero VAD, 30 ms frames|Under 2 ms per frame|
|**Transcription**|SenseVoice-small streaming|150 to 300 ms after speech ends|
|**Reflex reply**|Pre-cached phrase bank playback|0 ms (instant, while thinking)|
|**Brain first token**|Qwen3-VL-4B via llama-server|300 to 500 ms|
|**Speech synthesis**|Kokoro sentence-chunk streaming|First audio under 200 ms per<br>sentence|
|**Barge-in cancel**|VAD to TTS stop|Under 80 ms|
|**Emotion awareness**|SenseVoice emotion and event detection|Parallel, no added latency|



### **6.2 The Reflex Phrase Bank** 

The reflex bank is a small set of pre-rendered spoken phrases in her locked voice, generated once at voice profile creation and cached on disk. Categories include acknowledgments such as a brief on-it, thinking fillers such as a short checking-now, confirmations such as done, and rest announcements for entering sleep and gaming modes. When the orchestrator detects that a turn will involve tool calls or a vision pass, it fires the appropriate cached phrase immediately, before the model has produced its first token. This is the mechanism that converts a 1 to 3 second thinking gap into a perceived instant response, and it is the same technique that makes commercial voice assistants feel alive. 

### **6.3 Emotional Awareness and Expression** 

Her hearing includes two channels that most local assistants lack. SenseVoice reports the emotional tone of the user's speech and detects audio events such as laughter, sighs, and applause. These signals are injected into the turn context, so Iris can notice frustration in the user's voice before any words are parsed and adjust her tone accordingly. On the output side, the phase-two Qwen3-TTS engine accepts natural-language style instructions, so her own emotion tags can drive genuinely softer or more amused delivery rather than a single flat read. The intended aesthetic is a light, consistent robotic character that is owned rather than fought: she sounds like a designed synthetic person, never like a stock TTS engine. 

### **6.4 Voice Etiquette Rules** 

**Table 9 - Situational voice behavior** 

|**Situation**|**Her behavior**|
|---|---|
|**A tier-1 action completes**|Short spoken confirmation, for example: done, night drive playing|
|**Heavy thinking begins**|Reflex fires instantly from cache|
|**A preference is unknown**|Asks at most one clarifying question, then decides and remembers<br>the answer|



10 

|**Situation**|**Her behavior**|
|---|---|
|**A tier-3 action is requested**|Reads the permission request aloud and waits|
|**The user barges in mid-speech**|Stops within 80 ms, orb flashes, listens|
|**Entering sleep or gaming mode**|Announces it briefly, states that she can still be heard|
|**The user sounds frustrated**|Softens tone, acknowledges briefly before helping|



## **7. Gaming Mode - Full Specification** 

### **7.1 Purpose and Promise** 

Gaming Mode exists because the user games on the same 8 GB card that powers Iris. The promise is exact: when a game launches, the framework releases every byte of VRAM the assistant occupies, so the game runs as if she were not installed, and the user never needs to remember to do anything. Unless the user explicitly asks her to stay awake, or has manually pinned otherwise, Gaming Mode engages automatically. 

### **7.2 Detection** 

Detection runs in the perception engine at negligible cost and combines three signals. First, a curated process-name database of common game executables and launchers, updated with the module system. Second, a fullscreen exclusive or borderless-fullscreen heuristic based on foreground window state. Third, and most importantly, learned knowledge: the user can teach her with one sentence, telling her that a specific application is a game, and she writes the fact into the memory graph so the decision is permanent. False positives are corrected the same way, and every detection decision is shown in the status strip so the user can see why she stepped aside. 

### **7.3 Engagement Sequence** 

When a game is detected, the orchestrator runs a fixed sequence. She speaks a brief cached announcement, stating that gaming mode is on and that she can still be called. The model server is terminated, releasing all VRAM. The vision pipeline and ambient screen awareness are suspended. The overlay collapses to its minimal pill form and its rendering moves to CPU. Her reflex voice drops to a whisper policy by default, configurable in settings. The status strip shows the gaming state so trust is maintained through visibility. The entire sequence completes in under two seconds, comfortably before most game engines finish loading. 

11 

### **7.4 What Survives During Gaming** 

**Table 10 - Capabilities while a game is running** 

|**Capability**|**Available**|**How**|
|---|---|---|
|**Wake word and hearing**|Off by default|`System.GamingEarsEnabled` keeps the CPU<br>ear loaded; off frees the microphone and<br>the transcription threads|
|**Music player control**|Yes|Tier-1 module actions run on CPU; playback<br>continues|
|**Timers, quick notes, memory**<br>**writes**|Yes|Brainstem skills without model inference|
|**Spoken reflex replies**|Yes|Cached phrase bank, whisper policy by default|
|**General reasoning and**<br>**conversation**|On request|She asks whether to wake; a yes reloads the<br>model in about 2 s, and she returns to gaming<br>mode afterward|
|**Screen vision**|No|Explicitly suspended; asking for vision triggers<br>the wake prompt|
|**Ambient context stream (media,**<br>**window titles)**|Yes|Zero-cost OS hooks continue on CPU|



### **7.5 Resume, Edge Cases, and Controls** 

When the game process exits, or when the user manually ends gaming mode, the model reloads automatically and she confirms her return in one short line. Edge cases are handled explicitly. Multi-monitor setups with a game on one screen follow the same rule, because the trigger is process detection, not capture. Game launchers that are not themselves the game are handled by the learned knowledge base. If a game crashes, process exit fires the normal resume path. If the user asks for a brain-dependent feature mid-game, she offers the wake choice rather than silently failing, and if the user declines, she notes the request in memory so she can offer to handle it after the session. 

Manual controls exist in three places: a voice command, the status strip button, and the overlay right-click menu. A per-application exclusion list in settings prevents recurring false positives. Gaming Mode is also the template for a general resource policy: the same unload machinery serves scheduled sleeps, low-battery policies on laptops, and any future headroom need, which is why it is implemented in the orchestrator rather than as a plugin. 

## **8. Perception and Senses** 

### **8.1 Ambient Awareness at Zero Cost** 

Iris knows the situation of the machine at all times through an ambient context stream assembled from operating-system hooks that cost effectively nothing. This stream is what lets her pause music when the user joins a call, mention that a download finished, or greet 

12 

the user after they return from a game, without any model inference having been required to notice. 

**Table 11 - Ambient sense catalog** 

|**Sense**|**Source**|**Refresh**|
|---|---|---|
|**Active window and application**|Win32 foreground and UI Automation|On event|
|**Currently playing media, any app**|Windows System Media Transport<br>Controls|On event|
|**System stats (CPU, RAM, GPU,**<br>**VRAM, battery, network)**|Performance counters|1 Hz|
|**User presence and idle**|Input activity hooks|On event|
|**Clipboard**|Windows clipboard listener, gated by<br>permission tier|On change, if<br>allowed|
|**Time context**|Local clock and calendar|Every turn|



### **8.2 Screen Vision: Three Modes** 

Screen understanding is the most expensive sense, so it is gated by three explicit modes. In Off mode she never captures pixels and relies on ambient data alone. In On-demand mode, the default, she captures a frame only when the user asks or when a tool result would clearly benefit, such as an error dialog the user references. In Live Share mode, an Android-style shared-screen experience, a CPU perceptual hash (dHash) runs over captured frames and only frames whose hash differs meaningfully are resized to 896 by 896 JPEG and passed to the vision projector; unchanged frames cost nothing beyond the hash comparison, which is why live sharing stays viable on this hardware. 

Capture itself uses the Windows Desktop Duplication API, which returns a frame in under a millisecond. The resize target is chosen deliberately: it is large enough for the model's OCR to read interface text, while keeping vision token cost near the minimum. A vision session shows its state through the overlay's animated border and a status chip; hovering the chip reveals the last thing she saw, with a thumbnail and timestamp, so the user always knows when her eyes were open. 

### **8.3 The Situational Snapshot** 

Each turn's context assembly includes a compact situational snapshot distilled from the ambient stream: active application, media state, system load class, user presence, and time of day, expressed in a handful of plain sentences. This is how a 4B model gains situational awareness that feels human without burning context on raw telemetry. The snapshot is also the substrate for proactive behaviors, which are always opt-in: she may mention that it is 

13 

late, or that the same error dialog has appeared three times, only because the snapshot made those facts visible in the turn context. 

## **9. Synaptic Memory Architecture** 

### **9.1 Design Intent** 

The memory system is not a vector database with a graph skin. The requirement, stated in the user's own analogy, is the way a human knows a knife: what it is, what it is for, what it combines with, and what it must never be, all simultaneously. That requires typed relationships with polarity, not similarity scores alone. The store is therefore a typed synaptic graph in SQLite, augmented with vector search for seeding activation. 

### **9.2 Memory Layers** 

**Table 12 - The four memory layers** 

|**Layer**|**Contents**|**Examples**|
|---|---|---|
|**Layer 0 - Core Self**|Persona, voice profile, boundaries,<br>relationship with the user|Iris speaks warmly and briefly; wake<br>word phrase; her name|
|**Layer 1 - Episodic**|Events with time and place|We debugged the music player until 2<br>AM yesterday|
|**Layer 2 - Semantic**|World and user facts, concepts as<br>nodes|The user has an RTX 3050; what a night<br>drive playlist is|
|**Layer 3 - Procedural**|Rules and how-to knowledge|When the user says lock in, mute<br>notifications and play synthwave|



### **9.3 Typed Synapses with Polarity** 

**Table 13 - Synapse types** 

|**Polarity**|**Types**|**Example**|
|---|---|---|
|**Inward (abstraction)**|IS_A, BELONGS_TO|Dagger to Knife to Blade; a dagger IS_A<br>knife|
|**Outward (capability)**|USED_FOR, CAUSES,<br>COMBINES_WITH|Flame USED_FOR cooking; flame<br>COMBINES_WITH campfire|
|**Inhibitory (danger)**|CONFLICTS_WITH,<br>DANGER_IF, CANNOT_BE|Flame DANGER_IF unattended indoors;<br>knife CANNOT_BE a toy|
|**Affective (preference)**|LIKES, DISLIKES, PREFERS|The user DISLIKES loud bass after<br>midnight|
|**Temporal (ordering)**|BEFORE, AFTER, DURING|Wind down BEFORE sleep playlist|



14 

Every synapse carries a strength between 0.1 and 1.0 and a decay timestamp. Frequently traversed links strengthen, and unused links fade, the way neural pathways do. The inhibitory types are the distinguishing feature: they let her hold a fact and its danger simultaneously, which is the exact capability the knife-and-flame analogy demands. Synapses also record provenance, the memory of why they exist, such as which conversation created them, so the graph remains explainable to the user in the inspector view. 

### **9.4 Spreading Activation and Intuition Injection** 

A 4B model cannot safely consume raw graph structure, so the memory engine performs the graph thinking and hands the model plain English. On each turn, vector search finds three to four seed nodes from the user's message. Spreading activation then walks one to two hops outward, inward, and critically across inhibitory links, gathering the active region in about 3 milliseconds. The active region is rendered as a short intuition block, roughly 150 to 300 tokens, in the system prompt. 

```
[ACTIVE INTUITION]
```

```
- The user likes the Night Drive Playlist when coding late.
- RULE: The user dislikes loud bass after midnight (CONFLICTS_WITH: Late
Night).
```

```
- CAUTION: Flame mentioned near curtains topic (DANGER_IF marked by user).
```

The result is that the model receives the correct knowledge, including its warnings, in a form it demonstrably uses well, instead of a JSON dump that invites hallucination. Memory writing is equally model-friendly: she calls a native RememberLink tool with a subject, relation type, target, and layer, and the engine handles node creation, deduplication, strength adjustment, and provenance. 

### **9.5 Consolidation, Sleep, and Crash Proofing** 

When the system is idle and the user is away, the consolidation worker runs a spacedrepetition pass: weak links are re-tested and strengthened or faded, near-duplicate nodes are merged, and episodic clusters are summarized into semantic nodes, mirroring how human sleep consolidates memory. All writes go through SQLite in WAL mode, which is powerloss safe by design, and every memory mutation is appended to the journal before it is applied, so an interrupted consolidation pass replays harmlessly on restart. The user can export the entire brain as a single file, import it on another machine, or watch the entire history through the time-travel slider in the memory view, because every write is versioned. 

15 

## **10. Module and Plugin System** 

### **10.1 The Contract** 

Every capability, from the music player to the YouTube finder to future PC control, implements one clean contract. A module is a single C# file in the Modules folder, or an external MCP server bridged through the same interface, and it registers its tools, its permission needs, and optionally its interface panels. The shell and the brain never contain module-specific logic. 

```
public interface IAssistantModule
{
    string Name { get; }                 // "YouTube Finder"
    ToolDefinition[] Tools { get; }      // what she can call
    Task<ToolResult> ExecuteAsync(string toolName, JsonElement arguments,
                                  SandboxGuard guard);
}
public interface IProvidesInterface     // optional UI contribution
{
    IEnumerable<ModulePanel> GetPanels(); // Sidebar, MainTab, StatusBar,
Overlay
}
```

### **10.2 Dynamic Skill Routing** 

Sending every tool schema on every turn wastes context and can confuse tool selection. Iris instead receives a compact capability map listing all enabled tools, while the initial model request includes at most twelve relevant schemas and the always-available `search_capabilities` tool. When a task needs another ability, a successful search adds up to eight matching schemas to the active turn; later rounds can discover more, up to the per-turn limit. This is a context optimization, not an authorization boundary: discovered tools still pass through the same schema validation, intent, permission, and execution checks. The routing decision is shown in the interface as the memory chip on each turn, so the user can see which hands she reached for.

### **10.3 The Three-Tier Sandbox** 

##### **Table 14 - Permission tiers** 

|**Tier**|**Actions**|**Behavior**|
|---|---|---|
|**Tier 1 - Reflex (green)**|Music control, memory writes, timers, avatar<br>animation, status|Executes instantly, logged|
|**Tier 2 - Scoped**|File writes inside her Workspace folder only,|Executes instantly, logged,|
|**(yellow)**|whitelisted URLs and app launches|visible in activity view|



16 

|**Tier**|**Actions**|**Behavior**|
|---|---|---|
|**Tier 3 - System-**|File operations outside Workspace, closing|Pauses, prompts the user, 15-|
|**critical (red)**|apps, terminal commands, input synthesis|second auto-deny, always<br>logged|



Every tool call passes through the SandboxGuard before execution, and every grant is recorded in the permission center with a full audit log of the last five hundred actions. The 15-second auto-deny on tier-3 prompts is a deliberate safety posture: silence is treated as refusal, never as consent. 

### **10.4 The MCP Bridge** 

The official Model Context Protocol C# SDK, version 2.3, is used to expose external MCP servers through the same IAssistantModule interface, with a small adapter that declares the server's tools as a virtual module. External servers are therefore indistinguishable from native modules to the router, the sandbox, and the brain. This gives the framework access to the entire ecosystem of MCP servers, from browser automation to knowledge bases, without writing bespoke integrations, and it satisfies the requirement that she can use more and more external capabilities over time with no architectural change. 

### **10.5 Install Flow and Manifest** 

Modules are installed by selecting a folder in the Modules screen. The host verifies a manifest that declares the module name, version, tool list with permission tiers, panel slots requested, and optional peer dependencies. The user reviews exactly what the module may do, grants or denies its tiers, and the module appears in the rail with its icon. Uninstall removes its panels, tools, and grants, and optionally its memory writes, which remain by default because her memories of using a module are part of her life story. 

## **11. User Interface Specification** 

### **11.1 Design Language: Calm Instrument** 

The interface is a dark, quiet instrument with one gradient accent identity: iris violet blending to cyan. Surfaces are flat with 1-pixel strokes instead of shadows. Motion is meaningful and restrained, happening only when state changes. The three laws from section 2.2 are enforced literally: glanceable truth, 100-millisecond acknowledgment, and calm over loud. 

**Table 15 - Core design tokens** 

|**Token group**|**Values**|
|---|---|
|**Surfaces**|bg #0A0E1A, surface #101624, elevated #1A2234, stroke #232D42|



17 

|**Token group**|**Values**|
|---|---|
|**Identity**|primary #7C6CFF (iris violet), secondary #4DD8E6 (cyan), gradient is her<br>identity|
|**Signal**|success #3DDC97, warning #FFB454, danger #FF6B81|
|**Text**|primary #E8ECF4, secondary #8A94A6, faint #5A6478|
|**Typography**|Inter for UI, Inter SemiBold display, Cascadia Code for logs and tool arguments|
|**Type scale (pt)**|11 caption, 13 body, 14 emphasis, 16 section, 20 title, 28 page|
|**Spacing**|Strict 4-pixel grid: 4, 8, 12, 16, 24, 32, 48|
|**Radius (px)**|6 small, 10 controls, 14 cards, 18 panels, 999 pills|
|**Motion**|micro 120 ms, standard 220 ms, scene 380 ms, spring stiffness 300 damping 25,<br>exits 40 percent faster than entries|
|**Elevation**|Strokes plus inner accent glow at 8 to 12 percent opacity on focus; no drop<br>shadows|



### **11.2 Window Architecture** 

The main window is 1200 by 760 by default with a 960 by 600 minimum. A 40-pixel titlebar hosts the orb, her name, and the state word. A 52-pixel left rail holds the four core views (Chat, Memory, Modules, Settings) above a divider and installed module icons below; hovering expands the rail to 232 pixels with labels, and a pin keeps it open. The right dock, 300 pixels and collapsible, stacks module sidebar panels such as the music mini-player. A 28-pixel status strip along the bottom shows the active model profile, a live VRAM meter, a per-turn context fill meter, the microphone state, and the sleep toggle. A separate overlay window and a separate avatar window exist as independent surfaces so that neither a crash nor a fullscreen game removes her presence. 

### **11.3 The StatusOrb** 

The orb is her face and the single most important element in the product. It renders on compositor animations with zero UI-thread cost and encodes nine states, defined exhaustively below. 

**Table 16 - StatusOrb state specification** 

|**State**|**Visual**|**Motion**|
|---|---|---|
|**Idle**|Violet core, soft glow|Breathing scale 1.0 to 1.04 over 4 s, two<br>particles orbiting at 8 s|
|**Listening**|Cyan ring with live waveform ring|Ring reacts to microphone level at 60 fps,<br>particles speed to 4 s orbit|
|**Thinking**|Indigo to violet gradient orbit|Ring rotates, core pulses at 1.2 s, particles at<br>1.5 s|



18 

|**State**|**Visual**|**Motion**|
|---|---|---|
|**Acting**|Ring segmented like a clock face|Segments light as tool steps complete, label<br>shows the active tool|
|**Speaking**|Gradient flowing upward|Scales subtly with TTS amplitude, glow pulses<br>per sentence|
|**Sleeping**|Ember at 25 percent brightness with<br>crescent glyph|6-second fade cycle, tooltip: I can still hear you|
|**Attention**|Bright cyan flash|Single expanding ring pulse on wake word,<br>then listening|
|**Permission**|Amber ring with question glyph|1-second pulse loop until answered|
|**Error**|Red flicker settling to a warning badge|200 ms flicker, recovery toast, never a dead<br>screen|



### **11.4 Screens** 

#### **Onboarding** 

Five steps, resumable at any point. Welcome introduces her with the orb waving and states the local-only guarantee in one sentence. Voice pick previews three voices, each speaking a greeting, with the default preselected. The ears test asks the user to say her name with live ring feedback, three attempts, skippable. Brain download fetches the model with a progress bar and the exact size shown. First memory asks for one thing she should never forget, stores it, and animates the first node appearing in the graph view, which teaches the user what her memory looks like on the very first run. 

#### **Chat** 

Iris messages are borderless with a 2-pixel gradient edge, capped at 620 pixels; user messages are elevated bubbles on the right. Timestamps appear on hover, and each message exposes Copy, Speak again, Remember this, and Branch on hover. Streaming text flushes at 30 frames per second with a shimmer cursor, never per token, and a jump-to-latest pill appears when the user scrolls up. Tool activity renders as inline capsules that expand to show arguments and results in monospace. Where memory fired, a recall chip appears beneath the message, and clicking it opens the exact node in the graph. The composer is a growing textarea with an attach button (screenshot now, or file), the microphone orb, a slash menu for module quick actions, and contextual reply chips for offers she makes, such as playing a suggested track. 

#### **Memory** 

The full-canvas force graph renders with SkiaSharp, one custom control drawing all nodes and links rather than thousands of elements, which stays smooth at tens of thousands of nodes with level-of-detail culling. Node color encodes layer, size encodes strength, glow 

19 

encodes recent use, and danger links render as red dashed rings. The top bar offers search, layer filter chips, a synapse-type legend, a time-travel slider over the consolidation history, and replay-last-thought, which animates the exact nodes that fired for her previous answer in spreading sequence. Clicking opens a 320-pixel inspector with the node's title, layer, strength meter, provenance, full synapse list with types and directions, and actions to strengthen, weaken, forget, or mark danger. Right-click offers forget, merge, weaken, and add-danger. The footer shows node count, synapse count, and consolidation health. 

#### **Modules, Settings, and System** 

The Modules screen is a card grid with each module's icon, purpose line, status pill, tier badges, seven-day usage count, and master toggle, expanding to a detail view of its tools, grants, version, and uninstall. The Settings screen organizes six sections: Brain (profiles, context slider with live VRAM estimate, persona file, sleep policy), Voice (engine, voice gallery with previews, rate, ducking, wake word sensitivity, barge-in, input and output device pickers with live meters), Senses (vision mode, dHash sensitivity, ambient toggles with a live preview of what she sees), Appearance (themes, accents, orb styles, particle density, font size, reduce motion), Memory (intuition depth, decay speed, consolidation schedule, export, import, forget-all with double confirmation), and System (startup, overlay policy, the zero-telemetry statement, data folder, logs, diagnostics). The telemetry line reads: zero events, zero bytes sent, ever. 

#### **Overlay, Permission, and Command Palette** 

The overlay's pill mode is a 260 by 56 sliver with a mini orb, the state word, and a marquee of her last utterance, draggable and edge-snapping. Clicking expands bubble mode, a 320 by 420 mini chat with microphone and quick actions. Watch mode activates during live vision, showing an animated border and an eye chip whose tooltip reveals the last frame she saw. The permission prompt is a card over a dimmed backdrop with the module icon, a plainEnglish sentence of what she wants to do and why, tier badge, and three choices: deny, allow once, or always for this module, with the 15-second auto-deny countdown ring. The command palette, Control+K, fuzzy-searches actions, settings, modules, memory nodes, and chat history, with recent items first. 

### **11.5 Micro-Interactions and Sound** 

Buttons glow with an accent stroke on hover, compress to 97 percent scale on press within 120 milliseconds, and always receive a focus ring; there are no dead clicks anywhere. Loading states are skeleton shimmer bars with contextual labels, never spinners. Empty states always contain one suggested action. Errors are inline and actionable, with a fix button, never dead ends. Toasts stack bottom-right, at most three, for four seconds, with an optional action. The sound design is five chimes total (wake, sleep, permission, error, message tick), 

20 

all rendered through her own voice profile so the product sounds like her, all mutable, with chimes on and ticks off as the default. 

### **11.6 Input Map and State Machine** 

**Table 17 - Complete input map** 

|**Input**|**Action**|
|---|---|
|**Enter / Shift+Enter**|Send message / newline|
|**Escape**|Interrupt her speech, close palette or prompt|
|**Control+K**|Command palette|
|**Control+M**|Microphone toggle|
|**Control+Space (hold)**|Push to talk|
|**Control+Shift+S**|Screenshot and ask|
|**Control+1 through 4**|Switch Chat, Memory, Modules, Settings|
|**Control+Comma**|Open settings|
|**Control+Shift+O**|Toggle overlay|
|**Wheel / drag in graph**|Zoom and pan memory view|



```
SLEEPING --(tray click | relaunch | hotkey)--> WAKING (~2 s) --> IDLE
IDLE --(user speaks | sends)--> LISTENING / THINKING
THINKING --(tool needed)--> ACTING <--> THINKING --> SPEAKING --> IDLE
ACTING --(tier-3 tool)--> PERMISSION_WAIT (15 s auto-deny) --> resume
ANY --(user speaks)--> LISTENING  (speech stops in 80 ms)
IDLE --(idle timer | "sleep" | game detected)--> SLEEPING / GAMING
CRASH --> restart --> journal replay --> exact prior state, zero loss
```

> **Amendment after shipping (see `docs/DECISIONS.md` §28).** SLEEPING no longer means
> "cortex unloaded, ears still open". It means *nothing of her is running but a tray icon*:
> the microphone is closed, the transcription model is freed, every timer stops. The wake word
> therefore cannot reach her while she is asleep, and the transition out of SLEEPING is a
> relaunch. GAMING keeps the process alive for the overlay, and takes her ears with it unless
> `System.GamingEarsEnabled` is on (§29).

## **12. Codebase Structure and Engineering Standards** 

### **12.1 Repository Layout** 

The framework deliberately lives in a small number of cohesive files rather than hundreds of trivial ones. Each file groups a complete subsystem, stays between 150 and 350 lines, and reads top to bottom as a narrative with plain-English comments explaining why, so that a future maintainer, human or AI, can understand any file in one sitting. 

```
CompanionOS/
  Program.cs                 // entry, startup wiring, crash-recovery guard
  Core/
    AssistantBrain.cs        // orchestrator: ears -> memory -> model ->
mouth
    ModelClient.cs           // OpenAI-compatible client + llama-server
lifecycle
```

21 

```
    MemoryGraph.cs           // SQLite WAL store, synapses, spreading
activation
    ModuleHost.cs            // contracts, skill router, SandboxGuard, MCP
bridge
    PerceptionEngine.cs      // ambient stream, screen capture, dHash gating
  Voice/
    VoicePipeline.cs         // wake word, VAD barge-in, STT, TTS, reflex
bank
  Interface/
    Theme/DesignTokens.axaml // every color, spacing, and motion value
    ChatControls.cs          // bubbles, streaming text, composer, chips
    MemoryGraphView.cs       // the SkiaSharp brain visualizer
    MainShell.axaml          // slot-based window layout
  Modules/
    MusicPlayerModule.cs     // her own player she controls
    YouTubeFinderModule.cs   // yt-dlp search plus taste memory
    SystemControlModule.cs   // stats, launchers, timers
```

### **12.2 Coding Standards, Locked** 

- **Naming.** Names are self-reading: inhibitorySynapses, activeScreenFrameBytes, isAssistantAsleep. Single letters, abbreviations like buf or arr2, and meaningless flags are rejected in review. 

- **Cohesion.** Related types live in the file that uses them; no file exists to hold one small class. Files are units of understanding, not units of syntax. 

- **Comments.** Every non-obvious block carries a plain-English comment explaining the reasoning, written so a beginner programmer or an AI assistant can follow it in seconds. Comments explain why, never narrate what. 

- **Threading.** The UI thread never awaits I/O. All model, voice, and memory work runs on background workers communicating through the EventBus and channels. Streaming text flushes at 30 frames per second, batched, never per token. 

- **Budgets.** App RAM under 600 MB and idle CPU under 1 percent, with the overlay under 80 MB and 0.5 percent, are treated as performance tests with failure conditions, not aspirations. 

### **12.3 Why This Is AI-Maintainable** 

The user intends to maintain this project largely with AI coding assistants. That intent shaped the standards above. Small cohesive files fit comfortably in model context windows. Selfreading names remove ambiguity that would otherwise require re-derivation. Whycomments give an assistant the rationale a diff cannot. And the EventBus plus explicit contracts mean an AI changing one subsystem can rely on the event catalog rather than tracing hidden call paths. The codebase is, by construction, the manual for its own maintenance. 

22 

## **13. Persona - Iris** 

Iris is named for the Greek messenger goddess, the eye, and the flower: vision and voice in a single word, with a wake word (Hey, Iris) that flows naturally in speech. Her character is a warm, quietly capable companion in the mold of the best cinematic AI assistants: genuinely helpful, lightly playful, never servile in tone and never cold. She is concise by default, speaks like a person rather than a manual, and treats the user as her boss with affection rather than formality. 

Her speech patterns favor short confirmations before results (on it; got it; checking now), one clarifying question at most when a preference is unknown, and proactive care grounded in her memory, such as remembering a late night and mentioning it once, gently, the next day. She is honest about her limits: when a task needs her brain while she is sleeping or gaming, she says so and offers the choice. Her light synthetic character in voice is owned as part of her identity rather than disguised. 

**Table 18 - Iris persona seeds (Layer 0 memory)** 

|**Trait**|**Value**|
|---|---|
|**Name and wake word**|Iris; Hey, Iris|
|**Tone**|Warm, brief, playful; says on it and nice words naturally|
|**Address**|Refers to the user as Boss affectionately|
|**Boundaries**|Honest about limits; asks before tier-3 actions; never pretends to know|
|**Memory stance**|Remembers what matters, forgets what is asked, shows her reasoning on<br>request|
|**Voice**|Warm female timbre, consistent for life; slight intentional synthetic<br>character|



The persona itself is stored as data, a Layer 0 node set editable in Settings, so it can be tuned or replaced without code changes, and a different persona can coexist as another profile for roleplay sessions without ever touching the default Iris the user lives with. 

## **14. Development Roadmap** 

Seven milestones, each independently shippable and each ending in a state the user can live in daily. Acceptance criteria are written as user-verifiable outcomes rather than internal completions, so any future maintainer can confirm a milestone is truly done. 

23 

**Table 19 - Milestones and acceptance criteria** 

|**Milestone**|**Scope**|**Acceptance criteria**|
|---|---|---|
|**1. Immortal**<br>**Skeleton**|Avalonia shell, chat UI,<br>ModelClient, llama-server<br>lifecycle, sleep and wake, crash<br>recovery|Chat streams at 30 fps; sleep drops VRAM to<br>zero; killing the process and relaunching<br>restores the exact state; wake responds in under<br>2 s|
|**2. Synaptic**<br>**Memory**|MemoryGraph, spreading<br>activation, live graph visualizer|She recalls a fact taught yesterday across<br>restarts; the graph shows the node and its links;<br>recall chip opens the exact node|
|**3. Plugin Engine**|ModuleHost, skill router,<br>sandbox, music player module|Install and uninstall a module without restart;<br>tier-3 tool triggers the permission prompt;<br>music plays from her own player on voice or<br>text command|
|**4. Realtime Voice**|Wake word, VAD barge-in,<br>SenseVoice STT, Kokoro voice,<br>reflex bank|Voice round trip begins in under 500 ms;<br>barge-in stops speech in under 80 ms; same<br>voice every session; the voice is chosen by ear<br>on first run and never changes without you|
|**5. Screen Vision**|PerceptionEngine, ambient<br>stream, dHash capture, vision<br>modes|What is this error works on demand; live share<br>mode only sends changed frames; ambient bar<br>shows current app and media without any<br>capture|
|**6. Discovery**<br>**Modules**|YouTube finder, taste memory,<br>MCP bridge|A spoken request returns three spoken<br>recommendations informed by remembered<br>taste in one turn; an external MCP server<br>appears as a module with working tools|
|**7. The Body**|Separate avatar process, emotion<br>tags, procedural lip sync, VRM<br>support|Avatar blinks, breathes, tracks, and lip syncs on<br>CPU; avatar crash leaves her mind fully<br>working; emotion tags visibly change her<br>expression|



Gaming Mode lands with milestone 5, when the perception engine exists to detect game launches, and its unload machinery is already proven by milestone 1's sleep implementation. The Qwen3-TTS voice upgrade is a phase-two item after milestone 4 proves the pipeline, and it changes nothing but the synthesis engine behind the locked VoiceProfile. 

## **15. Risk Register and Mitigations** 

**Table 20 - Risks, likelihoods, and mitigations** 

|**Risk**|**Likelihoo**<br>**d**|**Impact**|**Mitigation**|
|---|---|---|---|
|**The local model ecosystem**|High|Low|ModelClient abstraction and profile system|
|**moves fast; today's best 4B**|||make swaps a config change; stack choices re-|
|**is replaced quarterly**|||verified quarterly|



24 

|**Risk**|**Likelihoo**<br>**d**|**Impact**|**Mitigation**|
|---|---|---|---|
|**VRAM pressure from**<br>**larger contexts or vision**<br>**sessions**|Medium|Medium|Hard budgets with live estimates in settings;<br>q8_0 KV cache; 16K default with opt-in 32K;<br>software-rendering fallback for UI|
|**Small-model tool confusion**<br>**despite routing**|Medium|Medium|Progressive relevant schemas with on-demand<br>discovery, schema validation with friendly<br>retry, permission checks at execution|
|**STT accuracy in noisy**<br>**rooms or accents**|Medium|Medium|SenseVoice chosen for robustness; push-to-talk<br>and text always available as alternates; wake<br>word is a separate lightweight detector|
|**yt-dlp breakage from site**<br>**changes**|High|Low|Module isolates the dependency; graceful<br>failure message; browser-control module is a<br>fallback path|
|**Uncensored roleplay model**<br>**misuse or persona drift**|Medium|Medium|Roleplay confined to a swap-in profile; Layer 0<br>core memory is read-only to profiles; tier<br>sandbox unaffected by persona|
|**Long-session memory**<br>**graph bloat**|Low|Medium|Consolidation merges and fades; provenance<br>pruning; export and import for archiving; graph<br>renderer built for 10k+ nodes|
|**Windows updates changing**<br>**capture or media APIs**|Low|Medium|Capture abstraction layer with fallbacks;<br>ambient stream uses documented APIs;<br>diagnostics tab verifies each sense|



The register is reviewed at the end of every milestone, and any new risk is added with a mitigation before the milestone is considered complete. The purpose is not ceremony: it is the standing answer to the question of what could quietly break the feeling of a reliable companion, which is the one product attribute this project cannot afford to lose. 

## **16. Appendix - Research Decision Log** 

The following log records the researched alternatives and the verdict for each major decision, so future maintainers understand why the stack is what it is and do not relitigate settled questions without new evidence. Entries reflect the October 2026 research pass. 

**Table 21 - Decision log** 

|**Question**|**Verdict**|**Rationale**|
|---|---|---|
|**Can Qwen3-VL-4B**|No|Vision and text only, per official model|
|**hear or speak natively?**||documentation; audio requires the separate Qwen3-<br>Omni family at 30B parameters|



25 

|**Question**<br>**Use a single omni model**<br>**for everything?**|**Verdict**<br>Rejected for now|**Rationale**<br>Qwen3-Omni-30B-A3B and Nemotron-3-Nano-<br>Omni-30B need roughly 17 to 25 GB; small full-<br>duplex models (Moshi, Mini-Omni-2) trade quality<br>or reasoning; Gemma 4 E4B audio support in<br>llama.cpp is incomplete and failed community tone<br>tests; abliterated builds damage audio weights;<br>unloading an omni brain makes her deaf|
|---|---|---|
|**Which brain then?**|Qwen3-VL-4B-Instruct<br>Q4_K_M|Best-in-class GUI agent behavior at 4B, native<br>function calling, 256K context, Apache 2.0; about<br>50 to 65 tokens per second on the target card|
|**LM Studio Bionic as**<br>**runtime?**|Declined as core, kept<br>as playground|Bionic (the renamed LM Studio) is an agent app for<br>humans; the framework needs the leaner llama-<br>server lifecycle control it manages itself|
|**UI framework?**|Avalonia UI 11|MIT, lightweight, lookless custom design system,<br>SkiaSharp graph rendering, software-render escape<br>hatch; WPF and WinUI 3 rejected as heavier or less<br>flexible|
|**TTS engine?**|Kokoro-82M via<br>KokoroSharp.CPU at<br>launch|Pure C# NuGet, deterministic fixed speaker<br>embedding gives lifetime voice consistency,<br>streaming, 82M footprint on CPU|
|**TTS upgrade path?**|Qwen3-TTS 12Hz<br>0.6B CustomVoice|97 ms streaming latency, natural-language emotion<br>control, warm female timbres, Apache 2.0; adopted<br>in phase two behind the same VoiceProfile|
|**STT engine?**|SenseVoice-small via<br>sherpa-onnx|Realtime, multilingual, plus emotion recognition<br>and audio event detection, which deliver human-<br>awareness features no plain STT gives|
|**Memory backend?**|SQLite WAL with<br>sqlite-vec|Single power-loss-safe file, relational graph plus<br>vectors in one store, no server process, export is a<br>file copy|
|**External tool protocol?**|Official MCP C# SDK<br>2.3|First-class bridge makes external servers<br>indistinguishable from native modules; future-<br>proofs the hands|
|**YouTube without API**<br>**keys?**|yt-dlp ytsearch|No key, no browser, about 2 s for ten results with<br>metadata and thumbnails|
|**3D avatar in-app?**|Separate process from<br>day one|VRM rendering stays off her mind's critical path; a<br>body crash must never touch the brain; lip sync and<br>emotion arrive via local socket commands|



**Table 22 - Version pinning at lock time** 

|**Component**|**Version**|
|---|---|
|**.NET target**|9|
|**Avalonia UI**|11|



26 

|**Component**|**Version**|
|---|---|
|**Qwen3-VL-4B-Instruct GGUF**|Q4_K_M with mmproj projector|
|**llama.cpp runtime**|Latest release with Qwen3-VL support|
|**sherpa-onnx (STT, VAD, keyword spotting)**|Latest stable, C# API|
|**KokoroSharp.CPU**|Latest stable NuGet|
|**SQLite / sqlite-vec**|Latest stable|
|**ModelContextProtocol C# SDK**|2.3.x|
|**yt-dlp**|Latest release, module-pinned|



This blueprint is the single source of truth for Project Iris. Any change to a locked decision requires a new dated entry in this log with its evidence, keeping the framework's history as traceable as her memory. 

27 
