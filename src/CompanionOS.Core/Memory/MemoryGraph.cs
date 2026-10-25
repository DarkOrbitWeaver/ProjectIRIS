using System.Globalization;
using System.Text.Json;
using CompanionOS.Core.Diagnostics;
using Microsoft.Data.Sqlite;

namespace CompanionOS.Core.Memory;

/// <summary>
/// Her long-term memory: a typed graph of nodes and synapses stored in one SQLite file.
///
/// Three promises this class keeps:
///
/// 1. NOTHING IS LOST. The database runs in WAL mode with synchronous=FULL, so a
///    pulled power cable loses at most the statement that was in flight, never the
///    file. Every write is also a single transaction.
///
/// 2. READS ARE FAST. The whole graph is mirrored in memory (a few megabytes even
///    at tens of thousands of nodes), so recall never waits on the disk. The
///    database is the durable copy, the dictionary is the working copy.
///
/// 3. MEMORY BEHAVES LIKE MEMORY. Links have a strength that fades with disuse and
///    grows with use, duplicates are merged while she rests, and every link keeps
///    the reason it exists so the inspector can explain it to you.
/// </summary>
public sealed partial class MemoryGraph : IAsyncDisposable
{
    private readonly string _databasePath;
    private readonly SemaphoreSlim _writeGate = new(1, 1);
    private readonly object _cacheLock = new();
    private readonly Dictionary<string, MemoryNode> _nodesById = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _nodeIdByNormalizedTitle = new(StringComparer.Ordinal);
    private readonly Dictionary<string, List<MemorySynapse>> _outgoing = new(StringComparer.Ordinal);
    private readonly Dictionary<string, List<MemorySynapse>> _incoming = new(StringComparer.Ordinal);

    private SqliteConnection? _connection;
    private bool _isInitialised;

    public MemoryGraph(string databasePath) => _databasePath = databasePath;

    /// <summary>Raised after any node or synapse changes, so the graph view can redraw.</summary>
    public event Action<MemoryNode, string>? NodeChanged;

    /// <summary>Raised when a node is removed.</summary>
    public event Action<string>? NodeForgotten;

    /// <summary>Total nodes currently held.</summary>
    public int NodeCount
    {
        get { lock (_cacheLock) { return _nodesById.Count; } }
    }

    /// <summary>Total synapses currently held.</summary>
    public int SynapseCount
    {
        get
        {
            lock (_cacheLock)
            {
                return _outgoing.Values.Sum(list => list.Count);
            }
        }
    }

    /// <summary>A stable snapshot of every node, safe to iterate while she writes.</summary>
    public IReadOnlyList<MemoryNode> SnapshotNodes()
    {
        lock (_cacheLock)
        {
            return _nodesById.Values.ToList();
        }
    }

    /// <summary>A stable snapshot of every synapse, safe to iterate while she writes.</summary>
    public IReadOnlyList<MemorySynapse> SnapshotSynapses()
    {
        lock (_cacheLock)
        {
            return _outgoing.Values.SelectMany(list => list).ToList();
        }
    }

    /// <summary>Opens the file, creates the schema when needed, and warms the cache.</summary>
    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        if (_isInitialised)
        {
            return;
        }

        Directory.CreateDirectory(Path.GetDirectoryName(_databasePath)!);
        TryCreateDailyBackup();

        SqliteConnectionStringBuilder builder = new()
        {
            DataSource = _databasePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Default
        };

        _connection = new SqliteConnection(builder.ToString());
        await _connection.OpenAsync(cancellationToken).ConfigureAwait(false);

        // Durability first: WAL survives power loss, FULL makes every commit hit the disk.
        await ExecuteNonQueryAsync("PRAGMA journal_mode=WAL;", cancellationToken).ConfigureAwait(false);
        await ExecuteNonQueryAsync("PRAGMA synchronous=FULL;", cancellationToken).ConfigureAwait(false);
        await ExecuteNonQueryAsync("PRAGMA busy_timeout=5000;", cancellationToken).ConfigureAwait(false);
        await ExecuteNonQueryAsync("PRAGMA wal_autocheckpoint=256;", cancellationToken).ConfigureAwait(false);

        await CreateSchemaAsync(cancellationToken).ConfigureAwait(false);
        await LoadCacheAsync(cancellationToken).ConfigureAwait(false);

        _isInitialised = true;
        CompanionLog.Info("Memory", $"Brain opened with {NodeCount} nodes and {SynapseCount} synapses.");
    }

    /// <summary>
    /// One snapshot of the brain per day, at most: the day's first boot copies the
    /// database and its WAL sidecar before this process opens the file, into
    /// <c>memory\backups</c>, keeping the newest three plus today's. She must never be
    /// the one who loses everything she remembers, and a backup that requires a command
    /// the user does not remember is no backup at all.
    /// </summary>
    private void TryCreateDailyBackup()
    {
        try
        {
            if (!File.Exists(_databasePath))
            {
                return;
            }

            string backups = Path.Combine(Path.GetDirectoryName(_databasePath)!, "backups");
            Directory.CreateDirectory(backups);

            string todayStamp = DateTime.Now.ToString("yyyyMMdd");
            string target = Path.Combine(backups, $"brain-{todayStamp}.db");

            if (File.Exists(target))
            {
                return;
            }

            // The previous process checkpointed its WAL on the way out, so the main file
            // holds a complete image. Copy the WAL too when one is left behind by a crash.
            File.Copy(_databasePath, target, overwrite: true);
            string wal = _databasePath + "-wal";
            if (File.Exists(wal))
            {
                File.Copy(wal, target + "-wal", overwrite: true);
            }

            // Keep today plus the three newest previous days; delete the rest.
            List<string> stale = Directory.GetFiles(backups, "brain-*.db")
                .Where(file => !file.StartsWith(target, StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(file => file)
                .Skip(3)
                .ToList();
            foreach (string file in stale)
            {
                try
                {
                    File.Delete(file);
                    if (File.Exists(file + "-wal"))
                    {
                        File.Delete(file + "-wal");
                    }
                }
                catch (Exception deleteFailure)
                {
                    CompanionLog.Trace("Memory", $"Old backup could not be deleted: {deleteFailure.Message}");
                }
            }

            CompanionLog.Info("Memory", $"Daily snapshot saved: {Path.GetFileName(target)}");
        }
        catch (Exception failure)
        {
            // A failed backup must never stop her from booting.
            CompanionLog.Trace("Memory", $"Daily snapshot skipped: {failure.Message}");
        }
    }

    /// <summary>Finds a node by its identifier.</summary>
    public MemoryNode? FindById(string? nodeId)
    {
        if (string.IsNullOrWhiteSpace(nodeId))
        {
            return null;
        }

        lock (_cacheLock)
        {
            return _nodesById.TryGetValue(nodeId, out MemoryNode? node) ? node : null;
        }
    }

    /// <summary>Finds a node by its human title, ignoring case and spacing.</summary>
    public MemoryNode? FindByTitle(string? title)
    {
        string normalized = TextVectorizer.NormalizeForComparison(title);
        if (normalized.Length == 0)
        {
            return null;
        }

        lock (_cacheLock)
        {
            return _nodeIdByNormalizedTitle.TryGetValue(normalized, out string? nodeId)
                   && _nodesById.TryGetValue(nodeId, out MemoryNode? node)
                ? node
                : null;
        }
    }

    /// <summary>Synapses leaving a node (its capabilities, dangers and preferences).</summary>
    public IReadOnlyList<MemorySynapse> OutgoingOf(string nodeId)
    {
        lock (_cacheLock)
        {
            return _outgoing.TryGetValue(nodeId, out List<MemorySynapse>? list)
                ? list.ToList()
                : Array.Empty<MemorySynapse>();
        }
    }

    /// <summary>Synapses arriving at a node (what points at it).</summary>
    public IReadOnlyList<MemorySynapse> IncomingOf(string nodeId)
    {
        lock (_cacheLock)
        {
            return _incoming.TryGetValue(nodeId, out List<MemorySynapse>? list)
                ? list.ToList()
                : Array.Empty<MemorySynapse>();
        }
    }

    /// <summary>
    /// Creates a node, or returns the existing one with the same title.
    /// Existing nodes are strengthened slightly, which is how repeated mentions
    /// make a memory more important.
    /// </summary>
    public async Task<MemoryNode> UpsertNodeAsync(string title, string content, MemoryLayer layer,
        string provenance, bool pinned = false, CancellationToken cancellationToken = default)
    {
        string displayTitle = TextVectorizer.ToDisplayTitle(title);
        string normalized = TextVectorizer.NormalizeForComparison(displayTitle);

        if (normalized.Length == 0)
        {
            throw new ArgumentException("A memory needs a title.", nameof(title));
        }

        MemoryNode? existing = FindByTitle(displayTitle);
        if (existing is not null)
        {
            bool contentGrew = !string.IsNullOrWhiteSpace(content) &&
                               !existing.Content.Contains(content, StringComparison.OrdinalIgnoreCase);

            existing.Strength = Math.Min(1.0, existing.Strength + 0.02);
            existing.UpdatedAt = DateTimeOffset.Now;
            if (contentGrew)
            {
                existing.Content = string.IsNullOrWhiteSpace(existing.Content)
                    ? content
                    : existing.Content + " " + content;
                existing.Vector = TextVectorizer.Embed(existing.Title + " " + existing.Content);
            }

            if (layer < existing.Layer)
            {
                existing.Layer = layer;
            }

            await PersistNodeAsync(existing, cancellationToken).ConfigureAwait(false);
            NotifyNodeChanged(existing, "strengthened");
            return existing;
        }

        MemoryNode node = new()
        {
            Id = Guid.NewGuid().ToString("N"),
            Title = displayTitle,
            NormalizedTitle = normalized,
            Content = content ?? string.Empty,
            Layer = layer,
            Strength = pinned ? 1.0 : 0.75,
            Provenance = provenance ?? string.Empty,
            Pinned = pinned,
            Vector = TextVectorizer.Embed(displayTitle + " " + content)
        };

        lock (_cacheLock)
        {
            _nodesById[node.Id] = node;
            _nodeIdByNormalizedTitle[node.NormalizedTitle] = node.Id;
        }

        await PersistNodeAsync(node, cancellationToken).ConfigureAwait(false);
        NotifyNodeChanged(node, "created");
        return node;
    }

    /// <summary>Creates or strengthens a typed link between two nodes.</summary>
    public async Task<MemorySynapse> LinkAsync(string fromNodeId, string toNodeId, SynapseType type,
        double strength, string provenance, CancellationToken cancellationToken = default)
    {
        if (string.Equals(fromNodeId, toNodeId, StringComparison.Ordinal))
        {
            throw new ArgumentException("A memory cannot link to itself.", nameof(toNodeId));
        }

        MemorySynapse? existing;
        lock (_cacheLock)
        {
            existing = _outgoing.TryGetValue(fromNodeId, out List<MemorySynapse>? list)
                ? list.FirstOrDefault(synapse => synapse.ToNodeId == toNodeId && synapse.Type == type)
                : null;
        }

        if (existing is not null)
        {
            existing.Strength = Math.Min(1.0, existing.Strength + Math.Max(0.05, strength * 0.3));
            existing.LastUsedAt = DateTimeOffset.Now;
            existing.UseCount++;
            await PersistSynapseAsync(existing, isNew: false, cancellationToken).ConfigureAwait(false);
            if (FindById(fromNodeId) is { } sourceNode)
            {
                NotifyNodeChanged(sourceNode, "linked");
            }

            return existing;
        }

        MemorySynapse synapse = new()
        {
            Id = Guid.NewGuid().ToString("N"),
            FromNodeId = fromNodeId,
            ToNodeId = toNodeId,
            Type = type,
            Strength = Math.Clamp(strength, 0.1, 1.0),
            Provenance = provenance ?? string.Empty
        };

        lock (_cacheLock)
        {
            AddToIndex(_outgoing, synapse.FromNodeId, synapse);
            AddToIndex(_incoming, synapse.ToNodeId, synapse);
        }

        await PersistSynapseAsync(synapse, isNew: true, cancellationToken).ConfigureAwait(false);

        if (FindById(fromNodeId) is { } changedNode)
        {
            NotifyNodeChanged(changedNode, "linked");
        }

        return synapse;
    }

    /// <summary>Marks a node as just used: it grows stronger and glows in the graph.</summary>
    public async Task TouchNodeAsync(string nodeId, CancellationToken cancellationToken = default)
    {
        MemoryNode? node = FindById(nodeId);
        if (node is null)
        {
            return;
        }

        node.LastUsedAt = DateTimeOffset.Now;
        node.UseCount++;
        node.Strength = Math.Min(1.0, node.Strength + 0.01);
        await PersistNodeAsync(node, cancellationToken).ConfigureAwait(false);
        NotifyNodeChanged(node, "used");
    }

    /// <summary>Marks a synapse as traversed, strengthening the pathway.</summary>
    public async Task TouchSynapseAsync(MemorySynapse synapse, CancellationToken cancellationToken = default)
    {
        synapse.LastUsedAt = DateTimeOffset.Now;
        synapse.UseCount++;
        synapse.Strength = Math.Min(1.0, synapse.Strength + 0.01);
        await PersistSynapseAsync(synapse, isNew: false, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Changes a node's strength by hand (inspector buttons).</summary>
    public async Task AdjustNodeStrengthAsync(string nodeId, double change, CancellationToken cancellationToken = default)
    {
        MemoryNode? node = FindById(nodeId);
        if (node is null)
        {
            return;
        }

        node.Strength = Math.Clamp(node.Strength + change, 0.05, 1.0);
        node.UpdatedAt = DateTimeOffset.Now;
        await PersistNodeAsync(node, cancellationToken).ConfigureAwait(false);
        NotifyNodeChanged(node, change >= 0 ? "strengthened" : "weakened");
    }

    /// <summary>
    /// Pins or unpins a node. Pinned memories never fade and are never merged away —
    /// the way she holds her core self exactly as it was taught.
    /// </summary>
    public async Task SetPinnedAsync(string nodeId, bool pinned,
        CancellationToken cancellationToken = default)
    {
        MemoryNode? node = FindById(nodeId);
        if (node is null || node.Pinned == pinned)
        {
            return;
        }

        node.Pinned = pinned;
        node.UpdatedAt = DateTimeOffset.Now;
        await PersistNodeAsync(node, cancellationToken).ConfigureAwait(false);
        NotifyNodeChanged(node, pinned ? "pinned" : "unpinned");
        CompanionLog.Info("Memory", $"{(pinned ? "Pinned" : "Unpinned")}: {node.Title}.");
        Core.Diagnostics.IrisEvent.Log("Memory", pinned ? "memory.pinned" : "memory.unpinned", LogLevel.Debug,
            data: new Dictionary<string, string?> { ["title"] = node.Title });
    }

    /// <summary>Removes a node and every link that touched it.</summary>
    public async Task ForgetNodeAsync(string nodeId, CancellationToken cancellationToken = default)
    {
        if (FindById(nodeId) is not { } node || node.Pinned)
        {
            return;
        }

        await _writeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using SqliteTransaction transaction = (SqliteTransaction)
                await _connection!.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
            await using (SqliteCommand command = _connection.CreateCommand())
            {
                command.Transaction = transaction;
                command.CommandText = "DELETE FROM synapses WHERE from_node = $id OR to_node = $id;";
                command.Parameters.AddWithValue("$id", nodeId);
                await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }

            await using (SqliteCommand command = _connection.CreateCommand())
            {
                command.Transaction = transaction;
                command.CommandText = "DELETE FROM nodes WHERE id = $id;";
                command.Parameters.AddWithValue("$id", nodeId);
                await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }

            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _writeGate.Release();
        }

        lock (_cacheLock)
        {
            _nodesById.Remove(nodeId);
            _nodeIdByNormalizedTitle.Remove(node.NormalizedTitle);
            _outgoing.Remove(nodeId);
            _incoming.Remove(nodeId);
            foreach (List<MemorySynapse> list in _outgoing.Values)
            {
                list.RemoveAll(synapse => synapse.ToNodeId == nodeId);
            }

            foreach (List<MemorySynapse> list in _incoming.Values)
            {
                list.RemoveAll(synapse => synapse.FromNodeId == nodeId);
            }
        }

        NodeForgotten?.Invoke(nodeId);
    }

    /// <summary>
    /// Finds the nodes that best match a sentence. Combines vector similarity with
    /// exact title hits so names she knows ("Night Drive") always win over vague words.
    /// </summary>
    public IReadOnlyList<(MemoryNode Node, double Score)> SearchNodes(string? query, int maximumResults)
    {
        List<MemoryNode> candidates;
        lock (_cacheLock)
        {
            candidates = _nodesById.Values.ToList();
        }

        if (candidates.Count == 0 || string.IsNullOrWhiteSpace(query))
        {
            return Array.Empty<(MemoryNode, double)>();
        }

        float[] queryVector = TextVectorizer.Embed(query);
        string normalizedQuery = TextVectorizer.NormalizeForComparison(query);
        List<(MemoryNode Node, double Score)> scored = new(candidates.Count);

        foreach (MemoryNode node in candidates)
        {
            double vectorScore = TextVectorizer.Cosine(queryVector, node.Vector);
            double titleBonus = 0;

            if (normalizedQuery.Length > 0 && normalizedQuery.Contains(node.NormalizedTitle, StringComparison.Ordinal))
            {
                titleBonus = 0.45;
            }
            else if (normalizedQuery.Length > 3 && node.NormalizedTitle.Length > 3 &&
                     node.NormalizedTitle.Contains(normalizedQuery, StringComparison.Ordinal))
            {
                // The reversed direction: the title says more than the query asked, so it
                // is related — a weaker bonus than an exact title subsumption.
                titleBonus = 0.3;
            }

            double score = vectorScore + titleBonus;
            if (score > 0.12)
            {
                scored.Add((node, score));
            }
        }

        return scored
            .OrderByDescending(pair => pair.Score)
            .Take(Math.Max(1, maximumResults))
            .ToList();
    }
}
