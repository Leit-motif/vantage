using System.Diagnostics;
using System.Reflection;
using Vantage.App.Shell;
using Vantage.Tests.TestSupport;

namespace Vantage.Tests.ShellSeam;

/// <summary>
/// One overlay, one indexer. This is proven across real processes: the built application is
/// launched and asked what it decided about the instance already running, so the evidence is the
/// shipped executable's own startup decision rather than a re-implementation of it in a test.
/// <para>
/// The session it is asked about is never the one the dashboard claims. A test that took
/// <see cref="SingleInstanceGuard.DashboardSession"/> would be competing with the dashboard on the
/// machine it is running on, and would lose — a failure that says nothing about the guard and reads
/// exactly like the regression these tests exist to catch. The suite claims names of its own, and
/// the last two tests here are what keep that structural rather than remembered.
/// </para>
/// </summary>
[TestClass]
public sealed class SingleInstanceTests
{
    /// <summary>Exit codes the application reports when asked to decide and stop.</summary>
    private const int FirstInstance = 0;

    private const int AlreadyRunning = 2;

    [TestMethod]
    public void The_application_starts_normally_when_nothing_else_is_running()
    {
        Assert.AreEqual(
            FirstInstance,
            LaunchProbe(TestSession.Unclaimed()),
            "With no dashboard running, the application must take the session for itself.");
    }

    [TestMethod]
    public void A_second_application_stands_down_instead_of_opening_a_competing_overlay()
    {
        var session = TestSession.Unclaimed();
        using var running = TestSession.Claim(session);

        Assert.IsTrue(running.IsOnlyInstance, "The test holds the session first.");
        Assert.AreEqual(
            AlreadyRunning,
            LaunchProbe(session),
            "A second launch must stand down rather than open a second overlay and a second indexer.");
    }

    [TestMethod]
    public void The_session_is_released_when_the_dashboard_exits()
    {
        var session = TestSession.Unclaimed();

        using (var first = TestSession.Claim(session))
        {
            Assert.IsTrue(first.IsOnlyInstance);
            Assert.AreEqual(AlreadyRunning, LaunchProbe(session));
        }

        Assert.AreEqual(
            FirstInstance,
            LaunchProbe(session),
            "Once the dashboard exits, the next launch has to be allowed to start.");
    }

    // ---- The suite's own sessions, asserted about the suite ----------------------------------

    /// <summary>
    /// The product's session name is part of its behaviour — two dashboards started any way at all
    /// have to find each other — so it is asserted here. It is asserted as a constant, without
    /// anything claiming it.
    /// </summary>
    [TestMethod]
    public void The_session_the_dashboard_claims_is_the_one_named_for_the_dashboard()
    {
        Assert.AreEqual(
            @"Local\Vantage.SingleInstance",
            SingleInstanceGuard.DashboardSession,
            "A second launch finds the first by this name, so changing it silently allows two.");

        Assert.IsFalse(
            typeof(SingleInstanceGuard).GetConstructors().Single().GetParameters().Single().IsOptional,
            "The name has to stay required: a caller allowed to omit it claims the dashboard's "
            + "session by accident, which is the whole failure this ticket is about.");
    }

    /// <summary>
    /// The escape hatch the suite needs must not be one the product has. Asserted about the
    /// resolution itself, because the only other way to ask is to start a real dashboard.
    /// </summary>
    [TestMethod]
    public void Only_the_probe_may_decide_about_a_session_other_than_the_dashboard_s()
    {
        Assert.AreEqual(
            @"Local\Vantage.SomeoneElses",
            LaunchSession.For([LaunchSession.ProbeArgument, LaunchSession.NameArgument, @"Local\Vantage.SomeoneElses"]),
            "The probe has to be able to decide about a session the suite owns.");

        Assert.AreEqual(
            SingleInstanceGuard.DashboardSession,
            LaunchSession.For([LaunchSession.NameArgument, @"Local\Vantage.SomeoneElses"]),
            "An ordinary launch decides about the dashboard's own session whatever it is asked to, "
            + "or the argument is a way to run two dashboards over one cache.");

        Assert.AreEqual(
            SingleInstanceGuard.DashboardSession,
            LaunchSession.For([LaunchSession.ProbeArgument]),
            "A probe that names nothing asks about the dashboard's own session, as it always did.");

        Assert.AreEqual(SingleInstanceGuard.DashboardSession, LaunchSession.For([]));

        Assert.IsTrue(LaunchSession.IsProbe([LaunchSession.ProbeArgument]));
        Assert.IsFalse(LaunchSession.IsProbe([LaunchSession.NameArgument, "x"]));
    }

    [TestMethod]
    public void The_names_this_suite_claims_are_its_own_and_never_repeat()
    {
        var first = TestSession.Unclaimed();
        var second = TestSession.Unclaimed();

        Assert.AreNotEqual(
            SingleInstanceGuard.DashboardSession,
            first,
            "A name the dashboard also claims makes this suite unrunnable on a machine it is installed on.");
        Assert.AreNotEqual(
            first,
            second,
            "Two claims in one run must not decide each other's question.");
        StringAssert.Contains(
            first,
            Environment.ProcessId.ToString(),
            "Two copies of the suite must not collide either, so the run is part of the name.");
    }

    /// <summary>
    /// Read out of the compiled assembly rather than out of the source, so a test added later
    /// cannot quietly opt out of it: whatever it writes has to compile to a construction, and a
    /// construction anywhere but <see cref="TestSession"/> is what this finds.
    /// </summary>
    [TestMethod]
    public void Nothing_in_this_suite_claims_a_session_except_through_the_name_it_owns()
    {
        var elsewhere = MethodsConstructingTheGuardOutside(typeof(TestSession));

        Assert.AreEqual(
            0,
            elsewhere.Count,
            "Every session this suite claims has to come from TestSession, which never returns the "
            + "dashboard's own. Found: " + string.Join(", ", elsewhere));
    }

    /// <summary>
    /// Runs the built application with the flag that makes it decide, report, and exit without
    /// ever building a window — about the session it is given rather than about the owner's.
    /// </summary>
    private static int LaunchProbe(string session)
    {
        var executable = LocateApplication();

        using var process = Process.Start(new ProcessStartInfo(executable)
        {
            ArgumentList = { LaunchSession.ProbeArgument, LaunchSession.NameArgument, session },
            UseShellExecute = false,
            CreateNoWindow = true,
        }) ?? throw new InvalidOperationException($"Could not launch {executable}.");

        Assert.IsTrue(
            process.WaitForExit(TimeSpan.FromSeconds(30)),
            "The instance probe must decide and exit promptly, without building any UI.");

        return process.ExitCode;
    }

    private static string LocateApplication()
    {
        // The application is a project reference, so its executable sits beside the test assembly.
        var beside = Path.Combine(AppContext.BaseDirectory, "Vantage.exe");
        if (File.Exists(beside))
        {
            return beside;
        }

        throw new AssertInconclusiveException(
            $"The built application was not found at {beside}; the running-shell seam needs it.");
    }

    /// <summary>
    /// Every method in this assembly that constructs a <see cref="SingleInstanceGuard"/>, save
    /// those declared by the sanctioned type. A construction is a <c>newobj</c> naming the guard's
    /// constructor, so the search is for that instruction and a token that resolves to it; a byte
    /// that merely looks like one resolves to something else and is dropped.
    /// </summary>
    private static List<string> MethodsConstructingTheGuardOutside(Type sanctioned)
    {
        const byte Newobj = 0x73;

        var guard = typeof(SingleInstanceGuard).GetConstructors().Single();
        const BindingFlags Declared = BindingFlags.Public | BindingFlags.NonPublic
            | BindingFlags.Static | BindingFlags.Instance | BindingFlags.DeclaredOnly;

        return (from type in typeof(SingleInstanceTests).Assembly.GetTypes()
                where type != sanctioned
                from method in type.GetMethods(Declared).Cast<MethodBase>().Concat(type.GetConstructors(Declared))
                where Constructs(method, guard)
                select $"{type.Name}.{method.Name}").ToList();

        static bool Constructs(MethodBase method, ConstructorInfo guard)
        {
            if (method.GetMethodBody()?.GetILAsByteArray() is not { } il)
            {
                return false;
            }

            for (var i = 0; i + 4 < il.Length; i++)
            {
                if (il[i] == Newobj && Resolve(method.Module, BitConverter.ToInt32(il, i + 1)) == guard)
                {
                    return true;
                }
            }

            return false;
        }

        static MethodBase? Resolve(Module module, int token)
        {
            try
            {
                return module.ResolveMethod(token);
            }
            catch (ArgumentException)
            {
                // The bytes were an operand rather than an instruction; nothing was constructed.
                return null;
            }
        }
    }
}
