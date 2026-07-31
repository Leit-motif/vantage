using System.Windows;
using System.Windows.Threading;

namespace Vantage.App.Shell;

/// <summary>
/// Scrolls an element into view when the view model says it has been asked for. Navigation that
/// only selects and expands is not navigation: in a detail pane taller than its viewport the
/// evidence can sit below the fold, and an aggregate warning has to lead the owner to it.
/// <para>
/// The trigger is a count rather than a flag, so asking twice works and nothing has to be reset
/// from the view. The scroll is deferred to after layout, because the region is usually being
/// rebuilt by the same change that asked for it.
/// </para>
/// </summary>
public static class BringIntoView
{
    public static readonly DependencyProperty OnRequestProperty = DependencyProperty.RegisterAttached(
        "OnRequest",
        typeof(int),
        typeof(BringIntoView),
        new PropertyMetadata(0, OnRequestChanged));

    public static void SetOnRequest(DependencyObject element, int value) =>
        element.SetValue(OnRequestProperty, value);

    public static int GetOnRequest(DependencyObject element) => (int)element.GetValue(OnRequestProperty);

    private static void OnRequestChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not FrameworkElement element || e.NewValue is not int requests || requests <= 0)
        {
            return;
        }

        element.Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () =>
        {
            element.UpdateLayout();
            element.BringIntoView();
        });
    }
}
