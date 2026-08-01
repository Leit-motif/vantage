using System.Text.Json;
using System.Text.Json.Serialization;
using Vantage.Core;

namespace Vantage.Infrastructure.Settings;

public sealed record SettingsLoadResult(DashboardSettings Settings, IReadOnlyList<Diagnostic> Diagnostics);

/// <summary>
/// Reads and writes the settings file. Writes go to a sibling temp file and are swapped into
/// place, so an interrupted save can never leave configuration half-written.
/// </summary>
public sealed class SettingsStore(AppPaths paths)
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
        Converters = { new JsonStringEnumConverter() },
    };

    private readonly Lock _gate = new();

    public string FilePath => paths.SettingsFile;

    public SettingsLoadResult Load()
    {
        var diagnostics = new List<Diagnostic>();

        if (!File.Exists(paths.SettingsFile))
        {
            return new SettingsLoadResult(new DashboardSettings(), diagnostics);
        }

        try
        {
            var json = File.ReadAllText(paths.SettingsFile);
            var settings = JsonSerializer.Deserialize<DashboardSettings>(json, SerializerOptions);
            if (settings is null)
            {
                throw new JsonException("Settings file deserialized to null.");
            }

            return new SettingsLoadResult(Migrate(settings, diagnostics), diagnostics);
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            // Configuration must never block startup: keep the unreadable file for inspection
            // and carry on with defaults, loudly.
            TryQuarantine(diagnostics, ex);
            return new SettingsLoadResult(new DashboardSettings(), diagnostics);
        }
    }

    public void Save(DashboardSettings settings)
    {
        paths.EnsureCreated();
        settings.SchemaVersion = DashboardSettings.CurrentSchemaVersion;

        lock (_gate)
        {
            // Read before serializing: a change made while this write is in flight belongs to the
            // next save, not to this one.
            var revision = settings.Revision;

            var temp = paths.SettingsFile + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(settings, SerializerOptions));

            if (File.Exists(paths.SettingsFile))
            {
                File.Replace(temp, paths.SettingsFile, null, ignoreMetadataErrors: true);
            }
            else
            {
                File.Move(temp, paths.SettingsFile);
            }

            // Only once the swap succeeded, and only for the revision actually written: a failed
            // write must leave the change outstanding, and so must one made during the write.
            settings.SavedRevision = revision;
        }
    }

    /// <summary>Forward-only settings migration. Unknown future versions are left untouched.</summary>
    private static DashboardSettings Migrate(DashboardSettings settings, ICollection<Diagnostic> diagnostics)
    {
        if (settings.SchemaVersion > DashboardSettings.CurrentSchemaVersion)
        {
            diagnostics.Add(Diagnostic.Warning(
                DiagnosticCode.SettingsRecovered,
                $"Settings were written by a newer version (schema {settings.SchemaVersion}); unknown values are preserved but ignored.",
                "settings.json"));
            return settings;
        }

        // Before the ribbon there were two views and one flag to choose between them. Keyed off the
        // flag rather than off the schema version: the flag is only ever written by a build that
        // predates modes, so its presence identifies the file more reliably than a version does.
        if (settings.Ui.StartCompact is { } startCompact)
        {
            settings.Ui.ViewMode = startCompact ? DashboardViewMode.Compact : DashboardViewMode.Expanded;
            settings.Ui.StartCompact = null;
        }

        // Schema 3 removed the remote source's keys, and there is deliberately no step for them.
        // Deserialization reads past a key this build no longer declares, so a file carrying
        // GitHubEnrichmentEnabled, MaxGitHubIssuesPerRepository, ConfirmedOrigin or PendingOrigin
        // is ordinary configuration rather than a file that failed to parse — it loads clean, and
        // the next save writes the object, which no longer has anywhere to put them.
        settings.SchemaVersion = DashboardSettings.CurrentSchemaVersion;
        return settings;
    }

    private void TryQuarantine(ICollection<Diagnostic> diagnostics, Exception cause)
    {
        var quarantine = paths.SettingsFile + ".invalid";
        try
        {
            File.Copy(paths.SettingsFile, quarantine, overwrite: true);
        }
        catch (IOException)
        {
            // Best effort only; recovering the session matters more than keeping the copy.
        }

        diagnostics.Add(Diagnostic.Warning(
            DiagnosticCode.SettingsRecovered,
            $"Settings could not be read ({cause.Message}); defaults are in use and the previous file was kept as settings.json.invalid.",
            paths.SettingsFile));
    }
}
