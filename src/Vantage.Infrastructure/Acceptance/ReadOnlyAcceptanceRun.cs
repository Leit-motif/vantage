using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Vantage.Core;
using Vantage.Infrastructure.Discovery;
using Vantage.Infrastructure.Persistence;
using Vantage.Infrastructure.Processes;
using Vantage.Infrastructure.Refresh;
using Vantage.Infrastructure.Settings;

namespace Vantage.Infrastructure.Acceptance;

public sealed record AcceptanceEnvironment(
    string OperatingSystem,
    string Runtime,
    string Architecture,
    int ProcessorCount,
    string Commit,
    string StartedAtUtc);

public sealed record AcceptanceBounds(
    int Roots,
    int MaxDiscoveryDepth,
    int MaxProjects,
    int MaxDirectoriesScanned,
    int MaxConcurrentExternalProcesses,
    int ExternalProcessTimeoutSeconds);

public sealed record AcceptancePass(
    string Name,
    long ElapsedMilliseconds,
    int Projects,
    int ProjectsWithProgress,
    int ProjectsWithRemainingWork,
    int StaleProjects,
    bool ScanTruncated,
    int PeakConcurrentProcesses,
    IReadOnlyList<string> DiagnosticCodes);

/// <summary>
/// How long a refresh kept going after it was told to stop — and, because that number means
/// nothing without it, what the refresh was doing at the time. A pass cancelled while it is still
/// walking the roots stops almost instantly and proves only the easy half.
/// </summary>
public sealed record CancellationEvidence(
    long WaitedForIndexingMilliseconds,
    int ExternalCommandsSubmittedBeforeCancelling,
    int ChildProcessesRunningWhenCancelled,
    bool StillRunningWhenCancelled,
    double LatencyMilliseconds,
    bool StoppedByCancellation);

public sealed record SafetyEvidence(
    int ProjectsFingerprinted,
    IReadOnlyList<MonitoredStateChange> MonitoredStateChanges,
    IReadOnlyList<FingerprintGap> FingerprintGaps,
    IReadOnlyList<RefusedCommand> RefusedCommands,
    IReadOnlyDictionary<string, int> CommandsIssued);

/// <summary>
/// The whole run, in a form that can be committed. Every project and repository appears as a
/// per-run salted identifier, so the report proves what happened without carrying the names of
/// private work out of the machine it ran on.
/// </summary>
public sealed record AcceptanceReport(
    AcceptanceEnvironment Environment,
    AcceptanceBounds Bounds,
    IReadOnlyList<AcceptancePass> Passes,
    CancellationEvidence Cancellation,
    SafetyEvidence Safety)
{
    /// <summary>Nothing the run observed moved, and nothing it tried to do was stopped.</summary>
    [JsonIgnore]
    public bool NothingWasChanged =>
        Safety.MonitoredStateChanges.Count == 0 && Safety.RefusedCommands.Count == 0;

    /// <summary>
    /// Everything the run set out to observe, it actually saw. Kept apart from
    /// <see cref="NothingWasChanged"/> deliberately: a source that could not be read is not evidence
    /// that it is unchanged, but neither is it a safety failure — a private or deleted repository is
    /// an ordinary fact of a real workspace. Reporting them as one thing would either make a clean
    /// run impossible or let the strongest report be the one that observed the least.
    /// </summary>
    [JsonIgnore]
    public bool ObservationWasComplete => Safety.FingerprintGaps.Count == 0;
}

/// <summary>
/// A read-only pass of the real dashboard over the real configured roots, with the evidence the
/// acceptance slice asks for taken as it goes.
/// <para>
/// Development-only instrumentation: it is reached by an explicit command-line switch, never by
/// ordinary use, and nothing it produces leaves the machine. It writes no dashboard state where
/// the running dashboard keeps its own — the cache and settings it needs live under the output
/// directory — so an acceptance run cannot damage either the owner's workspaces or the owner's
/// dashboard.
/// </para>
/// </summary>
public sealed class ReadOnlyAcceptanceRun(
    DashboardSettings settings,
    AppPaths isolatedState,
    string commit)
{
    /// <summary>
    /// How long the cancellation pass will wait for indexing to start before giving up on waiting
    /// and cancelling anyway. The wait itself is what matters: cancelling on a timer would land in
    /// whatever phase the timer happened to fall in.
    /// </summary>
    private static readonly TimeSpan WaitForIndexing = TimeSpan.FromSeconds(30);

    /// <summary>
    /// How far into indexing the cancellation pass gets before it is stopped. One command in flight
    /// is already past discovery, but it is the first thing the scan does; this is far enough in for
    /// several projects to be under way at once, which is the state worth interrupting.
    /// </summary>
    private const int CommandsBeforeCancelling = 20;

    private readonly byte[] _salt = RandomNumberGenerator.GetBytes(32);

    /// <summary>
    /// Refuses to run when the run's own output would land inside something it is about to observe.
    /// A cache and a report written under a monitored root are changes to that workspace, and worse,
    /// they are changes present in *both* fingerprints — so the run would mutate a live project and
    /// then report that nothing moved.
    /// </summary>
    public static string? RejectOutputInsideARoot(string outputDirectory, IEnumerable<string> roots)
    {
        var output = ProjectDiscovery.CanonicalizeFully(outputDirectory);

        foreach (var root in roots)
        {
            var canonical = ProjectDiscovery.CanonicalizeFully(root);
            if (string.Equals(output, canonical, StringComparison.OrdinalIgnoreCase)
                || output.StartsWith(canonical + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            {
                return $"The output directory is inside the configured root '{root}'. "
                    + "Choose somewhere outside every monitored root: writing there would change a "
                    + "workspace this run is supposed to observe, in a way the comparison could not see.";
            }
        }

        return null;
    }

    /// <summary>
    /// Which project each identifier in the last report stands for, but only for the ones a reader
    /// would need to act on. A sanitized report says a workspace moved without saying whose, which
    /// is unusable exactly when it matters most — so the names are kept here, in memory, for the
    /// caller to write somewhere local and never commit.
    /// </summary>
    public IReadOnlyDictionary<string, string> AffectedProjects { get; private set; } =
        new Dictionary<string, string>(StringComparer.Ordinal);

    public async Task<AcceptanceReport> ExecuteAsync(CancellationToken cancellationToken)
    {
        if (RejectOutputInsideARoot(isolatedState.Root, settings.Roots) is { } rejection)
        {
            throw new InvalidOperationException(rejection);
        }

        isolatedState.EnsureCreated();

        var timeout = TimeSpan.FromSeconds(settings.ExternalProcessTimeoutSeconds);
        using var bounded = new BoundedProcessRunner(settings.MaxConcurrentExternalProcesses, timeout);
        var runner = new ReadOnlyProcessRunner(bounded);

        var environment = new AcceptanceEnvironment(
            System.Runtime.InteropServices.RuntimeInformation.OSDescription,
            System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription,
            System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture.ToString(),
            System.Environment.ProcessorCount,
            commit,
            DateTimeOffset.UtcNow.ToString("u"));

        var bounds = new AcceptanceBounds(
            settings.Roots.Count,
            settings.MaxDiscoveryDepth,
            settings.MaxProjects,
            settings.MaxDirectoriesScanned,
            settings.MaxConcurrentExternalProcesses,
            settings.ExternalProcessTimeoutSeconds);

        var discovered = new ProjectDiscovery(settings).Discover(cancellationToken).Projects;
        var projectPaths = discovered.Select(p => p.CanonicalPath).ToList();

        var stateReader = new MonitoredStateReader(runner, Identify);

        var before = await stateReader.ReadAsync(projectPaths, cancellationToken).ConfigureAwait(false);

        using var cache = DashboardCache.Open(isolatedState.CacheFile);
        var store = new SettingsStore(isolatedState);
        var service = new RefreshService(settings, runner, cache, settingsStore: store);

        var passes = new List<AcceptancePass>
        {
            await PassAsync("cold", service, bounded, cancellationToken).ConfigureAwait(false),
            await PassAsync("warm", service, bounded, cancellationToken).ConfigureAwait(false),
        };

        var cancellation = await CancellationPassAsync(service, runner, bounded, cancellationToken).ConfigureAwait(false);

        var after = await stateReader.ReadAsync(projectPaths, cancellationToken).ConfigureAwait(false);

        var issued = runner.Issued.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);

        var changes = MonitoredStateReader.Diff(before, after);
        var gaps = MonitoredStateReader.Gaps(before, after);

        var affected = changes.Select(c => c.ProjectId)
            .Concat(gaps.Select(g => g.ProjectId))
            .ToHashSet(StringComparer.Ordinal);

        AffectedProjects = projectPaths
            .Where(path => affected.Contains(Identify(path)))
            .ToDictionary(Identify, path => path, StringComparer.Ordinal);

        return new AcceptanceReport(
            environment,
            bounds,
            passes,
            cancellation,
            new SafetyEvidence(before.Count, changes, gaps, runner.Refused, issued));
    }

    private static async Task<AcceptancePass> PassAsync(
        string name,
        RefreshService service,
        BoundedProcessRunner bounded,
        CancellationToken cancellationToken)
    {
        var elapsed = Stopwatch.StartNew();
        var snapshot = await service.RefreshAsync(cancellationToken).ConfigureAwait(false);
        elapsed.Stop();

        return new AcceptancePass(
            name,
            elapsed.ElapsedMilliseconds,
            snapshot.Projects.Count,
            snapshot.Projects.Count(p => p.Progress.Total > 0),
            snapshot.Projects.Count(p => p.HasRemainingWork),
            snapshot.Projects.Count(p => p.IsStale),
            snapshot.ScanTruncated,
            bounded.PeakConcurrentProcesses,
            [.. snapshot.Projects
                .SelectMany(p => p.Diagnostics)
                .Concat(snapshot.Diagnostics)
                .GroupBy(d => d.Code, StringComparer.Ordinal)
                .OrderBy(g => g.Key, StringComparer.Ordinal)
                .Select(g => $"{g.Key}={g.Count()}")]);
    }

    /// <summary>
    /// Cancellation is measured from the moment it is asked for, not from the start of the pass:
    /// what the owner experiences is how long a refresh keeps going after they stop it.
    /// </summary>
    private static async Task<CancellationEvidence> CancellationPassAsync(
        RefreshService service,
        ReadOnlyProcessRunner runner,
        BoundedProcessRunner bounded,
        CancellationToken cancellationToken)
    {
        var commandsBefore = runner.Issued.Values.Sum();

        using var source = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var refresh = Task.Run(() => service.RefreshAsync(source.Token), CancellationToken.None);

        // Waiting for the first external command rather than for a fixed delay. Discovery walks the
        // roots before anything is indexed, and a pass cancelled during that walk stops promptly for
        // reasons that say nothing about stopping a scan already talking to git and gh.
        var waiting = Stopwatch.StartNew();
        while (runner.Issued.Values.Sum() - commandsBefore < CommandsBeforeCancelling
            && !refresh.IsCompleted
            && waiting.Elapsed < WaitForIndexing)
        {
            await Task.Delay(25, cancellationToken).ConfigureAwait(false);
        }

        waiting.Stop();

        // Two different numbers, and only the second says anything about interrupting real work.
        // `Issued` counts calls submitted to the boundary over the pass's lifetime; with a cap of
        // four, twenty of them were never running at once.
        var issuedAtCancel = runner.Issued.Values.Sum() - commandsBefore;
        var childrenAtCancel = bounded.ActiveProcesses;
        var stillRunning = !refresh.IsCompleted;

        var latency = Stopwatch.StartNew();
        await source.CancelAsync().ConfigureAwait(false);

        var stopped = false;
        try
        {
            await refresh.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            stopped = true;
        }

        latency.Stop();

        return new CancellationEvidence(
            waiting.ElapsedMilliseconds,
            issuedAtCancel,
            childrenAtCancel,
            stillRunning,
            latency.Elapsed.TotalMilliseconds,
            stopped);
    }

    /// <summary>
    /// A stable identifier for a path or a repository within one report, and nothing outside it.
    /// The salt is generated per run and never recorded, so the identifiers cannot be turned back
    /// into the names of the owner's projects by anyone reading the committed evidence.
    /// </summary>
    private string Identify(string value) =>
        Convert.ToHexStringLower(HMACSHA256.HashData(_salt, Encoding.UTF8.GetBytes(value.ToLowerInvariant())))[..12];

    public static string Serialize(AcceptanceReport report) =>
        JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true });
}
