using Vantage.Tests.TestSupport;

namespace Vantage.Tests;

/// <summary>
/// The engine suite's whole point, asserted against the running process rather than the project
/// file. Targeting <c>net10.0</c> stops this assembly <em>referencing</em> a Windows one, and the
/// CI job that runs it on Linux and macOS shows it works without Windows — but neither reads the
/// set of assemblies actually loaded, which is what the acceptance for this split asks about.
///
/// Deliberately an assembly fixture and not a test: it has to see the process after every test has
/// run, and adding a test would change a count this suite is held to.
/// </summary>
[TestClass]
public sealed class PortabilityGuard
{
    [AssemblyCleanup]
    public static void NoAssemblyTargetingWindowsWasLoaded()
    {
        var windows = WindowsAssemblies.Loaded();

        Assert.AreEqual(
            0,
            windows.Count,
            "The engine suite ran something that targets Windows, so it is no longer evidence that "
            + $"the engine is portable. Loaded: {string.Join(", ", windows)}.");
    }
}
