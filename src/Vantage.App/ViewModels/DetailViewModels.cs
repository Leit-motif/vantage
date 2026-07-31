using System.Windows.Input;
using Vantage.Core.Projection;
using Vantage.Core.Workflow;

namespace Vantage.App.ViewModels;

/// <summary>
/// One disagreement between explicitly linked sources, with everything needed to judge it: which
/// item it is about, what each side says, which side was kept, and where each side was observed.
/// A conflict the owner cannot challenge is only a warning.
/// </summary>
public sealed class ConflictItemViewModel(ConflictReport report, string ticketTitle)
{
    public string TicketId => report.TicketId;

    public string TicketTitle => ticketTitle;

    /// <summary>The disputed field in the owner's words rather than the enum's.</summary>
    public string FieldLabel => report.Field switch
    {
        ConflictField.Title => "Title",
        ConflictField.State => "Open or closed",
        ConflictField.WorkflowStatus => "Workflow status",
        ConflictField.Blockers => "Blockers",
        ConflictField.Effort => "Effort",
        _ => report.Field.ToString(),
    };

    public string Headline => $"{FieldLabel} · {TicketTitle}";

    public string LocalValue => report.LocalValue;

    public string RemoteValue => report.RemoteValue;

    public string Resolution => report.Resolution;

    /// <summary>Where the local side was observed: source, locator, timestamp kind, and refresh.</summary>
    public string LocalProvenance => report.LocalProvenance.Description;

    public string RemoteProvenance => report.RemoteProvenance.Description;

    public string AccessibleSummary =>
        $"{FieldLabel} conflict on {TicketTitle}. Local: {LocalValue}. Remote: {RemoteValue}. " +
        $"{Resolution} Local evidence {LocalProvenance}. Remote evidence {RemoteProvenance}.";
}

/// <summary>
/// One ticket as the detail pane shows it, carrying the control that inspects its own
/// disagreement so a conflict can be opened where it arose.
/// </summary>
public sealed class TicketItemViewModel(TicketView ticket, ICommand inspectConflicts)
{
    public string Id => ticket.Id;

    public string Title => ticket.Title;

    public StatusReading Status => ticket.Status;

    public string ProvenanceDescription => ticket.Provenance.Description;

    public int ConflictCount => ticket.Conflicts.Count;

    public ICommand InspectConflictsCommand => inspectConflicts;

    public string ConflictControlName => $"Inspect {ConflictCount} conflict(s) on ticket {Title}";
}

public sealed class EffortItemViewModel(EffortView effort, IReadOnlyList<TicketItemViewModel> tickets)
{
    public string Id => effort.Id;

    public string Name => effort.Name;

    public IReadOnlyList<TicketItemViewModel> Tickets => tickets;
}
