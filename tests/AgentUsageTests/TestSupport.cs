using System.Net;
using System.Text;
using Microsoft.Data.Sqlite;
using Xunit;

namespace AgentUsageTests;

/// <summary>
/// A configurable <see cref="HttpMessageHandler"/> that never touches the network. Routes requests
/// by method + path suffix to canned responses, and records outbound headers for assertions.
/// </summary>
public sealed class StubHttpHandler : HttpMessageHandler
{
    private sealed record Route(string Method, string PathSuffix, Func<HttpRequestMessage, (int Status, string Body)> Responder);

    private readonly List<Route> _routes = new();
    private readonly List<(HttpRequestMessage Request, HttpResponseMessage Response)> _seen = new();
    private readonly object _gate = new();

    public int RequestCount
    {
        get { lock (_gate) return _seen.Count; }
    }

    public StubHttpHandler On(string method, string pathSuffix, (int Status, string Body) response)
        => On(method, pathSuffix, _ => response);

    public StubHttpHandler On(string method, string pathSuffix, Func<HttpRequestMessage, (int Status, string Body)> responder)
    {
        lock (_gate)
            _routes.Add(new Route(method.ToUpperInvariant(), pathSuffix, responder));
        return this;
    }

    public IReadOnlyList<HttpRequestMessage> Requests
    {
        get
        {
            lock (_gate)
                return _seen.Select(x => x.Request).ToList();
        }
    }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        Route? route;
        lock (_gate)
            route = _routes.FirstOrDefault(r =>
                string.Equals(r.Method, request.Method.Method, StringComparison.OrdinalIgnoreCase) &&
                (request.RequestUri?.AbsolutePath.EndsWith(r.PathSuffix, StringComparison.OrdinalIgnoreCase) ?? false));

        if (route is null)
            throw new HttpRequestException($"No stub for {request.Method} {request.RequestUri}");

        (int status, string body) = route.Responder(request);
        var response = new HttpResponseMessage((HttpStatusCode)status)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
            RequestMessage = request,
        };

        lock (_gate)
            _seen.Add((request, response));
        return await Task.FromResult(response);
    }
}

/// <summary>Throws on every request — simulates a total network failure.</summary>
public sealed class NetworkDownHandler : HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        => throw new HttpRequestException("simulated network failure (no connection)");
}

/// <summary>Builds in-memory / temp-file SQLite databases matching the production schemas.</summary>
public static class TestDb
{
    /// <summary>Creates a temp file with the 9router <c>usageHistory</c> schema and returns its path.</summary>
    public static string CreateUsageHistoryDb(string provider, IEnumerable<(string TimestampUtc, long Prompt, long Completion, double Cost, string Status)> rows)
    {
        string path = TempFile();
        using var connection = OpenReadWrite(path);
        using (var create = connection.CreateCommand())
        {
            create.CommandText =
                "CREATE TABLE usageHistory (" +
                "id INTEGER PRIMARY KEY, " +
                "timestamp TEXT NOT NULL, " +
                "provider TEXT, model TEXT, connectionId TEXT, apiKey TEXT, endpoint TEXT, " +
                "promptTokens INTEGER DEFAULT 0, completionTokens INTEGER DEFAULT 0, " +
                "cost REAL DEFAULT 0, status TEXT, tokens TEXT, meta TEXT);";
            create.ExecuteNonQuery();
        }
        using var insert = connection.CreateCommand();
        insert.CommandText =
            "INSERT INTO usageHistory (timestamp, provider, model, promptTokens, completionTokens, cost, status) " +
            "VALUES ($ts, $provider, $model, $prompt, $completion, $cost, $status);";
        var ts = insert.CreateParameter();
        ts.ParameterName = "$ts";
        var pr = insert.CreateParameter();
        pr.ParameterName = "$provider";
        var model = insert.CreateParameter();
        model.ParameterName = "$model";
        var prompt = insert.CreateParameter();
        prompt.ParameterName = "$prompt";
        var completion = insert.CreateParameter();
        completion.ParameterName = "$completion";
        var cost = insert.CreateParameter();
        cost.ParameterName = "$cost";
        var status = insert.CreateParameter();
        status.ParameterName = "$status";
        insert.Parameters.AddRange(new[] { ts, pr, model, prompt, completion, cost, status });

        foreach (var (timestamp, p, c, co, st) in rows)
        {
            ts.Value = timestamp;
            pr.Value = provider;
            model.Value = "deepseek/deepseek-v4-flash";
            prompt.Value = p;
            completion.Value = c;
            cost.Value = co;
            status.Value = st;
            insert.ExecuteNonQuery();
        }
        connection.Close();
        return path;
    }

    /// <summary>Creates a temp file with the opencode <c>session</c> schema and returns its path.</summary>
    public static string CreateSessionDb(IEnumerable<(long TimeCreatedMs, double Cost, long Input, long Output, long Reasoning, long CacheRead, long CacheWrite)> rows)
    {
        string path = TempFile();
        using var connection = OpenReadWrite(path);
        using (var create = connection.CreateCommand())
        {
            create.CommandText =
                "CREATE TABLE session (" +
                "id TEXT PRIMARY KEY, project_id TEXT NOT NULL, workspace_id TEXT, parent_id TEXT, " +
                "slug TEXT NOT NULL, directory TEXT NOT NULL, path TEXT, title TEXT NOT NULL, version TEXT, " +
                "cost REAL NOT NULL DEFAULT 0, " +
                "tokens_input INTEGER NOT NULL DEFAULT 0, tokens_output INTEGER NOT NULL DEFAULT 0, " +
                "tokens_reasoning INTEGER NOT NULL DEFAULT 0, tokens_cache_read INTEGER NOT NULL DEFAULT 0, " +
                "tokens_cache_write INTEGER NOT NULL DEFAULT 0, " +
                "model TEXT, agent TEXT, time_created INTEGER NOT NULL, time_updated INTEGER);";
            create.ExecuteNonQuery();
        }
        using var insert = connection.CreateCommand();
        insert.CommandText =
            "INSERT INTO session (id, project_id, slug, directory, title, cost, tokens_input, tokens_output, " +
            "tokens_reasoning, tokens_cache_read, tokens_cache_write, model, agent, time_created) " +
            "VALUES ($id, 'p', $slug, 'd', 't', $cost, $input, $output, $reasoning, $cacheRead, $cacheWrite, " +
            "'{\"id\":\"big-pickle\"}', 'build', $time);";
        var id = insert.CreateParameter();
        id.ParameterName = "$id";
        var slug = insert.CreateParameter();
        slug.ParameterName = "$slug";
        var cost = insert.CreateParameter();
        cost.ParameterName = "$cost";
        var input = insert.CreateParameter();
        input.ParameterName = "$input";
        var output = insert.CreateParameter();
        output.ParameterName = "$output";
        var reasoning = insert.CreateParameter();
        reasoning.ParameterName = "$reasoning";
        var cacheRead = insert.CreateParameter();
        cacheRead.ParameterName = "$cacheRead";
        var cacheWrite = insert.CreateParameter();
        cacheWrite.ParameterName = "$cacheWrite";
        var time = insert.CreateParameter();
        time.ParameterName = "$time";
        insert.Parameters.AddRange(new[] { id, slug, cost, input, output, reasoning, cacheRead, cacheWrite, time });

        int n = 0;
        foreach (var (timeMs, co, i, o, r, cr, cw) in rows)
        {
            n++;
            id.Value = $"sess-{n}";
            slug.Value = $"slug-{n}";
            cost.Value = co;
            input.Value = i;
            output.Value = o;
            reasoning.Value = r;
            cacheRead.Value = cr;
            cacheWrite.Value = cw;
            time.Value = timeMs;
            insert.ExecuteNonQuery();
        }
        connection.Close();
        return path;
    }

    public static string TempFile()
    {
        string path = Path.Combine(Path.GetTempPath(), "AgentUsageTests-" + Guid.NewGuid().ToString("N") + ".sqlite");
        File.Delete(path);
        return path;
    }

    private static SqliteConnection OpenReadWrite(string path)
    {
        var builder = new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Pooling = false,
        };
        var connection = new SqliteConnection(builder.ToString());
        connection.Open();
        return connection;
    }

    /// <summary>Formats a UTC offset as the DB's ISO-8601 text (<c>2026-08-10T09:00:00.000Z</c>).</summary>
    public static string IsoUtc(DateTimeOffset value)
        => value.ToUniversalTime().UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'");

    /// <summary>Deletes the temp DB file if present (best-effort cleanup).</summary>
    public static void Delete(string path)
    {
        try
        {
            if (File.Exists(path))
                File.Delete(path);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}

/// <summary>Asserts a token never leaks into a string.</summary>
public static class AssertNoSecret
{
    public static void DoesNotContain(string? text, string secret)
    {
        Assert.DoesNotContain(secret, text ?? string.Empty);
    }
}
