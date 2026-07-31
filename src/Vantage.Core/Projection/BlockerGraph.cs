using Vantage.Core.Workflow;

namespace Vantage.Core.Projection;

/// <summary>The resolved dependency position of one ticket inside its effort.</summary>
public sealed record BlockerResolution(
    string TicketId,
    IReadOnlyList<string> ResolvedBlockerIds,
    bool HasOpenBlocker,
    bool HasInvalidBlocker)
{
    /// <summary>
    /// A ticket can only move when every blocker resolved cleanly and every one of them is done.
    /// An invalid dependency graph must fail visibly rather than silently permitting work.
    /// </summary>
    public bool CanMove => !HasOpenBlocker && !HasInvalidBlocker;
}

/// <summary>
/// Resolves blocker references within a single effort. References never reach outside the effort
/// that declares them, so similarly named work elsewhere cannot satisfy a dependency.
/// </summary>
public static class BlockerGraph
{
    public static IReadOnlyDictionary<string, BlockerResolution> Resolve(
        WorkflowEffort effort,
        ICollection<Diagnostic> diagnostics)
    {
        var byKey = BuildLookup(effort);
        var edges = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        var invalid = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var ticket in effort.Tickets)
        {
            var resolved = new List<string>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var blocker in ticket.Blockers)
            {
                if (!seen.Add(blocker.NormalizedKey))
                {
                    invalid.Add(ticket.Id);
                    diagnostics.Add(Diagnostic.Warning(
                        DiagnosticCode.BlockerDuplicate,
                        $"'{ticket.Title}' lists blocker '{blocker.RawValue}' more than once.",
                        ticket.SourcePath,
                        ticket.Provenance));
                    continue;
                }

                if (!byKey.TryGetValue(blocker.NormalizedKey, out var targetId))
                {
                    invalid.Add(ticket.Id);
                    diagnostics.Add(Diagnostic.Warning(
                        DiagnosticCode.BlockerMissing,
                        $"'{ticket.Title}' is blocked by '{blocker.RawValue}', which does not exist in effort '{effort.Name}'.",
                        ticket.SourcePath,
                        ticket.Provenance));
                    continue;
                }

                if (string.Equals(targetId, ticket.Id, StringComparison.OrdinalIgnoreCase))
                {
                    invalid.Add(ticket.Id);
                    diagnostics.Add(Diagnostic.Warning(
                        DiagnosticCode.BlockerSelf,
                        $"'{ticket.Title}' declares itself as a blocker.",
                        ticket.SourcePath,
                        ticket.Provenance));
                    continue;
                }

                resolved.Add(targetId);
            }

            edges[ticket.Id] = resolved;
        }

        foreach (var cycleMember in FindCycleMembers(edges))
        {
            if (invalid.Add(cycleMember))
            {
                var ticket = effort.Tickets.First(t => string.Equals(t.Id, cycleMember, StringComparison.OrdinalIgnoreCase));
                diagnostics.Add(Diagnostic.Warning(
                    DiagnosticCode.BlockerCycle,
                    $"'{ticket.Title}' takes part in a circular blocker chain in effort '{effort.Name}'.",
                    ticket.SourcePath,
                    ticket.Provenance));
            }
        }

        var completion = effort.Tickets.ToDictionary(t => t.Id, t => t.IsComplete, StringComparer.OrdinalIgnoreCase);

        return effort.Tickets.ToDictionary(
            t => t.Id,
            t =>
            {
                var blockerIds = edges.TryGetValue(t.Id, out var ids) ? ids : [];
                var hasOpen = blockerIds.Any(id => !completion.TryGetValue(id, out var done) || !done);
                return new BlockerResolution(t.Id, blockerIds, hasOpen, invalid.Contains(t.Id));
            },
            StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Builds the effort-local reference table. A reference may name a ticket by id, by its
    /// numeric prefix, or by its linked GitHub number — all explicit identifiers, never a title.
    /// </summary>
    private static Dictionary<string, string> BuildLookup(WorkflowEffort effort)
    {
        var lookup = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var ambiguous = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        void Add(string key, string ticketId)
        {
            if (string.IsNullOrEmpty(key) || ambiguous.Contains(key))
            {
                return;
            }

            if (lookup.TryGetValue(key, out var existing) && !string.Equals(existing, ticketId, StringComparison.OrdinalIgnoreCase))
            {
                lookup.Remove(key);
                ambiguous.Add(key);
                return;
            }

            lookup[key] = ticketId;
        }

        foreach (var ticket in effort.Tickets)
        {
            Add(NormalizeReference(ticket.LocalKey), ticket.Id);

            var numericPrefix = NumericPrefix(ticket.LocalKey);
            if (numericPrefix is not null)
            {
                Add(numericPrefix, ticket.Id);
                Add(numericPrefix.TrimStart('0'), ticket.Id);
            }

            if (ticket.Link is { } link)
            {
                Add(link.Number.ToString(), ticket.Id);
            }
        }

        return lookup;
    }

    /// <summary>Normalizes a blocker reference for effort-local matching.</summary>
    public static string NormalizeReference(string raw)
    {
        var value = raw.Trim().Trim('#', '"', '\'', '`');
        if (value.EndsWith(".md", StringComparison.OrdinalIgnoreCase))
        {
            value = value[..^3];
        }

        return value.Trim().ToLowerInvariant();
    }

    private static string? NumericPrefix(string id)
    {
        var length = 0;
        while (length < id.Length && char.IsDigit(id[length]))
        {
            length++;
        }

        return length > 0 ? id[..length] : null;
    }

    /// <summary>
    /// Every node that sits on a cycle. A node is on a cycle exactly when its strongly connected
    /// component has more than one member, or when it points at itself; self-references are
    /// reported separately, so only multi-member components are collected here.
    /// </summary>
    private static HashSet<string> FindCycleMembers(Dictionary<string, List<string>> edges)
    {
        var members = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var index = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var lowLink = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var onStack = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var stack = new Stack<string>();
        var next = 0;

        void StrongConnect(string node)
        {
            index[node] = next;
            lowLink[node] = next;
            next++;
            stack.Push(node);
            onStack.Add(node);

            foreach (var child in edges.TryGetValue(node, out var list) ? list : [])
            {
                if (!index.ContainsKey(child))
                {
                    StrongConnect(child);
                    lowLink[node] = Math.Min(lowLink[node], lowLink[child]);
                }
                else if (onStack.Contains(child))
                {
                    lowLink[node] = Math.Min(lowLink[node], index[child]);
                }
            }

            if (lowLink[node] != index[node])
            {
                return;
            }

            var component = new List<string>();
            string popped;
            do
            {
                popped = stack.Pop();
                onStack.Remove(popped);
                component.Add(popped);
            }
            while (!string.Equals(popped, node, StringComparison.OrdinalIgnoreCase));

            if (component.Count > 1)
            {
                foreach (var member in component)
                {
                    members.Add(member);
                }
            }
        }

        foreach (var node in edges.Keys)
        {
            if (!index.ContainsKey(node))
            {
                StrongConnect(node);
            }
        }

        return members;
    }
}
