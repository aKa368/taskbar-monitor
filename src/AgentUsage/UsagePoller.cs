using System.Globalization;
using System.IO;
using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace TaskbarMonitor.AgentUsage;

/// <summary>
/// Stable identifiers for every monitored coding-agent CLI.
/// </summary>
public static class AgentIds
{
    public const string CommandCode = "commandcode";
    public const string OpenCode = "opencode";
    public const string Codex = "codex";
    public const string Antigravity = "antigravity";
    public const string Claude = "claude";

    /// <summary>All agent ids, in display order.</summary>
    public static readonly IReadOnlyList<string> All =
        new[] { CommandCode, OpenCode, Codex, Antigravity, Claude };
}

/// <summary>
/// A single rolling 5-hour or 7-day window's aggregate totals (cost / tokens / requests).
/// Used by the SQLite-backed agents (CommandCode, OpenCode) which have exact local data.
/// </summary>
public sealed record WindowTotals
{
    public double? Cost { get; init; }
    public long? TokensInput { get; init; }
    public long? TokensOutput { get; init; }
    public long? TokensReasoning { get; init; }
    public long? TokensCacheRead { get; init; }
    public long? TokensCacheWrite { get; init; }
    public long? TokensTotal { get; init; }
    public int? Requests { get; init; }

    public static readonly WindowTotals Empty = new();

    public WindowTotals Add(WindowTotals other) => new()
    {
        Cost = (Cost ?? 0) + (other.Cost ?? 0),
        TokensInput = (TokensInput ?? 0) + (other.TokensInput ?? 0),
        TokensOutput = (TokensOutput ?? 0) + (other.TokensOutput ?? 0),
        TokensReasoning = (TokensReasoning ?? 0) + (other.TokensReasoning ?? 0),
        TokensCacheRead = (TokensCacheRead ?? 0) + (other.TokensCacheRead ?? 0),
        TokensCacheWrite = (TokensCacheWrite ?? 0) + (other.TokensCacheWrite ?? 0),
        TokensTotal = (TokensTotal ?? 0) + (other.TokensTotal ?? 0),
        Requests = (Requests ?? 0) + (other.Requests ?? 0),
    };
}

/// <summary>
/// Immutable snapshot of one agent's usage. <c>null</c> fields mean "not available for this source".
/// <list type="bullet">
/// <item>API agents (Codex / Antigravity / Claude) fill <see cref="UsedPercent5h"/> / <see cref="ResetsAt5h"/> / <see cref="UsedPercent7d"/> / <see cref="ResetsAt7d"/>.</item>
/// <item>SQLite agents (CommandCode / OpenCode) fill <see cref="Last5h"/> / <see cref="Last7d"/> cost + token totals.</item>
/// <item><see cref="Error"/> is always a redacted string — secrets never appear in it.</item>
/// </list>
/// </summary>
public sealed record UsageData
{
    public string Agent { get; init; } = string.Empty;

    /// <summary>"sqlite" | "api" | null (unknown).</summary>
    public string? Source { get; init; }

    /// <summary>Quota already consumed in the 5h window, 0-100 (API agents only).</summary>
    public double? UsedPercent5h { get; init; }

    public DateTimeOffset? ResetsAt5h { get; init; }

    /// <summary>Quota already consumed in the 7d window, 0-100 (API agents only).</summary>
    public double? UsedPercent7d { get; init; }

    public DateTimeOffset? ResetsAt7d { get; init; }

    /// <summary>Rolling 5h aggregate (SQLite agents).</summary>
    public WindowTotals? Last5h { get; init; }

    /// <summary>Rolling 7d aggregate (SQLite agents).</summary>
    public WindowTotals? Last7d { get; init; }

    public DateTimeOffset? LastUpdated { get; init; }

    /// <summary>Redacted reason when the last poll failed; null on success.</summary>
    public string? Error { get; init; }

    public static UsageData Failure(string agent, string redactedError, DateTimeOffset? now = null, params string?[] secrets) => new()
    {
        Agent = agent,
        Error = Redact.Apply(redactedError, secrets),
        LastUpdated = now ?? DateTimeOffset.UtcNow,
    };
}

/// <summary>
/// Orchestrates polling of every agent: SQLite sources every 30s, API sources every 5m.
/// Results are cached thread-safely; a failed poll never crashes and keeps the last known good value.
/// </summary>
public sealed class UsagePoller : IDisposable, IAsyncDisposable
{
    private readonly object _gate = new();
    private readonly Dictionary<string, UsageData> _cache = new(StringComparer.Ordinal);
    private readonly CancellationTokenSource _cts = new();
    private readonly List<Task> _inFlight = new();
    private readonly CommandCodeUsage _commandCode;
    private readonly OpenCodeUsage _opencode;
    private readonly CodexUsage _codex;
    private readonly AntigravityUsage _antigravity;
    private readonly ClaudeCodeUsage _claude;
    private readonly UsagePollerOptions _options;
    private Timer? _sqliteTimer;
    private Timer? _apiTimer;
    private int _polling;
    private bool _disposed;

    public UsagePoller(
        UsagePollerOptions? options = null,
        CommandCodeUsage? commandCode = null,
        OpenCodeUsage? openCode = null,
        CodexUsage? codex = null,
        AntigravityUsage? antigravity = null,
        ClaudeCodeUsage? claude = null)
    {
        _options = options ?? new UsagePollerOptions();
        _commandCode = commandCode ?? new CommandCodeUsage();
        _opencode = openCode ?? new OpenCodeUsage();
        _codex = codex ?? new CodexUsage();
        _antigravity = antigravity ?? new AntigravityUsage();
        _claude = claude ?? new ClaudeCodeUsage();
    }

    public static class DefaultIntervals
    {
        public static readonly TimeSpan Sqlite = TimeSpan.FromSeconds(30);
        public static readonly TimeSpan Api = TimeSpan.FromMinutes(5);
    }

    /// <summary>Starts the periodic pollers. SQLite agents poll at <see cref="UsagePollerOptions.SqlitePollInterval"/>, API agents at <see cref="UsagePollerOptions.ApiPollInterval"/>.</summary>
    public void Start()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_sqliteTimer is not null || _apiTimer is not null)
            return;

        _sqliteTimer = new Timer(_ => TrackInFlight(RefreshSqliteAsync(_cts.Token)), null, TimeSpan.Zero, _options.SqlitePollInterval);
        // Fetch immediately; waiting five minutes made the usage pods look dead
        // after startup even when valid credentials were already present.
        _apiTimer = new Timer(_ => TrackInFlight(RefreshApiAsync(_cts.Token)), null, TimeSpan.Zero, _options.ApiPollInterval);
    }

    /// <summary>Tracks a fire-and-forget poll so <see cref="DisposeAsync"/> can await it.</summary>
    private void TrackInFlight(Task task)
    {
        lock (_gate)
            _inFlight.Add(task);
        _ = task.ContinueWith(
            static (t, state) =>
            {
                var poller = (UsagePoller)state!;
                lock (poller._gate)
                    poller._inFlight.Remove(t);
            },
            this,
            TaskScheduler.Default);
    }

    public void Stop()
    {
        _sqliteTimer?.Dispose();
        _apiTimer?.Dispose();
        _sqliteTimer = null;
        _apiTimer = null;
    }

    /// <summary>Polls every enabled agent once. Safe to call concurrently — only one poll runs at a time.</summary>
    public async Task RefreshOnceAsync(CancellationToken ct = default)
    {
        if (Interlocked.CompareExchange(ref _polling, 1, 0) != 0)
            return;
        try
        {
            PollSqlite();
            await PollApiAsync(ct).ConfigureAwait(false);
        }
        finally
        {
            Interlocked.Exchange(ref _polling, 0);
        }
    }

    public Task RefreshSqliteAsync(CancellationToken ct = default) => Task.Run(() =>
    {
        ct.ThrowIfCancellationRequested();
        if (Interlocked.CompareExchange(ref _polling, 1, 0) != 0)
            return;
        try
        {
            PollSqlite();
        }
        finally
        {
            Interlocked.Exchange(ref _polling, 0);
        }
    }, ct);

    public async Task RefreshApiAsync(CancellationToken ct = default)
    {
        if (Interlocked.CompareExchange(ref _polling, 1, 0) != 0)
            return;
        try
        {
            await PollApiAsync(ct).ConfigureAwait(false);
        }
        finally
        {
            Interlocked.Exchange(ref _polling, 0);
        }
    }

    private void PollSqlite()
    {
        if (_options.CommandCodeEnabled)
            Update(_commandCode.TryRead(out var cc) ? cc : null);
        if (_options.OpenCodeEnabled)
            Update(_opencode.TryRead(out var oc) ? oc : null);
    }

    private async Task PollApiAsync(CancellationToken ct)
    {
        var tasks = new List<Task<UsageData?>>(3);
        if (_options.CodexEnabled)
            tasks.Add(_codex.FetchAsync(now: null, ct));
        if (_options.AntigravityEnabled)
            tasks.Add(_antigravity.FetchAsync(now: null, ct));
        if (_options.ClaudeEnabled)
            tasks.Add(_claude.FetchAsync(now: null, ct));

        UsageData?[] results;
        try
        {
            results = await Task.WhenAll(tasks).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            results = tasks.Select(_ => UsageData.Failure("api", Redact.Apply(ex.Message))).ToArray();
        }

        foreach (var result in results)
            Update(result);
    }

    /// <summary>Returns the last known <see cref="UsageData"/> for an agent, or null before the first successful poll.</summary>
    public UsageData? Get(string agent)
    {
        lock (_gate)
            return _cache.TryGetValue(agent, out var data) ? data : null;
    }

    public IReadOnlyDictionary<string, UsageData> GetAll()
    {
        lock (_gate)
            return new Dictionary<string, UsageData>(_cache, StringComparer.Ordinal);
    }

    /// <summary>Replaces the cache entry for an agent. Failed polls never clobber a previously-good value.</summary>
    private void Update(UsageData? data)
    {
        if (data is null)
            return;
        lock (_gate)
        {
            if (data.Error is not null &&
                _cache.TryGetValue(data.Agent, out var previous) &&
                previous.Error is null)
            {
                return;
            }
            _cache[data.Agent] = data;
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
            return;
        _disposed = true;

        // Graceful shutdown: stop new polls, cancel in-flight HTTP requests,
        // then wait for the current poll before disposing the HTTP clients.
        Stop();
        _cts.Cancel();

        Task[] pending;
        lock (_gate)
            pending = _inFlight.ToArray();
        if (pending.Length > 0)
        {
            try
            {
                await Task.WhenAll(pending).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // Expected: cancellation raced the request completion.
            }
            catch (Exception)
            {
                // Poll failures are already surfaced redacted via UsageData.Error.
            }
        }

        _cts.Dispose();
        if (_commandCode is IDisposable c) c.Dispose();
        if (_opencode is IDisposable o) o.Dispose();
        if (_codex is IDisposable k) k.Dispose();
        if (_antigravity is IDisposable a) a.Dispose();
        if (_claude is IDisposable cl) cl.Dispose();
    }

    /// <summary>Convenience disposal for <c>using</c> blocks.</summary>
    public void Dispose() => DisposeAsync().AsTask().GetAwaiter().GetResult();
}

/// <summary>Configuration for <see cref="UsagePoller"/>.</summary>
public sealed record UsagePollerOptions
{
    public bool CommandCodeEnabled { get; init; } = true;
    public bool OpenCodeEnabled { get; init; } = true;
    public bool CodexEnabled { get; init; } = true;
    public bool AntigravityEnabled { get; init; } = true;

    /// <summary>Claude Code is optional — off by default because it needs an OAuth login and the endpoint rate-limits aggressively.</summary>
    public bool ClaudeEnabled { get; init; } = false;

    public TimeSpan SqlitePollInterval { get; init; } = TimeSpan.FromSeconds(30);
    public TimeSpan ApiPollInterval { get; init; } = TimeSpan.FromMinutes(5);
}

/// <summary>Shared helpers for building redacted error strings. Sensitive values are never persisted or logged.</summary>
internal static class Redact
{
    public const string Redacted = "[REDACTED]";

    /// <summary>Replaces every occurrence of any supplied secret with <c>[REDACTED]</c>. Short strings are ignored to avoid over-redaction.</summary>
    public static string Apply(string? message, params string?[] secrets)
    {
        if (string.IsNullOrEmpty(message))
            return Redacted;

        string result = message;
        foreach (var secret in secrets)
        {
            if (!string.IsNullOrEmpty(secret) && secret.Length >= 8)
                result = result.Replace(secret, Redacted, StringComparison.Ordinal);
        }
        return result;
    }

    /// <summary>True when <paramref name="text"/> contains any of the supplied secrets verbatim.</summary>
    public static bool ContainsAnySecret(string? text, params string?[] secrets)
    {
        if (string.IsNullOrEmpty(text))
            return false;
        foreach (var secret in secrets)
        {
            if (!string.IsNullOrEmpty(secret) && text.Contains(secret, StringComparison.Ordinal))
                return true;
        }
        return false;
    }
}

/// <summary>Opens SQLite databases read-only. Never writes; retries briefly when the DB is busy/locked.</summary>
internal static class SqliteDatabase
{
    /// <summary>Opens <paramref name="path"/> read-only or returns null. Never throws.</summary>
    public static SqliteConnection? TryOpenReadOnly(string path, int retries = 3, int retryDelayMs = 250)
    {
        if (string.IsNullOrEmpty(path) || !File.Exists(path))
            return null;

        Exception? last = null;
        for (int attempt = 0; attempt < retries; attempt++)
        {
            try
            {
                var builder = new SqliteConnectionStringBuilder
                {
                    DataSource = path,
                    Mode = SqliteOpenMode.ReadOnly,
                    Pooling = false,
                    DefaultTimeout = 5,
                    ForeignKeys = false,
                };
                var connection = new SqliteConnection(builder.ToString());
                connection.Open();

                using var pragma = connection.CreateCommand();
                pragma.CommandText = "PRAGMA busy_timeout = 3000; PRAGMA query_only = ON;";
                pragma.ExecuteNonQuery();

                return connection;
            }
            catch (Exception ex) when (attempt < retries - 1)
            {
                last = ex;
                Thread.Sleep(retryDelayMs * (attempt + 1));
            }
            catch (Exception ex)
            {
                last = ex;
            }
        }
        _ = last;
        return null;
    }
}

/// <summary>Reads the Windows Credential Manager via <c>CredReadW</c>. Blob bytes live in memory only, never persisted.</summary>
internal static class WindowsCredential
{
    public const uint CredTypeGeneric = 1;

    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    private struct CREDENTIAL
    {
        public uint Flags;
        public uint Type;
        [System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.LPWStr)]
        public string? TargetName;
        [System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.LPWStr)]
        public string? Comment;
        public long LastWritten;
        public uint CredentialBlobSize;
        public IntPtr CredentialBlob;
        public uint Persist;
        public uint AttributeCount;
        public IntPtr Attributes;
        [System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.LPWStr)]
        public string? TargetAlias;
        [System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.LPWStr)]
        public string? UserName;
    }

    [System.Runtime.InteropServices.DllImport("advapi32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode, SetLastError = true)]
    private static extern bool CredReadW(string target, uint type, uint reservedFlag, out IntPtr credentialPtr);

    [System.Runtime.InteropServices.DllImport("advapi32.dll", SetLastError = true)]
    private static extern void CredFree(IntPtr buffer);

    /// <summary>Returns the credential blob decoded as UTF-8, or null when the credential is missing/unreadable. Never throws.</summary>
    public static byte[]? TryReadBlob(string target)
    {
        try
        {
            if (!CredReadW(target, CredTypeGeneric, 0, out IntPtr credentialPtr))
                return null;
            try
            {
                var credential = System.Runtime.InteropServices.Marshal.PtrToStructure<CREDENTIAL>(credentialPtr);
                if (credential.CredentialBlob == IntPtr.Zero || credential.CredentialBlobSize == 0)
                    return null;
                byte[] blob = new byte[credential.CredentialBlobSize];
                System.Runtime.InteropServices.Marshal.Copy(credential.CredentialBlob, blob, 0, blob.Length);
                return blob;
            }
            finally
            {
                CredFree(credentialPtr);
            }
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Returns the blob as UTF-8 text, or null when missing/unreadable. Never throws.</summary>
    public static string? TryReadText(string target)
    {
        byte[]? blob = TryReadBlob(target);
        if (blob is null)
            return null;
        try
        {
            return System.Text.Encoding.UTF8.GetString(blob);
        }
        catch
        {
            return null;
        }
    }
}

/// <summary>Tiny JSON helpers used across the API pollers.</summary>
internal static class Json
{
    public static string? GetString(JsonElement element, string name)
    {
        return element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var prop)
            && prop.ValueKind == JsonValueKind.String
            ? prop.GetString()
            : null;
    }

    public static double? GetDouble(JsonElement element, string name)
    {
        if (element.ValueKind != JsonValueKind.Object || !element.TryGetProperty(name, out var prop))
            return null;
        if (prop.ValueKind == JsonValueKind.Number)
        {
            if (prop.TryGetDouble(out double d) && double.IsFinite(d))
                return d;
            return null;
        }
        if (prop.ValueKind == JsonValueKind.String &&
            double.TryParse(prop.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out double parsed) &&
            double.IsFinite(parsed))
        {
            return parsed;
        }
        return null;
    }

    public static long? GetLong(JsonElement element, string name)
    {
        if (element.ValueKind != JsonValueKind.Object || !element.TryGetProperty(name, out var prop))
            return null;
        if (prop.ValueKind == JsonValueKind.Number && prop.TryGetInt64(out long l))
            return l;
        if (prop.ValueKind == JsonValueKind.String &&
            long.TryParse(prop.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out long parsed))
        {
            return parsed;
        }
        return null;
    }
}
