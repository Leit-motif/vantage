using MattWorkflowDashboard.Core.Projection;
using MattWorkflowDashboard.Infrastructure;
using MattWorkflowDashboard.Infrastructure.Persistence;
using MattWorkflowDashboard.Infrastructure.Processes;
using MattWorkflowDashboard.Infrastructure.Refresh;
using MattWorkflowDashboard.Infrastructure.Settings;

namespace MattWorkflowDashboard.Tests.TestSupport;

/// <summary>
/// Drives the product's single refresh boundary over controlled roots, a controlled process
/// boundary, and a disposable cache. Tests assert the resulting snapshot and the externally
/// visible effects of the refresh — never private helpers or database layout.
/// </summary>
public sealed class RefreshHarness : IDisposable
{
    private DashboardCache _cache;

    public RefreshHarness(WorkspaceFixture workspace, FakeProcessRunner? runner = null, DateTimeOffset? now = null)
    {
        Workspace = workspace;
        Runner = runner ?? new FakeProcessRunner();
        Clock = new FixedTimeProvider(now ?? DateTimeOffset.Parse("2026-07-29T12:00:00Z"));

        Paths = new AppPaths(Path.Combine(workspace.Root, "appdata"));
        SettingsStore = new SettingsStore(Paths);

        Settings = new DashboardSettings
        {
            Roots = [workspace.WorkspacesRoot],
            GitHubEnrichmentEnabled = true,
        };

        // The settings file exists from the start, so a restart re-reads real configuration
        // rather than falling back to defaults.
        SettingsStore.Save(Settings);

        _cache = DashboardCache.Open(workspace.CacheFile);
    }

    public WorkspaceFixture Workspace { get; }

    public FakeProcessRunner Runner { get; }

    public FixedTimeProvider Clock { get; }

    public AppPaths Paths { get; }

    public SettingsStore SettingsStore { get; }

    public DashboardSettings Settings { get; private set; }

    public DashboardCache Cache => _cache;

    /// <summary>Uses real git while keeping <c>gh</c> scripted, for repository-behaviour tests.</summary>
    public RefreshHarness WithRealGit()
    {
        Runner.Fallback = new BoundedProcessRunner(4, TimeSpan.FromSeconds(30));
        return this;
    }

    public Task<DashboardSnapshot> RefreshAsync(CancellationToken cancellationToken = default) =>
        new RefreshService(Settings, Runner, _cache, Clock).RefreshAsync(cancellationToken);

    /// <summary>Reopens the cache and re-reads settings from disk, as a restart would.</summary>
    public void Restart()
    {
        _cache.Dispose();
        _cache = DashboardCache.Open(Workspace.CacheFile);
        Settings = SettingsStore.Load().Settings;
    }

    public void Dispose() => _cache.Dispose();
}

public static class SnapshotAssertions
{
    public static ProjectView Project(this DashboardSnapshot snapshot, string name) =>
        snapshot.Projects.SingleOrDefault(p => p.Identity.Name == name)
        ?? throw new AssertFailedException(
            $"No project named '{name}'. Found: {string.Join(", ", snapshot.Projects.Select(p => p.Identity.Name))}");

    /// <summary>
    /// Finds a ticket by its project-unique id or by the name it answers to inside its effort,
    /// so fixtures can keep saying "001" without repeating the effort name.
    /// </summary>
    public static TicketView Ticket(this ProjectView project, string id) =>
        project.Efforts.SelectMany(e => e.Tickets)
            .SingleOrDefault(t => t.Id == id || t.Id.EndsWith("/" + id, StringComparison.OrdinalIgnoreCase))
        ?? throw new AssertFailedException(
            $"No ticket '{id}'. Found: {string.Join(", ", project.Efforts.SelectMany(e => e.Tickets).Select(t => t.Id))}");

    public static bool HasDiagnostic(this ProjectView project, string code) =>
        project.Diagnostics.Any(d => d.Code == code);
}
