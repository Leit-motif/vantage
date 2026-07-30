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
    TimeProvider? timeProvider = null)
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

    /// <summary>Raised when the registry gains a project that settings had not seen before.</summary>
    public event Action<IReadOnlyList<ProjectRegistryEntry>>? RegistryDiscovered;

    public async Task<DashboardSnapshot> RefreshAsync(CancellationToken cancellationToken)
    {
        var refreshId = $"r{_clock.GetUtcNow():yyyyMMddHHmmssfff}";
        var now = _clock.GetUtcNow();
        var diagnostics = new List<Diagnostic>(cache.Diagnostics);

        var discovery = new ProjectDiscovery(settings).Discover(cancellationToken);
        diagnostics.AddRange(discovery.Diagnostics);

        var selected = SelectProjects(discovery.Projects, diagnostics);

        var gitHubAuthenticated = settings.GitHubEnrichmentEnabled
            && await _gitHub.IsAuthenticatedAsync(cancellationToken).ConfigureAwait(false);

        if (settings.GitHubEnrichmentEnabled && !gitHubAuthenticated)
        {
            diagnostics.Add(Diagnostic.Info(
                DiagnosticCode.GitHubUnauthenticated,
                "No authenticated gh session; the dashboard is showing local evidence only.",
                "gh auth status"));
        }

        var views = new ConcurrentBag<(int Order, ProjectView View)>();
        var failures = new ConcurrentBag<Diagnostic>();

        await Parallel.ForEachAsync(
            selected.Select((entry, index) => (entry, index)),
            new ParallelOptions
            {
                CancellationToken = cancellationToken,
                MaxDegreeOfParallelism = Math.Max(1, settings.MaxConcurrentExternalProcesses),
            },
            async (item, token) =>
            {
                var view = await RefreshProjectAsync(item.entry, refreshId, now, gitHubAuthenticated, failures, token)
                    .ConfigureAwait(false);
                views.Add((item.index, view));
            }).ConfigureAwait(false);

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
    /// Applies registry intent to discovery. Nested projects stay out unless the owner opted them
    /// back in, and hiding or excluding a project preserves the intent rather than forgetting it.
    /// </summary>
    private IReadOnlyList<(DiscoveredProject Project, ProjectRegistryEntry Entry)> SelectProjects(
        IReadOnlyList<DiscoveredProject> discovered,
        ICollection<Diagnostic> diagnostics)
    {
        var selected = new List<(DiscoveredProject, ProjectRegistryEntry)>();
        var added = new List<ProjectRegistryEntry>();

        foreach (var project in discovered)
        {
            var entry = settings.FindProject(project.CanonicalPath);
            if (entry is null)
            {
                entry = new ProjectRegistryEntry
                {
                    Path = project.CanonicalPath,
                    State = ProjectRegistryState.Enabled,
                };
                added.Add(entry);
                settings.Projects.Add(entry);
            }

            if (entry.State != ProjectRegistryState.Enabled)
            {
                continue;
            }

            if (project.IsNested && !entry.NestedOptIn)
            {
                diagnostics.Add(Diagnostic.Info(
                    DiagnosticCode.ProjectScanFailed,
                    $"'{project.Name}' is nested inside '{project.ParentProjectPath}' and is excluded; opt it in from Settings to track it.",
                    project.CanonicalPath));
                continue;
            }

            selected.Add((project, entry));
        }

        if (added.Count > 0)
        {
            RegistryDiscovered?.Invoke(added);
        }

        return selected;
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
            return await IndexProjectAsync(project, entry, refreshId, now, gitHubAuthenticated, cancellationToken)
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

        var localTickets = normalized.AllTickets
            .Where(t => t.EffortId != TicketReconciler.GitHubEffortId)
            .ToList();

        var newActivity = DetectSemanticChanges(project.CanonicalPath, localTickets, now, refreshId);
        if (newActivity.Count > 0)
        {
            cache.RecordActivity(newActivity);
        }

        cache.SaveTicketSnapshots(project.CanonicalPath, localTickets);

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
    /// differs from the confirmed one is reported and ignored, so a changed origin cannot silently
    /// attach unrelated GitHub work.
    /// </summary>
    private static GitHubOrigin? ConfirmOrigin(
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
            }

            return observed;
        }

        if (observed is null || string.Equals(entry.ConfirmedOrigin, observed.Slug, StringComparison.OrdinalIgnoreCase))
        {
            return GitHubOrigin.TryParse($"https://github.com/{entry.ConfirmedOrigin}");
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

            if (string.Equals(before.SemanticHash, ticket.SemanticHash, StringComparison.Ordinal))
            {
                continue;
            }

            // Completion is not movement worth surfacing: closing work must not displace
            // actionable work in Recent.
            if (ticket.IsComplete)
            {
                continue;
            }

            var kind = string.Equals(before.RawStatus, ticket.Status.RawValue, StringComparison.OrdinalIgnoreCase)
                ? ActivityKind.TicketChanged
                : ActivityKind.WorkflowFileChanged;

            events.Add(Change(ticket, kind, $"{ticket.Title}: {before.RawStatus} → {ticket.Status.RawValue}"));
        }

        return events;

        ActivityEvent Change(WorkflowTicket ticket, ActivityKind kind, string summary) => new(
            now,
            kind,
            summary,
            projectPath,
            ticket.Id,
            Provenance.Watcher(ticket.SourcePath, now, refreshId));
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
