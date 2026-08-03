namespace Vantage.App.Shell;

/// <summary>
/// Which single-instance session a launch decides about, read from the command line.
/// <para>
/// An ordinary launch always decides about the dashboard's own, whatever it is asked: the session
/// is what stops a second overlay and a second indexer competing for one cache, and an argument
/// that could move it would be an argument for running two.
/// </para>
/// <para>
/// The probe is the exception, and exists for the suite. Tests have to ask the shipped executable
/// what it decided, and they cannot ask about the dashboard's session without competing with the
/// dashboard the owner is running — a loss that says nothing about the guard and reads exactly
/// like the regression those tests exist to catch.
/// </para>
/// </summary>
public static class LaunchSession
{
    /// <summary>
    /// Asks the application to decide whether another dashboard already owns this session, report
    /// it as an exit code, and stop without building any UI.
    /// </summary>
    public const string ProbeArgument = "--single-instance-probe";

    /// <summary>The session the probe decides about. Honoured only alongside the probe.</summary>
    public const string NameArgument = "--instance-name";

    public static bool IsProbe(IReadOnlyList<string> arguments) => Contains(arguments, ProbeArgument);

    public static string For(IReadOnlyList<string> arguments) =>
        IsProbe(arguments) && Named(arguments) is { Length: > 0 } named
            ? named
            : SingleInstanceGuard.DashboardSession;

    private static bool Contains(IReadOnlyList<string> arguments, string name)
    {
        for (var i = 0; i < arguments.Count; i++)
        {
            if (arguments[i].Equals(name, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private static string? Named(IReadOnlyList<string> arguments)
    {
        for (var i = 0; i + 1 < arguments.Count; i++)
        {
            if (arguments[i].Equals(NameArgument, StringComparison.OrdinalIgnoreCase))
            {
                return arguments[i + 1];
            }
        }

        return null;
    }
}
