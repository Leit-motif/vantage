using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;

// WinForms is referenced for the tray icon; here the WPF types are meant.
using Application = System.Windows.Application;
using Size = System.Windows.Size;

namespace MattWorkflowDashboard.Tests.TestSupport;

/// <summary>
/// One STA thread carrying a WPF <see cref="Application"/> loaded with the dictionaries the
/// product ships. A window's own content tree is laid out here without the window ever being
/// shown, so what the dashboard renders can be read straight from the visual tree — no desktop
/// session, no screenshot, and nothing that depends on an interactive login.
/// </summary>
public static class WpfTestHost
{
    private static readonly Lock Gate = new();
    private static Dispatcher? _dispatcher;

    public static void Run(Action work) => Ui().Invoke(work);

    public static T Run<T>(Func<T> work) => Ui().Invoke(work);

    /// <summary>
    /// Detaches a window's content into a standalone host and lays it out. Window layout needs a
    /// real HWND; the content tree beneath it does not, and it is the content that carries
    /// everything the owner reads.
    /// </summary>
    public static FrameworkElement HostContent(Window window, double width, double height)
    {
        var content = (FrameworkElement)window.Content;
        window.Content = null;
        content.DataContext = window.DataContext;

        var host = new Border { Child = content };
        LayOut(host, width, height);
        return host;
    }

    public static void LayOut(FrameworkElement element, double width, double height)
    {
        element.Width = width;
        element.Height = height;
        element.Measure(new Size(width, height));
        element.Arrange(new Rect(0, 0, width, height));
        element.UpdateLayout();
    }

    /// <summary>Every element of a kind in a laid-out visual tree, in tree order.</summary>
    public static IReadOnlyList<T> Descendants<T>(DependencyObject root)
        where T : DependencyObject
    {
        var found = new List<T>();
        Walk(root);
        return found;

        void Walk(DependencyObject node)
        {
            var count = VisualTreeHelper.GetChildrenCount(node);
            for (var i = 0; i < count; i++)
            {
                var child = VisualTreeHelper.GetChild(node, i);
                if (child is T match)
                {
                    found.Add(match);
                }

                Walk(child);
            }
        }
    }

    /// <summary>
    /// The part of the tree a screen reader would announce under a given name. Assertions about
    /// one region of the dashboard have to be made against that region: the same words often
    /// appear elsewhere on screen for entirely different reasons.
    /// </summary>
    public static FrameworkElement? Region(DependencyObject root, string automationName) =>
        Descendants<FrameworkElement>(root)
            .FirstOrDefault(e => string.Equals(
                AutomationProperties.GetName(e),
                automationName,
                StringComparison.Ordinal));

    /// <summary>
    /// Whether an element was actually given room by the layout pass. A collapsed element stays
    /// in the visual tree with nothing but its bindings, so a search that ignores this would
    /// happily find evidence and controls the owner can never see. <c>UIElement.IsVisible</c>
    /// cannot answer this here: it is false for everything in a tree with no presentation source.
    /// </summary>
    public static bool IsRendered(FrameworkElement element) =>
        element.Visibility == Visibility.Visible && element.ActualWidth > 0 && element.ActualHeight > 0;

    /// <summary>Everything the laid-out tree actually puts on screen as text.</summary>
    public static string RenderedText(DependencyObject root) =>
        string.Join(
            "\n",
            Descendants<TextBlock>(root)
                .Where(t => IsRendered(t) && !string.IsNullOrEmpty(t.Text))
                .Select(t => t.Text));

    private static Dispatcher Ui()
    {
        lock (Gate)
        {
            if (_dispatcher is not null)
            {
                return _dispatcher;
            }

            using var ready = new ManualResetEventSlim();
            var thread = new Thread(() =>
            {
                var application = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };

                // The assembly is named explicitly: the product's own pack URIs resolve against
                // the entry assembly, which in a test run is the test host rather than the app.
                foreach (var name in new[] { "Dark", "Controls" })
                {
                    application.Resources.MergedDictionaries.Add(new ResourceDictionary
                    {
                        Source = new Uri(
                            $"pack://application:,,,/MattWorkflowDashboard;component/Themes/{name}.xaml",
                            UriKind.Absolute),
                    });
                }

                _dispatcher = Dispatcher.CurrentDispatcher;
                ready.Set();
                Dispatcher.Run();
            })
            {
                IsBackground = true,
                Name = "wpf-test-host",
            };

            thread.SetApartmentState(ApartmentState.STA);
            thread.Start();
            ready.Wait(TimeSpan.FromSeconds(30));

            return _dispatcher ?? throw new InvalidOperationException("The WPF test host did not start.");
        }
    }
}
