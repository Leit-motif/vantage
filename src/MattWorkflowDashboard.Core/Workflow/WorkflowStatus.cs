namespace MattWorkflowDashboard.Core.Workflow;

public enum WorkflowStatus
{
    /// <summary>Recognized as a status, but not a status this dashboard understands.</summary>
    Unknown,
    Open,
    Ready,
    InProgress,
    Blocked,
    Resolved,
    Done,
}

/// <summary>
/// A status as observed and as understood. The raw value survives normalization so an
/// alias can never quietly replace what the source actually said.
/// </summary>
public sealed record StatusReading(WorkflowStatus Status, string RawValue, string? AliasApplied = null)
{
    public bool IsComplete => Status is WorkflowStatus.Resolved or WorkflowStatus.Done;

    public string Describe() => AliasApplied is null
        ? $"{RawValue} → {Status}"
        : $"{RawValue} → alias '{AliasApplied}' → {Status}";
}

/// <summary>
/// The built-in, visible status vocabulary. Recognition is case-insensitive and tolerant of
/// hyphen, underscore, and space separators; free-form completion markers such as
/// <c>DONE ✅</c> resolve through a named alias rather than a silent rewrite.
/// </summary>
public static class WorkflowStatusVocabulary
{
    private static readonly Dictionary<string, WorkflowStatus> Canonical = new(StringComparer.OrdinalIgnoreCase)
    {
        ["open"] = WorkflowStatus.Open,
        ["todo"] = WorkflowStatus.Open,
        ["to do"] = WorkflowStatus.Open,
        ["backlog"] = WorkflowStatus.Open,
        ["ready"] = WorkflowStatus.Ready,
        ["ready for agent"] = WorkflowStatus.Ready,
        ["ready for human"] = WorkflowStatus.Ready,
        ["in progress"] = WorkflowStatus.InProgress,
        ["inprogress"] = WorkflowStatus.InProgress,
        ["wip"] = WorkflowStatus.InProgress,
        ["doing"] = WorkflowStatus.InProgress,
        ["claimed"] = WorkflowStatus.InProgress,
        ["active"] = WorkflowStatus.InProgress,
        ["blocked"] = WorkflowStatus.Blocked,
        ["resolved"] = WorkflowStatus.Resolved,
        ["done"] = WorkflowStatus.Done,
        ["complete"] = WorkflowStatus.Done,
        ["completed"] = WorkflowStatus.Done,
        ["closed"] = WorkflowStatus.Done,
    };

    /// <summary>Aliases applied to free-form values, surfaced on the reading rather than hidden.</summary>
    private static readonly Dictionary<string, string> FreeFormAliases = new(StringComparer.OrdinalIgnoreCase)
    {
        ["done"] = "done",
        ["resolved"] = "resolved",
        ["complete"] = "done",
        ["completed"] = "done",
    };

    public static IReadOnlyDictionary<string, WorkflowStatus> KnownStatuses => Canonical;

    public static IReadOnlyDictionary<string, string> KnownAliases => FreeFormAliases;

    public static StatusReading Read(string? raw)
    {
        var rawValue = raw ?? string.Empty;

        var strict = FoldSeparators(rawValue);
        if (strict.Length == 0)
        {
            return new StatusReading(WorkflowStatus.Unknown, rawValue);
        }

        if (Canonical.TryGetValue(strict, out var direct))
        {
            return new StatusReading(direct, rawValue);
        }

        // Free-form completion markers ("DONE ✅", "[done]", "Done!") resolve through a
        // visible alias while the observed text is preserved verbatim.
        foreach (var word in StripDecoration(rawValue).Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            if (FreeFormAliases.TryGetValue(word, out var alias) && Canonical.TryGetValue(alias, out var aliased))
            {
                return new StatusReading(aliased, rawValue, alias);
            }
        }

        return new StatusReading(WorkflowStatus.Unknown, rawValue);
    }

    /// <summary>Lowercases and folds hyphen, underscore, and whitespace runs to single spaces.</summary>
    private static string FoldSeparators(string value)
    {
        var buffer = new char[value.Length];
        var length = 0;
        foreach (var c in value)
        {
            if (c is '-' or '_' || char.IsWhiteSpace(c))
            {
                if (length > 0 && buffer[length - 1] != ' ')
                {
                    buffer[length++] = ' ';
                }
            }
            else
            {
                buffer[length++] = char.ToLowerInvariant(c);
            }
        }

        return new string(buffer, 0, length).Trim();
    }

    /// <summary>
    /// Additionally drops punctuation and symbols. Used only on the alias path, so decoration
    /// never turns an otherwise unrecognized status into a recognized one without a visible alias.
    /// </summary>
    private static string StripDecoration(string value)
    {
        var buffer = new char[value.Length];
        var length = 0;
        foreach (var c in value)
        {
            if (char.IsLetterOrDigit(c))
            {
                buffer[length++] = char.ToLowerInvariant(c);
            }
            else if (length > 0 && buffer[length - 1] != ' ')
            {
                buffer[length++] = ' ';
            }
        }

        return new string(buffer, 0, length).Trim();
    }
}
