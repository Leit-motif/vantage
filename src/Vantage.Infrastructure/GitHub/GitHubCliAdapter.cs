using System.Text.Json;
using Vantage.Core;
using Vantage.Core.Observed;
using Vantage.Core.Workflow;
using Vantage.Infrastructure.Processes;

namespace Vantage.Infrastructure.GitHub;

public sealed record GitHubReadResult(
    bool Available,
    IReadOnlyList<ObservedGitHubIssue> Issues,
    IReadOnlyList<Diagnostic> Diagnostics)
{
    public static GitHubReadResult Unavailable(IReadOnlyList<Diagnostic> diagnostics) => new(false, [], diagnostics);
}

/// <summary>
/// Enriches local facts through the owner's already-authenticated <c>gh</c> session. Queries are
/// scoped to repositories explicitly associated with a local project, so the dashboard never
/// enumerates the account, and no separate token is ever required or stored.
/// </summary>
public sealed class GitHubCliAdapter(IProcessRunner runner, int maxIssues = 200)
{
    // The body carries the workflow metadata a linked ticket can disagree with, and the comment
    // count is qualifying activity in its own right.
    private const string Fields = "number,title,state,labels,assignees,updatedAt,closedAt,url,body,comments";

    /// <summary>Authentication is probed once per refresh, not once per repository.</summary>
    public async Task<bool> IsAuthenticatedAsync(CancellationToken cancellationToken)
    {
        var result = await runner.RunAsync("gh", ["auth", "status"], null, cancellationToken).ConfigureAwait(false);
        return result.Succeeded;
    }

    public async Task<GitHubReadResult> ReadIssuesAsync(
        GitHubOrigin origin,
        string refreshId,
        CancellationToken cancellationToken)
    {
        var diagnostics = new List<Diagnostic>();

        var result = await runner.RunAsync(
            "gh",
            [
                "issue", "list",
                "--repo", origin.Slug,
                "--state", "all",
                "--limit", maxIssues.ToString(),
                "--json", Fields,
            ],
            null,
            cancellationToken).ConfigureAwait(false);

        if (!result.Succeeded)
        {
            diagnostics.Add(Classify(result, origin));
            return GitHubReadResult.Unavailable(diagnostics);
        }

        try
        {
            var issues = Parse(result.StandardOutput, origin, refreshId);
            if (issues.Count == maxIssues)
            {
                diagnostics.Add(Diagnostic.Info(
                    DiagnosticCode.ScanTruncated,
                    $"Only the first {maxIssues} issues of {origin.Slug} were read; raise the limit in Settings to see more.",
                    origin.Slug));
            }

            return new GitHubReadResult(true, issues, diagnostics);
        }
        catch (JsonException ex)
        {
            diagnostics.Add(Diagnostic.Warning(
                DiagnosticCode.GitHubMalformedOutput,
                $"gh returned output for {origin.Slug} that could not be read: {ex.Message}",
                origin.Slug));
            return GitHubReadResult.Unavailable(diagnostics);
        }
    }

    private static IReadOnlyList<ObservedGitHubIssue> Parse(string json, GitHubOrigin origin, string refreshId)
    {
        using var document = JsonDocument.Parse(json);
        if (document.RootElement.ValueKind != JsonValueKind.Array)
        {
            throw new JsonException($"Expected an array of issues, got {document.RootElement.ValueKind}.");
        }

        var issues = new List<ObservedGitHubIssue>();

        foreach (var element in document.RootElement.EnumerateArray())
        {
            // Partial data is normal: an issue missing a field is still worth showing.
            if (!element.TryGetProperty("number", out var numberElement) || !numberElement.TryGetInt32(out var number))
            {
                continue;
            }

            var updatedAt = ReadDate(element, "updatedAt") ?? DateTimeOffset.MinValue;
            var url = ReadString(element, "url") ?? $"https://github.com/{origin.Slug}/issues/{number}";

            issues.Add(new ObservedGitHubIssue(
                number,
                ReadString(element, "title") ?? $"#{number}",
                ReadString(element, "state") ?? "OPEN",
                ReadNames(element, "labels"),
                ReadNames(element, "assignees", "login"),
                updatedAt,
                ReadDate(element, "closedAt"),
                CountComments(element),
                url,
                ReadString(element, "body") ?? string.Empty,
                Provenance.GitHub($"{origin.Slug}#{number}", updatedAt, refreshId)));
        }

        return issues;
    }

    private static int CountComments(JsonElement element) =>
        element.TryGetProperty("comments", out var comments) && comments.ValueKind == JsonValueKind.Array
            ? comments.GetArrayLength()
            : 0;

    private static string? ReadString(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static DateTimeOffset? ReadDate(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value)
        && value.ValueKind == JsonValueKind.String
        && DateTimeOffset.TryParse(value.GetString(), out var parsed)
            ? parsed
            : null;

    private static IReadOnlyList<string> ReadNames(JsonElement element, string property, string key = "name")
    {
        if (!element.TryGetProperty(property, out var array) || array.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        var names = new List<string>();
        foreach (var item in array.EnumerateArray())
        {
            var name = item.ValueKind == JsonValueKind.String ? item.GetString() : ReadString(item, key);
            if (!string.IsNullOrWhiteSpace(name))
            {
                names.Add(name);
            }
        }

        return names;
    }

    /// <summary>
    /// Distinguishes the failures that mean different things to the owner: a missing tool, a lost
    /// session, a hung call, and an ordinary API failure are not the same problem.
    /// </summary>
    private static Diagnostic Classify(ProcessResult result, GitHubOrigin origin)
    {
        if (result.NotFound)
        {
            return Diagnostic.Info(
                DiagnosticCode.GitHubUnavailable,
                "gh was not found on PATH; GitHub enrichment is disabled.",
                origin.Slug);
        }

        if (result.TimedOut)
        {
            return Diagnostic.Warning(
                DiagnosticCode.GitHubUnavailable,
                $"gh timed out reading {origin.Slug}; cached GitHub evidence is shown instead.",
                origin.Slug);
        }

        var error = result.StandardError;
        if (error.Contains("auth", StringComparison.OrdinalIgnoreCase)
            && (error.Contains("login", StringComparison.OrdinalIgnoreCase)
                || error.Contains("token", StringComparison.OrdinalIgnoreCase)))
        {
            return Diagnostic.Warning(
                DiagnosticCode.GitHubUnauthenticated,
                $"The gh session is not authenticated for {origin.Slug}; run 'gh auth login' to restore enrichment.",
                origin.Slug);
        }

        return Diagnostic.Warning(
            DiagnosticCode.GitHubUnavailable,
            $"gh could not read {origin.Slug}: {error.Split('\n').FirstOrDefault()?.Trim() ?? $"exit code {result.ExitCode}"}",
            origin.Slug);
    }
}
