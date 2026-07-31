using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using Vantage.Infrastructure.Processes;

namespace Vantage.Infrastructure.Acceptance;

/// <summary>
/// A command this runner would not let out. The arguments themselves are never kept: a refusal is
/// exactly the moment when a command line is most likely to hold a private absolute path, and this
/// record is serialized into committed evidence. The shape says what was attempted, and the digest
/// tells two refusals apart without saying what either was about.
/// </summary>
public sealed record RefusedCommand(string FileName, string Shape, int ArgumentCount, string ArgumentsDigest);

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
    /// <summary>
    /// Options that make an otherwise reading command write, or make it run something. A verb
    /// allowlist alone is not structural: <c>git log --output=&lt;path&gt;</c> is a read by verb and
    /// creates a file, and <c>-c</c> injects configuration that can point git at a program to
    /// execute. These are refused wherever they appear, on any command.
    /// </summary>
    private static readonly string[] DangerousOptions =
    [
        "--output", "--output-directory", "-o",
        "-c", "--config-env",
        "--git-dir", "--work-tree",
        "--exec", "--exec-path", "--upload-pack", "--receive-pack",
        "--edit", "-e", "-i", "--interactive",
    ];

    private readonly ConcurrentBag<RefusedCommand> _refused = [];
    private readonly ConcurrentDictionary<string, int> _issued = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, byte> _repositories = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Commands stopped at this boundary. Anything here is a safety failure, not a warning.</summary>
    public IReadOnlyList<RefusedCommand> Refused => [.. _refused];

    /// <summary>Each distinct command shape that ran, and how often — the run's whole outward footprint.</summary>
    public IReadOnlyDictionary<string, int> Issued => _issued;

    /// <summary>
    /// Every repository <c>gh</c> was pointed at. The dashboard must never enumerate an account, so
    /// what matters is not only that the calls were reads but that each one named a repository the
    /// owner's own projects are associated with.
    /// </summary>
    public IReadOnlyCollection<string> RepositoriesQueried => [.. _repositories.Keys];

    public Task<ProcessResult> RunAsync(
        string fileName,
        IReadOnlyList<string> arguments,
        string? workingDirectory,
        CancellationToken cancellationToken)
    {
        if (!IsReadOnly(fileName, arguments))
        {
            _refused.Add(new RefusedCommand(
                fileName,
                Shape(fileName, arguments),
                arguments.Count,
                Digest(arguments)));

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

    public static bool IsReadOnly(string fileName, IReadOnlyList<string> arguments)
    {
        // Checked before the verb, and for every command: an option that writes a file or points
        // the tool at a program to run is a write whatever the verb in front of it says.
        foreach (var argument in arguments)
        {
            var name = argument.Split('=', 2)[0];
            if (Array.Exists(DangerousOptions, option => string.Equals(option, name, StringComparison.Ordinal)))
            {
                return false;
            }
        }

        return fileName switch
        {
            "git" => IsReadOnlyGit(arguments),
            "gh" => IsReadOnlyGh(arguments),

            // Nothing else has a read-only story here, so nothing else is allowed one.
            _ => false,
        };
    }

    private static bool IsReadOnlyGit(IReadOnlyList<string> arguments) => Verb(arguments) switch
    {
        "rev-parse" or "log" or "status" or "show-ref" or "for-each-ref" or "rev-list"
            or "ls-files" or "cat-file" or "version" or "describe" or "symbolic-ref" => true,

        // Both have plenty of subcommands that write; only the reading ones are allowed, and a
        // reading flag alongside a writing one is a write.
        "remote" => arguments.Contains("get-url") || arguments.Contains("show"),
        "config" => arguments.Any(a => a is "--get" or "--get-all" or "--get-regexp" or "--list" or "-l")
            && !arguments.Any(a => a is "--unset" or "--unset-all" or "--add" or "--replace-all"),

        _ => false,
    };

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
    /// The command without any of its subjects: verbs only, so the footprint can be reported without
    /// naming a repository, a path, or an issue.
    /// </summary>
    private static string Shape(string fileName, IReadOnlyList<string> arguments) => fileName switch
    {
        "git" => $"git {Verb(arguments)}{(Verb(arguments) is "remote" or "config" ? " " + SubVerb(arguments) : string.Empty)}".Trim(),
        "gh" => $"gh {(arguments.Count > 0 ? arguments[0] : string.Empty)} {(arguments.Count > 1 ? arguments[1] : string.Empty)}".Trim(),
        _ => fileName,
    };

    /// <summary>
    /// The verb, past whatever global options came first. <c>-C &lt;path&gt;</c> takes a value, so
    /// skipping the option alone would read that value as the verb.
    /// </summary>
    private static string Verb(IReadOnlyList<string> arguments)
    {
        for (var i = 0; i < arguments.Count; i++)
        {
            if (arguments[i] is "-C" or "--namespace")
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
        for (var i = 0; i < arguments.Count - 1; i++)
        {
            if (string.Equals(arguments[i], verb, StringComparison.Ordinal))
            {
                return arguments[i + 1];
            }
        }

        return string.Empty;
    }

    /// <summary>A separator no argument can contain, so two argument lists cannot collide.</summary>
    private const string ArgumentSeparator = "\u001f";

    private static string Digest(IReadOnlyList<string> arguments) =>
        Convert.ToHexStringLower(
            SHA256.HashData(Encoding.UTF8.GetBytes(string.Join(ArgumentSeparator, arguments))))[..12];
}
