using System.Windows;
using Vantage.App.ViewModels;

namespace Vantage.App.Views;

/// <summary>Configuration in its own window, so it never crowds the operational dashboard.</summary>
public partial class SettingsWindow : Window
{
    public SettingsWindow(SettingsViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
    }
}
