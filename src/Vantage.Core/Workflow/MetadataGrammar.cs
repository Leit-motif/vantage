using System.Text;
using Vantage.Core.Observed;

namespace Vantage.Core.Workflow;

/// <summary>
/// The metadata line grammar shared by workflow files and GitHub issue bodies. Plain and bold
/// syntax are both accepted, and keys are matched case-insensitively, so benign formatting
/// differences between workflow variants never hide work.
/// </summary>
public static class MetadataGrammar
{
    /// <summary>Keys the dashboard understands. Everything else is read but not interpreted.</summary>
    public static class Keys
    {
        public const string Status = "status";
        public const string Stage = "stage";
        public const string Type = "type";
        public const string BlockedBy = "blocked by";
        public const string Blocks = "blocks";
        public const string GitHub = "github";
        public const string Effort = "effort";
        public const string Labels = "labels";
        public const string Assignee = "assignee";
        public const string Title = "title";
    }

    /// <summary>Reads the heading title and every metadata field from a markdown document.</summary>
    public static (string? HeadingTitle, IReadOnlyList<ObservedMetadataField> Fields) Parse(string content)
    {
        var fields = new List<ObservedMetadataField>();
        string? heading = null;

        var lines = content.Split('\n');
        var inFrontMatter = false;
        var inFence = false;

        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i].TrimEnd('\r');
            var trimmed = line.Trim();

            if (i == 0 && trimmed == "---")
            {
                inFrontMatter = true;
                continue;
            }

            if (inFrontMatter)
            {
                if (trimmed == "---")
                {
                    inFrontMatter = false;
                    continue;
                }

                if (TryReadField(trimmed, i + 1, out var frontField))
                {
                    fields.Add(frontField);
                }

                continue;
            }

            if (trimmed.StartsWith("```", StringComparison.Ordinal))
            {
                inFence = !inFence;
                continue;
            }

            if (inFence)
            {
                continue;
            }

            if (heading is null && trimmed.StartsWith("# ", StringComparison.Ordinal))
            {
                heading = trimmed[2..].Trim();
                continue;
            }

            if (TryReadField(trimmed, i + 1, out var field))
            {
                fields.Add(field);
            }
        }

        return (heading, fields);
    }

    /// <summary>
    /// Accepts <c>Key: value</c>, <c>**Key:** value</c>, <c>**Key**: value</c>, and the same forms
    /// behind a list bullet.
    /// </summary>
    public static bool TryReadField(string line, int lineNumber, out ObservedMetadataField field)
    {
        field = null!;
        var text = line.Trim();

        if (text.StartsWith("- ", StringComparison.Ordinal) || text.StartsWith("* ", StringComparison.Ordinal))
        {
            text = text[2..].Trim();
        }

        var colon = IndexOfKeyColon(text);
        if (colon <= 0)
        {
            return false;
        }

        var key = text[..colon].Trim();
        var value = text[(colon + 1)..].Trim();

        key = StripEmphasis(key);
        if (key.Length == 0 || key.Length > 40 || key.Contains('#'))
        {
            return false;
        }

        // A metadata key is a short label, not a sentence fragment or a URL scheme.
        if (!key.All(c => char.IsLetterOrDigit(c) || c is ' ' or '-' or '_'))
        {
            return false;
        }

        field = new ObservedMetadataField(NormalizeKey(key), StripEmphasis(value), lineNumber);
        return true;
    }

    public static string NormalizeKey(string key) =>
        key.Replace('-', ' ').Replace('_', ' ').Trim().ToLowerInvariant();

    /// <summary>Splits a comma or semicolon separated value into its items.</summary>
    public static IReadOnlyList<string> SplitList(string? value) =>
        string.IsNullOrWhiteSpace(value)
            ? []
            : value.Split([',', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Where(v => v.Length > 0)
                .ToList();

    /// <summary>
    /// The colon that ends the key, ignoring one inside bold emphasis (<c>**Key:**</c>) and
    /// refusing anything that looks like a URL scheme.
    /// </summary>
    private static int IndexOfKeyColon(string text)
    {
        for (var i = 0; i < text.Length; i++)
        {
            if (text[i] != ':')
            {
                continue;
            }

            // "**Key:**" — the colon sits before the closing emphasis.
            if (i + 2 < text.Length && text[i + 1] == '*' && text[i + 2] == '*')
            {
                return i;
            }

            if (i + 2 < text.Length && text[i + 1] == '/' && text[i + 2] == '/')
            {
                return -1;
            }

            return i;
        }

        return -1;
    }

    private static string StripEmphasis(string value)
    {
        var builder = new StringBuilder(value.Length);
        var i = 0;
        while (i < value.Length)
        {
            if (value[i] == '*' || value[i] == '_' && (i == 0 || value[i - 1] == ' '))
            {
                i++;
                continue;
            }

            builder.Append(value[i]);
            i++;
        }

        return builder.ToString().Trim().Trim(':').Trim();
    }
}
