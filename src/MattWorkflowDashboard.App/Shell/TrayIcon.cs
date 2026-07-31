using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace MattWorkflowDashboard.App.Shell;

/// <summary>
/// The built-in notification-area icon. Every lifecycle control lives here, so the dashboard
/// stays recoverable even when the overlay itself is ignoring the mouse.
/// </summary>
public sealed class TrayIcon : IDisposable
{
    private readonly NotifyIcon _icon;
    private readonly ToolStripMenuItem _showHide;
    private readonly ToolStripMenuItem _compactExpand;
    private readonly ToolStripMenuItem _clickThrough;
    private readonly ToolStripMenuItem _launchAtSignIn;
    private Icon? _generated;

    public TrayIcon()
    {
        _showHide = new ToolStripMenuItem("Show", null, (_, _) => ShowHideRequested?.Invoke());
        _compactExpand = new ToolStripMenuItem("Expand", null, (_, _) => CompactExpandRequested?.Invoke());
        _clickThrough = new ToolStripMenuItem("Click-through", null, (_, _) => ClickThroughToggled?.Invoke())
        {
            CheckOnClick = false,
        };
        _launchAtSignIn = new ToolStripMenuItem("Launch at sign-in", null, (_, _) => LaunchAtSignInToggled?.Invoke())
        {
            CheckOnClick = false,
        };

        Menu = new ContextMenuStrip();
        Menu.Items.AddRange(
        [
            _showHide,
            _compactExpand,
            new ToolStripSeparator(),
            _clickThrough,
            new ToolStripMenuItem("Refresh now", null, (_, _) => RefreshRequested?.Invoke()),
            new ToolStripSeparator(),
            new ToolStripMenuItem("Settings…", null, (_, _) => SettingsRequested?.Invoke()),
            new ToolStripMenuItem("Open logs folder", null, (_, _) => LogsRequested?.Invoke()),
            _launchAtSignIn,
            new ToolStripSeparator(),
            new ToolStripMenuItem("Exit", null, (_, _) => ExitRequested?.Invoke()),
        ]);

        _icon = new NotifyIcon
        {
            Icon = CreateIcon(),
            Text = "Workflow dashboard",
            Visible = true,
            ContextMenuStrip = Menu,
        };

        _icon.DoubleClick += (_, _) => ShowHideRequested?.Invoke();
    }

    /// <summary>
    /// The commands as they actually hang in the notification area. Exposed so the shell's
    /// lifecycle controls can be exercised the way the owner reaches them.
    /// </summary>
    public ContextMenuStrip Menu { get; }

    public event Action? ShowHideRequested;

    public event Action? CompactExpandRequested;

    public event Action? ClickThroughToggled;

    public event Action? RefreshRequested;

    public event Action? SettingsRequested;

    public event Action? LogsRequested;

    public event Action? LaunchAtSignInToggled;

    public event Action? ExitRequested;

    public void UpdateState(bool visible, bool expanded, bool clickThrough, bool launchAtSignIn)
    {
        _showHide.Text = visible ? "Hide" : "Show";
        _compactExpand.Text = expanded ? "Compact" : "Expand";
        _clickThrough.Checked = clickThrough;
        _launchAtSignIn.Checked = launchAtSignIn;
    }

    public void UpdateTooltip(string text) =>
        // The shell truncates beyond 63 characters, so say the most useful thing first.
        _icon.Text = text.Length <= 63 ? text : text[..63];

    /// <summary>
    /// Draws the icon rather than shipping a binary asset, keeping the repository free of
    /// generated artwork while still giving the tray something legible at small sizes.
    /// </summary>
    private Icon CreateIcon()
    {
        using var bitmap = new Bitmap(32, 32);
        using (var graphics = Graphics.FromImage(bitmap))
        {
            graphics.SmoothingMode = SmoothingMode.AntiAlias;
            graphics.Clear(Color.Transparent);

            using var surface = new SolidBrush(Color.FromArgb(235, 32, 32, 35));
            using var accent = new SolidBrush(Color.FromArgb(255, 49, 195, 176));
            using var muted = new SolidBrush(Color.FromArgb(255, 140, 140, 149));

            graphics.FillRectangle(surface, 1, 1, 30, 30);
            graphics.FillRectangle(accent, 6, 7, 20, 4);
            graphics.FillRectangle(muted, 6, 15, 13, 4);
            graphics.FillRectangle(muted, 6, 23, 17, 4);
        }

        _generated = Icon.FromHandle(bitmap.GetHicon());
        return _generated;
    }

    public void Dispose()
    {
        _icon.Visible = false;
        _icon.Dispose();
        _generated?.Dispose();
    }
}
