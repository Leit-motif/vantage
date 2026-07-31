using System.Collections.Concurrent;
using MattWorkflowDashboard.Core;
using MattWorkflowDashboard.Core.Observed;
using MattWorkflowDashboard.Core.Projection;
using MattWorkflowDashboard.Core.Reconciliation;
using MattWorkflowDashboard.Core.Workflow;
using MattWorkflowDashboard.Infrastructure.Discovery;
using MattWorkflowDashboard.Infrastructure.Git;
using MattWorkflowDashboard.Infrastructure.GitHub;
using MattWorkflowDashboard.Infrastructure.Parsing;
using MattWorkflowDashboard.Infrastructure.Persistence;
using MattWorkflowDashboard.Infrastructure.Processes;
using MattWorkflowDashboard.Infrastructure.Settings;

namespace MattWorkflowDashboard.Infrastructure.Refresh;

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
    private readonly GitHubCliAdapter _gitHub = new(processRunner, settings.MaxGitHubIssuesPerRepository);
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
        bool gitHubAuthenticated;

        // Everything from selection onwards can change registry intent, and every way out of it —
        // cancellation during the gh session check included — has to leave that intent written.
        try
        {
            var selected = SelectProjects(discovery.Projects, diagnostics);

            gitHubAuthenticated = settings.GitHubEnrichmentEnabled
                && await _gitHub.IsAuthenticatedAsync(cancellationToken).ConfigureAwait(false);

            if (settings.GitHubEnrichmentEnabled && !gitHubAuthenticated)
            {
                diagnostics.Add(Diagnostic.Info(
                    DiagnosticCode.GitHubUnauthenticated,
                    "No authenticated gh session; the dashboard is showing local evidence only.",
                    "gh auth status"));
            }

            await Parallel.ForEachAsync(
                selected.Select((entry, index) => (entry, index)),
                new ParallelOptions
                {
                    CancellationToken = cancellationToken,
                    MaxDegreeOfParallelism = Math.Max(1, settings.MaxConcurrentExternalProcesses),
                },
                async (item, token) =>
                {
                    var view = await RefreshProjectAsync(
                            item.entry, refreshId, now, gitHubAuthenticated, failures, token)
                        .ConfigureAwait(false);
                    views.Add((item.index, view));
                }).ConfigureAwait(false);
        }
        finally
        {
            // A superseded or failed pass still changed registry intent in memory. Writing it here
            // means a cancelled refresh cannot quietly drop a discovered project or a remote that
            // is waiting on confirmation, and a failed write stays outstanding for the next pass.
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
            Offline = settings.GitHubEnrichmentEnabled && !gitHubAuthenticated,
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
    /// hidden, excluded, opted-in, and confirmed-origin intent has to follow it there.
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
    /// Writes registry intent the refresh itself produced — a newly discovered project, or a
    /// first-seen or pending remote — so the association the owner sees is the one that comes back
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
        bool gitHubAuthenticated,
        ConcurrentBag<Diagnostic> failures,
        CancellationToken cancellationToken)
    {
        var (project, entry) = item;
        var gate = _projectLocks.GetOrAdd(project.CanonicalPath, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            return await IndexProjectAsync(
                    project, entry, refreshId, now, gitHubAuthenticated, cancellationToken)
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
        bool gitHubAuthenticated,
        CancellationToken cancellationToken)
    {
        var diagnostics = new List<Diagnostic>();

        var git = project.IsGitRepository
            ? await _git.ReadAsync(project.CanonicalPath, cancellationToken).ConfigureAwait(false)
            : GitReadResult.Unavailable([]);

        diagnostics.AddRange(git.Diagnostics);

        var origin = ConfirmOrigin(entry, git, diagnostics);

        var index = _indexer.Index(project.CanonicalPath, git.FileFacts, refreshId, cancellationToken);
        diagnostics.AddRange(index.Diagnostics);

        var gitHub = origin is not null && gitHubAuthenticated
            ? await _gitHub.ReadIssuesAsync(origin, refreshId, cancellationToken).ConfigureAwait(false)
            : GitHubReadResult.Unavailable([]);

        diagnostics.AddRange(gitHub.Diagnostics);

        var reconciled = TicketReconciler.Reconcile(index.Efforts, gitHub.Issues, origin);
        diagnostics.AddRange(reconciled.Diagnostics);

        var normalized = new NormalizedProject
        {
            Identity = new ProjectIdentity(project.CanonicalPath, project.Name),
            Origin = origin,
            Efforts = reconciled.Efforts,
            Commits = git.Commits,
            GitHubIssues = gitHub.Issues,
            Diagnostics = diagnostics,
            GitAvailable = git.Available,
            GitHubAvailable = gitHub.Available,
        };

        var artifacts = reconciled.Efforts.SelectMany(e => e.Artifacts).ToList();

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
            Conflicts = reconciled.Conflicts,
            IsPinned = entry.Pinned,
        });

        cache.SaveProjectSnapshot(view, now);
        return view;
    }

    /// <summary>
    /// A remote is an association on the local identity, never the identity itself. A remote that
    /// differs from the confirmed one is recorded as pending and reported, so a changed origin
    /// cannot silently attach unrelated GitHub work — and so confirming a relink adopts the origin
    /// the owner was actually shown rather than whatever the remote reads by then.
    /// </summary>
    private GitHubOrigin? ConfirmOrigin(
        ProjectRegistryEntry entry,
        GitReadResult git,
        ICollection<Diagnostic> diagnostics)
    {
        var observed = git.Origin;

        if (entry.ConfirmedOrigin is null)
        {
            if (observed is not null)
            {
                entry.ConfirmedOrigin = observed.Slug;
                entry.PendingOrigin = null;
                settings.MarkChanged();
            }

            return observed;
        }

        if (observed is null)
        {
            // Not being able to read the remote says nothing about it. A relink that is waiting on
            // the owner must survive an unreadable repository rather than quietly cancelling itself.
            return GitHubOrigin.TryParse($"https://github.com/{entry.ConfirmedOrigin}");
        }

        if (string.Equals(entry.ConfirmedOrigin, observed.Slug, StringComparison.OrdinalIgnoreCase))
        {
            // The remote agrees with the confirmed association again; there is nothing to confirm.
            if (entry.PendingOrigin is not null)
            {
                entry.PendingOrigin = null;
                settings.MarkChanged();
            }

            return GitHubOrigin.TryParse($"https://github.com/{entry.ConfirmedOrigin}");
        }

        if (!string.Equals(entry.PendingOrigin, observed.Slug, StringComparison.OrdinalIgnoreCase))
        {
            entry.PendingOrigin = observed.Slug;
            settings.MarkChanged();
        }

        diagnostics.Add(Diagnostic.Warning(
            DiagnosticCode.OriginChanged,
            $"The remote changed from '{entry.ConfirmedOrigin}' to '{observed.Slug}'. Confirm the relink in Settings before GitHub evidence is used.",
            entry.Path));

        return GitHubOrigin.TryParse($"https://github.com/{entry.ConfirmedOrigin}");
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

            if (ticket.CommentCount > before.CommentCount)
            {
                events.Add(Change(
                    ticket,
                    ActivityKind.CommentAdded,
                    $"{ticket.Title}: {ticket.CommentCount - before.CommentCount} new comment(s)"));
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
