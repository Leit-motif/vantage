using System.Text;
using Vantage.Core.Projection;
using Vantage.Infrastructure;
using Vantage.Infrastructure.Persistence;
using Vantage.Infrastructure.Processes;
using Vantage.Infrastructure.Refresh;
using Vantage.Infrastructure.Settings;
using Vantage.Tests.TestSupport;

namespace Vantage.Tests.RefreshSeam;

/// <summary>
/// A one-off measurement, not a permanent test. The identical source is dropped into a worktree at
/// the review baseline and into the branch head, run in both, and the two dumps are compared. It
/// exists to answer one acceptance cell — "the dashboard reports the same local evidence it does
/// today for a project with no remote" — with a comparison rather than with a proxy.
///
/// It touches only fields both builds have, so the source compiles unchanged against each. The
/// remote-only fields are absent from the dump because they are the thing being removed; every
/// local conclusion is in it.
/// </summary>
[TestClass]
public sealed class ProjectionDump
{
    [TestMethod]
    public async Task Dump()
    {
        using var workspace = new WorkspaceFixture();

        // A project with no remote, in every shape the local evidence can take: a wayfinder effort
        // with a map, blocking edges that resolve and one that does not, every work-unit kind, a
        // ticket that names a GitHub issue in its own file, an unparseable status, and an effort
        // with nothing in it.
        var mapped = workspace.NewProject("mapped");
        var effort = workspace.NewEffort(mapped, "feature");
        workspace.WriteMap(effort, "# Map\n\n- [One](issues/001.md)\n- [Two](issues/002.md)\n- [Three](issues/003.md)\n");
        workspace.WriteFile(Path.Combine(effort, "spec.md"), "# Spec\n\nScope.\n");
        workspace.WriteTicket(effort, "001.md", Fixtures.Ticket("Plan it", "done", type: "planning"));
        workspace.WriteTicket(effort, "002.md", Fixtures.Ticket("Build it", "ready", blockedBy: "001", gitHub: "acme/widget#7"));
        workspace.WriteTicket(effort, "003.md", Fixtures.Ticket("Review it", "open", type: "review", blockedBy: "002"));
        workspace.WriteTicket(effort, "004.md", Fixtures.Ticket("Dangling", "open", blockedBy: "099"));
        workspace.WriteTicket(effort, "005.md", Fixtures.Ticket("Odd", "marinating", labels: "ready-for-agent"));
        workspace.WriteTicket(effort, "006.md", Fixtures.Ticket("Claimed", "in progress", assignee: "someone"));
        workspace.NewEffort(mapped, "empty");

        // The same, without a map: openness is not readiness outside a wayfinder context.
        var unmapped = workspace.NewProject("unmapped");
        var plain = workspace.NewEffort(unmapped, "plain");
        workspace.WriteTicket(plain, "001.md", Fixtures.Ticket("Open work", "open"));
        workspace.WriteTicket(plain, "002.md", Fixtures.Ticket("Ready work", "ready"));

        // A real git repository with no remote at all.
        var repo = workspace.NewProject("repo-no-remote");
        var repoEffort = workspace.NewEffort(repo, "feature");
        workspace.WriteTicket(repoEffort, "001.md", Fixtures.Ticket("Tracked work", "ready"));
        workspace.InitGitRepository(repo);
        workspace.Commit(repo, "add planning");

        // A project with nothing to say.
        workspace.NewProject("bare");

        var settings = new DashboardSettings { Roots = [workspace.WorkspacesRoot] };
        var paths = new AppPaths(Path.Combine(workspace.Root, "appdata"));
        var store = new SettingsStore(paths);
        store.Save(settings);

        var runner = new FakeProcessRunner { Fallback = new BoundedProcessRunner(4, TimeSpan.FromSeconds(30)) };
        using var cache = DashboardCache.Open(workspace.CacheFile);
        var clock = new FixedTimeProvider(DateTimeOffset.Parse("2026-07-29T12:00:00Z"));

        var service = new RefreshService(settings, runner, cache, clock, store);
        var snapshot = await service.RefreshAsync(CancellationToken.None);

        var text = new StringBuilder();
        text.Append("counters ").Append(snapshot.Counters.Active).Append('/')
            .Append(snapshot.Counters.Ready).Append('/').Append(snapshot.Counters.Blocked).Append('\n');
        text.Append("truncated ").Append(snapshot.ScanTruncated).Append('\n');
        text.Append("stale ").Append(snapshot.IsStale).Append('\n');

        foreach (var project in snapshot.Projects.OrderBy(p => p.Identity.Name, StringComparer.Ordinal))
        {
            text.Append("\nPROJECT ").Append(project.Identity.Name).Append('\n');
            text.Append("  state ").Append(project.State).Append(" | ").Append(project.StateReason).Append('\n');
            text.Append("  progress ").Append(project.Progress.Completed).Append('/')
                .Append(project.Progress.Total).Append(" excluded ").Append(project.Progress.Excluded).Append('\n');
            text.Append("  stale ").Append(project.IsStale).Append(" gitAvailable ").Append(project.GitAvailable).Append('\n');
            text.Append("  pipeline ").Append(string.Join(", ", project.Pipeline.Select(s => $"{s.Kind}={s.Completed}/{s.Total}"))).Append('\n');
            text.Append("  nextAction ").Append(project.NextAction is { } a
                ? $"{a.TicketId} | {a.Source} | {a.Reason}"
                : "none").Append('\n');
            text.Append("  conflicts ").Append(project.Conflicts.Count).Append('\n');
            text.Append("  activityKinds ").Append(string.Join(", ",
                project.RecentActivity.Select(e => e.Kind.ToString()).OrderBy(k => k, StringComparer.Ordinal))).Append('\n');

            foreach (var d in project.Diagnostics.OrderBy(d => d.Code + d.Message, StringComparer.Ordinal))
            {
                text.Append("  diagnostic ").Append(d.Severity).Append(' ').Append(d.Code).Append(" | ")
                    .Append(Relative(d.Message, workspace.Root)).Append('\n');
            }

            foreach (var e in project.Efforts.OrderBy(e => e.Id, StringComparer.Ordinal))
            {
                text.Append("  EFFORT ").Append(e.Id).Append(" | ").Append(e.Name)
                    .Append(" | wayfinder ").Append(e.IsWayfinderContext)
                    .Append(" | ").Append(e.Progress.Completed).Append('/').Append(e.Progress.Total).Append('\n');

                foreach (var t in e.Tickets.OrderBy(t => t.Id, StringComparer.Ordinal))
                {
                    text.Append("    TICKET ").Append(t.Id)
                        .Append(" | ").Append(t.Title)
                        .Append(" | ").Append(t.Status.Status).Append('(').Append(t.Status.RawValue).Append(')')
                        .Append(" | ").Append(t.Kind)
                        .Append(" | actionable ").Append(t.IsActionable)
                        .Append(" | blocked ").Append(t.IsBlocked)
                        .Append(" | blockedBy ").Append(string.Join('+', t.BlockedBy))
                        .Append(" | link ").Append(t.Link?.RawValue ?? "none")
                        .Append(" | source ").Append(Relative(t.SourcePath, workspace.Root))
                        .Append(" | provenance ").Append(t.Provenance.Source).Append('/').Append(t.Provenance.TimestampKind)
                        .Append(" | conflicts ").Append(t.Conflicts.Count)
                        .Append('\n');
                }
            }
        }

        var target = Environment.GetEnvironmentVariable("PROJECTION_DUMP")
            ?? Path.Combine(Path.GetTempPath(), "projection-dump.txt");

        File.WriteAllText(target, text.ToString().Replace("\r\n", "\n"));
        Console.WriteLine($"wrote {target}");
    }

    private static string Relative(string value, string root) =>
        value.Replace(root, "<root>", StringComparison.OrdinalIgnoreCase).Replace('\\', '/');
}
