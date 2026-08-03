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
/// A one-off measurement, not a permanent test. It runs the product's own refresh boundary over a
/// real repository — this one — and writes out every disagreement the three producers found, with
/// both sides, both origins, and the counts beside them.
///
/// The fixtures show that each producer fires on a case built for it. This answers the question
/// fixtures cannot: what the badge actually says on a real tracker, where the failure mode is not
/// a missing conflict but a hundred useless ones.
///
/// Point <c>LIVE_ROOT</c> at the directory holding the repositories and <c>LIVE_DUMP</c> at the
/// file to write. Nothing is written inside an observed repository: settings and cache go to a
/// temporary directory of their own.
/// <para>
/// What it writes is committed, and this directory is published, so the output carries no absolute
/// path — not the root, not a project's, not a ticket's. A project is named only when
/// <c>LIVE_DETAIL</c> matches it, and every other one is an index and its counts. The owner's own
/// workspaces are the reason: their project names are theirs, and a run over them still has to be
/// able to say how many of them reported nothing.
/// </para>
/// </summary>
[TestClass]
public sealed class LiveConflictDump
{
    [TestMethod]
    public async Task Dump()
    {
        var root = Environment.GetEnvironmentVariable("LIVE_ROOT")
            ?? throw new AssertFailedException("LIVE_ROOT must name the directory holding the repository to observe.");

        var scratch = Path.Combine(Path.GetTempPath(), "live-conflict-dump", Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(scratch);

        var settings = new DashboardSettings { Roots = [root] };
        var store = new SettingsStore(new AppPaths(Path.Combine(scratch, "appdata")));
        store.Save(settings);

        using var cache = DashboardCache.Open(Path.Combine(scratch, "cache", "cache.db"));
        using var runner = new BoundedProcessRunner(4, TimeSpan.FromSeconds(30));

        var service = new RefreshService(settings, runner, cache, TimeProvider.System, store);
        var snapshot = await service.RefreshAsync(CancellationToken.None);

        var detail = Environment.GetEnvironmentVariable("LIVE_DETAIL");

        var text = new StringBuilder();
        text.Append("projects ").Append(snapshot.Projects.Count).Append('\n');
        text.Append("projectsReportingNoConflict ").Append(snapshot.Projects.Count(p => p.Conflicts.Count == 0)).Append('\n');
        text.Append("conflicts ").Append(snapshot.Projects.Sum(p => p.Conflicts.Count)).Append('\n');

        var index = 0;
        foreach (var project in snapshot.Projects.OrderBy(p => p.Identity.Name, StringComparer.Ordinal))
        {
            index++;
            var tickets = project.Efforts.SelectMany(e => e.Tickets).ToList();

            var named = detail is not null
                && project.Identity.Name.Contains(detail, StringComparison.OrdinalIgnoreCase);

            text.Append("\nPROJECT ").Append(named ? project.Identity.Name : $"project-{index:00}").Append('\n');
            text.Append("  tickets ").Append(tickets.Count)
                .Append(" | diagnostics ").Append(project.Diagnostics.Count)
                .Append(" | conflicts ").Append(project.Conflicts.Count).Append('\n');
            text.Append("  itemsCarryingAConflict ").Append(tickets.Count(t => t.Conflicts.Count > 0)).Append('\n');

            if (!named)
            {
                continue;
            }

            foreach (var conflict in project.Conflicts
                .OrderBy(c => c.TicketId, StringComparer.Ordinal)
                .ThenBy(c => c.Field.ToString(), StringComparer.Ordinal)
                .ThenBy(c => c.First.Value, StringComparer.Ordinal))
            {
                text.Append("  CONFLICT ").Append(conflict.Field).Append(" on ").Append(conflict.TicketId).Append('\n');
                text.Append("    ").Append(conflict.First.Provenance.Origin).Append(" :: ").Append(conflict.First.Value).Append('\n');
                text.Append("    ").Append(conflict.Second.Provenance.Origin).Append(" :: ").Append(conflict.Second.Value).Append('\n');
                text.Append("    kept ").Append(conflict.Resolution).Append('\n');
            }
        }

        var target = Environment.GetEnvironmentVariable("LIVE_DUMP")
            ?? Path.Combine(Path.GetTempPath(), "live-conflict-dump.txt");

        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        File.WriteAllText(target, text.ToString().Replace("\r\n", "\n"));
        Console.WriteLine($"wrote {target}");
    }
}
