using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using MattWorkflowDashboard.Core;
using MattWorkflowDashboard.Infrastructure.Discovery;
using MattWorkflowDashboard.Infrastructure.Persistence;
using MattWorkflowDashboard.Infrastructure.Processes;
using MattWorkflowDashboard.Infrastructure.Refresh;
using MattWorkflowDashboard.Infrastructure.Settings;

namespace MattWorkflowDashboard.Infrastructure.Acceptance;

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
    int ExternalProcessTimeoutSeconds,
    int MaxGitHubIssuesPerRepository);

public sealed record AcceptancePass(
    string Name,
    long ElapsedMilliseconds,
    int Projects,
    int ProjectsWithProgress,
    int ProjectsWithRemainingWork,
    int StaleProjects,
    bool Offline,
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

public sealed record OfflineEvidence(
    bool GhReportedNoSession,
    bool SnapshotMarkedOffline,
    int Projects,
    int ProjectsWithProgress,
    int ProjectsWithRemainingWork,
    int StaleProjects,
    long ElapsedMilliseconds);

/// <summary>
/// Projects and repositories are counted separately on purpose: several projects can share one
/// repository — a worktree, a second clone — so a count of distinct slugs is not a count of
/// projects, and comparing the two would look like an inconsistency.
/// </summary>
public sealed record AssociationEvidence(
    int Projects,
    int GitRepositories,
    int ProjectsWithAssociation,
    int DistinctRepositoriesAssociated,
    int AssociationMatchesObservedRemote,
    int AssociationDiffersFromObservedRemote,
    int RemoteUnreadable,
    int AssociationAwaitingRelink,
    int ProjectsCarryingGitHubEvidence,
    int GitHubEvidenceFromAnotherRepository,
    int RepositoriesQueried,
    int RepositoriesQueriedWithoutAnAssociation);

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
    OfflineEvidence Offline,
    AssociationEvidence Associations,
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
            settings.ExternalProcessTimeoutSeconds,
            settings.MaxGitHubIssuesPerRepository);

        var discovered = new ProjectDiscovery(settings).Discover(cancellationToken).Projects;
        var projectPaths = discovered.Select(p => p.CanonicalPath).ToList();

        var stateReader = new MonitoredStateReader(
            runner,
            Identify,
            settings.MaxGitHubIssuesPerRepository,
            AssociationOf);

        var before = await stateReader
            .ReadAsync(projectPaths, settings.GitHubEnrichmentEnabled, cancellationToken)
            .ConfigureAwait(false);

        using var cache = DashboardCache.Open(isolatedState.CacheFile);
        var store = new SettingsStore(isolatedState);
        var service = new RefreshService(settings, runner, cache, settingsStore: store);

        var passes = new List<AcceptancePass>
        {
            await PassAsync("cold", service, bounded, cancellationToken).ConfigureAwait(false),
            await PassAsync("warm", service, bounded, cancellationToken).ConfigureAwait(false),
        };

        var cancellation = await CancellationPassAsync(service, runner, bounded, cancellationToken).ConfigureAwait(false);

        // Associations are read off a settled pass rather than the cancelled one: a cancelled pass
        // has no complete answer to be right or wrong about.
        var settled = await service.RefreshAsync(cancellationToken).ConfigureAwait(false);
        var associations = await AssociationsAsync(settled, discovered, runner, cancellationToken).ConfigureAwait(false);

        var (offline, offlineRunner) = await OfflinePassAsync(cache, store, timeout, cancellationToken)
            .ConfigureAwait(false);

        var after = await stateReader
            .ReadAsync(projectPaths, settings.GitHubEnrichmentEnabled, cancellationToken)
            .ConfigureAwait(false);

        // Both boundaries are reported together. The offline pass runs behind a second runner, and
        // a refusal it recorded would otherwise never be looked at.
        var issued = runner.Issued.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);
        foreach (var (shape, count) in offlineRunner.Issued)
        {
            issued[shape] = issued.GetValueOrDefault(shape) + count;
        }

        return new AcceptanceReport(
            environment,
            bounds,
            passes,
            cancellation,
            offline,
            associations,
            new SafetyEvidence(
                before.Count,
                MonitoredStateReader.Diff(before, after),
                MonitoredStateReader.Gaps(before, after),
                [.. runner.Refused, .. offlineRunner.Refused],
                issued));
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
            snapshot.Offline,
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
    /// The same real <c>gh</c>, asked to work without a session: its configuration directory is
    /// pointed at an empty one and every inherited token is removed from the child's environment.
    /// Nothing on the machine is changed, and nothing is imitated — the tool itself decides it has
    /// no session, and the dashboard has to stay useful anyway.
    /// </summary>
    private async Task<(OfflineEvidence Evidence, ReadOnlyProcessRunner Runner)> OfflinePassAsync(
        DashboardCache cache,
        SettingsStore store,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        var emptyConfig = Path.Combine(isolatedState.Root, "gh-no-session");
        Directory.CreateDirectory(emptyConfig);

        using var blinded = new BoundedProcessRunner(
            settings.MaxConcurrentExternalProcesses,
            timeout,
            new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
            {
                ["GH_CONFIG_DIR"] = emptyConfig,
                ["GH_TOKEN"] = null,
                ["GITHUB_TOKEN"] = null,
                ["GH_ENTERPRISE_TOKEN"] = null,
                ["GITHUB_ENTERPRISE_TOKEN"] = null,
            });

        var runner = new ReadOnlyProcessRunner(blinded);

        var probe = await runner
            .RunAsync("gh", ["auth", "status"], null, cancellationToken)
            .ConfigureAwait(false);

        var offlineService = new RefreshService(settings, runner, cache, settingsStore: store);

        var elapsed = Stopwatch.StartNew();
        var snapshot = await offlineService.RefreshAsync(cancellationToken).ConfigureAwait(false);
        elapsed.Stop();

        return (
            new OfflineEvidence(
                !probe.Succeeded,
                snapshot.Offline,
                snapshot.Projects.Count,
                snapshot.Projects.Count(p => p.Progress.Total > 0),
                snapshot.Projects.Count(p => p.HasRemainingWork),
                snapshot.Projects.Count(p => p.IsStale),
                elapsed.ElapsedMilliseconds),
            runner);
    }

    private async Task<AssociationEvidence> AssociationsAsync(
        Core.Projection.DashboardSnapshot snapshot,
        IReadOnlyList<DiscoveredProject> discovered,
        ReadOnlyProcessRunner runner,
        CancellationToken cancellationToken)
    {
        var associated = snapshot.Projects
            .Where(p => p.Origin is not null)
            .Select(p => p.Origin!.Slug)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var awaitingRelink = snapshot.Projects
            .Count(p => p.Diagnostics.Any(d => d.Code == DiagnosticCode.OriginChanged));

        // Each association is compared against the remote read independently here, rather than
        // inferred from the absence of a diagnostic. A remote that cannot be read, or one that was
        // removed, produces no diagnostic at all — so counting the projects the refresh did not
        // complain about would report agreement it never established.
        var matches = 0;
        var differs = 0;
        var unreadable = 0;

        foreach (var project in snapshot.Projects.Where(p => p.Origin is not null))
        {
            cancellationToken.ThrowIfCancellationRequested();

            var result = await runner.RunAsync(
                "git",
                ["-C", project.Identity.CanonicalPath, "remote", "get-url", "origin"],
                project.Identity.CanonicalPath,
                cancellationToken).ConfigureAwait(false);

            var observed = result.Succeeded
                ? Core.Workflow.GitHubOrigin.TryParse(result.StandardOutput.Trim())?.Slug
                : null;

            if (observed is null)
            {
                unreadable++;
            }
            else if (string.Equals(observed, project.Origin!.Slug, StringComparison.OrdinalIgnoreCase))
            {
                matches++;
            }
            else
            {
                differs++;
            }
        }

        var withGitHubEvidence = 0;
        var foreignEvidence = 0;

        foreach (var project in snapshot.Projects)
        {
            var locators = project.Efforts
                .SelectMany(e => e.Tickets)
                .Select(t => t.Provenance)
                .Concat(project.RecentActivity.Select(a => a.Provenance))
                .Where(p => p.Source == EvidenceSource.GitHubCli)
                .Select(p => p.Locator)
                .ToList();

            if (locators.Count == 0)
            {
                continue;
            }

            withGitHubEvidence++;

            var slug = project.Origin?.Slug;
            foreignEvidence += locators.Count(l =>
                slug is null || !l.StartsWith(slug + "#", StringComparison.OrdinalIgnoreCase));
        }

        return new AssociationEvidence(
            snapshot.Projects.Count,
            discovered.Count(d => d.IsGitRepository),
            snapshot.Projects.Count(p => p.Origin is not null),
            associated.Count,
            matches,
            differs,
            unreadable,
            awaitingRelink,
            withGitHubEvidence,
            foreignEvidence,
            runner.RepositoriesQueried.Count,
            runner.RepositoriesQueried.Count(r => !associated.Contains(r)));
    }

    /// <summary>
    /// The repository a project is confirmed to be associated with, as the registry records it.
    /// Not the remote as it currently reads: a changed remote is held pending the owner's
    /// confirmation, and the fingerprint must not query a repository they have not approved.
    /// </summary>
    private ProjectAssociation AssociationOf(string projectPath) =>
        new(settings.FindProject(projectPath)?.ConfirmedOrigin);

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
