using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;

namespace TaskbarMonitor.AgentUsage;

/// <summary>
/// Polls the OpenAI Codex usage endpoint (<c>GET /backend-api/wham/usage</c>) using the access
/// token and account id stored in <c>~/.codex/auth.json</c>. The token is read into memory only
/// for the duration of the request and is never stored, logged, or included in error messages.
/// On a 401 it performs no refresh, CLI action, or retry; the caller sees an unavailable value instead.
/// </summary>
public sealed class CodexUsage : IDisposable
{
    /// <summary>~/.codex/auth.json</summary>
    public static string DefaultAuthPath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".codex", "auth.json");

    public static Uri UsageUrl { get; } = new("https://chatgpt.com/backend-api/wham/usage");

    private readonly HttpClient _http;
    private readonly string _authPath;
    private readonly TimeProvider? _timeProvider;
    private string? _lastSecret;

    public CodexUsage() : this(handler: null, authPath: null, timeProvider: null) { }

    /// <param name="handler">Custom <see cref="HttpMessageHandler"/> for tests (null = real network).</param>
    /// <param name="authPath">Path to auth.json (null = default).</param>
    public CodexUsage(HttpMessageHandler? handler = null, string? authPath = null, TimeProvider? timeProvider = null)
    {
        _http = handler is null ? new HttpClient() : new HttpClient(handler, disposeHandler: true);
        _http.Timeout = TimeSpan.FromSeconds(10);
        _authPath = authPath ?? DefaultAuthPath;
        _timeProvider = timeProvider;
    }

    public DateTimeOffset UtcNow => _timeProvider?.GetUtcNow() ?? DateTimeOffset.UtcNow;

    /// <summary>Fetches the latest usage, or null on failure. Never throws.</summary>
    public async Task<UsageData?> FetchAsync(DateTimeOffset? now = null, CancellationToken ct = default)
    {
        DateTimeOffset reference = now ?? UtcNow;
        try
        {
            var auth = ReadAuth(_authPath);
            if (auth is null)
                return UsageData.Failure(AgentIds.Codex, "codex auth.json missing or has no access token", reference);
            _lastSecret = auth.AccessToken;

            (_, UsageData data) = await TryFetchAsync(auth, reference, ct).ConfigureAwait(false);
            return data;
        }
        catch (Exception ex)
        {
            return UsageData.Failure(AgentIds.Codex, Redact.Apply(ex.Message, _lastSecret), reference);
        }
        finally
        {
            _lastSecret = null;
        }
    }

    private async Task<(bool Ok, UsageData Data)> TryFetchAsync(CodexAuth auth, DateTimeOffset reference, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, UsageUrl);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", auth.AccessToken);
        if (!string.IsNullOrEmpty(auth.AccountId))
            request.Headers.TryAddWithoutValidation("ChatGPT-Account-Id", auth.AccountId);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

        HttpResponseMessage response = await _http.SendAsync(request, HttpCompletionOption.ResponseContentRead, ct).ConfigureAwait(false);
        using (response)
        {
            if (response.StatusCode == HttpStatusCode.Unauthorized)
                return (false, UsageData.Failure(AgentIds.Codex, "codex token expired (HTTP 401)", reference));
            if (!response.IsSuccessStatusCode)
                return (false, UsageData.Failure(AgentIds.Codex, $"codex usage HTTP {(int)response.StatusCode}", reference));

            string json = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            var parsed = ParseResponse(json);
            if (parsed is null)
                return (false, UsageData.Failure(AgentIds.Codex, "codex usage: no rate-limit data in response", reference));
            return (true, ToUsageData(parsed, reference));
        }
    }

    /// <summary>Reads <c>tokens.access_token</c> + <c>tokens.account_id</c> from auth.json. Never throws.</summary>
    internal static CodexAuth? ReadAuth(string path)
    {
        if (string.IsNullOrEmpty(path) || !File.Exists(path))
            return null;
        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(path));
            if (!document.RootElement.TryGetProperty("tokens", out JsonElement tokens) ||
                tokens.ValueKind != JsonValueKind.Object)
            {
                return null;
            }
            string? accessToken = Json.GetString(tokens, "access_token");
            if (string.IsNullOrEmpty(accessToken))
                return null;
            string? accountId = Json.GetString(tokens, "account_id")
                ?? Json.GetString(document.RootElement, "account_id");
            return new CodexAuth(accessToken, accountId ?? string.Empty);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>A single rolling rate-limit window from the wham/usage payload.</summary>
    public sealed record CodexWindow(double UsedPercent, int? WindowMinutes, DateTimeOffset? ResetsAt);

    /// <summary>Parsed <c>GET /wham/usage</c> payload.</summary>
    public sealed record CodexParseResult(CodexWindow? Primary, CodexWindow? Secondary, string? CreditsBalance, string? LimitName);

    /// <summary>Parses a <c>GET /backend-api/wham/usage</c> JSON body. Returns null when no usable window is present.</summary>
    public static CodexParseResult? ParseResponse(string json, DateTimeOffset? now = null)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            JsonElement root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
                return null;

            JsonElement rateLimit = root.TryGetProperty("rate_limit", out var rl) && rl.ValueKind == JsonValueKind.Object
                ? rl
                : default;

            CodexWindow? primary = ParseWindow(rateLimit, "primary_window");
            CodexWindow? secondary = ParseWindow(rateLimit, "secondary_window");

            string? creditsBalance = null;
            if (root.TryGetProperty("credits", out JsonElement credits) && credits.ValueKind == JsonValueKind.Object)
            {
                bool hasCredits = credits.TryGetProperty("has_credits", out var hc) && hc.ValueKind == JsonValueKind.True;
                if (hasCredits)
                    creditsBalance = Json.GetString(credits, "balance");
            }

            string? limitName = null;
            string? reachedType = Json.GetString(root, "rate_limit_reached_type");
            if (!string.IsNullOrWhiteSpace(reachedType))
                limitName = reachedType;

            if (primary is null && secondary is null)
                return null;

            return new CodexParseResult(primary, secondary, creditsBalance, limitName);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static CodexWindow? ParseWindow(JsonElement rateLimit, string name)
    {
        if (rateLimit.ValueKind != JsonValueKind.Object ||
            !rateLimit.TryGetProperty(name, out JsonElement window) ||
            window.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        double? usedPercent = Json.GetDouble(window, "used_percent");
        if (usedPercent is null)
            return null;

        int? windowMinutes = null;
        long? windowSeconds = Json.GetLong(window, "limit_window_seconds");
        if (windowSeconds is > 0)
            windowMinutes = (int)((windowSeconds.Value + 59) / 60);

        DateTimeOffset? resetsAt = null;
        long? resetAt = Json.GetLong(window, "reset_at");
        if (resetAt is not null)
        {
            try { resetsAt = DateTimeOffset.FromUnixTimeSeconds(resetAt.Value); }
            catch { resetsAt = null; }
        }

        return new CodexWindow(Math.Clamp(usedPercent.Value, 0, 100), windowMinutes, resetsAt);
    }

    /// <summary>Builds a <see cref="UsageData"/> from a parsed payload, classifying windows by size (5h primary, 7d secondary).</summary>
    public static UsageData ToUsageData(CodexParseResult parsed, DateTimeOffset? now = null)
    {
        CodexWindow? fiveHour;
        CodexWindow? sevenDay;
        ClassifyWindows(parsed.Primary, parsed.Secondary, out fiveHour, out sevenDay);

        return new UsageData
        {
            Agent = AgentIds.Codex,
            Source = "api",
            UsedPercent5h = fiveHour?.UsedPercent,
            ResetsAt5h = fiveHour?.ResetsAt,
            UsedPercent7d = sevenDay?.UsedPercent,
            ResetsAt7d = sevenDay?.ResetsAt,
            LastUpdated = now ?? DateTimeOffset.UtcNow,
        };
    }

    /// <summary>Maps two rate-limit windows onto the 5h / 7d buckets. Prefers window size when available, else position (primary = 5h).</summary>
    public static void ClassifyWindows(CodexWindow? primary, CodexWindow? secondary, out CodexWindow? fiveHour, out CodexWindow? sevenDay)
    {
        fiveHour = null;
        sevenDay = null;

        CodexWindow? sized5h = null;
        CodexWindow? sized7d = null;
        if (primary?.WindowMinutes is not null)
        {
            if (primary.WindowMinutes <= 24 * 60)
                sized5h = primary;
            else
                sized7d = primary;
        }
        if (secondary?.WindowMinutes is not null)
        {
            if (secondary.WindowMinutes <= 24 * 60)
                sized5h = secondary;
            else
                sized7d = secondary;
        }

        fiveHour = sized5h ?? (primary is not null && primary.WindowMinutes is null ? primary : null);
        sevenDay = sized7d ?? (secondary is not null && secondary.WindowMinutes is null ? secondary : null);

        if (fiveHour is null && sevenDay is null)
        {
            fiveHour = primary ?? secondary;
        }
    }

    public void Dispose() => _http.Dispose();
}

/// <summary>In-memory Codex auth payload (access token + account id). Never serialized.</summary>
internal sealed record CodexAuth(string AccessToken, string AccountId);
