namespace TaskbarMonitor;

/// <summary>
/// Opt-in diagnostic surface for expected environment failures. Nothing is
/// written anywhere unless a subscriber is attached; the app never logs.
/// Exceptions are reported with a redacted context string — never with
/// credentials, tokens, or PII.
/// </summary>
public static class Diagnostics
{
    /// <summary>Raised when an expected reader/environment failure occurs.
    /// Subscribe in a debug build or troubleshooting session only.</summary>
    public static event Action<string, Exception>? ReaderFailed;

    public static void ReportReaderFailure(string source, Exception ex)
        => ReaderFailed?.Invoke(source, ex);
}
