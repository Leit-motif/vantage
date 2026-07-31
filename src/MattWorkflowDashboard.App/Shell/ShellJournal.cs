using System.Globalization;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Media;
using MattWorkflowDashboard.App.ViewModels;
using MattWorkflowDashboard.Infrastructure.Settings;
using Microsoft.Win32;

// WinForms brings System.Drawing into scope for the tray icon; here the WPF types are meant.
using Color = System.Windows.Media.Color;

namespace MattWorkflowDashboard.App.Shell;

/// <summary>
/// Development-only instrumentation: an append-only record of what the running window actually is,
/// sampled whenever something the dashboard's appearance or geometry depends on moves.
/// <para>
/// It exists for the claims a screenshot cannot settle. "The running window restyled without a
/// restart" is two claims — that the palette changed, and that it is the same process it was
/// before — and the second is invisible in any frame. Every line therefore carries the process id
/// and its start time alongside the resolved palette, so a run that quietly restarted cannot read
/// as a run that restyled. The same line carries the window's position, its scale, and the monitor
/// it sits on, which is what makes a restore across a scale change checkable in physical pixels
/// rather than in whichever units each end happened to speak.
/// </para>
/// <para>
/// Nothing here is shipped telemetry. It writes only when <c>--shell-journal</c> names a file, and
/// it records the shell's own state — no workspace content, no project or repository names.
/// </para>
/// </summary>
public sealed partial class ShellJournal : IDisposable
{
    private const string PersonalizeKey = @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize";
    private const int GwlExStyle = -20;

    /// <summary>
    /// The palette entries sampled on every line. Enough to tell the three themes apart at a
    /// glance, and to show that the content layers moved with the surface rather than only it.
    /// </summary>
    private static readonly string[] SampledColors =
    [
        "SurfaceColor",
        "SurfaceRaisedColor",
        "TextPrimaryColor",
        "TextSecondaryColor",
        "StateReadyColor",
        "StateBlockedColor",
    ];

    private static readonly JsonSerializerOptions Compact = new() { WriteIndented = false };

    private readonly Application _application;
    private readonly Window _window;
    private readonly DashboardViewModel _viewModel;
    private readonly DashboardSettings _settings;
    private readonly string _path;
    private readonly Lock _gate = new();

    public ShellJournal(
        Application application,
        Window window,
        DashboardViewModel viewModel,
        DashboardSettings settings,
        string path)
    {
        _application = application;
        _window = window;
        _viewModel = viewModel;
        _settings = settings;
        _path = Path.GetFullPath(path);

        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
    }

    [LibraryImport("user32.dll", SetLastError = true)]
    private static partial int GetWindowLongW(nint hWnd, int nIndex);

    /// <summary>
    /// Writes one line for the window as it stands right now. Called on the dispatcher thread:
    /// everything sampled here belongs to the window, and reading it from anywhere else would be
    /// reading it from outside its own apartment.
    /// </summary>
    public void Record(string reason)
    {
        var process = System.Diagnostics.Process.GetCurrentProcess();
        var scale = VisualTreeHelper.GetDpi(_window).DpiScaleX;
        var handle = WindowInterop.HandleOf(_window);

        // Windows names a display by a physical-pixel point, so the window's own units are
        // converted before asking which monitor it is on — the same conversion the restore path
        // makes, checked here against the answer Windows gives.
        var physicalLeft = _window.Left * scale;
        var physicalTop = _window.Top * scale;

        var line = new Dictionary<string, object?>
        {
            ["utc"] = DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture),
            ["reason"] = reason,
            ["pid"] = process.Id,
            ["processStartUtc"] = process.StartTime.ToUniversalTime().ToString("o", CultureInfo.InvariantCulture),
            ["uptimeSeconds"] = Math.Round((DateTime.Now - process.StartTime).TotalSeconds, 1),

            ["themeSetting"] = _settings.Ui.Theme.ToString(),
            ["windowsAppsUseLightTheme"] = AppsUseLightTheme(),
            ["highContrast"] = SystemParameters.HighContrast,
            ["paletteSource"] = PaletteSource(),
            ["colors"] = SampledColors.ToDictionary(key => key, Resolved),
            ["systemWindowColor"] = Hex(System.Windows.SystemColors.WindowColor),

            ["surfaceOpacity"] = Math.Round(_viewModel.SurfaceOpacity, 4),
            ["surfaceOpacityPercent"] = _settings.Ui.SurfaceOpacityPercent,

            ["isVisible"] = _window.IsVisible,
            ["isExpanded"] = _viewModel.IsExpanded,
            ["topmost"] = _window.Topmost,
            ["exStyle"] = handle == 0 ? null : "0x" + GetWindowLongW(handle, GwlExStyle).ToString("X8", CultureInfo.InvariantCulture),

            ["left"] = Math.Round(_window.Left, 2),
            ["top"] = Math.Round(_window.Top, 2),
            ["width"] = Math.Round(_window.Width, 2),
            ["height"] = Math.Round(_window.Height, 2),
            ["dpiScale"] = scale,
            ["physicalLeft"] = Math.Round(physicalLeft, 1),
            ["physicalTop"] = Math.Round(physicalTop, 1),
            ["monitorDeviceName"] = System.Windows.Forms.Screen
                .FromPoint(new System.Drawing.Point((int)physicalLeft, (int)physicalTop)).DeviceName,

            // What the settings file will be read back as on the next launch, recorded beside the
            // live window so a restore can be checked against what was actually saved.
            ["savedGeometry"] = new Dictionary<string, object?>
            {
                ["left"] = _settings.Ui.Geometry.Left,
                ["top"] = _settings.Ui.Geometry.Top,
                ["dpiScale"] = _settings.Ui.Geometry.DpiScale,
                ["monitorDeviceName"] = _settings.Ui.Geometry.MonitorDeviceName,
            },
        };

        // Append and flush per line: the interesting runs are the ones where Windows is being
        // changed underneath the process, and a buffered last line is the one worth having.
        lock (_gate)
        {
            using var stream = new FileStream(_path, FileMode.Append, FileAccess.Write, FileShare.Read);
            using var writer = new StreamWriter(stream);
            writer.WriteLine(JsonSerializer.Serialize(line, Compact));
        }
    }

    /// <summary>The dictionary the theme manager put first, named rather than described.</summary>
    private string? PaletteSource()
    {
        var merged = _application.Resources.MergedDictionaries;
        return merged.Count > 0 ? merged[0].Source?.ToString() : null;
    }

    private string? Resolved(string key) =>
        _application.TryFindResource(key) is Color color ? Hex(color) : null;

    private static string Hex(Color color) =>
        $"#{color.A:X2}{color.R:X2}{color.G:X2}{color.B:X2}";

    private static int? AppsUseLightTheme()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(PersonalizeKey);
            return key?.GetValue("AppsUseLightTheme") as int?;
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    public void Dispose()
    {
    }
}
