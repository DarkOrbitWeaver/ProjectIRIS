using System.Globalization;
using System.Text.Json;
using CompanionOS.Core.Diagnostics;
using Microsoft.Data.Sqlite;

namespace CompanionOS.Core.Memory;

/// <summary>
/// Reading the graph: snapshots for the interface, search for the router, neighbours for
/// spreading activation, decay and consolidation for maintenance.
///
/// Why this is split from the writing half: mutation is where durability lives (transactions,
/// WAL, clamps) and reading is where the algorithms live (similarity, walks, ranking). Keeping
/// them in separate parts of the same class makes each half readable at a glance.
/// </summary>
public sealed partial class MemoryGraph : IAsyncDisposable
{
    /// <summary>
    /// Lets unused strength fade. Runs at most once per day so the numbers stay
    /// meaningful. Pinned nodes (her core self) are exempt.
    /// </summary>
    public async Task ApplyDecayIfDueAsync(double dailyDecay, CancellationToken cancellationToken = default)
    {
        if (_connection is null || dailyDecay >= 1.0 || dailyDecay <= 0)
        {
            return;
        }

        string? lastRunText = await ReadMetaAsync("last_decay", cancellationToken).ConfigureAwait(false);
        if (DateTimeOffset.TryParse(lastRunText, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out DateTimeOffset lastRun)
            && lastRun > DateTimeOffset.Now.AddHours(-23))
        {
            return;
        }

        // One transaction, one pass. The old per-node persist loop committed once per
        // memory on the boot path — tens of thousands of fsyncs on a large graph while
        // every memory write queued behind the whole decay (2026-10-27 audit). Replaced
        // with two bulk UPDATEs and the meta stamp inside a single transaction; the
        // in-memory cache is mirrored afterwards, only once the store has accepted it.
        List<MemoryNode> nodes = SnapshotNodes().ToList();
        List<MemorySynapse> synapses = SnapshotSynapses().ToList();

        await _writeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using SqliteTransaction transaction = (SqliteTransaction)
                await _connection!.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
            await using SqliteCommand command = _connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = "UPDATE nodes SET strength = max(0.05, strength * $dailyDecay) WHERE pinned = 0;";
            command.Parameters.AddWithValue("$dailyDecay", dailyDecay);
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);

            command.Parameters.Clear();
            command.CommandText =
                "UPDATE synapses SET strength = max(0.05, strength * $dailyDecay) WHERE strength > 0.05;";
            command.Parameters.AddWithValue("$dailyDecay", dailyDecay);
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);

            command.Parameters.Clear();
            command.CommandText =
                """
                INSERT INTO meta (key, value) VALUES ('last_decay', $stamp)
                ON CONFLICT(key) DO UPDATE SET value = excluded.value;
                """;
            command.Parameters.AddWithValue("$stamp", DateTimeOffset.Now.ToString("O"));
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);

            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _writeGate.Release();
        }

        foreach (MemoryNode node in nodes)
        {
            if (!node.Pinned)
            {
                node.Strength = Math.Max(0.05, node.Strength * dailyDecay);
            }
        }

        foreach (MemorySynapse synapse in synapses)
        {
            synapse.Strength = Math.Max(0.05, synapse.Strength * dailyDecay);
        }

        CompanionLog.Info("Memory", "Applied daily decay to unused memories.");
    }

    /// <summary>
    /// The rest pass. Merges near-duplicate nodes of the same layer and removes
    /// links that have faded to nothing. Runs while the user is away.
    /// </summary>
    public async Task<int> ConsolidateAsync(CancellationToken cancellationToken = default)
    {
        int consolidated = await ConsolidateCoreAsync(cancellationToken).ConfigureAwait(false);
        IrisEvent.Log("Memory", "consolidation.done", LogLevel.Info,
            durationMs: null, data: new Dictionary<string, string?> { ["count"] = consolidated.ToString() });
        return consolidated;
    }

    /// <summary>Merge core; split out so the wrapper can record the outcome cleanly.</summary>
    private async Task<int> ConsolidateCoreAsync(CancellationToken cancellationToken)
    {
        if (_connection is null)
        {
            return 0;
        }

        List<MemoryNode> nodes = SnapshotNodes()
            .Where(node => !node.Pinned)
            .OrderByDescending(node => node.Strength)
            .ThenByDescending(node => node.UseCount)
            .ToList();

        int mergedCount = 0;

        for (int outer = 0; outer < nodes.Count; outer++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            // Live re-fetch of both ends. The list above is a stale snapshot: a node
            // this pass already merged away used to be processed anyway, and writing
            // its stale copy upserted deleted rows back into existence with doubled
            // content — permanent long-term corruption on exactly the runs this pass
            // exists for. Absent nodes skip; the merge edits the live object.
            MemoryNode? keep = FindById(nodes[outer].Id);
            if (keep is null || keep.Pinned)
            {
                continue;
            }

            for (int inner = outer + 1; inner < nodes.Count; inner++)
            {
                MemoryNode? candidate = FindById(nodes[inner].Id);
                if (candidate is null || candidate.Layer != keep.Layer)
                {
                    continue;
                }

                double similarity = TextVectorizer.Cosine(keep.Vector, candidate.Vector);
                bool titlesMatch = keep.NormalizedTitle.Contains(candidate.NormalizedTitle, StringComparison.Ordinal) ||
                                   candidate.NormalizedTitle.Contains(keep.NormalizedTitle, StringComparison.Ordinal);

                if (similarity < 0.93 || (!titlesMatch && similarity < 0.985))
                {
                    continue;
                }

                // Move every link from the duplicate onto the survivor, then forget it.
                foreach (MemorySynapse outgoing in OutgoingOf(candidate.Id))
                {
                    if (FindById(outgoing.ToNodeId) is not null && outgoing.ToNodeId != keep.Id)
                    {
                        await LinkAsync(keep.Id, outgoing.ToNodeId, outgoing.Type, outgoing.Strength,
                            "consolidation:" + candidate.Title, cancellationToken).ConfigureAwait(false);
                    }
                }

                if (!string.IsNullOrWhiteSpace(candidate.Content))
                {
                    keep.Content = string.IsNullOrWhiteSpace(keep.Content)
                        ? candidate.Content
                        : keep.Content + " " + candidate.Content;
                    keep.Vector = TextVectorizer.Embed(keep.Title + " " + keep.Content);
                    await PersistNodeAsync(keep, cancellationToken).ConfigureAwait(false);
                }

                await ForgetNodeAsync(candidate.Id, cancellationToken).ConfigureAwait(false);
                mergedCount++;
            }
        }

        if (mergedCount > 0)
        {
            CompanionLog.Info("Memory", $"Consolidation merged {mergedCount} duplicate memories.");
            NotifyNodeChanged(new MemoryNode
            {
                Id = "consolidation",
                Title = "Consolidation",
                NormalizedTitle = "consolidation"
            }, "consolidated");
        }

        return mergedCount;
    }

    /// <summary>Writes the whole brain to a JSON file the user can keep or move.</summary>
    public async Task ExportAsync(string destinationPath, CancellationToken cancellationToken = default)
    {
        object payload = new
        {
            exportedAt = DateTimeOffset.Now,
            nodes = SnapshotNodes().Select(node => new
            {
                node.Id, node.Title, node.Content, Layer = (int)node.Layer, node.Strength,
                node.CreatedAt, node.UpdatedAt, node.UseCount, node.Provenance, node.Pinned
            }),
            synapses = SnapshotSynapses().Select(synapse => new
            {
                synapse.Id, synapse.FromNodeId, synapse.ToNodeId,
                Type = synapse.Type.ToString(), synapse.Strength, synapse.Provenance
            })
        };

        await File.WriteAllTextAsync(destinationPath,
            JsonSerializer.Serialize(payload, new JsonSerializerOptions { WriteIndented = true }),
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>True when the cache knows this identifier.</summary>
    public bool ContainsNode(string nodeId)
    {
        lock (_cacheLock)
        {
            return _nodesById.ContainsKey(nodeId);
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_connection is not null)
        {
            await _connection.CloseAsync().ConfigureAwait(false);
            await _connection.DisposeAsync().ConfigureAwait(false);
            _connection = null;
        }

        _writeGate.Dispose();
    }

    private static void AddToIndex(Dictionary<string, List<MemorySynapse>> index, string key, MemorySynapse synapse)
    {
        if (!index.TryGetValue(key, out List<MemorySynapse>? list))
        {
            list = new List<MemorySynapse>();
            index[key] = list;
        }

        list.Add(synapse);
    }

    private void NotifyNodeChanged(MemoryNode node, string change)
    {
        try
        {
            NodeChanged?.Invoke(node, change);
        }
        catch (Exception exception)
        {
            CompanionLog.Warning("Memory", $"A graph subscriber failed: {exception.Message}");
        }
    }

    private async Task CreateSchemaAsync(CancellationToken cancellationToken)
    {
        const string schema = """
            CREATE TABLE IF NOT EXISTS nodes (
                id TEXT PRIMARY KEY,
                title TEXT NOT NULL,
                normalized_title TEXT NOT NULL UNIQUE,
                content TEXT NOT NULL DEFAULT '',
                layer INTEGER NOT NULL DEFAULT 2,
                strength REAL NOT NULL DEFAULT 0.75,
                created_at TEXT NOT NULL,
                updated_at TEXT NOT NULL,
                last_used_at TEXT NULL,
                use_count INTEGER NOT NULL DEFAULT 0,
                vector BLOB NULL,
                provenance TEXT NOT NULL DEFAULT '',
                pinned INTEGER NOT NULL DEFAULT 0
            );

            CREATE TABLE IF NOT EXISTS synapses (
                id TEXT PRIMARY KEY,
                from_node TEXT NOT NULL,
                to_node TEXT NOT NULL,
                type TEXT NOT NULL,
                strength REAL NOT NULL DEFAULT 0.6,
                created_at TEXT NOT NULL,
                last_used_at TEXT NULL,
                use_count INTEGER NOT NULL DEFAULT 0,
                provenance TEXT NOT NULL DEFAULT '',
                UNIQUE (from_node, to_node, type)
            );

            CREATE INDEX IF NOT EXISTS idx_synapses_from ON synapses (from_node);
            CREATE INDEX IF NOT EXISTS idx_synapses_to ON synapses (to_node);

            CREATE TABLE IF NOT EXISTS meta (
                key TEXT PRIMARY KEY,
                value TEXT NOT NULL
            );
            """;

        await ExecuteNonQueryAsync(schema, cancellationToken).ConfigureAwait(false);
    }

    private async Task LoadCacheAsync(CancellationToken cancellationToken)
    {
        List<MemoryNode> nodes = new();
        List<MemorySynapse> synapses = new();

        await _writeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using (SqliteCommand command = _connection!.CreateCommand())
            {
                command.CommandText = "SELECT id, title, normalized_title, content, layer, strength, created_at, updated_at, last_used_at, use_count, vector, provenance, pinned FROM nodes;";
                await using SqliteDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
                while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                {
                    nodes.Add(new MemoryNode
                    {
                        Id = reader.GetString(0),
                        Title = reader.GetString(1),
                        NormalizedTitle = reader.GetString(2),
                        Content = reader.GetString(3),
                        Layer = (MemoryLayer)reader.GetInt32(4),
                        Strength = reader.GetDouble(5),
                        CreatedAt = ParseTime(reader.GetString(6)),
                        UpdatedAt = ParseTime(reader.GetString(7)),
                        LastUsedAt = reader.IsDBNull(8) ? null : ParseTime(reader.GetString(8)),
                        UseCount = reader.GetInt32(9),
                        Vector = TextVectorizer.FromBytes(reader.IsDBNull(10) ? null : (byte[])reader["vector"]),
                        Provenance = reader.GetString(11),
                        Pinned = reader.GetInt32(12) != 0
                    });
                }
            }

            await using (SqliteCommand command = _connection!.CreateCommand())
            {
                command.CommandText = "SELECT id, from_node, to_node, type, strength, created_at, last_used_at, use_count, provenance FROM synapses;";
                await using SqliteDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
                while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                {
                    synapses.Add(new MemorySynapse
                    {
                        Id = reader.GetString(0),
                        FromNodeId = reader.GetString(1),
                        ToNodeId = reader.GetString(2),
                        Type = SynapseTypes.Parse(reader.GetString(3)),
                        Strength = reader.GetDouble(4),
                        CreatedAt = ParseTime(reader.GetString(5)),
                        LastUsedAt = reader.IsDBNull(6) ? null : ParseTime(reader.GetString(6)),
                        UseCount = reader.GetInt32(7),
                        Provenance = reader.GetString(8)
                    });
                }
            }
        }
        finally
        {
            _writeGate.Release();
        }

        lock (_cacheLock)
        {
            _nodesById.Clear();
            _nodeIdByNormalizedTitle.Clear();
            _outgoing.Clear();
            _incoming.Clear();

            foreach (MemoryNode node in nodes)
            {
                _nodesById[node.Id] = node;
                _nodeIdByNormalizedTitle[node.NormalizedTitle] = node.Id;
            }

            foreach (MemorySynapse synapse in synapses)
            {
                if (!_nodesById.ContainsKey(synapse.FromNodeId) || !_nodesById.ContainsKey(synapse.ToNodeId))
                {
                    continue;
                }

                AddToIndex(_outgoing, synapse.FromNodeId, synapse);
                AddToIndex(_incoming, synapse.ToNodeId, synapse);
            }
        }
    }

    private async Task PersistNodeAsync(MemoryNode node, CancellationToken cancellationToken)
    {
        await _writeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using SqliteCommand command = _connection!.CreateCommand();
            command.CommandText = """
                INSERT INTO nodes (id, title, normalized_title, content, layer, strength, created_at, updated_at,
                                   last_used_at, use_count, vector, provenance, pinned)
                VALUES ($id, $title, $normalized, $content, $layer, $strength, $created, $updated,
                        $lastUsed, $useCount, $vector, $provenance, $pinned)
                ON CONFLICT(id) DO UPDATE SET
                    title = excluded.title,
                    normalized_title = excluded.normalized_title,
                    content = excluded.content,
                    layer = excluded.layer,
                    strength = excluded.strength,
                    updated_at = excluded.updated_at,
                    last_used_at = excluded.last_used_at,
                    use_count = excluded.use_count,
                    vector = excluded.vector,
                    provenance = excluded.provenance,
                    pinned = excluded.pinned;
                """;
            command.Parameters.AddWithValue("$id", node.Id);
            command.Parameters.AddWithValue("$title", node.Title);
            command.Parameters.AddWithValue("$normalized", node.NormalizedTitle);
            command.Parameters.AddWithValue("$content", node.Content);
            command.Parameters.AddWithValue("$layer", (int)node.Layer);
            command.Parameters.AddWithValue("$strength", node.Strength);
            command.Parameters.AddWithValue("$created", ToText(node.CreatedAt));
            command.Parameters.AddWithValue("$updated", ToText(node.UpdatedAt));
            command.Parameters.AddWithValue("$lastUsed", node.LastUsedAt is null ? DBNull.Value : ToText(node.LastUsedAt.Value));
            command.Parameters.AddWithValue("$useCount", node.UseCount);
            command.Parameters.AddWithValue("$vector", TextVectorizer.ToBytes(node.Vector));
            command.Parameters.AddWithValue("$provenance", node.Provenance);
            command.Parameters.AddWithValue("$pinned", node.Pinned ? 1 : 0);

            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _writeGate.Release();
        }
    }

    private async Task PersistSynapseAsync(MemorySynapse synapse, bool isNew, CancellationToken cancellationToken)
    {
        const string upsertSql = """
            INSERT INTO synapses (id, from_node, to_node, type, strength, created_at, last_used_at, use_count, provenance)
            VALUES ($id, $from, $to, $type, $strength, $created, $lastUsed, $useCount, $provenance)
            ON CONFLICT(from_node, to_node, type) DO UPDATE SET strength = excluded.strength;
            """;
        const string updateSql = "UPDATE synapses SET strength = $strength, last_used_at = $lastUsed, use_count = $useCount WHERE id = $id;";

        await _writeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using SqliteCommand command = _connection!.CreateCommand();
            command.CommandText = isNew ? upsertSql : updateSql;

            command.Parameters.AddWithValue("$id", synapse.Id);
            command.Parameters.AddWithValue("$from", synapse.FromNodeId);
            command.Parameters.AddWithValue("$to", synapse.ToNodeId);
            command.Parameters.AddWithValue("$type", synapse.Type.ToString());
            command.Parameters.AddWithValue("$strength", synapse.Strength);
            command.Parameters.AddWithValue("$created", ToText(synapse.CreatedAt));
            command.Parameters.AddWithValue("$lastUsed",
                synapse.LastUsedAt is null ? DBNull.Value : ToText(synapse.LastUsedAt.Value));
            command.Parameters.AddWithValue("$useCount", synapse.UseCount);
            command.Parameters.AddWithValue("$provenance", synapse.Provenance);

            int touched = await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            if (!isNew && touched == 0)
            {
                // The row lives in the cache but has gone from the store (a forget or a
                // crash landed in between). A silent zero-row update would drop this
                // bookkeeping forever, so re(insert it and say so instead of pretending
                // the write landed.
                command.CommandText = upsertSql;
                await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
                CompanionLog.Info("Memory",
                    $"Synapse {synapse.Id} was missing from the store when its update ran; re-inserted it.");
            }
        }
        finally
        {
            _writeGate.Release();
        }
    }

    private async Task<string?> ReadMetaAsync(string key, CancellationToken cancellationToken)
    {
        await _writeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using SqliteCommand command = _connection!.CreateCommand();
            command.CommandText = "SELECT value FROM meta WHERE key = $key;";
            command.Parameters.AddWithValue("$key", key);
            object? value = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
            return value as string;
        }
        finally
        {
            _writeGate.Release();
        }
    }

    private async Task WriteMetaAsync(string key, string value, CancellationToken cancellationToken)
    {
        await _writeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using SqliteCommand command = _connection!.CreateCommand();
            command.CommandText = "INSERT INTO meta (key, value) VALUES ($key, $value) ON CONFLICT(key) DO UPDATE SET value = excluded.value;";
            command.Parameters.AddWithValue("$key", key);
            command.Parameters.AddWithValue("$value", value);
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _writeGate.Release();
        }
    }

    private async Task ExecuteNonQueryAsync(string sql, CancellationToken cancellationToken)
    {
        await _writeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using SqliteCommand command = _connection!.CreateCommand();
            command.CommandText = sql;
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _writeGate.Release();
        }
    }

    private static string ToText(DateTimeOffset value) =>
        value.ToString("O", CultureInfo.InvariantCulture);

    private static DateTimeOffset ParseTime(string? text) =>
        DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out DateTimeOffset parsed)
            ? parsed
            : DateTimeOffset.Now;
}
