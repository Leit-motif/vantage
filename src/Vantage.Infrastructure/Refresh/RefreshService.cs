using System.Collections.Concurrent;
using Vantage.Core;
using Vantage.Core.Observed;
using Vantage.Core.Projection;
using Vantage.Core.Workflow;
using Vantage.Infrastructure.Discovery;
using Vantage.Infrastructure.Git;
using Vantage.Infrastructure.Parsing;
using Vantage.Infrastructure.Persistence;
using Vantage.Infrastructure.Processes;
using Vantage.Infrastructure.Settings;

namespace Vantage.Infrastructure.Refresh;

/// <summary>
/// The single application refresh boundary. Discovery, adapter reads, reconciliation,
/// persistence, and projection all happen here, and one complete immutable snapshot comes out.
/// This is the seam the product tests drive.
/// </summary>
public sealed class RefreshService(
    DashboardSettings settings,
    IProcessRunner processRunner,
    DashboardCache cache,
    TimeProvider? timeProvider = null,
    SettingsStore? settingsStore = null)
{
    private readonly TimeProvider _clock = timeProvider ?? TimeProvider.System;
    private readonly GitAdapter _git = new(processRunner);
    private readonly WorkingTreeAdapter _workingTree = new(processRunner);
    private readonly WorkflowIndexer _indexer = new();

    /// <summary>
    /// One indexing pass per project at a time. Overlapping refreshes and watcher bursts must
    /// never interleave into an internally inconsistent snapshot for a single project.
    /// </summary>
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _projectLocks = new(StringComparer.OrdinalIgnoreCase);

    public async Task<DashboardSnapshot> RefreshAsync(CancellationToken cancellationToken)
    {
        var refreshId = $"r{_clock.GetUtcNow():yyyyMMddHHmmssfff}";
        var now = _clock.GetUtcNow();
        var diagnostics = new List<Diagnostic>(cache.Diagnostics);

        var discovery = new ProjectDiscovery(settings).Discover(cancellationToken);
        diagnostics.AddRange(discovery.Diagnostics);

        var views = new ConcurrentBag<(int Order, ProjectView View)>();
        var failures = new ConcurrentBag<Diagnostic>();

        // Everything from selection onwards can change registry intent, and every way out of it has
        // to leave that intent written.
        try
        {
            var selected = SelectProjects(discovery.Projects, diagnostics);

            await Parallel.ForEachAsync(
                selected.Select((entry, index) => (entry, index)),
                new ParallelOptions
                {
                    CancellationToken = cancellationToken,
                    MaxDegreeOfParallelism = Math.Max(1, settings.MaxConcurrentExternalProcesses),
                },
                async (item, token) =>
                {
                    var view = await RefreshProjectAsync(item.entry, refreshId, now, failures, token)
                        .ConfigureAwait(false);
                    views.Add((item.index, view));
                }).ConfigureAwait(false);
        }
        finally
        {
            // A superseded or failed pass still changed registry intent in memory. Writing it here
            // means a cancelled refresh cannot quietly drop a discovered project, and a failed
            // write stays outstanding for the next pass.
            PersistRegistry(diagnostics);
        }

        diagnostics.AddRange(failures);

        var ordered = views
            .OrderBy(v => v.Order)
            .Select(v => v.View)
            .ToList();

        cache.PruneActivity(now.AddDays(-settings.ActivityRetentionDays));

        var counters = new SnapshotCounters(
            ordered.Count(p => p.State == ProjectState.InProgress),
            ordered.Count(p => p.State == ProjectState.Ready),
            ordered.Count(p => p.State == ProjectState.Blocked),
            ordered.Sum(p => p.Diagnostics.Count) + diagnostics.Count);

        return new DashboardSnapshot
        {
            RefreshId = refreshId,
            GeneratedAt = now,
            Projects = ordered,
            Diagnostics = diagnostics,
            Counters = counters,
            IsStale = ordered.Any(p => p.IsStale),
            ScanTruncated = discovery.Truncated,
        };
    }

    /// <summary>
    /// Applies registry intent to discovery. A project inside a vendor, dependency, tool, build, or
    /// cache location stays out unless the owner opted it back in; ordinary nesting is no reason to
    /// suppress a project. Hiding or excluding preserves the intent rather than forgetting it.
    /// </summary>
    private IReadOnlyList<(DiscoveredProject Project, ProjectRegistryEntry Entry)> SelectProjects(
        IReadOnlyList<DiscoveredProject> discovered,
        ICollection<Diagnostic> diagnostics)
    {
        var selected = new List<(DiscoveredProject, ProjectRegistryEntry)>();

        // Built only if something looks new, and only once: an entry recorded under a path that
        // resolves somewhere else is the owner's intent under an old name, not a different project.
        Dictionary<string, ProjectRegistryEntry>? entriesByResolvedPath = null;

        foreach (var project in discovered)
        {
            var entry = settings.FindProject(project.CanonicalPath);

            if (entry is null)
            {
                entriesByResolvedPath ??= IndexEntriesByResolvedPath();
                if (entriesByResolvedPath.TryGetValue(project.CanonicalPath, out var recorded))
                {
                    entry = Migrate(recorded, project.CanonicalPath);
                }
            }

            if (entry is null)
            {
                entry = new ProjectRegistryEntry
                {
                    Path = project.CanonicalPath,
                    State = ProjectRegistryState.Enabled,
                };
                settings.Projects.Add(entry);
                settings.MarkChanged();
            }

            if (entry.State != ProjectRegistryState.Enabled)
            {
                continue;
            }

            if (project.IsBeneathExcludedLocation && !entry.NestedOptIn)
            {
                diagnostics.Add(Diagnostic.Info(
                    DiagnosticCode.ProjectScanFailed,
                    $"'{project.Name}' sits inside '{project.ExcludedLocation}' and is excluded; opt it in from Settings to track it.",
                    project.CanonicalPath));
                continue;
            }

            selected.Add((project, entry));
        }

        return selected;
    }

    /// <summary>
    /// Registry entries keyed by where their recorded path actually resolves to, for the entries
    /// where those differ. A path recorded before links were resolved through every segment — or
    /// one whose links have since changed — names the same directory under a different name, and
    /// hidden, excluded, and opted-in intent has to follow it there.
    /// </summary>
    private Dictionary<string, ProjectRegistryEntry> IndexEntriesByResolvedPath()
    {
        var index = new Dictionary<string, ProjectRegistryEntry>(StringComparer.OrdinalIgnoreCase);

        foreach (var entry in settings.Projects.Where(e => e.Path.Length > 0))
        {
            string resolved;
            try
            {
                resolved = ProjectDiscovery.CanonicalizeFully(entry.Path);
            }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
            {
                // A path that is no longer even well-formed cannot be matched; leave it alone.
                continue;
            }

            if (!string.Equals(resolved, entry.Path, StringComparison.OrdinalIgnoreCase))
            {
                index.TryAdd(resolved, entry);
            }
        }

        return index;
    }

    /// <summary>
    /// Moves an existing entry onto the identity this refresh discovered, keeping every choice the
    /// owner made about it. The name it was recorded under becomes its route when it is an opt-in
    /// that has none, because that name may be the only way back into an excluded location.
    /// </summary>
    private ProjectRegistryEntry Migrate(ProjectRegistryEntry entry, string canonicalPath)
    {
        var recordedPath = entry.Path;

        entry.Path = canonicalPath;
        if (entry.NestedOptIn && string.IsNullOrWhiteSpace(entry.OptInPath))
        {
            entry.OptInPath = recordedPath;
        }

        settings.MarkChanged();
        return entry;
    }

    /// <summary>
    /// Writes registry intent the refresh itself produced — a newly discovered project, or an
    /// entry moved onto the identity discovery emits — so what the owner sees is what comes back
    /// after a restart.
    /// </summary>
    private void PersistRegistry(ICollection<Diagnostic> diagnostics)
    {
        if (settingsStore is null || !settings.HasUnsavedChanges)
        {
            return;
        }

        try
        {
            settingsStore.Save(settings);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            diagnostics.Add(Diagnostic.Warning(
                DiagnosticCode.SettingsRecovered,
                $"Registry changes from this refresh could not be saved: {ex.Message}",
                settingsStore.FilePath));
        }
    }

    private async Task<ProjectView> RefreshProjectAsync(
        (DiscoveredProject Project, ProjectRegistryEntry Entry) item,
        string refreshId,
        DateTimeOffset now,
        ConcurrentBag<Diagnostic> failures,
        CancellationToken cancellationToken)
    {
        var (project, entry) = item;
        var gate = _projectLocks.GetOrAdd(project.CanonicalPath, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            return await IndexProjectAsync(project, entry, refreshId, now, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            // One broken repository must never blank the dashboard: fall back to the last thing
            // known to be true about this project and say so.
            failures.Add(Diagnostic.Error(
                DiagnosticCode.ProjectScanFailed,
                $"'{project.Name}' could not be indexed: {ex.Message}",
                project.CanonicalPath));

            return FallbackView(project, entry, ex);
        }
        finally
        {
            gate.Release();
        }
    }

    private async Task<ProjectView> IndexProjectAsync(
        DiscoveredProject project,
        ProjectRegistryEntry entry,
        string refreshId,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var diagnostics = new List<Diagnostic>();

        var git = project.IsGitRepository
            ? await _git.ReadAsync(project.CanonicalPath, cancellationToken).ConfigureAwait(false)
            : GitReadResult.Unavailable([]);

        diagnostics.AddRange(git.Diagnostics);

        var index = _indexer.Index(project.CanonicalPath, git.FileFacts, refreshId, cancellationToken);
        diagnostics.AddRange(index.Diagnostics);

        // What the working tree says against what the last commit recorded. Only git can answer
        // it, so it is observed here and handed to the projection; the disagreements a project's
        // own files hold are found by the projector itself.
        var workingTree = git.Available
            ? await _workingTree.CompareAsync(
                project.CanonicalPath,
                index.Efforts.SelectMany(e => e.Tickets).ToList(),
                refreshId,
                cancellationToken).ConfigureAwait(false)
            : WorkingTreeComparison.None;

        diagnostics.AddRange(workingTree.Diagnostics);

        var normalized = new NormalizedProject
        {
            Identity = new ProjectIdentity(project.CanonicalPath, project.Name),
            Efforts = index.Efforts,
            Commits = git.Commits,
            Diagnostics = diagnostics,
            GitAvailable = git.Available,
        };

        var artifacts = index.Efforts.SelectMany(e => e.Artifacts).ToList();

        var newActivity = DetectSemanticChanges(project.CanonicalPath, normalized.AllTickets.ToList(), now, refreshId);
        newActivity.AddRange(DetectPlanningChanges(project.CanonicalPath, artifacts, now, refreshId));

        if (newActivity.Count > 0)
        {
            cache.RecordActivity(newActivity);
        }

        cache.SaveTicketSnapshots(project.CanonicalPath, normalized.AllTickets);
        cache.SaveArtifactSnapshots(project.CanonicalPath, artifacts);

        var persisted = cache.LoadActivity(
            project.CanonicalPath,
            now.AddDays(-settings.ActivityRetentionDays));

        var view = ProjectProjector.Project(normalized, new ProjectionOptions
        {
            Now = now,
            RecentWindow = TimeSpan.FromHours(settings.RecentWindowHours),
            PersistedActivity = persisted,
            Conflicts = workingTree.Conflicts,
            IsPinned = entry.Pinned,
        });

        cache.SaveProjectSnapshot(view, now);

        return view;
    }

    /// <summary>
    /// Compares this pass against the previous one. A change only becomes activity once there is
    /// a previous observation to compare with, so a first scan never presents pre-existing files
    /// as things that just happened.
    /// </summary>
    private List<ActivityEvent> DetectSemanticChanges(
        string projectPath,
        IReadOnlyList<WorkflowTicket> tickets,
        DateTimeOffset now,
        string refreshId)
    {
        var previous = cache.LoadTicketSnapshots(projectPath);
        if (previous.Count == 0)
        {
            return [];
        }

        var events = new List<ActivityEvent>();

        foreach (var ticket in tickets)
        {
            if (!previous.TryGetValue(ticket.Id, out var before))
            {
                if (!ticket.IsComplete)
                {
                    events.Add(Change(ticket, ActivityKind.TicketChanged, $"Added: {ticket.Title}"));
                }

                continue;
            }

            // Completion is not movement worth surfacing: closing work must not displace
            // actionable work in Recent.
            if (ticket.IsComplete)
            {
                continue;
            }

            // Each kind of change is reported for what it is, rather than collapsed into a
            // single "something changed".
            if (!string.Equals(before.Labels, DashboardCache.Canonical(ticket.Labels), StringComparison.Ordinal))
            {
                events.Add(Change(ticket, ActivityKind.LabelChanged, $"{ticket.Title}: labels changed"));
            }

            if (!string.Equals(before.Assignees, DashboardCache.Canonical(ticket.Assignees), StringComparison.Ordinal))
            {
                events.Add(Change(ticket, ActivityKind.AssignmentChanged, $"{ticket.Title}: assignment changed"));
            }

            var blockers = DashboardCache.Canonical(ticket.Blockers.Select(b => b.NormalizedKey));
            if (!string.Equals(before.Blockers, blockers, StringComparison.Ordinal))
            {
                events.Add(Change(ticket, ActivityKind.BlockerChanged, $"{ticket.Title}: blockers changed"));
            }

            if (string.Equals(before.SemanticHash, ticket.SemanticHash, StringComparison.Ordinal))
            {
                continue;
            }

            var statusChanged = !string.Equals(before.RawStatus, ticket.Status.RawValue, StringComparison.OrdinalIgnoreCase);
            events.Add(Change(
                ticket,
                statusChanged ? ActivityKind.WorkflowFileChanged : ActivityKind.TicketChanged,
                statusChanged
                    ? $"{ticket.Title}: {before.RawStatus} → {ticket.Status.RawValue}"
                    : $"{ticket.Title}: edited"));
        }

        return events;

        ActivityEvent Change(WorkflowTicket ticket, ActivityKind kind, string summary) => new(
            now,
            kind,
            summary,
            projectPath,
            ticket.Id,
            Provenance.ObservedChange(ticket.SourcePath, now, refreshId));
    }

    /// <summary>
    /// A change to a map, spec, or PRD is direction or scope moving. It is not a work unit, so it
    /// never touches progress, but it is exactly the kind of movement Recent exists to show.
    /// </summary>
    private List<ActivityEvent> DetectPlanningChanges(
        string projectPath,
        IReadOnlyList<PlanningArtifact> artifacts,
        DateTimeOffset now,
        string refreshId)
    {
        var previous = cache.LoadArtifactSnapshots(projectPath);
        if (previous.Count == 0)
        {
            return [];
        }

        var events = new List<ActivityEvent>();

        foreach (var artifact in artifacts)
        {
            if (!previous.TryGetValue(artifact.Path, out var before)
                || string.Equals(before.SemanticHash, artifact.SemanticHash, StringComparison.Ordinal))
            {
                continue;
            }

            events.Add(new ActivityEvent(
                now,
                artifact.Kind == EffortArtifactKind.Map ? ActivityKind.MapChanged : ActivityKind.SpecChanged,
                $"{artifact.Kind} changed: {Path.GetFileName(artifact.Path)}",
                projectPath,
                null,
                Provenance.ObservedChange(artifact.Path, now, refreshId)));
        }

        return events;
    }

    /// <summary>
    /// The last-known-good view for a project whose live read failed, marked stale so its age is
    /// never mistaken for freshness.
    /// </summary>
    private ProjectView FallbackView(DiscoveredProject project, ProjectRegistryEntry entry, Exception cause)
    {
        var (cached, capturedAt) = cache.LoadProjectSnapshot(project.CanonicalPath);
        var identity = new ProjectIdentity(project.CanonicalPath, project.Name);

        if (cached is not null)
        {
            return cached with
            {
                IsStale = true,
                IsPinned = entry.Pinned,
                Diagnostics =
                [
                    .. cached.Diagnostics,
                    Diagnostic.Warning(
                        DiagnosticCode.ProjectScanFailed,
                        $"Showing the snapshot captured {capturedAt:u}; the live read failed: {cause.Message}",
                        project.CanonicalPath),
                ],
            };
        }

        return new ProjectView
        {
            Identity = identity,
            State = ProjectState.Idle,
            StateReason = "The project could not be read and no earlier snapshot exists.",
            Progress = new ProgressSummary(0, 0, 0),
            IsStale = true,
            IsPinned = entry.Pinned,
            Diagnostics =
            [
                Diagnostic.Error(
                    DiagnosticCode.ProjectScanFailed,
                    cause.Message,
                    project.CanonicalPath),
            ],
        };
    }
}
