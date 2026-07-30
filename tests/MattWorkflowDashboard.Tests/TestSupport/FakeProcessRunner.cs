using MattWorkflowDashboard.Infrastructure.Processes;

namespace MattWorkflowDashboard.Tests.TestSupport;

/// <summary>One external command as the dashboard actually invoked it.</summary>
public sealed record ProcessInvocation(string FileName, IReadOnlyList<string> Arguments, string? WorkingDirectory);

/// <summary>
/// A controlled external-process boundary. Success, authentication loss, timeout, malformed
/// output, partial data, and cancellation are all deterministic here, without a mocking library.
/// </summary>
public sealed class FakeProcessRunner : IProcessRunner
{
    private readonly List<(Func<string, IReadOnlyList<string>, bool> Match, Func<ProcessResult> Respond)> _rules = [];

    public List<ProcessInvocation> Invocations { get; } = [];

    /// <summary>When set, every call delegates here instead — used to exercise real git.</summary>
    public IProcessRunner? Fallback { get; set; }

    public FakeProcessRunner When(Func<string, IReadOnlyList<string>, bool> match, Func<ProcessResult> respond)
    {
        _rules.Add((match, respond));
        return this;
    }

    public FakeProcessRunner WhenCommand(string fileName, string firstArgument, ProcessResult result) =>
        When(
            (name, args) => name == fileName && args.Count > 0 && args.Contains(firstArgument),
            () => result);

    public FakeProcessRunner GhAuthenticated() =>
        WhenCommand("gh", "auth", Ok(string.Empty));

    public FakeProcessRunner GhUnauthenticated() =>
        WhenCommand("gh", "auth", new ProcessResult(1, string.Empty, "gh auth login required: no token found", false, false));

    public FakeProcessRunner GhIssues(string json) =>
        WhenCommand("gh", "issue", Ok(json));

    public static ProcessResult Ok(string stdout) => new(0, stdout, string.Empty, false, false);

    public static ProcessResult TimedOut() => new(-1, string.Empty, string.Empty, true, false);

    public static ProcessResult NotFound() => new(-1, string.Empty, "not found", false, true);

    public async Task<ProcessResult> RunAsync(
        string fileName,
        IReadOnlyList<string> arguments,
        string? workingDirectory,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Invocations.Add(new ProcessInvocation(fileName, [.. arguments], workingDirectory));

        foreach (var (match, respond) in _rules)
        {
            if (match(fileName, arguments))
            {
                return respond();
            }
        }

        if (Fallback is not null)
        {
            return await Fallback.RunAsync(fileName, arguments, workingDirectory, cancellationToken).ConfigureAwait(false);
        }

        return new ProcessResult(1, string.Empty, $"no rule for {fileName} {string.Join(' ', arguments)}", false, false);
    }
}
