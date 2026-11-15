using Winche.Core;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using NpgsqlTypes;

namespace Winche.Webhooks;

/// <summary>A delivery: one event for one hook, and how it went.</summary>
public sealed record HookDelivery(Guid Id, string HookId, string HookName, string Source, string Type, string? Path, string Status, int Attempts,
    DateTimeOffset CreatedAt, DateTimeOffset? NextAttemptAt, DateTimeOffset? FinishedAt, int? LastStatus, string? LastResult, int? DurationMs, JsonObject Event);

/// <summary>
/// The queue of deliveries, in the project's database (table winche_hook_deliveries): written when events happen, read
/// by the delivery worker on any server (rows are claimed, so each is sent by one at a time).
/// </summary>
public sealed class HookOutbox([FromKeyedServices(HookOutbox.DataSourceKey)] NpgsqlDataSource db)
{
    public const string DataSourceKey = "winche-webhooks";
    public const string Pending = "pending";
    public const string Delivered = "delivered";
    public const string Failed = "failed";

    internal NpgsqlDataSource Db => db;

    /// <summary>The delivery table, in the order its parts were introduced (<see cref="WincheSchema"/>).</summary>
    internal static readonly string[] Steps =
    [
        """
            CREATE TABLE IF NOT EXISTS winche_hook_deliveries (
              id uuid PRIMARY KEY,
              hook_id text NOT NULL,
              hook_name text NOT NULL,
              source text NOT NULL,
              type text NOT NULL,
              path text NULL,
              event jsonb NOT NULL,
              status text NOT NULL,
              attempts int NOT NULL DEFAULT 0,
              created_at timestamptz NOT NULL,
              next_attempt_at timestamptz NULL,
              finished_at timestamptz NULL,
              last_status int NULL,
              last_result text NULL,
              duration_ms int NULL
            );
            CREATE INDEX IF NOT EXISTS winche_hook_deliveries_due ON winche_hook_deliveries (next_attempt_at) WHERE status = 'pending';
            CREATE INDEX IF NOT EXISTS winche_hook_deliveries_hook ON winche_hook_deliveries (hook_id, created_at DESC);
        """,
    ];

    public async Task InitializeAsync(CancellationToken ct = default)
    {
        await WincheSchema.EnsureAsync(db, "webhooks", Steps, ct);
    }

    internal async Task AddAsync(IReadOnlyList<(HookSettings Hook, JsonObject Event, WincheChange Source)> items, CancellationToken ct)
    {
        if (items.Count == 0) return;
        await using var batch = db.CreateBatch();
        foreach (var (hook, evt, e) in items)
        {
            var cmd = new NpgsqlBatchCommand("""
                INSERT INTO winche_hook_deliveries (id, hook_id, hook_name, source, type, path, event, status, created_at, next_attempt_at)
                VALUES ($1, $2, $3, $4, $5, $6, $7, 'pending', $8, $8)
                """);
            cmd.Parameters.AddWithValue(Guid.NewGuid());
            cmd.Parameters.AddWithValue(hook.Id);
            cmd.Parameters.AddWithValue(hook.Name);
            cmd.Parameters.AddWithValue(e.Source);
            cmd.Parameters.AddWithValue(e.Type);
            cmd.Parameters.AddWithValue((object?)e.Path ?? DBNull.Value);
            cmd.Parameters.AddWithValue(NpgsqlDbType.Jsonb, evt.ToJsonString());
            cmd.Parameters.AddWithValue(e.At);
            batch.BatchCommands.Add(cmd);
        }
        await batch.ExecuteNonQueryAsync(ct);
    }

    /// <summary>Claims up to <paramref name="max"/> due deliveries for this server (others see them again after a few minutes if it dies).</summary>
    internal async Task<IReadOnlyList<HookDelivery>> ClaimAsync(int max, DateTimeOffset now, CancellationToken ct)
    {
        await using var cmd = db.CreateCommand($"""
            UPDATE winche_hook_deliveries SET next_attempt_at = $2 + interval '5 minutes'
            WHERE id IN (SELECT id FROM winche_hook_deliveries WHERE status = 'pending' AND next_attempt_at <= $2
                         ORDER BY next_attempt_at LIMIT $1 FOR UPDATE SKIP LOCKED)
            RETURNING {Columns}
            """);
        cmd.Parameters.AddWithValue(max);
        cmd.Parameters.AddWithValue(now);
        return await ReadAsync(cmd, ct);
    }

    internal async Task RecordAsync(Guid id, string status, int attempts, DateTimeOffset? next, DateTimeOffset? finished, int? httpStatus, string? result, int durationMs, CancellationToken ct)
    {
        await using var cmd = db.CreateCommand("""
            UPDATE winche_hook_deliveries SET status = $2, attempts = $3, next_attempt_at = $4, finished_at = $5, last_status = $6, last_result = $7, duration_ms = $8
            WHERE id = $1
            """);
        cmd.Parameters.AddWithValue(id);
        cmd.Parameters.AddWithValue(status);
        cmd.Parameters.AddWithValue(attempts);
        cmd.Parameters.AddWithValue((object?)next ?? DBNull.Value);
        cmd.Parameters.AddWithValue((object?)finished ?? DBNull.Value);
        cmd.Parameters.AddWithValue((object?)httpStatus ?? DBNull.Value);
        cmd.Parameters.AddWithValue((object?)(result is { Length: > 1000 } ? result[..1000] : result) ?? DBNull.Value);
        cmd.Parameters.AddWithValue(durationMs);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    /// <summary>Recent deliveries, newest first; for one hook, or one status, when given.</summary>
    public async Task<IReadOnlyList<HookDelivery>> ListAsync(string? hookId = null, string? status = null, int limit = 100, CancellationToken ct = default)
    {
        await using var cmd = db.CreateCommand($"""
            SELECT {Columns} FROM winche_hook_deliveries
            WHERE ($1::text IS NULL OR hook_id = $1) AND ($2::text IS NULL OR status = $2)
            ORDER BY created_at DESC LIMIT $3
            """);
        cmd.Parameters.AddWithValue(NpgsqlDbType.Text, (object?)hookId ?? DBNull.Value);
        cmd.Parameters.AddWithValue(NpgsqlDbType.Text, (object?)status ?? DBNull.Value);
        cmd.Parameters.AddWithValue(Math.Clamp(limit, 1, 500));
        return await ReadAsync(cmd, ct);
    }

    /// <summary>Counts per hook and status over the last day, for the console's list.</summary>
    public async Task<IReadOnlyDictionary<string, IReadOnlyDictionary<string, int>>> CountsAsync(CancellationToken ct = default)
    {
        await using var cmd = db.CreateCommand("""
            SELECT hook_id, status, count(*)::int FROM winche_hook_deliveries WHERE created_at > now() - interval '1 day' GROUP BY hook_id, status
            """);
        var counts = new Dictionary<string, Dictionary<string, int>>();
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
        {
            if (!counts.TryGetValue(r.GetString(0), out var c)) counts[r.GetString(0)] = c = [];
            c[r.GetString(1)] = r.GetInt32(2);
        }
        return counts.ToDictionary(kv => kv.Key, kv => (IReadOnlyDictionary<string, int>)kv.Value);
    }

    /// <summary>Sends a delivery again (e.g. after fixing the receiver).</summary>
    public async Task<bool> RetryAsync(Guid id, CancellationToken ct = default)
    {
        await using var cmd = db.CreateCommand("UPDATE winche_hook_deliveries SET status = 'pending', next_attempt_at = now(), finished_at = NULL WHERE id = $1");
        cmd.Parameters.AddWithValue(id);
        return await cmd.ExecuteNonQueryAsync(ct) > 0;
    }

    /// <summary>Forgets finished deliveries: delivered ones after a week, failed ones after a month.</summary>
    internal async Task<int> PruneAsync(CancellationToken ct)
    {
        await using var cmd = db.CreateCommand("""
            DELETE FROM winche_hook_deliveries
            WHERE (status = 'delivered' AND finished_at < now() - interval '7 days') OR (status = 'failed' AND finished_at < now() - interval '30 days')
            """);
        return await cmd.ExecuteNonQueryAsync(ct);
    }

    private const string Columns = "id, hook_id, hook_name, source, type, path, status, attempts, created_at, next_attempt_at, finished_at, last_status, last_result, duration_ms, event";

    private static async Task<IReadOnlyList<HookDelivery>> ReadAsync(NpgsqlCommand cmd, CancellationToken ct)
    {
        var list = new List<HookDelivery>();
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
            list.Add(new HookDelivery(r.GetGuid(0), r.GetString(1), r.GetString(2), r.GetString(3), r.GetString(4), r.IsDBNull(5) ? null : r.GetString(5),
                r.GetString(6), r.GetInt32(7), r.GetFieldValue<DateTimeOffset>(8), r.IsDBNull(9) ? null : r.GetFieldValue<DateTimeOffset>(9),
                r.IsDBNull(10) ? null : r.GetFieldValue<DateTimeOffset>(10), r.IsDBNull(11) ? null : r.GetInt32(11), r.IsDBNull(12) ? null : r.GetString(12),
                r.IsDBNull(13) ? null : r.GetInt32(13), JsonNode.Parse(r.GetString(14))!.AsObject()));
        return list;
    }
}
