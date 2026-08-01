using System.Reflection;
using System.Runtime.Versioning;

namespace Vantage.Tests.TestSupport;

/// <summary>
/// What is actually loaded in this process, as opposed to what a project file said it would
/// reference. The SDK stamps <see cref="TargetPlatformAttribute"/> onto any assembly built for a
/// platform-specific framework such as <c>net10.0-windows</c>, so this reads the criterion itself
/// rather than a list of assembly names somebody remembered to write down.
/// </summary>
public static class WindowsAssemblies
{
    /// <summary>Every currently loaded assembly that declares Windows as its target platform.</summary>
    public static IReadOnlyList<string> Loaded() =>
        [.. AppDomain.CurrentDomain.GetAssemblies()
            .Select(assembly => (Name: assembly.GetName().Name ?? "<unnamed>", Platform: PlatformOf(assembly)))
            .Where(a => a.Platform is not null
                && a.Platform.StartsWith("windows", StringComparison.OrdinalIgnoreCase))
            .Select(a => $"{a.Name} (TargetPlatform: {a.Platform})")
            .Order(StringComparer.Ordinal)];

    private static string? PlatformOf(Assembly assembly)
    {
        try
        {
            return assembly.GetCustomAttribute<TargetPlatformAttribute>()?.PlatformName;
        }
        catch (Exception ex) when (ex is FileNotFoundException or TypeLoadException or BadImageFormatException)
        {
            // A dynamic or unreadable assembly cannot be carrying a Windows target.
            return null;
        }
    }
}
