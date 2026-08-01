using System.ComponentModel;
using System.Diagnostics;

namespace Vantage.Tests.TestSupport;

/// <summary>
/// A directory junction, which is how discovery is asked whether one place on disk reached by two
/// routes is one project or two. Both suites need it: the refresh tests build the trees, and the
/// registry tests record intent against them.
/// </summary>
public static class Junction
{
    /// <summary>
    /// Creates a junction, reporting whether it exists afterwards rather than throwing. A junction
    /// needs no elevation, but a host may still refuse it — and a host that is not Windows has no
    /// <c>cmd.exe</c> to ask, which is a refusal like any other. Callers report inconclusive.
    /// </summary>
    public static bool TryCreate(string link, string target)
    {
        try
        {
            using var process = Process.Start(new ProcessStartInfo("cmd.exe")
            {
                ArgumentList = { "/c", "mklink", "/J", link, target },
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            })!;

            process.WaitForExit();
            return process.ExitCode == 0 && Directory.Exists(link);
        }
        catch (Exception ex) when (ex is Win32Exception or PlatformNotSupportedException)
        {
            // No cmd.exe to run, so no junction to test with. Not a failure of the product.
            return false;
        }
    }
}
