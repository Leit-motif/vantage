using System.Collections.Concurrent;
using MattWorkflowDashboard.Infrastructure.Processes;

namespace MattWorkflowDashboard.Infrastructure.Acceptance;

/// <summary>A command this runner would not let out, and the command line it would have been.</summary>
public sealed record RefusedCommand(string FileName, string Arguments);

/// <summary>
/// The external-process boundary with the read-only promise made structural. Every command is
/// checked against what the dashboard is allowed to ask of git and <c>gh</c> before it runs, and
/// anything else is refused rather than executed.
/// <para>
/// This exists for the run over the owner's real workspaces, where "the adapters only read" has to
/// be something the run cannot violate rather than something the code review concluded. An empty
/// refusal list is then evidence: not that nothing was checked, but that nothing was stopped.
/// </para>
/// </summary>
public sealed class ReadOnlyProcessRunner(IProcessRunner inner) : IProcessRunner
{
    private readonly ConcurrentBag<RefusedCommand> _refused = [];
    private readonly ConcurrentDictionary<string, int> _issued = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, byte> _repositories = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Every repository <c>gh</c> was pointed at. The dashboard must never enumerate an account, so
    /// what matters is not only that the calls were reads but that each one named a repository the
    /// owner's own projects are associated with.
    /// </summary>
    public IReadOnlyCollection<string> RepositoriesQueried => [.. _repositories.Keys];

    /// <summary>Commands stopped at this boundary. Anything here is a safety failure, not a warning.</summary>
    public IReadOnlyList<RefusedCommand> Refused => [.. _refused];

    /// <summary>Each distinct command shape that ran, and how often — the run's whole outward footprint.</summary>
    public IReadOnlyDictionary<string, int> Issued => _issued;

    public Task<ProcessResult> RunAsync(
        string fileName,
        IReadOnlyList<string> arguments,
        string? workingDirectory,
        CancellationToken cancellationToken)
    {
        if (!IsReadOnly(fileName, arguments))
        {
            _refused.Add(new RefusedCommand(fileName, string.Join(' ', arguments)));
            return Task.FromResult(new ProcessResult(
                -1,
                string.Empty,
                $"'{fileName}' was refused: only read-only commands run against a live workspace.",
                TimedOut: false,
                NotFound: false));
        }

        _issued.AddOrUpdate(Shape(fileName, arguments), 1, (_, count) => count + 1);

        for (var i = 0; i < arguments.Count - 1; i++)
        {
            if (arguments[i] is "--repo" or "-R")
            {
                _repositories.TryAdd(arguments[i + 1], 0);
            }
        }

        return inner.RunAsync(fileName, arguments, workingDirectory, cancellationToken);
    }

    /// <summary>
    /// The command without any of its subjects: verbs only, so the footprint can be reported without
    /// naming a repository, a path, or an issue.
    /// </summary>
    private static string Shape(string fileName, IReadOnlyList<string> arguments) => fileName switch
    {
        "git" => $"git {Verb(arguments)}{(Verb(arguments) is "remote" or "config" ? " " + SubVerb(arguments) : string.Empty)}",
        "gh" => $"gh {(arguments.Count > 0 ? arguments[0] : string.Empty)} {(arguments.Count > 1 ? arguments[1] : string.Empty)}".TrimEnd(),
        _ => fileName,
    };

    public static bool IsReadOnly(string fileName, IReadOnlyList<string> arguments) => fileName switch
    {
        "git" => IsReadOnlyGit(arguments),
        "gh" => IsReadOnlyGh(arguments),

        // Nothing else has a read-only story here, so nothing else is allowed one.
        _ => false,
    };

    private static bool IsReadOnlyGit(IReadOnlyList<string> arguments)
    {
        var verb = Verb(arguments);

        return verb switch
        {
            "rev-parse" or "log" or "status" or "show-ref" or "for-each-ref" or "rev-list"
                or "ls-files" or "cat-file" or "version" or "describe" or "symbolic-ref" => true,

            // Both have plenty of subcommands that write; only the reading ones are allowed, and a
            // reading flag alongside a writing one is a write.
            "remote" => arguments.Contains("get-url") || arguments.Contains("show"),
            "config" => arguments.Any(a => a is "--get" or "--get-all" or "--get-regexp" or "--list" or "-l")
                && !arguments.Any(a => a is "--unset" or "--unset-all" or "--add" or "--replace-all" or "--edit" or "-e"),

            _ => false,
        };
    }

    /// <summary>
    /// Exactly the four <c>gh</c> reads this dashboard makes, and nothing else. <c>gh repo list</c>
    /// and <c>gh api</c> are refused although both can be perfectly read-only: each is a way to
    /// enumerate an account, which the product promises never to do. Widening this list is a
    /// decision to be taken deliberately, here.
    /// </summary>
    private static bool IsReadOnlyGh(IReadOnlyList<string> arguments) =>
        arguments.Count >= 2
        && (arguments[0], arguments[1]) is ("auth", "status")
            or ("issue", "list")
            or ("issue", "view")
            or ("label", "list");

    /// <summary>
    /// The verb, past whatever global options came first. <c>-C &lt;path&gt;</c> and
    /// <c>-c &lt;name=value&gt;</c> each take a value, so skipping the option alone would read that
    /// value as the verb.
    /// </summary>
    private static string Verb(IReadOnlyList<string> arguments)
    {
        for (var i = 0; i < arguments.Count; i++)
        {
            if (arguments[i] is "-C" or "-c" or "--git-dir" or "--work-tree" or "--namespace")
            {
                i++;
                continue;
            }

            if (arguments[i].StartsWith('-'))
            {
                continue;
            }

            return arguments[i];
        }

        return string.Empty;
    }

    private static string SubVerb(IReadOnlyList<string> arguments)
    {
        var verb = Verb(arguments);
        var index = arguments.ToList().IndexOf(verb);
        return index >= 0 && index + 1 < arguments.Count ? arguments[index + 1] : string.Empty;
    }
}
