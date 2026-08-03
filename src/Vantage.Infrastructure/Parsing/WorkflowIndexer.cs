using System.Text.RegularExpressions;
using Vantage.Core;
using Vantage.Core.Observed;
using Vantage.Core.Workflow;

namespace Vantage.Infrastructure.Parsing;

/// <summary>
/// Git-derived facts about the files in a project, used to give artifacts honest provenance.
/// Files git has never seen keep filesystem-only provenance.
/// </summary>
public sealed record GitFileFacts(
    IReadOnlyDictionary<string, DateTimeOffset> LastCommitByPath,
    bool GitAvailable)
{
    public static readonly GitFileFacts None = new(
        new Dictionary<string, DateTimeOffset>(StringComparer.OrdinalIgnoreCase),
        GitAvailable: false);
}

public sealed record WorkflowIndexResult(
    IReadOnlyList<WorkflowEffort> Efforts,
    IReadOnlyList<Diagnostic> Diagnostics);

/// <summary>
/// Reads a project's <c>.scratch</c> tree into normalized workflow facts. Monitored content is
/// treated purely as data: it is parsed, never interpreted as an instruction and never executed.
/// </summary>
public sealed partial class WorkflowIndexer(int maxFileBytes = 1_048_576, int maxFilesPerEffort = 500)
{
    private static readonly string[] MapNames = ["map"];
    private static readonly string[] SpecNames = ["spec"];
    private static readonly string[] PrdNames = ["prd"];

    public WorkflowIndexResult Index(string projectPath, GitFileFacts gitFacts, string refreshId, CancellationToken cancellationToken)
    {
        var diagnostics = new List<Diagnostic>();
        var scratch = Path.Combine(projectPath, ".scratch");

        if (!Directory.Exists(scratch))
        {
            return new WorkflowIndexResult([], diagnostics);
        }

        var efforts = new List<WorkflowEffort>();

        foreach (var directory in SafeDirectories(scratch, diagnostics).OrderBy(d => d, StringComparer.OrdinalIgnoreCase))
        {
            cancellationToken.ThrowIfCancellationRequested();

            var effort = ReadEffort(projectPath, directory, gitFacts, refreshId, diagnostics, cancellationToken);
            if (effort is not null)
            {
                efforts.Add(effort);
            }
        }

        return new WorkflowIndexResult(efforts, diagnostics);
    }

    private WorkflowEffort? ReadEffort(
        string projectPath,
        string effortPath,
        GitFileFacts gitFacts,
        string refreshId,
        ICollection<Diagnostic> diagnostics,
        CancellationToken cancellationToken)
    {
        var name = Path.GetFileName(effortPath);
        var markdown = CollectMarkdown(effortPath, diagnostics);

        if (markdown.Count == 0)
        {
            return null;
        }

        var mapFiles = markdown.Where(f => IsPlanningArtifact(f, effortPath, MapNames)).ToList();
        var specFiles = markdown.Where(f => IsPlanningArtifact(f, effortPath, SpecNames)).ToList();
        var prdFiles = markdown.Where(f => IsPlanningArtifact(f, effortPath, PrdNames)).ToList();
        var issuesDirectory = Path.Combine(effortPath, "issues");
        var hasIssuesDirectory = Directory.Exists(issuesDirectory)
            && markdown.Any(f => IsUnder(f, issuesDirectory));

        var ticketFiles = markdown
            .Where(f => !mapFiles.Contains(f) && !specFiles.Contains(f) && !prdFiles.Contains(f))
            .ToList();

        // An effort is an immediate .scratch child that plans work: a map, a spec, a PRD, or
        // markdown issues. A directory with none of those is not an effort.
        if (mapFiles.Count == 0 && specFiles.Count == 0 && prdFiles.Count == 0 && !hasIssuesDirectory && ticketFiles.Count == 0)
        {
            return null;
        }

        var effortId = MakeEffortId(projectPath, effortPath);
        var tickets = new List<WorkflowTicket>();
        var usedIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var file in ticketFiles.OrderBy(f => f, StringComparer.OrdinalIgnoreCase))
        {
            cancellationToken.ThrowIfCancellationRequested();

            var artifact = ReadArtifact(projectPath, file, EffortArtifactKind.Issue, gitFacts, refreshId, diagnostics);
            if (artifact is null)
            {
                continue;
            }

            tickets.Add(ToTicket(artifact, effortId, name, usedIds, diagnostics));
        }

        if (tickets.Count == 0)
        {
            diagnostics.Add(Diagnostic.Info(
                DiagnosticCode.EffortEmpty,
                $"Effort '{name}' has planning artifacts but no readable work units.",
                effortPath));
        }

        var mapOrder = mapFiles.Count == 0
            ? []
            : ReadMapOrder(mapFiles[0], tickets, diagnostics);

        return new WorkflowEffort
        {
            Id = effortId,
            Name = name,
            Path = effortPath,
            Artifacts =
            [
                .. Describe(mapFiles, EffortArtifactKind.Map, diagnostics),
                .. Describe(specFiles, EffortArtifactKind.Spec, diagnostics),
                .. Describe(prdFiles, EffortArtifactKind.Prd, diagnostics),
            ],
            Tickets = tickets,
            MapOrder = mapOrder,
        };
    }

    private ObservedArtifact? ReadArtifact(
        string projectPath,
        string file,
        EffortArtifactKind kind,
        GitFileFacts gitFacts,
        string refreshId,
        ICollection<Diagnostic> diagnostics)
    {
        string content;
        try
        {
            var info = new FileInfo(file);
            if (info.Length > maxFileBytes)
            {
                diagnostics.Add(Diagnostic.Info(
                    DiagnosticCode.TicketUnrecognized,
                    $"'{info.Name}' is larger than the {maxFileBytes / 1024} KB read limit and was skipped.",
                    file));
                return null;
            }

            content = File.ReadAllText(file);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            diagnostics.Add(Diagnostic.Warning(
                DiagnosticCode.ProjectScanFailed,
                $"Could not read '{file}': {ex.Message}",
                file));
            return null;
        }

        var (heading, fields) = MetadataGrammar.Parse(content);
        var relative = Path.GetRelativePath(projectPath, file).Replace('\\', '/');
        var tracked = gitFacts.LastCommitByPath.TryGetValue(relative, out var committedAt);

        // Untracked and ignored planning files are real work; they simply keep filesystem-only
        // provenance so their timestamps never masquerade as activity.
        var provenance = tracked
            ? Provenance.Git($"{relative}", committedAt, refreshId)
            : Provenance.File(file, refreshId);

        return new ObservedArtifact(
            file,
            relative,
            kind,
            heading,
            fields,
            MetadataGrammar.AcceptanceChecklist(content),
            SemanticHash.Compute(content),
            tracked,
            provenance);
    }

    /// <summary>
    /// Turns one artifact into a work unit. A file with no status at all is an unrecognized
    /// artifact: excluded from totals and reported, never quietly counted.
    /// </summary>
    private static WorkflowTicket ToTicket(
        ObservedArtifact artifact,
        string effortId,
        string effortName,
        HashSet<string> usedIds,
        ICollection<Diagnostic> diagnostics)
    {
        var localKey = MakeLocalKey(artifact.RelativePath, usedIds);
        var id = $"{effortName.ToLowerInvariant()}/{localKey}";
        var statusField = artifact.Field(MetadataGrammar.Keys.Status);
        var title = artifact.Field(MetadataGrammar.Keys.Title)?.RawValue
            ?? artifact.HeadingTitle
            ?? Path.GetFileNameWithoutExtension(artifact.AbsolutePath);

        if (statusField is null)
        {
            diagnostics.Add(Diagnostic.Warning(
                DiagnosticCode.TicketUnrecognized,
                $"'{title}' declares no Status and is excluded from progress.",
                artifact.AbsolutePath,
                artifact.Provenance));

            return new WorkflowTicket
            {
                Id = id,
                LocalKey = localKey,
                EffortId = effortId,
                Title = title,
                Status = new StatusReading(WorkflowStatus.Unknown, string.Empty),
                Kind = WorkUnitKind.Unrecognized,
                SourcePath = artifact.AbsolutePath,
                SemanticHash = artifact.SemanticHash,
                Provenance = artifact.Provenance,
            };
        }

        var status = WorkflowStatusVocabulary.Read(statusField.RawValue);
        if (status.Status == WorkflowStatus.Unknown)
        {
            diagnostics.Add(Diagnostic.Info(
                DiagnosticCode.TicketAmbiguousStatus,
                $"'{title}' has status '{statusField.RawValue}', which is not a recognized state; it stays incomplete.",
                artifact.AbsolutePath,
                artifact.Provenance));
        }

        var kind = ReadKind(artifact);
        var blockers = MetadataGrammar
            .SplitList(artifact.Field(MetadataGrammar.Keys.BlockedBy)?.RawValue)
            .Select(raw => new BlockerReference(raw, Core.Projection.BlockerGraph.NormalizeReference(raw)))
            .ToList();

        return new WorkflowTicket
        {
            Id = id,
            LocalKey = localKey,
            EffortId = effortId,
            Title = title,
            Status = status,
            StatusLine = statusField.LineNumber,
            Acceptance = artifact.Acceptance,
            Kind = kind,
            Stage = artifact.Field(MetadataGrammar.Keys.Stage)?.RawValue,
            Blockers = blockers,
            Link = ReadLink(artifact),
            Labels = MetadataGrammar.SplitList(artifact.Field(MetadataGrammar.Keys.Labels)?.RawValue),
            Assignees = MetadataGrammar.SplitList(artifact.Field(MetadataGrammar.Keys.Assignee)?.RawValue),
            SourcePath = artifact.AbsolutePath,
            SemanticHash = artifact.SemanticHash,
            Provenance = artifact.Provenance,
        };
    }

    /// <summary>Maps a declared type onto the full pipeline. Unstated work is implementation work.</summary>
    private static WorkUnitKind ReadKind(ObservedArtifact artifact)
    {
        var declared = artifact.Field(MetadataGrammar.Keys.Type)?.RawValue?.Trim().ToLowerInvariant();
        return declared switch
        {
            null or "" => WorkUnitKind.Implementation,
            "planning" or "plan" or "milestone" or "spec" or "prd" => WorkUnitKind.Planning,
            "research" => WorkUnitKind.Research,
            "grilling" or "grill" => WorkUnitKind.Grilling,
            "prototype" or "spike" => WorkUnitKind.Prototype,
            "review" or "audit" or "code-review" => WorkUnitKind.Review,
            "release" or "ship" or "deploy" => WorkUnitKind.Release,
            "map" or "container" or "epic" or "parent" => WorkUnitKind.Container,
            _ => WorkUnitKind.Implementation,
        };
    }

    private static GitHubLink? ReadLink(ObservedArtifact artifact)
    {
        var raw = artifact.Field(MetadataGrammar.Keys.GitHub)?.RawValue;
        if (string.IsNullOrWhiteSpace(raw))
        {
            return null;
        }

        var match = IssueReference().Match(raw);
        if (!match.Success || !int.TryParse(match.Groups["number"].Value, out var number))
        {
            return null;
        }

        var repository = match.Groups["repo"].Success ? match.Groups["repo"].Value : string.Empty;
        return new GitHubLink(repository, number, raw.Trim());
    }

    /// <summary>
    /// Reads direction and order from a map. A map orders tickets; it never overrides the facts
    /// a ticket file states about itself.
    /// </summary>
    private static IReadOnlyList<string> ReadMapOrder(
        string mapFile,
        IReadOnlyList<WorkflowTicket> tickets,
        ICollection<Diagnostic> diagnostics)
    {
        string content;
        try
        {
            content = File.ReadAllText(mapFile);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            diagnostics.Add(Diagnostic.Info(
                DiagnosticCode.ProjectScanFailed,
                $"Could not read map '{mapFile}': {ex.Message}",
                mapFile));
            return [];
        }

        var order = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        void Take(WorkflowTicket? ticket)
        {
            if (ticket is not null && seen.Add(ticket.Id))
            {
                order.Add(ticket.Id);
            }
        }

        foreach (Match match in MapReference().Matches(SemanticHash.Normalize(content)))
        {
            if (match.Groups["file"].Success)
            {
                var stem = Path.GetFileNameWithoutExtension(match.Groups["file"].Value);
                Take(tickets.FirstOrDefault(t =>
                    string.Equals(Path.GetFileNameWithoutExtension(t.SourcePath), stem, StringComparison.OrdinalIgnoreCase)));
                continue;
            }

            if (match.Groups["issue"].Success && int.TryParse(match.Groups["issue"].Value, out var number))
            {
                Take(tickets.FirstOrDefault(t => t.Link?.Number == number));
            }
        }

        return order;
    }

    /// <summary>
    /// Records a planning artifact's content identity so a change to direction or scope can be
    /// recognized later as movement, without the artifact ever counting as a work unit.
    /// </summary>
    private static IEnumerable<PlanningArtifact> Describe(
        IEnumerable<string> files,
        EffortArtifactKind kind,
        ICollection<Diagnostic> diagnostics)
    {
        foreach (var file in files)
        {
            string content;
            try
            {
                content = File.ReadAllText(file);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                diagnostics.Add(Diagnostic.Info(
                    DiagnosticCode.ProjectScanFailed,
                    $"Could not read '{file}': {ex.Message}",
                    file));
                continue;
            }

            yield return new PlanningArtifact(kind, file, SemanticHash.Compute(content));
        }
    }

    private static string MakeEffortId(string projectPath, string effortPath) =>
        Path.GetRelativePath(projectPath, effortPath).Replace('\\', '/').ToLowerInvariant();

    /// <summary>
    /// The name a ticket answers to inside its own effort. Two efforts may each hold an
    /// <c>001.md</c>; the project-unique id is built from this and the effort name.
    /// </summary>
    private static string MakeLocalKey(string relativePath, HashSet<string> usedIds)
    {
        var stem = Path.GetFileNameWithoutExtension(relativePath).ToLowerInvariant();
        if (usedIds.Add(stem))
        {
            return stem;
        }

        // Two files with the same stem in one effort still need distinct, stable identities.
        var qualified = relativePath.ToLowerInvariant();
        usedIds.Add(qualified);
        return qualified;
    }

    private List<string> CollectMarkdown(string effortPath, ICollection<Diagnostic> diagnostics)
    {
        var files = new List<string>();
        try
        {
            foreach (var file in Directory.EnumerateFiles(effortPath, "*.md", SearchOption.AllDirectories))
            {
                if (files.Count >= maxFilesPerEffort)
                {
                    diagnostics.Add(Diagnostic.Warning(
                        DiagnosticCode.ScanTruncated,
                        $"Effort '{Path.GetFileName(effortPath)}' has more than {maxFilesPerEffort} markdown files; the rest were skipped.",
                        effortPath));
                    break;
                }

                files.Add(file);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            diagnostics.Add(Diagnostic.Warning(
                DiagnosticCode.ProjectScanFailed,
                $"Could not enumerate '{effortPath}': {ex.Message}",
                effortPath));
        }

        return files;
    }

    private static IEnumerable<string> SafeDirectories(string path, ICollection<Diagnostic> diagnostics)
    {
        try
        {
            return Directory.GetDirectories(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            diagnostics.Add(Diagnostic.Warning(
                DiagnosticCode.ProjectScanFailed,
                $"Could not enumerate '{path}': {ex.Message}",
                path));
            return [];
        }
    }

    /// <summary>
    /// A planning artifact is a direct child of the effort whose stem is exactly map, spec, or
    /// PRD, matched case-insensitively so filename casing never hides one. The match is exact on
    /// purpose: treating <c>001-spec.md</c> as the effort's spec would silently drop a ticket
    /// from progress.
    /// </summary>
    private static bool IsPlanningArtifact(string file, string effortPath, string[] names)
    {
        var directory = Path.GetDirectoryName(file);
        if (!string.Equals(directory, effortPath, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var stem = Path.GetFileNameWithoutExtension(file);
        return names.Any(name => string.Equals(stem, name, StringComparison.OrdinalIgnoreCase));
    }

    private static bool IsUnder(string file, string directory) =>
        file.StartsWith(directory + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);

    [GeneratedRegex(
        @"https?://github\.com/(?<repo>[\w.\-]+/[\w.\-]+)/issues/(?<number>\d+)|(?<repo>[\w.\-]+/[\w.\-]+)?#(?<number>\d+)",
        RegexOptions.ExplicitCapture)]
    private static partial Regex IssueReference();

    [GeneratedRegex(@"\]\((?<file>[^)]+\.md)\)|(?<!\w)#(?<issue>\d+)", RegexOptions.ExplicitCapture)]
    private static partial Regex MapReference();
}
