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
/// <para>
/// The thread lives on a desktop of the suite's own — see <see cref="IsolatedDesktop"/>. Every
/// window the tests create belongs to it, so the shell-seam tests that do need a real HWND get one
/// somewhere the owner is not: off their screen, and out of reach of their keyboard.
/// </para>
/// </summary>
public static class WpfTestHost
{
    private static readonly Lock Gate = new();
    private static Dispatcher? _dispatcher;

    /// <summary>The shell's dispatcher, for tests that need to queue work at a chosen priority.</summary>
    public static Dispatcher Ui() => Dispatcher_();

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
    /// Whether an element is somewhere the owner can actually see it. Two things can hide one
    /// without removing it from the visual tree, and both have to be ruled out. A collapsed
    /// element keeps its bindings and its place in the tree, and an element scrolled past the
    /// bottom of a viewport keeps a perfectly good arranged size — so a search that asked only
    /// about size would find evidence and controls that are not on screen at all.
    /// <para>
    /// <c>UIElement.IsVisible</c> cannot answer this here: in a tree with no presentation source
    /// it is false for everything. So this walks the ancestors instead, requiring each to be
    /// visible and the element's own bounds to still intersect it — which is exactly what a
    /// scrolled-away element fails against the viewport that clips it.
    /// </para>
    /// </summary>
    public static bool IsRendered(FrameworkElement element)
    {
        if (element.Visibility != Visibility.Visible || element.ActualWidth <= 0 || element.ActualHeight <= 0)
        {
            return false;
        }

        var bounds = new Rect(element.RenderSize);

        for (var node = VisualTreeHelper.GetParent(element); node is not null; node = VisualTreeHelper.GetParent(node))
        {
            if (node is not FrameworkElement ancestor)
            {
                continue;
            }

            if (ancestor.Visibility != Visibility.Visible)
            {
                return false;
            }

            if (ancestor.ActualWidth <= 0 || ancestor.ActualHeight <= 0)
            {
                return false;
            }

            var inAncestor = element.TransformToAncestor(ancestor).TransformBounds(bounds);
            if (!inAncestor.IntersectsWith(new Rect(0, 0, ancestor.ActualWidth, ancestor.ActualHeight)))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// Lets work the shell queued for after layout — bringing a region into view, for one — run
    /// before the tree is inspected.
    /// </summary>
    public static void Drain() => Ui().Invoke(() => { }, DispatcherPriority.Loaded);

    /// <summary>Everything the laid-out tree actually puts on screen as text.</summary>
    public static string RenderedText(DependencyObject root) =>
        string.Join(
            "\n",
            Descendants<TextBlock>(root)
                .Where(t => IsRendered(t) && !string.IsNullOrEmpty(t.Text))
                .Select(t => t.Text));

    private static Dispatcher Dispatcher_()
    {
        lock (Gate)
        {
            if (_dispatcher is not null)
            {
                return _dispatcher;
            }

            using var ready = new ManualResetEventSlim();
            Exception? failure = null;

            // Every window the tests make belongs to this thread, so where the thread lives is
            // where they appear — which is why it is started on a desktop of the suite's own
            // rather than on the owner's.
            IsolatedDesktop.StartStaThreadOnIt(
                "wpf-test-host",
                () =>
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
                },
                error =>
                {
                    failure = error;
                    ready.Set();
                });

            ready.Wait(TimeSpan.FromSeconds(30));

            if (failure is not null)
            {
                throw new InvalidOperationException("The WPF test host could not be isolated.", failure);
            }

            return _dispatcher ?? throw new InvalidOperationException("The WPF test host did not start.");
        }
    }
}
