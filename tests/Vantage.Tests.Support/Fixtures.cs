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
}
