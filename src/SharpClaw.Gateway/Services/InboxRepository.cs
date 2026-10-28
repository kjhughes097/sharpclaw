using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using SharpClaw.Contracts;
using SharpClaw.Gateway.Data;

namespace SharpClaw.Gateway.Services;

public static class InboxRepository
{
    /// <summary>Requeues claimed messages whose claim is older than the threshold; returns their ids.</summary>
    public static async Task<List<Guid>> RequeueStaleAsync(
        SharpClawDbContext db, Guid agentId, DateTime thresholdUtc, CancellationToken ct)
    {
        var conn = (SqliteConnection)db.Database.GetDbConnection();
        if (conn.State != System.Data.ConnectionState.Open)
            await conn.OpenAsync(ct);

        await using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            UPDATE messages SET status = $pending, claimed_at = NULL
            WHERE agent_id = $agent AND role = $user AND status = $claimed
              AND claimed_at IS NOT NULL AND claimed_at < $threshold
            RETURNING id;";
        cmd.Parameters.AddWithValue("$agent", agentId);
        cmd.Parameters.AddWithValue("$threshold", thresholdUtc);
        AddStateParameters(cmd);

        var ids = new List<Guid>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
            ids.Add(reader.GetGuid(0));
        return ids;
    }

    /// <summary>
    /// Claim heartbeat: refreshes <c>claimed_at</c> on the agent's claimed message so a long tool
    /// turn or approval wait isn't mistaken for a crashed agent and requeued.
    /// </summary>
    public static Task TouchClaimAsync(SharpClawDbContext db, Guid agentId, Guid messageId, DateTime nowUtc, CancellationToken ct) =>
        db.Database.ExecuteSqlInterpolatedAsync($@"
            UPDATE messages SET claimed_at = {nowUtc}
            WHERE id = {messageId} AND agent_id = {agentId} AND role = {Roles.User} AND status = {MessageStatus.Claimed}", ct);

    /// <summary>Atomically claims the oldest pending user message for the agent (single statement).</summary>
    public static async Task<InboxMessageDto?> ClaimNextAsync(
        SharpClawDbContext db, Guid agentId, DateTime nowUtc, CancellationToken ct)
    {
        var conn = (SqliteConnection)db.Database.GetDbConnection();
        if (conn.State != System.Data.ConnectionState.Open)
            await conn.OpenAsync(ct);

        await using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            UPDATE messages SET status = $claimed, claimed_at = $now
            WHERE id = (
                SELECT id FROM messages
                WHERE agent_id = $agent AND role = $user AND status = $pending
                ORDER BY created_at, id LIMIT 1)
              AND status = $pending
            RETURNING id, thread_id, content, created_at;";
        cmd.Parameters.AddWithValue("$now", nowUtc);
        cmd.Parameters.AddWithValue("$agent", agentId);
        AddStateParameters(cmd);

        await using var reader = await cmd.ExecuteReaderAsync(ct);
        if (await reader.ReadAsync(ct))
        {
            var id = reader.GetGuid(0);
            var threadId = reader.GetGuid(1);
            var content = reader.GetString(2);
            var createdAt = DateTime.SpecifyKind(reader.GetDateTime(3), DateTimeKind.Utc);
            return new InboxMessageDto(id, threadId, content, createdAt);
        }
        return null;
    }

    /// <summary>The role and status values used by the raw SQL above, from the shared constants.</summary>
    private static void AddStateParameters(SqliteCommand cmd)
    {
        cmd.Parameters.AddWithValue("$user", Roles.User);
        cmd.Parameters.AddWithValue("$pending", MessageStatus.Pending);
        cmd.Parameters.AddWithValue("$claimed", MessageStatus.Claimed);
    }
}
