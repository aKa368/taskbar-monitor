using System.Globalization;
using System.IO;
using Microsoft.Data.Sqlite;

namespace TaskbarMonitor.AgentUsage;

/// <summary>
/// Reads CommandCode (Cline via 9router) usage from the local 9router SQLite database,
/// table <c>usageHistory</c> where <c>provider = 'commandcode'</c>. The database is opened
/// read-only; the <c>apiKeys</c> table is never touched. Aggregates cost, prompt/completion
/// tokens and request counts across rolling 5h and 7d windows.
/// </summary>
public sealed class CommandCodeUsage : IDisposable
{
    public const string Provider = "commandcode";

    /// <summary>%AppData%/9router/db/data.sqlite</summary>
    public static string DefaultDatabasePath =>
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "9router", "db", "data.sqlite");

    private readonly string _databasePath;
    private readonly TimeProvider? _timeProvider;

    public CommandCodeUsage() : this(DefaultDatabasePath, null) { }

    public CommandCodeUsage(string databasePath) : this(databasePath, null) { }

    /// <param name="databasePath">Path to the 9router data.sqlite file.</param>
    /// <param name="timeProvider">Overridable clock for deterministic tests.</param>
    public CommandCodeUsage(string databasePath, TimeProvider? timeProvider)
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
    /// Attempts to read usage. Returns false (and null data) only when the database file is
    /// missing, locked beyond retries, or its schema is unusable. A readable-but-empty history
    /// yields a valid zeroed snapshot (true, non-null).
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

            WindowTotalsBuilder fiveHour = new();
            WindowTotalsBuilder sevenDay = new();

            using var command = connection.CreateCommand();
            command.CommandText =
                "SELECT timestamp, promptTokens, completionTokens, cost, status " +
                "FROM usageHistory " +
                "WHERE provider = $provider AND timestamp >= $cutoff;";
            command.Parameters.AddWithValue("$provider", Provider);
            command.Parameters.AddWithValue("$cutoff", FormatCutoff(cutoff7d));

            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                string? rawTimestamp = reader.IsDBNull(0) ? null : reader.GetString(0);
                if (!TryParseTimestamp(rawTimestamp, out DateTimeOffset timestamp) || timestamp < cutoff7d)
                    continue;

                long prompt = reader.IsDBNull(1) ? 0 : reader.GetInt64(1);
                long completion = reader.IsDBNull(2) ? 0 : reader.GetInt64(2);
                double cost = reader.IsDBNull(3) ? 0 : reader.GetDouble(3);
                bool is7d = timestamp >= cutoff7d;
                bool is5h = timestamp >= cutoff5h;

                var entry = new WindowTotals
                {
                    Cost = cost,
                    TokensInput = prompt,
                    TokensOutput = completion,
                    TokensTotal = prompt + completion,
                    Requests = 1,
                };
                if (is7d)
                    sevenDay.Add(entry);
                if (is5h)
                    fiveHour.Add(entry);
            }

            data = new UsageData
            {
                Agent = AgentIds.CommandCode,
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

    /// <summary>Formats a UTC cutoff with maximal fractional precision so lexicographic comparisons match the DB's ISO-8601 text.</summary>
    private static string FormatCutoff(DateTimeOffset cutoff)
        => cutoff.ToUniversalTime().UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.fffffff", CultureInfo.InvariantCulture) + "Z";

    /// <summary>Parses <c>2026-08-08T09:21:41.682Z</c>-style timestamps; returns false on malformed input.</summary>
    internal static bool TryParseTimestamp(string? raw, out DateTimeOffset timestamp)
    {
        if (string.IsNullOrEmpty(raw) ||
            !DateTimeOffset.TryParse(
                raw,
                CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                out timestamp))
        {
            timestamp = default;
            return false;
        }
        return true;
    }

    public void Dispose() { }
}

/// <summary>Accumulates cost/token/request totals for one rolling window.</summary>
internal sealed class WindowTotalsBuilder
{
    private double _cost;
    private long _input;
    private long _output;
    private long _reasoning;
    private long _cacheRead;
    private long _cacheWrite;
    private long _total;
    private int _requests;

    public void Add(WindowTotals entry)
    {
        _cost += entry.Cost ?? 0;
        _input += entry.TokensInput ?? 0;
        _output += entry.TokensOutput ?? 0;
        _reasoning += entry.TokensReasoning ?? 0;
        _cacheRead += entry.TokensCacheRead ?? 0;
        _cacheWrite += entry.TokensCacheWrite ?? 0;
        _total += entry.TokensTotal ?? 0;
        _requests += entry.Requests ?? 0;
    }

    public WindowTotals Snapshot() => new()
    {
        Cost = _cost,
        TokensInput = _input,
        TokensOutput = _output,
        TokensReasoning = _reasoning,
        TokensCacheRead = _cacheRead,
        TokensCacheWrite = _cacheWrite,
        TokensTotal = _total,
        Requests = _requests,
    };
}
