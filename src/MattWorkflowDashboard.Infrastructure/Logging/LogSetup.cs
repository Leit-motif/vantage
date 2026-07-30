using Serilog;
using Serilog.Core;
using Serilog.Events;

namespace MattWorkflowDashboard.Infrastructure.Logging;

/// <summary>
/// Bounded rolling logs under local application data. Nothing is uploaded: there is no telemetry,
/// no analytics, and no crash reporting in this product.
/// </summary>
public static class LogSetup
{
    public static Logger Create(AppPaths paths, LogEventLevel minimumLevel = LogEventLevel.Information)
    {
        paths.EnsureCreated();

        return new LoggerConfiguration()
            .MinimumLevel.Is(minimumLevel)
            .WriteTo.File(
                Path.Combine(paths.LogDirectory, "dashboard-.log"),
                rollingInterval: RollingInterval.Day,
                retainedFileCountLimit: 7,
                fileSizeLimitBytes: 8 * 1024 * 1024,
                rollOnFileSizeLimit: true,
                shared: false,
                outputTemplate: "{Timestamp:yyyy-MM-dd HH:mm:ss.fff zzz} [{Level:u3}] {Message:lj}{NewLine}{Exception}")
            .CreateLogger();
    }
}
