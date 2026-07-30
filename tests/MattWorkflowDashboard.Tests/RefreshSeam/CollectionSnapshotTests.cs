using MattWorkflowDashboard.Core.Projection;
using MattWorkflowDashboard.Tests.TestSupport;

namespace MattWorkflowDashboard.Tests.RefreshSeam;

/// <summary>
/// Label, assignee, and blocker snapshots are what the dashboard compares one refresh against the
/// next to decide that something moved. A representation in which one item containing the
/// separator reads back as several distinct items makes a real change invisible, so each of these
/// changes a collection in exactly that way and demands the movement still be reported.
/// </summary>
[TestClass]
public sealed class CollectionSnapshotTests
{
    private WorkspaceFixture _workspace = null!;
    private RefreshHarness _harness = null!;
    private string _effort = null!;

    [TestInitialize]
    public void SetUp()
    {
        _workspace = new WorkspaceFixture();
        _harness = new RefreshHarness(_workspace);
        _effort = _workspace.NewEffort(_workspace.NewProject("app"), "feature");
    }

    [TestCleanup]
    public void TearDown()
    {
        _harness.Dispose();
        _workspace.Dispose();
    }

    /// <summary>Indexes the ticket as written, then as rewritten, and returns what moved.</summary>
    private async Task<IReadOnlyList<ActivityKind>> KindsAfterRewriting(string before, string after)
    {
        _workspace.WriteTicket(_effort, "001.md", before);
        await _harness.RefreshAsync();

        _workspace.WriteTicket(_effort, "001.md", after);
        var view = (await _harness.RefreshAsync()).Project("app");

        return [.. view.RecentActivity.Select(a => a.Kind)];
    }

    [TestMethod]
    public async Task One_label_containing_the_separator_is_not_the_same_as_two_labels()
    {
        var kinds = await KindsAfterRewriting(
            Fixtures.Ticket("Work", "ready", labels: "alpha|beta"),
            Fixtures.Ticket("Work", "ready", labels: "alpha, beta"));

        CollectionAssert.Contains(
            kinds.ToArray(),
            ActivityKind.LabelChanged,
            "One label written 'alpha|beta' must never compare equal to the two labels 'alpha' and 'beta'.");
    }

    [TestMethod]
    public async Task One_assignee_containing_the_separator_is_not_the_same_as_two_assignees()
    {
        var kinds = await KindsAfterRewriting(
            Fixtures.Ticket("Work", "ready", assignee: "ana|bo"),
            Fixtures.Ticket("Work", "ready", assignee: "ana, bo"));

        CollectionAssert.Contains(
            kinds.ToArray(),
            ActivityKind.AssignmentChanged,
            "Work passing from one oddly named assignee to two people is movement, not a re-spelling.");
    }

    [TestMethod]
    public async Task One_blocker_containing_the_separator_is_not_the_same_as_two_blockers()
    {
        var kinds = await KindsAfterRewriting(
            Fixtures.Ticket("Work", "ready", blockedBy: "aa|bb"),
            Fixtures.Ticket("Work", "ready", blockedBy: "aa, bb"));

        CollectionAssert.Contains(
            kinds.ToArray(),
            ActivityKind.BlockerChanged,
            "A dependency graph that gained an edge has changed, whatever the references are called.");
    }

    [TestMethod]
    public async Task Each_collection_reports_its_own_kind_of_movement()
    {
        var kinds = await KindsAfterRewriting(
            Fixtures.Ticket("Work", "ready", blockedBy: "prereq", labels: "alpha", assignee: "ana"),
            Fixtures.Ticket("Work", "ready", blockedBy: "prereq, other", labels: "alpha, beta", assignee: "ana, bo"));

        CollectionAssert.Contains(kinds.ToArray(), ActivityKind.LabelChanged);
        CollectionAssert.Contains(kinds.ToArray(), ActivityKind.AssignmentChanged);
        CollectionAssert.Contains(kinds.ToArray(), ActivityKind.BlockerChanged);
    }

    [TestMethod]
    public async Task Writing_the_same_collections_in_a_different_order_is_not_movement()
    {
        var kinds = await KindsAfterRewriting(
            Fixtures.Ticket("Work", "ready", labels: "Beta, alpha", assignee: "Ana, ana"),
            Fixtures.Ticket("Work", "ready", labels: "alpha, Beta", assignee: "ana, Ana"));

        CollectionAssert.DoesNotContain(kinds.ToArray(), ActivityKind.LabelChanged);
        CollectionAssert.DoesNotContain(
            kinds.ToArray(),
            ActivityKind.AssignmentChanged,
            "Two entries that differ only in case are still the same two entries; reordering them "
            + "is formatting, and a canonical representation must not read it as work moving.");
    }

    /// <summary>
    /// Changing how a collection is written down is not something that happened to the owner's
    /// work. A cache written by the previous encoding has to migrate to the new one silently.
    /// </summary>
    [TestMethod]
    public async Task Upgrading_a_cache_written_by_the_previous_encoding_is_not_movement()
    {
        // A backslash is the one character the two encodings render differently, so this is the
        // case a migration is actually needed for.
        _workspace.WriteTicket(_effort, "001.md", Fixtures.Ticket("Work", "ready", labels: @"a\b"));
        await _harness.RefreshAsync();

        WriteAsPreviousEncoding(@"a\b");
        _harness.Restart();

        var view = (await _harness.RefreshAsync()).Project("app");

        Assert.AreEqual(
            "Work",
            view.Ticket("001").Title,
            "A migratable cache must be migrated, not discarded.");

        CollectionAssert.DoesNotContain(
            view.RecentActivity.Select(a => a.Kind).ToArray(),
            ActivityKind.LabelChanged,
            "The ticket file did not change; only the way its labels were stored did.");
    }

    /// <summary>
    /// The previous encoding could not tell one item spelling the separator from several items.
    /// Neither reading of those bytes may be asserted as what was there before, so neither may
    /// produce movement on the refresh that migrates them.
    /// </summary>
    [TestMethod]
    public async Task Upgrading_a_value_the_previous_encoding_could_not_pin_down_is_not_movement()
    {
        _workspace.WriteTicket(_effort, "001.md", Fixtures.Ticket("Work", "ready", labels: "alpha|beta"));
        await _harness.RefreshAsync();

        // Exactly what schema 2 stored for this ticket — and equally, what it stored for a ticket
        // carrying the two separate labels 'alpha' and 'beta'.
        WriteAsPreviousEncoding("alpha|beta");
        _harness.Restart();

        var view = (await _harness.RefreshAsync()).Project("app");

        CollectionAssert.DoesNotContain(
            view.RecentActivity.Select(a => a.Kind).ToArray(),
            ActivityKind.LabelChanged,
            "Reading undecidable bytes one way would make the other way look like movement.");
    }

    /// <summary>Rewrites the stored labels the way schema 2 wrote them, and marks the file as
    /// schema 2 so the next open migrates it.</summary>
    private void WriteAsPreviousEncoding(string labels)
    {
        using var connection = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={_workspace.CacheFile}");
        connection.Open();

        using var command = connection.CreateCommand();
        command.CommandText = "UPDATE ticket_snapshot SET labels = $labels; PRAGMA user_version=2;";
        command.Parameters.AddWithValue("$labels", labels);
        command.ExecuteNonQuery();
    }
}
