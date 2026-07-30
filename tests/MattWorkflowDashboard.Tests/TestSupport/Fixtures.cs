namespace MattWorkflowDashboard.Tests.TestSupport;

/// <summary>
/// Sanitized ticket bodies derived from the workflow variants this dashboard has to read.
/// They carry no repository content, only shape.
/// </summary>
public static class Fixtures
{
    public static string Ticket(
        string title,
        string status,
        string? type = null,
        string? blockedBy = null,
        string? gitHub = null,
        string? stage = null,
        bool bold = false)
    {
        var lines = new List<string> { $"# {title}", string.Empty };

        void Add(string key, string? value)
        {
            if (value is not null)
            {
                lines.Add(bold ? $"**{key}:** {value}" : $"{key}: {value}");
            }
        }

        Add("Status", status);
        Add("Type", type);
        Add("Stage", stage);
        Add("Blocked by", blockedBy);
        Add("GitHub", gitHub);

        lines.Add(string.Empty);
        lines.Add("Body text that the dashboard must treat purely as data.");
        return string.Join("\n", lines) + "\n";
    }

    /// <summary>A gh issue-list payload with the fields the adapter reads.</summary>
    public static string GhIssues(params (int Number, string Title, string State, string[] Labels, string UpdatedAt)[] issues) =>
        "[" + string.Join(",", issues.Select(i => $$"""
            {
              "number": {{i.Number}},
              "title": {{System.Text.Json.JsonSerializer.Serialize(i.Title)}},
              "state": "{{i.State}}",
              "labels": [{{string.Join(",", i.Labels.Select(l => $$"""{"name":"{{l}}"}"""))}}],
              "assignees": [],
              "updatedAt": "{{i.UpdatedAt}}",
              "closedAt": {{(i.State == "CLOSED" ? $"\"{i.UpdatedAt}\"" : "null")}},
              "url": "https://github.com/acme/widget/issues/{{i.Number}}"
            }
            """)) + "]";
}
