using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;

namespace TaskbarMonitor.AgentUsage;

/// <summary>
/// OPTIONAL — polls Claude Code usage from Anthropic's internal OAuth endpoint
/// (<c>GET /api/oauth/usage</c>). The OAuth access token is read from the Windows Credential
/// Manager (targets below) with a fallback to <c>~/.claude/.credentials.json</c>. The token is
/// held in memory only and never stored or logged. This source is best-effort: the endpoint is
/// undocumented and rate-limits aggressively (known 429s), so a failed poll keeps the last known
/// value. Disabled by default in <see cref="UsagePollerOptions.ClaudeEnabled"/>.
/// </summary>
public sealed class ClaudeCodeUsage : IDisposable
{
    public static Uri UsageUrl { get; } = new("https://api.anthropic.com/api/oauth/usage");

    private const string AnthropicBetaHeader = "oauth-2025-04-20";

    /// <summary>Credential targets tried (in order) via CredReadW.</summary>
    public static readonly string[] CredentialTargets =
    {
        "claude:claude",
        "claudeCodeOAuth",
        "Claude Code",
    };

    private readonly HttpClient _http;
    private readonly string? _credentialsFilePath;
    private readonly TimeProvider? _timeProvider;
    private readonly Func<string?>? _credentialReader;
    private string? _lastSecret;

    public ClaudeCodeUsage() : this(handler: null, credentialsFilePath: null, timeProvider: null, credentialReader: null) { }

    /// <param name="credentialReader">Overrides credential lookup entirely (used by tests; never hit the real credential store).</param>
    public ClaudeCodeUsage(HttpMessageHandler? handler = null, string? credentialsFilePath = null, TimeProvider? timeProvider = null, Func<string?>? credentialReader = null)
    {
        _http = handler is null ? new HttpClient() : new HttpClient(handler, disposeHandler: true);
        _http.Timeout = TimeSpan.FromSeconds(10);
        _credentialsFilePath = credentialsFilePath ?? DefaultCredentialsFilePath();
        _timeProvider = timeProvider;
        _credentialReader = credentialReader;
    }

    public DateTimeOffset UtcNow => _timeProvider?.GetUtcNow() ?? DateTimeOffset.UtcNow;

    private static string? DefaultCredentialsFilePath()
    {
        string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return string.IsNullOrEmpty(home) ? null : Path.Combine(home, ".claude", ".credentials.json");
    }

    /// <summary>Fetches the latest usage, or null when not logged in / unreachable. Never throws.</summary>
    public async Task<UsageData?> FetchAsync(DateTimeOffset? now = null, CancellationToken ct = default)
    {
        DateTimeOffset reference = now ?? UtcNow;
        try
        {
            string? accessToken = ResolveAccessToken();
            if (string.IsNullOrEmpty(accessToken))
                return UsageData.Failure(AgentIds.Claude, "claude: no OAuth token found (not logged in)", reference);
            _lastSecret = accessToken;

            using var request = new HttpRequestMessage(HttpMethod.Get, UsageUrl);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
            request.Headers.TryAddWithoutValidation("anthropic-beta", AnthropicBetaHeader);
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

            using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseContentRead, ct).ConfigureAwait(false);
            if (response.StatusCode == HttpStatusCode.Unauthorized)
                return UsageData.Failure(AgentIds.Claude, "claude: token expired (HTTP 401)", reference);
            if (response.StatusCode == HttpStatusCode.TooManyRequests)
                return UsageData.Failure(AgentIds.Claude, "claude: usage endpoint rate-limited (HTTP 429)", reference);
            if (!response.IsSuccessStatusCode)
                return UsageData.Failure(AgentIds.Claude, $"claude usage HTTP {(int)response.StatusCode}", reference);

            string json = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            var parsed = ParseResponse(json);
            if (parsed is null)
                return UsageData.Failure(AgentIds.Claude, "claude: no usage data in response", reference);

            return new UsageData
            {
                Agent = AgentIds.Claude,
                Source = "api",
                UsedPercent5h = parsed.FiveHour?.UtilizationPercent,
                ResetsAt5h = parsed.FiveHour?.ResetsAt,
                UsedPercent7d = parsed.SevenDay?.UtilizationPercent,
                ResetsAt7d = parsed.SevenDay?.ResetsAt,
                LastUpdated = reference,
            };
        }
        catch (Exception ex)
        {
            return UsageData.Failure(AgentIds.Claude, Redact.Apply(ex.Message, _lastSecret), reference);
        }
        finally
        {
            _lastSecret = null;
        }
    }

    /// <summary>Locates an OAuth access token: Windows Credential Manager first, then the credentials file. Never throws.</summary>
    private string? ResolveAccessToken()
    {
        if (_credentialReader is not null)
        {
            string? text = _credentialReader();
            return string.IsNullOrEmpty(text) ? null : ParseTokenFromJson(text);
        }

        foreach (string target in CredentialTargets)
        {
            string? text = WindowsCredential.TryReadText(target);
            if (string.IsNullOrEmpty(text))
                continue;
            string? token = ParseTokenFromJson(text);
            if (!string.IsNullOrEmpty(token))
                return token;
        }

        if (string.IsNullOrEmpty(_credentialsFilePath) || !File.Exists(_credentialsFilePath))
            return null;
        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(_credentialsFilePath));
            JsonElement oauth = document.RootElement.ValueKind == JsonValueKind.Object &&
                                document.RootElement.TryGetProperty("claudeAiOauth", out var o)
                ? o
                : default;
            if (oauth.ValueKind == JsonValueKind.Object)
            {
                string? token = Json.GetString(oauth, "accessToken");
                if (!string.IsNullOrEmpty(token))
                    return token;
            }
            string? rootToken = Json.GetString(document.RootElement, "accessToken");
            return string.IsNullOrEmpty(rootToken) ? null : rootToken;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string? ParseTokenFromJson(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
                return null;
            string? token = Json.GetString(document.RootElement, "access_token")
                ?? Json.GetString(document.RootElement, "accessToken");
            return string.IsNullOrEmpty(token) ? null : token;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>A parsed utilization window: <c>UtilizationPercent</c> is 0-100, <c>ResetsAt</c> optional.</summary>
    public sealed record ClaudeWindow(double UtilizationPercent, DateTimeOffset? ResetsAt);

    /// <summary>Parsed <c>GET /api/oauth/usage</c> payload.</summary>
    public sealed record ClaudeParseResult(ClaudeWindow? FiveHour, ClaudeWindow? SevenDay);

    /// <summary>Parses the Anthropic usage payload: <c>{ five_hour: { utilization, resets_at }, seven_day: {...} }</c>. Returns null when unusable.</summary>
    public static ClaudeParseResult? ParseResponse(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            JsonElement root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
                return null;

            ClaudeWindow? fiveHour = ParseWindow(root, "five_hour");
            ClaudeWindow? sevenDay = ParseWindow(root, "seven_day");
            if (fiveHour is null && sevenDay is null)
                return null;
            return new ClaudeParseResult(fiveHour, sevenDay);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static ClaudeWindow? ParseWindow(JsonElement root, string name)
    {
        if (!root.TryGetProperty(name, out JsonElement window) || window.ValueKind != JsonValueKind.Object)
            return null;
        double? utilization = Json.GetDouble(window, "utilization");
        if (utilization is null)
            return null;

        DateTimeOffset? resetsAt = null;
        string? resetsAtText = Json.GetString(window, "resets_at");
        if (resetsAtText is not null && DateTimeOffset.TryParse(resetsAtText,
                System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.AssumeUniversal | System.Globalization.DateTimeStyles.AdjustToUniversal,
                out var parsed))
        {
            resetsAt = parsed;
        }

        return new ClaudeWindow(Math.Clamp(utilization.Value, 0, 100), resetsAt);
    }

    public void Dispose() => _http.Dispose();
}
