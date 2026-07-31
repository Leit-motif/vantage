using Vantage.Core.Projection;

namespace Vantage.App.ViewModels;

/// <summary>
/// Fluent outline glyphs, written as code points so the source stays readable in any editor.
/// Every glyph is a second cue alongside a written label — meaning is never colour-only, and
/// never icon-only either.
/// </summary>
public static class Glyphs
{
    public static readonly string InProgress = Char(0xE895);   // Sync
    public static readonly string Ready = Char(0xE768);        // Play
    public static readonly string Blocked = Char(0xE7BA);      // Warning
    public static readonly string Complete = Char(0xE73E);     // CheckMark
    public static readonly string Idle = Char(0xE823);         // Clock

    public static readonly string Refresh = Char(0xE72C);
    public static readonly string Settings = Char(0xE713);
    public static readonly string Expand = Char(0xE740);
    public static readonly string Collapse = Char(0xE73F);
    public static readonly string Pin = Char(0xE718);
    public static readonly string Conflict = Char(0xE783);     // Error circle
    public static readonly string Diagnostic = Char(0xE946);   // Info
    public static readonly string Search = Char(0xE721);
    public static readonly string Stale = Char(0xE7BA);
    public static readonly string Close = Char(0xE711);

    public static string ForState(ProjectState state) => state switch
    {
        ProjectState.InProgress => InProgress,
        ProjectState.Ready => Ready,
        ProjectState.Blocked => Blocked,
        ProjectState.Complete => Complete,
        _ => Idle,
    };

    private static string Char(int codePoint) => char.ConvertFromUtf32(codePoint);
}
