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
/// The rule holds in two halves, and needs both. Nothing outside this type constructs the guard at
/// all, which <c>Nothing_in_this_suite_claims_a_session_except_through_the_name_it_owns</c> reads
/// out of the compiled assembly; and this type will only claim a name it issued itself, so routing
/// the dashboard's session through <see cref="Claim"/> is refused rather than waved past. Either
/// half alone leaves a way to take the name the running dashboard holds.
/// </para>
/// </summary>
internal static class TestSession
{
    /// <summary>Distinguishes concurrent claims within one run; the process id separates runs.</summary>
    private static int _claims;

    /// <summary>The names this run has handed out, which are the only ones it will claim.</summary>
    private static readonly HashSet<string> Issued = new(StringComparer.Ordinal);

    /// <summary>
    /// A session name nothing holds: unique per call, and unique across two copies of the suite
    /// running at once, so neither can decide the other's question for it.
    /// </summary>
    internal static string Unclaimed()
    {
        var session =
            $@"Local\Vantage.SingleInstanceTests.{Environment.ProcessId}.{Interlocked.Increment(ref _claims)}";

        lock (Issued)
        {
            Issued.Add(session);
        }

        return session;
    }

    /// <summary>
    /// Holds a session the way a running dashboard holds the product's — the suite's only
    /// construction of the guard, and only ever of a name this type made up.
    /// </summary>
    internal static SingleInstanceGuard Claim(string session)
    {
        bool ours;
        lock (Issued)
        {
            ours = Issued.Contains(session);
        }

        return ours
            ? new SingleInstanceGuard(session)
            : throw new InvalidOperationException(
                $"This suite may only claim a session it issued itself, and '{session}' is not one. "
                + "A test that claims a name from anywhere else can take the session the owner's "
                + "running dashboard holds, which is the failure this is here to prevent.");
    }
}
