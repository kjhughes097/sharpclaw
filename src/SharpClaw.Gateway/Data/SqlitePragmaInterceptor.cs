using System.Data.Common;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace SharpClaw.Gateway.Data;

/// <summary>Applies WAL mode and a busy timeout whenever a SQLite connection opens.</summary>
public sealed class SqlitePragmaInterceptor : DbConnectionInterceptor
{
    public override void ConnectionOpened(DbConnection connection, ConnectionEndEventData eventData)
        => Apply(connection);

    public override Task ConnectionOpenedAsync(
        DbConnection connection, ConnectionEndEventData eventData, CancellationToken ct = default)
    {
        Apply(connection);
        return Task.CompletedTask;
    }

    private static void Apply(DbConnection connection)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "PRAGMA journal_mode=WAL; PRAGMA busy_timeout=5000; PRAGMA foreign_keys=ON;";
        cmd.ExecuteNonQuery();
    }
}
