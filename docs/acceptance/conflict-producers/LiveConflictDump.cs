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
/// Point <c>LIVE_ROOT</c> at the directory holding the repository and <c>LIVE_DUMP</c> at the file
/// to write. Nothing is written inside the observed repository: settings and cache go to a
/// temporary directory of their own.
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

        var text = new StringBuilder();
        text.Append("root ").Append(root.Replace('\\', '/')).Append('\n');
        text.Append("projects ").Append(snapshot.Projects.Count).Append('\n');

        foreach (var project in snapshot.Projects.OrderBy(p => p.Identity.Name, StringComparer.Ordinal))
        {
            var tickets = project.Efforts.SelectMany(e => e.Tickets).ToList();

            text.Append("\nPROJECT ").Append(project.Identity.Name).Append('\n');
            text.Append("  tickets ").Append(tickets.Count)
                .Append(" | diagnostics ").Append(project.Diagnostics.Count)
                .Append(" | conflicts ").Append(project.Conflicts.Count).Append('\n');
            text.Append("  itemsCarryingAConflict ").Append(tickets.Count(t => t.Conflicts.Count > 0)).Append('\n');

            foreach (var conflict in project.Conflicts
                .OrderBy(c => c.TicketId, StringComparer.Ordinal)
                .ThenBy(c => c.Field.ToString(), StringComparer.Ordinal))
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
