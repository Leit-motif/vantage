namespace Vantage.Tests.TestSupport;

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
        string? labels = null,
        string? assignee = null,
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
        Add("Labels", labels);
        Add("Assignee", assignee);

        lines.Add(string.Empty);
        lines.Add("Body text that the dashboard must treat purely as data.");
        return string.Join("\n", lines) + "\n";
    }

    /// <summary>One issue as gh would report it.</summary>
    public sealed record GhIssue(int Number, string Title, string State, string[] Labels, string UpdatedAt)
    {
        public string[] Assignees { get; init; } = [];

        public string Body { get; init; } = string.Empty;

        public int Comments { get; init; }
    }

    /// <summary>A gh issue-list payload with the fields the adapter reads.</summary>
    public static string GhIssues(params GhIssue[] issues) =>
        "[" + string.Join(",", issues.Select(i => $$"""
            {
              "number": {{i.Number}},
              "title": {{System.Text.Json.JsonSerializer.Serialize(i.Title)}},
              "state": "{{i.State}}",
              "labels": [{{string.Join(",", i.Labels.Select(l => $$"""{"name":"{{l}}"}"""))}}],
              "assignees": [{{string.Join(",", i.Assignees.Select(a => $$"""{"login":"{{a}}"}"""))}}],
              "updatedAt": "{{i.UpdatedAt}}",
              "closedAt": {{(i.State == "CLOSED" ? $"\"{i.UpdatedAt}\"" : "null")}},
              "url": "https://github.com/acme/widget/issues/{{i.Number}}",
              "body": {{System.Text.Json.JsonSerializer.Serialize(i.Body)}},
              "comments": [{{string.Join(",", Enumerable.Range(0, i.Comments).Select(_ => "{}"))}}]
            }
            """)) + "]";
}
