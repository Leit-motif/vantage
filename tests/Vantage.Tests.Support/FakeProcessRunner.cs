using Vantage.Infrastructure.Processes;

namespace Vantage.Tests.TestSupport;

/// <summary>
/// One external command as the dashboard actually invoked it, and when. The times are what makes
/// overlap observable: two index passes for one project are serialized only if their invocations
/// never interleave.
/// </summary>
public sealed record ProcessInvocation(
    string FileName,
    IReadOnlyList<string> Arguments,
    string? WorkingDirectory,
    DateTimeOffset StartedAt = default,
    DateTimeOffset CompletedAt = default);

/// <summary>
/// A controlled external-process boundary. Success, timeout, malformed output, partial data, and
/// cancellation are all deterministic here, without a mocking library.
/// </summary>
public sealed class FakeProcessRunner : IProcessRunner
{
    private readonly List<(Func<string, IReadOnlyList<string>, bool> Match, Func<CancellationToken, Task<ProcessResult>> Respond)> _rules = [];
    private readonly Lock _gate = new();
    private readonly List<ProcessInvocation> _invocations = [];

    /// <summary>
    /// The commands that ran, in completion order. A copy, because the refresh invokes this from
    /// several workers at once and a caller reading the list mid-refresh must not see it mutate.
    /// </summary>
    public IReadOnlyList<ProcessInvocation> Invocations
    {
        get
        {
            lock (_gate)
            {
                return [.. _invocations];
            }
        }
    }

    /// <summary>When set, every call delegates here instead — used to exercise real git.</summary>
    public IProcessRunner? Fallback { get; set; }

    public FakeProcessRunner When(Func<string, IReadOnlyList<string>, bool> match, Func<ProcessResult> respond) =>
        When(match, _ => Task.FromResult(respond()));

    public FakeProcessRunner When(
        Func<string, IReadOnlyList<string>, bool> match,
        Func<CancellationToken, Task<ProcessResult>> respond)
    {
        _rules.Add((match, respond));
        return this;
    }

    /// <summary>
    /// Makes a command take real time, so that whether two passes overlap is something the
    /// recorded invocations can actually answer.
    /// </summary>
    public FakeProcessRunner Slow(string fileName, string argument, TimeSpan duration, string stdout = "") =>
        When(
            (name, args) => name == fileName && args.Contains(argument),
            async token =>
            {
                await Task.Delay(duration, token).ConfigureAwait(false);
                return Ok(stdout);
            });

    public FakeProcessRunner WhenCommand(string fileName, string firstArgument, ProcessResult result) =>
        When(
            (name, args) => name == fileName && args.Count > 0 && args.Contains(firstArgument),
            () => result);

    /// <summary>Makes a command fail unexpectedly, so failure isolation can be exercised.</summary>
    public FakeProcessRunner Throwing(string fileName, string firstArgument) =>
        When(
            (name, args) => name == fileName && args.Contains(firstArgument),
            () => throw new InvalidOperationException($"{fileName} exploded"));

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
        var startedAt = DateTimeOffset.UtcNow;

        try
        {
            foreach (var (match, respond) in _rules)
            {
                if (match(fileName, arguments))
                {
                    return await respond(cancellationToken).ConfigureAwait(false);
                }
            }

            if (Fallback is not null)
            {
                return await Fallback.RunAsync(fileName, arguments, workingDirectory, cancellationToken).ConfigureAwait(false);
            }

            return new ProcessResult(1, string.Empty, $"no rule for {fileName} {string.Join(' ', arguments)}", false, false);
        }
        finally
        {
            lock (_gate)
            {
                _invocations.Add(new ProcessInvocation(
                    fileName,
                    [.. arguments],
                    workingDirectory,
                    startedAt,
                    DateTimeOffset.UtcNow));
            }
        }
    }
}
