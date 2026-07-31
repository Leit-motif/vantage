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
    int ProjectsWithRemainingWork,
    int StaleProjects,
    bool Offline,
    bool ScanTruncated,
    int PeakConcurrentProcesses,
    IReadOnlyList<string> DiagnosticCodes);

public sealed record CancellationEvidence(
    long RequestedAfterMilliseconds,
    long LatencyMilliseconds,
    bool StoppedByCancellation);

public sealed record OfflineEvidence(
    bool GhReportedNoSession,
    bool SnapshotMarkedOffline,
    int Projects,
    int ProjectsWithProgress,
    long ElapsedMilliseconds);

public sealed record AssociationEvidence(
    int Projects,
    int GitRepositories,
    int WithRemote,
    int AssociationMatchesRemote,
    int AssociationAwaitingRelink,
    int ProjectsCarryingGitHubEvidence,
    int GitHubEvidenceFromAnotherRepository,
    int RepositoriesQueried,
    int RepositoriesQueriedWithoutAnAssociation);

public sealed record SafetyEvidence(
    int ProjectsFingerprinted,
    IReadOnlyList<MonitoredStateChange> MonitoredStateChanges,
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
    [JsonIgnore]
    public bool NothingWasChanged =>
        Safety.MonitoredStateChanges.Count == 0 && Safety.RefusedCommands.Count == 0;
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
    /// How long the cancellation pass is allowed to run before it is stopped. Long enough for a
    /// real scan to be well under way, short enough that the answer is about responsiveness.
    /// </summary>
    private static readonly TimeSpan CancelAfter = TimeSpan.FromMilliseconds(250);

    private readonly byte[] _salt = RandomNumberGenerator.GetBytes(32);

    public async Task<AcceptanceReport> ExecuteAsync(CancellationToken cancellationToken)
    {
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

        var stateReader = new MonitoredStateReader(runner, Identify, settings.MaxGitHubIssuesPerRepository);
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

        var cancellation = await CancellationPassAsync(service, cancellationToken).ConfigureAwait(false);

        // Associations are read off the warm pass rather than the cancelled one: a cancelled pass
        // has no complete answer to be right or wrong about.
        var settled = await service.RefreshAsync(cancellationToken).ConfigureAwait(false);
        var associations = Associations(settled, discovered, runner);

        var offline = await OfflinePassAsync(cache, store, timeout, cancellationToken).ConfigureAwait(false);

        var after = await stateReader
            .ReadAsync(projectPaths, settings.GitHubEnrichmentEnabled, cancellationToken)
            .ConfigureAwait(false);

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
                runner.Refused,
                runner.Issued.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal)));
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
        CancellationToken cancellationToken)
    {
        using var source = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var refresh = Task.Run(() => service.RefreshAsync(source.Token), CancellationToken.None);

        await Task.Delay(CancelAfter, cancellationToken).ConfigureAwait(false);

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
        return new CancellationEvidence((long)CancelAfter.TotalMilliseconds, latency.ElapsedMilliseconds, stopped);
    }

    /// <summary>
    /// The same real <c>gh</c>, asked to work without a session: its configuration directory is
    /// pointed at an empty one and every inherited token is removed from the child's environment.
    /// Nothing on the machine is changed, and nothing is imitated — the tool itself decides it has
    /// no session, and the dashboard has to stay useful anyway.
    /// </summary>
    private async Task<OfflineEvidence> OfflinePassAsync(
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

        return new OfflineEvidence(
            !probe.Succeeded,
            snapshot.Offline,
            snapshot.Projects.Count,
            snapshot.Projects.Count(p => p.Progress.Total > 0),
            elapsed.ElapsedMilliseconds);
    }

    private AssociationEvidence Associations(
        Core.Projection.DashboardSnapshot snapshot,
        IReadOnlyList<DiscoveredProject> discovered,
        ReadOnlyProcessRunner runner)
    {
        var associated = snapshot.Projects
            .Where(p => p.Origin is not null)
            .Select(p => p.Origin!.Slug)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var awaitingRelink = snapshot.Projects
            .Count(p => p.Diagnostics.Any(d => d.Code == DiagnosticCode.OriginChanged));

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
            associated.Count,

            // The confirmed association and the remote agree unless the refresh said otherwise:
            // a disagreement is exactly what the pending-relink diagnostic reports.
            associated.Count - awaitingRelink,
            awaitingRelink,
            withGitHubEvidence,
            foreignEvidence,
            runner.RepositoriesQueried.Count,
            runner.RepositoriesQueried.Count(r => !associated.Contains(r)));
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
