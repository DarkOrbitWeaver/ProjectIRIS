using System.Text;
using CompanionOS.Core.Configuration;
using CompanionOS.Core.Diagnostics;
using CompanionOS.Core.Events;
using CompanionOS.Core.Modules;

namespace CompanionOS.Core.Memory;

/// <summary>
/// Thinks about her memory so the language model does not have to.
///
/// A small model given raw graph data hallucinates. So this class does the graph
/// work in milliseconds and hands the model a handful of plain sentences:
///
///   [ACTIVE INTUITION]
///   - Boss likes the Night Drive playlist for late coding sessions.
///   - CAUTION: flame is dangerous if left unattended indoors.
///   - RULE: when Boss says "lock in", mute notifications and play synthwave.
///
/// That block is roughly 200 tokens and it is the reason a 4B model can feel like
/// it remembers a lifetime.
/// </summary>
public sealed class SynapticMemoryEngine : IMemoryAccess
{
    /// <summary>How strongly each polarity carries activation to the next hop.</summary>
    private static readonly Dictionary<SynapsePolarity, double> PolarityCarry = new()
    {
        [SynapsePolarity.Inward] = 0.90,
        [SynapsePolarity.Outward] = 0.85,
        [SynapsePolarity.Inhibitory] = 1.00,
        [SynapsePolarity.Affective] = 0.95,
        [SynapsePolarity.Temporal] = 0.70
    };

    private readonly MemoryGraph _graph;
    private readonly SettingsStore _settings;
    private readonly EventBus _events;

    public SynapticMemoryEngine(MemoryGraph graph, SettingsStore settings, EventBus events)
    {
        _graph = graph;
        _settings = settings;
        _events = events;
    }

    /// <summary>The underlying store, for screens that draw the graph.</summary>
    public MemoryGraph Graph => _graph;

    /// <summary>
    /// Runs spreading activation for one user sentence:
    /// find seeds by similarity, then walk outward, inward and across danger links,
    /// letting each hop lose a little energy so distant trivia never floods the prompt.
    /// </summary>
    public async Task<ActivationResult> ActivateAsync(string? userMessage, CancellationToken cancellationToken = default)
    {
        MemorySettings settings = _settings.Current.Memory;
        IReadOnlyList<(MemoryNode Node, double Score)> matches =
            _graph.SearchNodes(userMessage, Math.Max(1, settings.SeedCount));

        if (matches.Count == 0)
        {
            return new ActivationResult();
        }

        Dictionary<string, double> scores = new(StringComparer.Ordinal);
        Dictionary<string, MemoryActivationPath> activationPaths = new(StringComparer.Ordinal);
        List<MemorySynapse> usedSynapses = new();

        foreach ((MemoryNode node, double score) in matches)
        {
            scores[node.Id] = Math.Max(score, scores.TryGetValue(node.Id, out double existing) ? existing : 0);
        }

        List<string> frontier = scores.Keys.ToList();

        for (int hop = 1; hop <= Math.Clamp(settings.IntuitionDepth, 1, 4); hop++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            List<string> nextFrontier = new();
            double hopFactor = Math.Pow(0.72, hop - 1);

            foreach (string nodeId in frontier)
            {
                double originScore = scores[nodeId];

                List<MemorySynapse> edges = new();
                edges.AddRange(_graph.OutgoingOf(nodeId));
                edges.AddRange(_graph.IncomingOf(nodeId));

                foreach (MemorySynapse synapse in edges)
                {
                    string neighbourId = synapse.FromNodeId == nodeId ? synapse.ToNodeId : synapse.FromNodeId;
                    if (!_graph.ContainsNode(neighbourId))
                    {
                        continue;
                    }

                    // Supersedes is audit metadata, not a semantic association.
                    if (synapse.Type == SynapseType.Supersedes)
                    {
                        continue;
                    }

                    double carry = PolarityCarry.TryGetValue(synapse.Polarity, out double factor) ? factor : 0.8;
                    double candidateScore = originScore * synapse.Strength * carry * hopFactor;

                    if (candidateScore < 0.15)
                    {
                        continue;
                    }

                    if (!scores.TryGetValue(neighbourId, out double current) || candidateScore > current)
                    {
                        scores[neighbourId] = candidateScore;
                        activationPaths[neighbourId] = new MemoryActivationPath(nodeId, synapse.Id);
                        if (!usedSynapses.Contains(synapse))
                        {
                            usedSynapses.Add(synapse);
                        }

                        nextFrontier.Add(neighbourId);
                    }
                }
            }

            if (nextFrontier.Count == 0)
            {
                break;
            }

            frontier = nextFrontier.Distinct(StringComparer.Ordinal).ToList();
        }

        List<(MemoryNode Node, double Score)> activated = new();
        foreach (KeyValuePair<string, double> pair in scores)
        {
            MemoryNode? node = _graph.FindById(pair.Key);
            if (node is not null)
            {
                activated.Add((node, pair.Value));
            }
        }

        activated.Sort((left, right) => right.Score.CompareTo(left.Score));

        // Remembering is also learning: the pathways she just walked get stronger.
        foreach ((MemoryNode node, _) in activated.Take(8))
        {
            await _graph.TouchNodeAsync(node.Id, cancellationToken).ConfigureAwait(false);
        }

        foreach (MemorySynapse synapse in usedSynapses.Take(12))
        {
            await _graph.TouchSynapseAsync(synapse, cancellationToken).ConfigureAwait(false);
        }

        return new ActivationResult
        {
            Activated = activated,
            ActivatedSynapses = usedSynapses,
            Seeds = matches.Select(match => match.Node).ToList(),
            ActivationPaths = activationPaths
        };
    }

    /// <summary>
    /// Rewrites an activation result as the short plain-English block that goes into
    /// the system prompt. Warnings come first: a 4B model reads the top of a block
    /// most reliably.
    /// </summary>
    public string RenderIntuition(ActivationResult activation)
    {
        if (activation.IsEmpty)
        {
            return string.Empty;
        }

        int characterBudget = Math.Max(400, _settings.Current.Memory.IntuitionCharacterBudget);
        StringBuilder builder = new();
        HashSet<string> writtenLines = new(StringComparer.OrdinalIgnoreCase);

        List<string> cautionLines = new();
        List<string> preferenceLines = new();
        List<string> factLines = new();
        List<string> episodeLines = new();

        foreach ((MemoryNode node, double score) in activation.Activated)
        {
            foreach (MemorySynapse synapse in _graph.OutgoingOf(node.Id)
                         .OrderByDescending(edge => edge.Provenance.Equals("user-correction", StringComparison.OrdinalIgnoreCase)))
            {
                MemoryNode? target = _graph.FindById(synapse.ToNodeId);
                if (target is null || synapse.Strength < 0.25)
                {
                    continue;
                }

                if (synapse.Type == SynapseType.Supersedes ||
                    (!synapse.Provenance.Equals("user-correction", StringComparison.OrdinalIgnoreCase) &&
                     IsSupersededByCorrection(node, synapse)))
                {
                    // Keep old facts and the audit edge in the graph, but do not make
                    // the replaced value part of the model's current working context.
                    continue;
                }

                if (synapse.Provenance.Equals("user-correction", StringComparison.OrdinalIgnoreCase))
                {
                    // Corrections are deliberately preserved rather than silently
                    // mutating history. Marking them in the prompt makes their
                    // precedence explicit to the model and auditable to the user.
                    factLines.Insert(0, $"CORRECTION: {node.Title} {SynapseTypes.ToWords(synapse.Type)} {target.Title}.");
                    continue;
                }

                switch (synapse.Polarity)
                {
                    case SynapsePolarity.Inhibitory:
                        cautionLines.Add($"CAUTION: {node.Title} {SynapseTypes.ToWords(synapse.Type)} {target.Title}.");
                        break;
                    case SynapsePolarity.Affective:
                        preferenceLines.Add($"{node.Title} {SynapseTypes.ToWords(synapse.Type)} {target.Title}.");
                        break;
                    case SynapsePolarity.Inward:
                        factLines.Add($"{node.Title} {SynapseTypes.ToWords(synapse.Type)} {target.Title}.");
                        break;
                    case SynapsePolarity.Outward:
                        factLines.Add($"{node.Title} {SynapseTypes.ToWords(synapse.Type)} {target.Title}.");
                        break;
                }
            }

            if (node.Layer == MemoryLayer.Episodic && !string.IsNullOrWhiteSpace(node.Content))
            {
                episodeLines.Add($"{node.Title}: {Trim(node.Content, 160)}");
            }
            else if (!string.IsNullOrWhiteSpace(node.Content))
            {
                factLines.Add($"{node.Title}: {Trim(node.Content, 160)}");
            }
            else if (score > 0.35)
            {
                factLines.Add($"{node.Title}");
            }
        }

        builder.AppendLine("[ACTIVE INTUITION]");

        foreach (string line in cautionLines.Concat(preferenceLines).Concat(factLines).Concat(episodeLines))
        {
            if (builder.Length + line.Length > characterBudget)
            {
                break;
            }

            if (writtenLines.Add(line))
            {
                builder.Append("- ").AppendLine(line);
            }
        }

        string rendered = builder.ToString().TrimEnd();
        return rendered == "[ACTIVE INTUITION]" ? string.Empty : rendered;
    }

    /// <summary>Stores one fact, creating both endpoints and the typed link between them.</summary>
    public async Task<RecalledMemory> RememberAsync(RememberRequest request, CancellationToken cancellationToken)
    {
        MemoryLayer layer = (MemoryLayer)Math.Clamp(request.Layer, 0, 3);
        MemoryNode subject = await _graph
            .UpsertNodeAsync(request.Subject, request.Content, layer, request.Provenance, cancellationToken: cancellationToken)
            .ConfigureAwait(false);
        MemoryNode target = await _graph
            .UpsertNodeAsync(request.Target, string.Empty, layer, request.Provenance, cancellationToken: cancellationToken)
            .ConfigureAwait(false);

        SynapseType type = SynapseTypes.Parse(request.Relation);
        MemorySynapse synapse = await _graph
            .LinkAsync(subject.Id, target.Id, type, 0.7, request.Provenance, cancellationToken)
            .ConfigureAwait(false);

        if (!string.IsNullOrWhiteSpace(request.Supersedes))
        {
            MemoryNode formerTarget = await _graph
                .UpsertNodeAsync(request.Supersedes.Trim(), string.Empty, layer, "user-correction",
                    cancellationToken: cancellationToken)
                .ConfigureAwait(false);
            if (!string.Equals(formerTarget.Id, target.Id, StringComparison.Ordinal))
            {
                await _graph.LinkAsync(target.Id, formerTarget.Id, SynapseType.Supersedes, 1.0,
                    "user-correction", cancellationToken).ConfigureAwait(false);
            }
        }

        _events.Publish(new MemoryChangedEvent { NodeId = subject.Id, Title = subject.Title, Change = "linked" });
        CompanionLog.Info("Memory", $"Learned: {subject.Title} {SynapseTypes.ToWords(type)} {target.Title} " +
                                    $"(strength {synapse.Strength:0.00}).");
        IrisEvent.Log("Memory", "memory.linked", LogLevel.Info,
            data: new Dictionary<string, string?>
            {
                ["subject"] = subject.Title,
                ["relation"] = type.ToString(),
                ["target"] = target.Title,
                ["layer"] = layer.ToString(),
                ["strength"] = synapse.Strength.ToString("0.00")
            });

        return ToRecalled(subject);
    }

    private bool IsSupersededByCorrection(MemoryNode subject, MemorySynapse assertion)
    {
        foreach (MemorySynapse correction in _graph.OutgoingOf(subject.Id)
                     .Where(edge => edge.Provenance.Equals("user-correction", StringComparison.OrdinalIgnoreCase)))
        {
            if (_graph.OutgoingOf(correction.ToNodeId).Any(edge =>
                    edge.Type == SynapseType.Supersedes &&
                    string.Equals(edge.ToNodeId, assertion.ToNodeId, StringComparison.Ordinal)))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Simple top-N recall for modules that need a fact, not a full intuition block.</summary>
    public Task<IReadOnlyList<RecalledMemory>> RecallAsync(string query, int maximumResults,
        CancellationToken cancellationToken)
    {
        IReadOnlyList<(MemoryNode Node, double Score)> matches =
            _graph.SearchNodes(query, Math.Max(1, maximumResults));

        IReadOnlyList<RecalledMemory> results = matches
            .Select(match => ToRecalled(match.Node))
            .ToList();

        return Task.FromResult(results);
    }

    /// <summary>Forgets an exact node title. Similar-looking memories are never guessed
    /// at here: the user can inspect them in Memory before naming the one to remove.</summary>
    public async Task<MemoryForgetResult> ForgetAsync(string title, CancellationToken cancellationToken)
    {
        MemoryNode? node = _graph.FindByTitle(title);
        if (node is null)
        {
            return new MemoryForgetResult
            {
                Succeeded = false,
                Message = $"I could not find an exact memory named '{title}'. Open Memory to inspect its name first."
            };
        }

        if (node.Pinned)
        {
            return new MemoryForgetResult
            {
                Succeeded = false,
                Message = $"'{node.Title}' is pinned and cannot be forgotten until it is unpinned in Memory."
            };
        }

        await _graph.ForgetNodeAsync(node.Id, cancellationToken).ConfigureAwait(false);
        _events.Publish(new MemoryChangedEvent { NodeId = node.Id, Title = node.Title, Change = "forgotten" });
        IrisEvent.Log("Memory", "memory.forgotten", LogLevel.Info,
            data: new Dictionary<string, string?> { ["title"] = node.Title, ["source"] = "user" });
        return new MemoryForgetResult { Succeeded = true, Message = $"Forgot '{node.Title}'." };
    }

    /// <summary>Records something that happened, so she can refer to it later.</summary>
    public async Task<RecalledMemory> RecordEpisodeAsync(string summary, string provenance = "conversation",
        CancellationToken cancellationToken = default)
    {
        string title = summary.Length > 60 ? summary[..60].TrimEnd() + "..." : summary;
        MemoryNode node = await _graph
            .UpsertNodeAsync(title, summary, MemoryLayer.Episodic, provenance, cancellationToken: cancellationToken)
            .ConfigureAwait(false);
        _events.Publish(new MemoryChangedEvent { NodeId = node.Id, Title = node.Title, Change = "created" });
        IrisEvent.Log("Memory", "memory.episode", LogLevel.Trace,
            data: new Dictionary<string, string?>
            {
                ["title"] = node.Title,
                ["chars"] = summary.Length.ToString(),
                ["provenance"] = provenance
            });
        return ToRecalled(node);
    }

    /// <summary>Publishes the recall report that becomes the memory chips in the chat.</summary>
    public void PublishRecall(string turnId, ActivationResult activation, string intuitionText)
    {
        List<RecalledMemory> memories = activation.Activated.Take(6).Select(pair =>
        {
            bool directMatch = activation.Seeds.Any(seed => seed.Id == pair.Node.Id);
            activation.ActivationPaths.TryGetValue(pair.Node.Id, out MemoryActivationPath? path);
            MemoryNode? related = path is null ? null : _graph.FindById(path.ParentNodeId);
            MemorySynapse? relationship = path is null
                ? null
                : activation.ActivatedSynapses.FirstOrDefault(synapse => synapse.Id == path.SynapseId);
            RecalledMemory memory = ToRecalled(pair.Node);
            return new RecalledMemory
            {
                NodeId = memory.NodeId,
                Title = memory.Title,
                Layer = memory.Layer,
                Strength = memory.Strength,
                Provenance = memory.Provenance,
                ActivationScore = pair.Score,
                IsDirectMatch = directMatch,
                RelatedMemoryTitle = related?.Title ?? string.Empty,
                Relationship = relationship is null ? string.Empty : SynapseTypes.ToWords(relationship.Type)
            };
        }).ToList();

        string evidence = System.Text.Json.JsonSerializer.Serialize(memories.Select(memory => new
        {
            node_id = memory.NodeId,
            layer = memory.Layer,
            direct_match = memory.IsDirectMatch,
            activation = memory.ActivationScore
        }));
        using IDisposable correlationScope = IrisEvent.BeginCorrelationScope(turnId);
        IrisEvent.Log("Memory", "memory.recall", LogLevel.Debug,
            data: new Dictionary<string, string?>
            {
                ["count"] = memories.Count.ToString(System.Globalization.CultureInfo.InvariantCulture),
                ["evidence"] = evidence
            });
        _events.Publish(new MemoryRecallEvent(turnId, memories, intuitionText));
    }

    /// <summary>Converts a node to the small shape the interface needs.</summary>
    public static RecalledMemory ToRecalled(MemoryNode node) => new()
    {
        NodeId = node.Id,
        Title = node.Title,
        Layer = (int)node.Layer,
        Strength = node.Strength,
        Provenance = node.Provenance
    };

    private static string Trim(string text, int maximumLength) =>
        text.Length <= maximumLength ? text : text[..maximumLength].TrimEnd() + "...";
}
