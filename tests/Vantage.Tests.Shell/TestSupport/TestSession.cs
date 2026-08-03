using Vantage.App.Shell;

namespace Vantage.Tests.TestSupport;

/// <summary>
/// Single-instance sessions the suite owns, in the same spirit as the scratch <c>Run</c> key the
/// startup tests write to instead of the real one.
/// <para>
/// The dashboard's session is a name in the logged-on session's namespace, so a test claiming it
/// is not isolated from the machine — it is competing with whatever the owner is running, and it
/// loses. That failure says nothing about the guard and is indistinguishable from the one thing
/// these tests exist to catch, so the suite claims names of its own instead.
/// </para>
/// <para>
/// Every claim the suite makes goes through <see cref="Claim"/>, which is what
/// <c>Nothing_in_this_suite_claims_the_session_the_dashboard_claims</c> checks: the rule holds
/// structurally rather than by anyone remembering it.
/// </para>
/// </summary>
internal static class TestSession
{
    /// <summary>Distinguishes concurrent claims within one run; the process id separates runs.</summary>
    private static int _claims;

    /// <summary>
    /// A session name nothing holds: unique per call, and unique across two copies of the suite
    /// running at once, so neither can decide the other's question for it.
    /// </summary>
    internal static string Unclaimed() =>
        $@"Local\Vantage.SingleInstanceTests.{Environment.ProcessId}.{Interlocked.Increment(ref _claims)}";

    /// <summary>
    /// Holds a session the way a running dashboard holds the product's — the suite's only
    /// construction of the guard.
    /// </summary>
    internal static SingleInstanceGuard Claim(string session) => new(session);
}
