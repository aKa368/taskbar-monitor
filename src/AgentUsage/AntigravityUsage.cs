using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;

namespace TaskbarMonitor.AgentUsage;

/// <summary>
/// Polls Google Antigravity quota from the Cloud Code Assist API
/// (<c>daily-cloudcode-pa*.googleapis.com</c>). The access token is read from the Windows
/// Credential Manager entry <c>gemini:antigravity</c> via <c>CredReadW</c>, held in memory only,
/// and never stored or logged. For publish-safe operation this monitor never embeds
/// an OAuth client secret or exchanges refresh tokens: an expired access token is
/// reported as unavailable until the owning CLI refreshes it.
/// </summary>
public sealed class AntigravityUsage : IDisposable
{
    public const string CredentialTarget = "gemini:antigravity";

    private const string FetchAvailableModelsPath = "/v1internal:fetchAvailableModels";
    private const string QuotaSummaryPath = "/v1internal:retrieveUserQuotaSummary";

    /// <summary>Endpoint fallback order: sandbox daily → daily → production.</summary>
    public static readonly string[] EndpointCandidates =
    {
        "https://daily-cloudcode-pa.sandbox.googleapis.com",
        "https://daily-cloudcode-pa.googleapis.com",
        "https://cloudcode-pa.googleapis.com",
    };

    private readonly HttpClient _http;
    private readonly TimeProvider? _timeProvider;
    private readonly Func<string?>? _credentialReader;
    private string? _lastSecret;

    public AntigravityUsage() : this(handler: null, timeProvider: null, credentialReader: null) { }

    /// <param name="credentialReader">Overrides the Windows Credential Manager read (used by tests; never hit the real store).</param>
    public AntigravityUsage(HttpMessageHandler? handler = null, TimeProvider? timeProvider = null, Func<string?>? credentialReader = null)
    {
        _http = handler is null ? new HttpClient() : new HttpClient(handler, disposeHandler: true);
        _http.Timeout = TimeSpan.FromSeconds(10);
        _timeProvider = timeProvider;
        _credentialReader = credentialReader;
    }

    public DateTimeOffset UtcNow => _timeProvider?.GetUtcNow() ?? DateTimeOffset.UtcNow;

    /// <summary>Fetches the latest quota, or null on failure. Never throws.</summary>
    public async Task<UsageData?> FetchAsync(DateTimeOffset? now = null, CancellationToken ct = default)
    {
        DateTimeOffset reference = now ?? UtcNow;
        try
        {
            string? credentialJson = _credentialReader is not null
                ? _credentialReader()
                : WindowsCredential.TryReadText(CredentialTarget);
            if (credentialJson is null)
                return UsageData.Failure(AgentIds.Antigravity, "antigravity credential not found in Windows Credential Manager", reference);

            var credential = AntigravityCredential.Parse(credentialJson);
            if (credential is null)
                return UsageData.Failure(AgentIds.Antigravity, "antigravity credential is malformed", reference);
            _lastSecret = credential.AccessToken;

            string? accessToken = ResolveAccessToken(credential, reference);
            if (string.IsNullOrEmpty(accessToken))
                return UsageData.Failure(AgentIds.Antigravity, "antigravity token missing or expired", reference);
            _lastSecret = accessToken;

            UsageData? result = await FetchQuotaAsync(accessToken, credential.ProjectId, reference, ct).ConfigureAwait(false);
            return result;
        }
        catch (Exception ex)
        {
            return UsageData.Failure(AgentIds.Antigravity, Redact.Apply(ex.Message, _lastSecret), reference);
        }
        finally
        {
            _lastSecret = null;
        }
    }

    /// <summary>Returns the stored access token only while it remains valid.</summary>
    private static string? ResolveAccessToken(AntigravityCredential credential, DateTimeOffset reference)
    {
        return !string.IsNullOrEmpty(credential.AccessToken) && !IsExpired(credential.ExpiryUtc, reference)
            ? credential.AccessToken
            : null;
    }

    private static bool IsExpired(DateTimeOffset? expiry, DateTimeOffset now)
        => expiry is null || expiry.Value <= now.AddMinutes(1);

    private async Task<UsageData?> FetchQuotaAsync(string accessToken, string? projectId, DateTimeOffset reference, CancellationToken ct)
    {
        var headers = new Dictionary<string, string>
        {
            ["Authorization"] = "Bearer " + accessToken,
            ["Accept"] = "application/json",
            ["User-Agent"] = "AntigravityCLI/1.0",
        };
        string body = string.IsNullOrEmpty(projectId) ? "{}" : "{\"project\":" + JsonSerializer.Serialize(projectId) + "}";

        var quotaSummary = await TryPostQuotaSummaryAsync(headers, body, ct).ConfigureAwait(false);
        if (quotaSummary is not null)
            return quotaSummary;

        var models = await TryFetchAvailableModelsAsync(headers, body, ct).ConfigureAwait(false);
        if (models is not null)
            return models;

        return UsageData.Failure(AgentIds.Antigravity, "antigravity quota API unavailable", reference);
    }

    /// <summary>Tries each endpoint candidate until one responds. Returns (endpoint, json) or null.</summary>
    private async Task<(string Endpoint, string Json)?> TryPostEachEndpointAsync(string path, Dictionary<string, string> headers, string body, CancellationToken ct)
    {
        foreach (string endpoint in EndpointCandidates)
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, endpoint + path);
            request.Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json");
            foreach (var (name, value) in headers)
            {
                if (name == "Authorization")
                    request.Headers.Authorization = AuthenticationHeaderValue.Parse(value);
                else
                    request.Headers.TryAddWithoutValidation(name, value);
            }

            try
            {
                using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseContentRead, ct).ConfigureAwait(false);
                if (!response.IsSuccessStatusCode)
                    continue;
                string json = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
                return (endpoint, json);
            }
            catch (HttpRequestException)
            {
                continue;
            }
        }
        return null;
    }

    private async Task<UsageData?> TryPostQuotaSummaryAsync(Dictionary<string, string> headers, string body, CancellationToken ct)
    {
        var response = await TryPostEachEndpointAsync(QuotaSummaryPath, headers, body, ct).ConfigureAwait(false);
        if (response is null)
            return null;
        var quota = ParseQuotaSummary(response.Value.Json);
        if (quota is null)
            return null;
        return ToUsageData(quota);
    }

    private async Task<UsageData?> TryFetchAvailableModelsAsync(Dictionary<string, string> headers, string body, CancellationToken ct)
    {
        var response = await TryPostEachEndpointAsync(FetchAvailableModelsPath, headers, body, ct).ConfigureAwait(false);
        if (response is null)
            return null;
        var quota = ParseAvailableModels(response.Value.Json);
        if (quota is null)
            return null;
        return ToUsageData(quota);
    }

    private UsageData ToUsageData(AntigravityQuota quota)
        => new()
        {
            Agent = AgentIds.Antigravity,
            Source = "api",
            UsedPercent5h = quota.Primary5h?.UsedPercent,
            ResetsAt5h = quota.Primary5h?.ResetsAt,
            UsedPercent7d = quota.Secondary7d?.UsedPercent,
            ResetsAt7d = quota.Secondary7d?.ResetsAt,
            LastUpdated = UtcNow,
        };

    /// <summary>Parses the <c>fetchAvailableModels</c> response: <c>models.{id}.quotaInfo</c> (single, array, or per-tier). Returns null when empty.</summary>
    public static AntigravityQuota? ParseAvailableModels(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            JsonElement root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("models", out JsonElement models) ||
                models.ValueKind != JsonValueKind.Object)
            {
                return null;
            }

            var collected = new List<AntigravityWindow>();
            var defaultModelWindows = new List<AntigravityWindow>();

            foreach (JsonProperty modelProp in models.EnumerateObject())
            {
                JsonElement model = modelProp.Value;
                if (model.ValueKind != JsonValueKind.Object)
                    continue;

                var windows = EnumerateQuotaInfos(model).ToList();
                collected.AddRange(windows);
                if (Json.GetString(root, "defaultAgentModelId") is string defaultModel &&
                    string.Equals(modelProp.Name, defaultModel, StringComparison.OrdinalIgnoreCase))
                {
                    defaultModelWindows.AddRange(windows);
                }
            }

            if (collected.Count == 0)
                return null;

            // Prefer the default agent model's window as the primary (5h) reading.
            var primaryWindow = defaultModelWindows.Count > 0
                ? PickPrimary(defaultModelWindows)
                : PickPrimary(collected);
            var secondaryWindow = collected
                .Where(w => w.WindowKind == WindowKind.SevenDay)
                .OrderByDescending(w => w.ResetsAt ?? DateTimeOffset.MinValue)
                .FirstOrDefault();
            if (secondaryWindow is null)
            {
                secondaryWindow = collected
                    .Where(w => !ReferenceEquals(w, primaryWindow) && w.WindowKind == WindowKind.Unknown)
                    .OrderByDescending(w => w.ResetsAt ?? DateTimeOffset.MinValue)
                    .FirstOrDefault();
            }
            return BuildQuota(primaryWindow, secondaryWindow);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>Flattens the <c>quotaInfo</c> / <c>quotaInfos</c> / <c>quotaInfoByTier</c> variants of one model entry.</summary>
    private static IEnumerable<AntigravityWindow> EnumerateQuotaInfos(JsonElement model)
    {
        var results = new List<AntigravityWindow>();
        void AddFrom(JsonElement element)
        {
            if (element.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in element.EnumerateArray())
                {
                    var parsed = ParseQuotaInfo(item);
                    if (parsed is not null)
                        results.Add(parsed);
                }
            }
            else if (element.ValueKind == JsonValueKind.Object)
            {
                var parsed = ParseQuotaInfo(element);
                if (parsed is not null)
                    results.Add(parsed);
            }
        }

        if (model.TryGetProperty("quotaInfo", out var quotaInfo))
            AddFrom(quotaInfo);
        if (model.TryGetProperty("quotaInfos", out var quotaInfos))
            AddFrom(quotaInfos);
        if (model.TryGetProperty("quotaInfoByTier", out var byTier) && byTier.ValueKind == JsonValueKind.Object)
        {
            foreach (var tier in byTier.EnumerateObject())
                AddFrom(tier.Value);
        }
        return results;
    }

    private static AntigravityWindow? ParseQuotaInfo(JsonElement info)
    {
        if (info.ValueKind != JsonValueKind.Object)
            return null;
        double? remainingFraction = Json.GetDouble(info, "remainingFraction");
        if (remainingFraction is null)
            return null;

        double usedPercent = Math.Clamp((1 - remainingFraction.Value) * 100, 0, 100);
        DateTimeOffset? resetsAt = ParseResetTime(Json.GetString(info, "resetTime"));
        string? windowId = Json.GetString(info, "windowId");
        string? windowLabel = Json.GetString(info, "windowLabel");
        bool exhausted = info.TryGetProperty("isExhausted", out var ex) && ex.ValueKind == JsonValueKind.True;

        WindowKind kind = ClassifyWindow(windowId, windowLabel, null);
        return new AntigravityWindow(usedPercent, resetsAt, kind, windowId, windowLabel, exhausted);
    }

    /// <summary>Parses the <c>retrieveUserQuotaSummary</c> response: <c>groups[].buckets[]</c> with <c>remainingFraction / resetTime / window</c>.</summary>
    public static AntigravityQuota? ParseQuotaSummary(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            JsonElement root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("groups", out JsonElement groups) ||
                groups.ValueKind != JsonValueKind.Array)
            {
                return null;
            }

            var buckets = new List<AntigravityWindow>();
            foreach (JsonElement group in groups.EnumerateArray())
            {
                if (group.ValueKind != JsonValueKind.Object || !group.TryGetProperty("buckets", out JsonElement bucketArr) ||
                    bucketArr.ValueKind != JsonValueKind.Array)
                {
                    continue;
                }
                foreach (JsonElement bucket in bucketArr.EnumerateArray())
                {
                    var window = ParseBucket(bucket);
                    if (window is not null)
                        buckets.Add(window);
                }
            }

            if (buckets.Count == 0)
                return null;

            AntigravityWindow? fiveHour = buckets.FirstOrDefault(w => w.WindowKind == WindowKind.FiveHour)
                ?? buckets.FirstOrDefault(w => w.WindowKind == WindowKind.Unknown && w.ResetsAt is not null);
            AntigravityWindow? sevenDay = buckets.FirstOrDefault(w => w.WindowKind == WindowKind.SevenDay);

            if (fiveHour is null && sevenDay is null)
            {
                // No labelled windows: assume the earliest reset is the 5h window, latest is the 7d window.
                fiveHour = buckets.Where(w => w.ResetsAt is not null).OrderBy(w => w.ResetsAt).FirstOrDefault();
                sevenDay = buckets.Where(w => w.ResetsAt is not null).OrderByDescending(w => w.ResetsAt).FirstOrDefault();
                if (fiveHour is not null && ReferenceEquals(fiveHour, sevenDay))
                    sevenDay = null;
            }
            else if (fiveHour is null && sevenDay is not null)
            {
                fiveHour = buckets.Where(w => w.WindowKind == WindowKind.Unknown).FirstOrDefault();
            }

            return BuildQuota(fiveHour, sevenDay);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static AntigravityWindow? ParseBucket(JsonElement bucket)
    {
        if (bucket.ValueKind != JsonValueKind.Object)
            return null;
        double? remainingFraction = Json.GetDouble(bucket, "remainingFraction");
        if (remainingFraction is null)
            return null;

        double usedPercent = Math.Clamp((1 - remainingFraction.Value) * 100, 0, 100);
        DateTimeOffset? resetsAt = ParseResetTime(Json.GetString(bucket, "resetTime"));
        string? window = Json.GetString(bucket, "window");
        string? displayName = Json.GetString(bucket, "displayName");
        WindowKind kind = ClassifyWindow(window, displayName, null);
        return new AntigravityWindow(usedPercent, resetsAt, kind, window, displayName, false);
    }

    private static DateTimeOffset? ParseResetTime(string? resetTime)
    {
        if (string.IsNullOrEmpty(resetTime))
            return null;
        return DateTimeOffset.TryParse(
            resetTime,
            System.Globalization.CultureInfo.InvariantCulture,
            System.Globalization.DateTimeStyles.AssumeUniversal | System.Globalization.DateTimeStyles.AdjustToUniversal,
            out var parsed)
            ? parsed
            : null;
    }

    /// <summary>Classifies a window as 5-hour, 7-day, or unknown by inspecting its id/label text.</summary>
    public static WindowKind ClassifyWindow(string? windowId, string? windowLabel, string? window)
    {
        string haystack = string.Join(' ', new[] { windowId, windowLabel, window }).ToLowerInvariant();
        if (haystack.Contains("7d") || haystack.Contains("7 day") || haystack.Contains("weekly") || haystack.Contains("week"))
            return WindowKind.SevenDay;
        if (haystack.Contains("5h") || haystack.Contains("5 hour") || haystack.Contains("five") ||
            haystack.Contains("hourly") || haystack.Contains("hour") || haystack.Contains("daily"))
        {
            return WindowKind.FiveHour;
        }
        return WindowKind.Unknown;
    }

    private static AntigravityWindow? PickPrimary(List<AntigravityWindow> windows)
    {
        // fetchAvailableModels quotaInfo has no window id/label; the Antigravity settings UI reports
        // this reading for the 5h window, so classify the picked primary as FiveHour.
        AntigravityWindow? primary = windows.FirstOrDefault(w => w.WindowKind == WindowKind.FiveHour)
            ?? windows.FirstOrDefault(w => w.WindowKind == WindowKind.Unknown)
            ?? windows.FirstOrDefault();
        if (primary is not null && primary.WindowKind == WindowKind.Unknown)
            primary = primary with { WindowKind = WindowKind.FiveHour };
        return primary;
    }

    private static AntigravityQuota BuildQuota(AntigravityWindow? fiveHour, AntigravityWindow? sevenDay)
        => new(fiveHour, sevenDay);

    public void Dispose() => _http.Dispose();
}

/// <summary>Window classification for Antigravity quota buckets.</summary>
public enum WindowKind
{
    Unknown = 0,
    FiveHour = 1,
    SevenDay = 2,
}

/// <summary>A parsed Antigravity quota window: <c>UsedPercent</c> is 0-100 already consumed.</summary>
public sealed record AntigravityWindow(double UsedPercent, DateTimeOffset? ResetsAt, WindowKind WindowKind, string? WindowId, string? WindowLabel, bool IsExhausted);

/// <summary>Parsed Antigravity quota result.</summary>
public sealed record AntigravityQuota(AntigravityWindow? Primary5h, AntigravityWindow? Secondary7d);

/// <summary>In-memory Antigravity credential (from the Windows Credential Manager). Never serialized.</summary>
internal sealed record AntigravityCredential(string? AccessToken, string? RefreshToken, DateTimeOffset? ExpiryUtc, string? ProjectId)
{
    /// <summary>Parses the <c>gemini:antigravity</c> credential JSON: <c>{ token: { access_token, token_type, refresh_token, expiry }, auth_method }</c>.</summary>
    public static AntigravityCredential? Parse(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            JsonElement root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
                return null;

            string? accessToken = null;
            string? refreshToken = null;
            DateTimeOffset? expiry = null;
            string? projectId = Json.GetString(root, "projectId") ?? Json.GetString(root, "project_id");

            if (root.TryGetProperty("token", out JsonElement token) && token.ValueKind == JsonValueKind.Object)
            {
                accessToken = Json.GetString(token, "access_token");
                refreshToken = Json.GetString(token, "refresh_token");
                string? expiryText = Json.GetString(token, "expiry");
                if (expiryText is not null && DateTimeOffset.TryParse(expiryText,
                        System.Globalization.CultureInfo.InvariantCulture,
                        System.Globalization.DateTimeStyles.AssumeUniversal | System.Globalization.DateTimeStyles.AdjustToUniversal,
                        out var parsedExpiry))
                {
                    expiry = parsedExpiry;
                }
            }

            if (string.IsNullOrEmpty(accessToken) && string.IsNullOrEmpty(refreshToken))
                return null;

            return new AntigravityCredential(accessToken, refreshToken, expiry, projectId);
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
