using System.Globalization;
using System.Windows;
using System.Windows.Data;
using Vantage.App.ViewModels;

namespace Vantage.App.Converters;

/// <summary>Turns a count into a proportional star width, which is how progress is drawn.</summary>
public sealed class StarLengthConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var amount = value is int count ? Math.Max(count, 0) : 0;

        // A zero-width star column collapses; a hair of width keeps the geometry stable.
        return new GridLength(amount == 0 ? 0.0001 : amount, GridUnitType.Star);
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

public sealed class CountToVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is int count && count > 0 ? Visibility.Visible : Visibility.Collapsed;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

public sealed class BoolToVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var flag = value is true;
        if (parameter is string s && s.Equals("invert", StringComparison.OrdinalIgnoreCase))
        {
            flag = !flag;
        }

        return flag ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>Shows a placeholder only while the field it labels is empty.</summary>
public sealed class EmptyTextToVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        string.IsNullOrEmpty(value as string) ? Visibility.Visible : Visibility.Collapsed;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>Binds a filter chip's checked state to the single active filter.</summary>
public sealed class FilterEqualsConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is DashboardFilter filter
        && parameter is string name
        && Enum.TryParse<DashboardFilter>(name, ignoreCase: true, out var expected)
        && filter == expected;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        System.Windows.Data.Binding.DoNothing;
}

/// <summary>Percentage opacity as the WPF 0–1 value, named so the visible result is unambiguous.</summary>
public sealed class PercentToOpacityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is int percent ? Math.Clamp(percent, 10, 100) / 100d : 1d;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is double opacity ? (int)Math.Round(opacity * 100) : 100;
}
