using System.Windows;
using MattWorkflowDashboard.App.ViewModels;

namespace MattWorkflowDashboard.App.Views;

/// <summary>Configuration in its own window, so it never crowds the operational dashboard.</summary>
public partial class SettingsWindow : Window
{
    public SettingsWindow(SettingsViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
    }
}
