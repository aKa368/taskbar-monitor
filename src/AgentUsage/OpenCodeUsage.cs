using System.IO;
using Microsoft.Data.Sqlite;

namespace TaskbarMonitor.AgentUsage;

/// <summary>
/// Reads OpenCode usage from the local opencode SQLite database (<c>opencode.db</c>), table
/// <c>session</c>. The database is opened read-only. Aggregates cost and token columns
/// (<c>tokens_input / tokens_output / tokens_reasoning / tokens_cache_read / tokens_cache_write</c>)
/// across rolling 5h and 7d windows using the <c>time_created</c> (Unix milliseconds) column.
/// </summary>
public sealed class OpenCodeUsage : IDisposable
{
    /// <summary>~/.local/share/opencode/opencode.db</summary>
    public static string DefaultDatabasePath =>
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".local", "share", "opencode", "opencode.db");

    private readonly string _databasePath;
    private readonly TimeProvider? _timeProvider;

    public OpenCodeUsage() : this(DefaultDatabasePath, null) { }

    public OpenCodeUsage(string databasePath) : this(databasePath, null) { }

    public OpenCodeUsage(string databasePath, TimeProvider? timeProvider)
    {
        _databasePath = databasePath;
        _timeProvider = timeProvider;
    }

    /// <summary>Reads the current snapshot, or null when the database is missing/unreadable.</summary>
    public UsageData? Read(DateTimeOffset? now = null)
    {
        return TryRead(out var data, now) ? data : null;
    }

    /// <summary>
    /// Attempts to read usage. Returns false (null data) only when the database is missing,
    /// locked beyond retries, or its schema is unusable. An empty <c>session</c> table yields a
    /// valid zeroed snapshot (true, non-null).
    /// </summary>
    public bool TryRead(out UsageData? data, DateTimeOffset? now = null)
    {
        data = null;
        DateTimeOffset reference = now ?? (_timeProvider?.GetUtcNow() ?? DateTimeOffset.UtcNow);
        using var connection = SqliteDatabase.TryOpenReadOnly(_databasePath);
        if (connection is null)
            return false;

        try
        {
            DateTimeOffset cutoff5h = reference.AddHours(-5);
            DateTimeOffset cutoff7d = reference.AddDays(-7);
            long cutoff7dMs = cutoff7d.ToUnixTimeMilliseconds();

            WindowTotalsBuilder fiveHour = new();
            WindowTotalsBuilder sevenDay = new();

            using var command = connection.CreateCommand();
            command.CommandText =
                "SELECT cost, tokens_input, tokens_output, tokens_reasoning, " +
                "tokens_cache_read, tokens_cache_write, time_created " +
                "FROM session WHERE time_created >= $cutoff;";
            command.Parameters.AddWithValue("$cutoff", cutoff7dMs);

            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                long createdMs = reader.IsDBNull(6) ? 0 : reader.GetInt64(6);
                DateTimeOffset created = DateTimeOffset.FromUnixTimeMilliseconds(createdMs);
                if (created < cutoff7d)
                    continue;

                long input = reader.IsDBNull(1) ? 0 : Math.Max(0, reader.GetInt64(1));
                long output = reader.IsDBNull(2) ? 0 : Math.Max(0, reader.GetInt64(2));
                long reasoning = reader.IsDBNull(3) ? 0 : Math.Max(0, reader.GetInt64(3));
                long cacheRead = reader.IsDBNull(4) ? 0 : Math.Max(0, reader.GetInt64(4));
                long cacheWrite = reader.IsDBNull(5) ? 0 : Math.Max(0, reader.GetInt64(5));
                double cost = reader.IsDBNull(0) ? 0 : Math.Max(0, reader.GetDouble(0));

                bool is7d = created >= cutoff7d;
                bool is5h = created >= cutoff5h;

                var entry = new WindowTotals
                {
                    Cost = cost,
                    TokensInput = input,
                    TokensOutput = output,
                    TokensReasoning = reasoning,
                    TokensCacheRead = cacheRead,
                    TokensCacheWrite = cacheWrite,
                    TokensTotal = input + output + reasoning + cacheRead + cacheWrite,
                    Requests = 1,
                };
                if (is7d)
                    sevenDay.Add(entry);
                if (is5h)
                    fiveHour.Add(entry);
            }

            data = new UsageData
            {
                Agent = AgentIds.OpenCode,
                Source = "sqlite",
                Last5h = fiveHour.Snapshot(),
                Last7d = sevenDay.Snapshot(),
                LastUpdated = reference,
            };
            return true;
        }
        catch (SqliteException)
        {
            data = null;
            return false;
        }
        catch (Exception)
        {
            data = null;
            return false;
        }
    }

    public void Dispose() { }
}
