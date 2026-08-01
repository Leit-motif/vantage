using System.ComponentModel;
using System.Diagnostics;

namespace Vantage.Tests.TestSupport;

/// <summary>
/// A directory that is a second name for another directory, which is how discovery is asked
/// whether one place on disk reached by two routes is one project or two. Both suites need it:
/// the refresh tests build the trees, and the registry tests record intent against them.
/// </summary>
public static class DirectoryLink
{
    /// <summary>
    /// Creates the link, reporting whether it exists afterwards rather than throwing. A host may
    /// refuse — Windows junctions need no elevation but can still fail, and a Unix symbolic link
    /// can be refused by the filesystem — and callers report inconclusive when it does.
    /// <para>
    /// Deliberately a different construct on each platform, because the product resolves both
    /// through the same <c>DirectoryInfo.ResolveLinkTarget</c>: a junction is what Windows users
    /// actually have, and a directory symbolic link is what everyone else does.
    /// </para>
    /// </summary>
    public static bool TryCreate(string link, string target)
    {
        try
        {
            if (!OperatingSystem.IsWindows())
            {
                Directory.CreateSymbolicLink(link, target);
                return Directory.Exists(link);
            }

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
        catch (Exception ex) when (ex is Win32Exception
            or IOException
            or UnauthorizedAccessException
            or PlatformNotSupportedException)
        {
            // No link to test discovery with. Not a failure of the product.
            return false;
        }
    }
}
