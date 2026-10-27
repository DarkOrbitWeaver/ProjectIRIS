using CompanionOS.Core.Events;
using CompanionOS.Core.Mind;
using CompanionOS.Core.Modules;

namespace CompanionOS.Core.Modules.BuiltIn;

/// <summary>
/// Her permanent body parts: the four things she can always do, no matter which
/// plugins are installed.
///
/// These tools are the ones routed into every single turn. They are written to be
/// almost impossible for a 4B model to misuse: flat arguments, obvious names, and
/// descriptions that say exactly when to call them.
/// </summary>
public sealed class CoreToolsModule : IAssistantModule
{
    private ModuleContext? _context;

    public string Id => "core";

    public string DisplayName => "Core Senses";

    public string Purpose => "Memory, eyes, sleep and self-awareness — always available.";

    public string CapabilityDescription =>
        "remember recall memory forget look at screen see screenshot situation sleep wake gaming " +
        "microphone listen capabilities help what can you do";

    public bool AlwaysAvailable => true;

    public IReadOnlyList<ModuleTool> Tools { get; } = new List<ModuleTool>
    {
        new(
            "remember",
            "Store a durable fact about the user, their tastes, their setup or a rule they want followed. " +
            "Use this whenever they tell you something worth keeping. Never store secrets or passwords.",
            ToolTier.Reflex,
            """
            {
              "type": "object",
              "properties": {
                "subject": { "type": "string", "description": "Who or what this is about, e.g. 'Boss' or 'Night Drive Playlist'." },
                "relation": { "type": "string", "description": "One of: likes, dislikes, prefers, is_a, belongs_to, used_for, causes, combines_with, conflicts_with, danger_if, cannot_be, before, after, during, related_to." },
                "target": { "type": "string", "description": "The other end of the fact, e.g. 'late night coding'." },
                "content": { "type": "string", "description": "Optional one-sentence explanation." },
                "layer": { "type": "integer", "description": "1 episode, 2 fact (default), 3 rule, 0 core self." }
              },
              "required": ["subject", "relation", "target"]
            }
            """,
            "remember", "learn", "note", "store", "keep in mind"),

        new(
            "recall_memory",
            "Search her long-term memory for what she knows about a topic, a person or a preference.",
            ToolTier.Reflex,
            """{"type":"object","properties":{"query":{"type":"string"},"count":{"type":"integer"}},"required":["query"]}""",
            "recall", "remember", "memory", "what do you know"),

        new(
            "forget_memory",
            "Forget one exact, user-named memory. A miss lists the closest existing titles as evidence; pick the exact title from that list or say none matched. Never guess which fact to erase.",
            ToolTier.Reflex,
            """{"type":"object","properties":{"title":{"type":"string"}},"required":["title"]}""",
            "forget", "delete memory", "remove memory"),

        new(
            "correct_memory",
            "Record a user correction without silently rewriting the old assertion. The correction is stored with user-correction provenance and should be preferred over the old fact.",
            ToolTier.Reflex,
            """{"type":"object","properties":{"subject":{"type":"string"},"relation":{"type":"string"},"target":{"type":"string"},"previous":{"type":"string"}},"required":["subject","relation","target"]}""",
            "correction", "actually", "that is wrong", "update memory"),

        new(
            "look_at_screen",
            "Capture the actual screen pixels and inspect the picture with her vision model, including app layout, images, colors, and Iris's red browser attention border. Use this for 'what is on my screen?', 'look at my screen', 'take a picture', or any visual question. This is a screenshot, not browser text extraction.",
            ToolTier.Reflex,
            """{"type":"object","properties":{"question":{"type":"string","description":"What to look for. Optional."}},"required":[]}""",
            "screen", "screenshot", "look", "see", "error", "window", "read this"),

        new(
            "describe_situation",
            "Report what the machine is doing right now: foreground app, playing media, load, idle time.",
            ToolTier.Reflex,
            """{"type":"object","properties":{},"required":[]}""",
            "situation", "what am i doing", "current app", "status", "playing"),

        new(
            "control_windows_media",
            "Windows' system media keys, for OTHER apps' media: pause, or play/resume, the video or music a browser " +
            "(YouTube), Spotify, or any desktop app exposed to Windows' media session. Its actions are pause and play " +
            "only — action=play is ALSO how you resume a paused session: play and resume are the same hand here. " +
            "NOT for her own music library or playlists — those belong to the music module's tools. If no media " +
            "session is exposed, say it did not happen. Report only the tool result; never claim success when Windows " +
            "does not confirm it.",
            ToolTier.Reflex,
            """{"type":"object","properties":{"action":{"type":"string","enum":["pause","play"]}},"required":["action"]}""",
            "pause video", "pause youtube", "pause playback", "resume video", "play video", "current video", "media player", "youtube"),

        new(
            "set_operating_mode",
            "Put her to sleep (unload the model and free the GPU), wake her up, or enter gaming mode. " +
            "Use when the user asks her to rest, wake, or step aside for a game.",
            ToolTier.Reflex,
            """{"type":"object","properties":{"mode":{"type":"string","enum":["awake","sleeping","gaming"]}},"required":["mode"]}""",
            "sleep", "rest", "wake", "unload", "gaming mode"),

        new(
            "control_microphone",
            "Open or close her microphone. Opening means the next thing the user says is treated as a request.",
            ToolTier.Reflex,
            """{"type":"object","properties":{"state":{"type":"string","enum":["open","close"]}},"required":["state"]}""",
            "listen", "microphone", "mic", "stop listening"),

        new(
            "search_capabilities",
            "List the modules and tools she currently has, or check whether she can do something.",
            ToolTier.Reflex,
            """{"type":"object","properties":{"query":{"type":"string"}},"required":[]}""",
            "can you", "abilities", "tools", "modules", "plugins"),

        new(
            "learn_game",
            "Teach the game detector that an application is or is not a game, so gaming mode triggers correctly.",
            ToolTier.Scoped,
            """{"type":"object","properties":{"executable":{"type":"string"},"is_game":{"type":"boolean"}},"required":["executable","is_game"]}""",
            "that is a game", "not a game", "gaming mode")
    };

    public Task InitializeAsync(ModuleContext context, CancellationToken cancellationToken)
    {
        _context = context;
        return Task.CompletedTask;
    }

    public async Task<ToolResult> ExecuteAsync(ToolInvocation invocation, CancellationToken cancellationToken)
    {
        ModuleContext context = _context ?? throw new InvalidOperationException("Core tools are not ready.");

        switch (invocation.Tool.Name)
        {
            case "remember":
                return await RememberAsync(context, invocation, cancellationToken).ConfigureAwait(false);

            case "recall_memory":
            {
                string query = invocation.GetString("query");
                int count = Math.Clamp(invocation.GetInt32("count", 5), 1, 10);
                IReadOnlyList<RecalledMemory> memories =
                    await context.Memory.RecallAsync(query, count, cancellationToken).ConfigureAwait(false);

                if (memories.Count == 0)
                {
                    return ToolResult.Ok("Nothing in memory matches that yet.");
                }

                // Titles plus the fact itself. A fact lives in the content; the 2026-10-27
                // audit caught the old title-only answer capping her entire knowledge
                // base at node names.
                string text = string.Join(";\n", memories.Select(memory =>
                {
                    string content = memory.Content.Trim();
                    if (content.Length > 220)
                    {
                        content = content[..200] + "…";
                    }

                    return content.Length > 0 && content != memory.Title
                        ? $"{memory.Title}: {content}"
                        : memory.Title;
                }));
                return ToolResult.Ok(text, displayText: string.Join("; ", memories.Select(memory => memory.Title)));
            }

            case "forget_memory":
            {
                MemoryForgetResult result = await context.Memory.ForgetAsync(invocation.GetString("title"), cancellationToken)
                    .ConfigureAwait(false);
                return result.Succeeded ? ToolResult.Ok(result.Message, result.Message) : ToolResult.Fail(result.Message);
            }

            case "correct_memory":
                return await CorrectMemoryAsync(context, invocation, cancellationToken).ConfigureAwait(false);

            case "look_at_screen":
            {
                var frame = await context.Host.LookAtScreenAsync(true, cancellationToken).ConfigureAwait(false);
                if (frame is null)
                {
                    return ToolResult.Fail("I cannot see the screen right now (vision is off or capture failed).");
                }

                return new ToolResult
                {
                    Succeeded = true,
                    ModelText = "Screenshot captured. It is attached to the next message; describe only what you can see.",
                    ImageDataUri = frame.DataUri
                };
            }

            case "describe_situation":
                return ToolResult.Ok(context.Host.DescribeSituation());

            case "control_windows_media":
            {
                MediaControlResult result = await context.Host.ControlMediaAsync(
                    invocation.GetString("action"), cancellationToken).ConfigureAwait(false);
                return result.Succeeded
                    ? ToolResult.Ok(result.Message, result.Message)
                    : ToolResult.Fail(result.Message);
            }

            case "set_operating_mode":
            {
                string requestedMode = invocation.GetString("mode").Trim().ToLowerInvariant();
                OperatingMode targetMode = requestedMode switch
                {
                    "awake" or "wake" or "wake up" => OperatingMode.Awake,
                    "sleeping" or "sleep" or "rest" => OperatingMode.Sleeping,
                    "gaming" or "game" => OperatingMode.Gaming,
                    _ => OperatingMode.Awake
                };

                if (requestedMode is not ("awake" or "wake" or "wake up" or "sleeping" or "sleep" or "rest" or
                    "gaming" or "game"))
                {
                    // The audit caught unknown input inventing Awake — which both woke an
                    // idle creature and dropped the game hand-off. Unknown word, no
                    // side effect, honest refusal with the valid set.
                    return ToolResult.Fail(
                        $"'{requestedMode}' is not a mode she has. Valid modes: awake, sleeping, gaming.");
                }

                if (targetMode == OperatingMode.Gaming)
                {
                    await context.Host.SetManualGamingAsync(true, cancellationToken).ConfigureAwait(false);
                }
                else if (targetMode == OperatingMode.Awake)
                {
                    await context.Host.SetManualGamingAsync(false, cancellationToken).ConfigureAwait(false);
                }
                return ToolResult.Ok($"Mode is now {targetMode}.");
            }

            case "control_microphone":
            {
                bool shouldOpen = invocation.GetString("state")
                    .Equals("open", StringComparison.OrdinalIgnoreCase);
                context.Host.SetMicrophoneOpen(shouldOpen);
                return ToolResult.Ok(shouldOpen ? "Microphone is open." : "Microphone is closed.");
            }

            case "search_capabilities":
                return ToolResult.Ok(BuildCapabilityReport(context, invocation.GetString("query")));

            case "learn_game":
            {
                string executable = invocation.GetString("executable");
                bool isGame = invocation.GetBoolean("is_game", true);
                context.Host.LearnGame(executable, isGame);
                return ToolResult.Ok($"I will treat {executable} as {(isGame ? "a game" : "not a game")} from now on.");
            }

            default:
                return ToolResult.Fail($"Unknown core tool '{invocation.Tool.Name}'.");
        }
    }

    public Task ShutdownAsync() => Task.CompletedTask;

    private static async Task<ToolResult> RememberAsync(ModuleContext context, ToolInvocation invocation,
        CancellationToken cancellationToken)
    {
        string subject = invocation.GetString("subject").Trim();
        string relation = invocation.GetString("relation").Trim();
        string target = invocation.GetString("target").Trim();

        if (subject.Length == 0 || relation.Length == 0 || target.Length == 0)
        {
            return ToolResult.Fail("A memory needs a subject, a relation and a target.");
        }

        RecalledMemory stored = await context.Memory.RememberAsync(new RememberRequest
        {
            Subject = subject,
            Relation = relation,
            Target = target,
            Content = invocation.GetString("content"),
            Layer = Math.Clamp(invocation.GetInt32("layer", 2), 0, 3),
            Provenance = "conversation"
        }, cancellationToken).ConfigureAwait(false);

        return ToolResult.Ok($"Stored: {subject} {relation} {target}.", displayText: $"Remembered: {stored.Title}");
    }

    private static async Task<ToolResult> CorrectMemoryAsync(ModuleContext context, ToolInvocation invocation,
        CancellationToken cancellationToken)
    {
        string subject = invocation.GetString("subject").Trim();
        string relation = invocation.GetString("relation").Trim();
        string target = invocation.GetString("target").Trim();
        if (subject.Length == 0 || relation.Length == 0 || target.Length == 0)
        {
            return ToolResult.Fail("A correction needs a subject, relation and corrected value.");
        }

        string previous = invocation.GetString("previous").Trim();
        string content = previous.Length == 0
            ? "User explicitly corrected this information."
            : $"User explicitly corrected the previous value: {previous}.";
        await context.Memory.RememberAsync(new RememberRequest
        {
            Subject = subject,
            Relation = relation,
            Target = target,
            Content = content,
            Layer = 2,
            Provenance = "user-correction",
            Supersedes = previous.Length == 0 ? null : previous
        }, cancellationToken).ConfigureAwait(false);

        return ToolResult.Ok(
            $"Recorded correction: {subject} {relation} {target}. The earlier assertion was preserved for review; this user correction should take precedence.",
            $"Got it. I'll treat that as the correction.");
    }

    private static string BuildCapabilityReport(ModuleContext context, string query)
    {
        string capabilities = context.Host.DescribeCapabilities();
        if (string.IsNullOrWhiteSpace(capabilities))
        {
            return "I have my core senses: memory, screen vision, microphone, sleep and mode changes.";
        }

        if (string.IsNullOrWhiteSpace(query))
        {
            return capabilities;
        }

        string[] matchingLines = capabilities
            .Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Where(line => line.Contains(query, StringComparison.OrdinalIgnoreCase))
            .ToArray();

        return matchingLines.Length > 0
            ? string.Join("\n", matchingLines)
            : $"Nothing matches \"{query}\" yet. Installed abilities:\n{capabilities}";
    }
}
