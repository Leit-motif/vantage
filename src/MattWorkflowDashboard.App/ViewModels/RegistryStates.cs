using MattWorkflowDashboard.Infrastructure.Settings;

namespace MattWorkflowDashboard.App.ViewModels;

/// <summary>The registry states a project can be in, for binding to a chooser.</summary>
public static class RegistryStates
{
    public static IReadOnlyList<ProjectRegistryState> All { get; } = Enum.GetValues<ProjectRegistryState>();
}
