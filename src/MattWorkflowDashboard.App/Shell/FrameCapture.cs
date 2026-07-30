using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

// WinForms brings System.Drawing into scope for the tray icon; here the WPF types are meant.
using Brush = System.Windows.Media.Brush;

namespace MattWorkflowDashboard.App.Shell;

/// <summary>
/// Development-only instrumentation: renders the live window to a PNG so the built application's
/// own layout, hierarchy, and colours can be reviewed as runtime evidence rather than inferred
/// from a mockup. It renders the window's visual tree, so what sits behind the translucent
/// surface on the real desktop is not part of the frame. Nothing here is shipped telemetry.
/// </summary>
public static class FrameCapture
{
    public static void Save(Window window, string path, Brush? backdrop = null)
    {
        window.UpdateLayout();

        var dpi = VisualTreeHelper.GetDpi(window);
        var width = (int)Math.Ceiling(window.ActualWidth * dpi.DpiScaleX);
        var height = (int)Math.Ceiling(window.ActualHeight * dpi.DpiScaleY);
        if (width <= 0 || height <= 0)
        {
            return;
        }

        var target = new RenderTargetBitmap(width, height, dpi.PixelsPerInchX, dpi.PixelsPerInchY, PixelFormats.Pbgra32);

        if (backdrop is not null)
        {
            // A known backdrop makes the surface's translucency visible without capturing the
            // owner's actual screen content.
            var visual = new DrawingVisual();
            using (var context = visual.RenderOpen())
            {
                context.DrawRectangle(backdrop, null, new Rect(0, 0, window.ActualWidth, window.ActualHeight));
            }

            target.Render(visual);
        }

        target.Render(window);

        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(target));

        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        using var stream = File.Create(path);
        encoder.Save(stream);
    }
}
