namespace Vantage.Core;

public enum DiagnosticSeverity
{
    Info,
    Warning,
    Error,
}

/// <summary>
/// Stable diagnostic codes. Bad input must fail visibly rather than silently distorting
/// counts, so every exclusion from progress or actionability emits one of these.
/// </summary>
public static class DiagnosticCode
{
    public const string TicketUnrecognized = "TICKET_UNRECOGNIZED";
    public const string TicketAmbiguousStatus = "TICKET_AMBIGUOUS_STATUS";
    public const string BlockerMissing = "BLOCKER_MISSING";
    public const string BlockerSelf = "BLOCKER_SELF";
    public const string BlockerCycle = "BLOCKER_CYCLE";
    public const string BlockerDuplicate = "BLOCKER_DUPLICATE";
    public const string EffortEmpty = "EFFORT_EMPTY";
    public const string GitUnavailable = "GIT_UNAVAILABLE";
    public const string NoRootsConfigured = "NO_ROOTS_CONFIGURED";
    public const string ProjectScanFailed = "PROJECT_SCAN_FAILED";
    public const string ScanTruncated = "SCAN_TRUNCATED";
    public const string CacheRebuilt = "CACHE_REBUILT";
    public const string SettingsRecovered = "SETTINGS_RECOVERED";
}

/// <summary>A visible explanation for something the dashboard could not interpret safely.</summary>
public sealed record Diagnostic(
    DiagnosticSeverity Severity,
    string Code,
    string Message,
    string Locator,
    Provenance? Provenance = null)
{
    public static Diagnostic Warning(string code, string message, string locator, Provenance? provenance = null) =>
        new(DiagnosticSeverity.Warning, code, message, locator, provenance);

    public static Diagnostic Error(string code, string message, string locator, Provenance? provenance = null) =>
        new(DiagnosticSeverity.Error, code, message, locator, provenance);

    public static Diagnostic Info(string code, string message, string locator, Provenance? provenance = null) =>
        new(DiagnosticSeverity.Info, code, message, locator, provenance);
}
