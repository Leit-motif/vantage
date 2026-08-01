using System.Diagnostics;
using System.Text;

namespace Vantage.Infrastructure.Processes;

public sealed record ProcessResult(
    int ExitCode,
    string StandardOutput,
    string StandardError,
    bool TimedOut,
    bool NotFound)
{
    public bool Succeeded => ExitCode == 0 && !TimedOut && !NotFound;
}

/// <summary>
/// The external-process boundary. Tests substitute this to make success, timeout, malformed
/// output, partial data, and cancellation deterministic.
/// </summary>
public interface IProcessRunner
{
    Task<ProcessResult> RunAsync(
        string fileName,
        IReadOnlyList<string> arguments,
        string? workingDirectory,
        CancellationToken cancellationToken);
}

/// <summary>
/// Runs Git as a bounded, cancellable child process. Arguments are passed as a structured list and
/// never through a shell, so no monitored content can be interpolated into a command line.
/// Concurrency is capped globally so a large refresh cannot swamp the machine.
/// </summary>
public sealed class BoundedProcessRunner : IProcessRunner, IDisposable
{
    private readonly SemaphoreSlim _slots;
    private readonly TimeSpan _timeout;
    private readonly IReadOnlyDictionary<string, string?> _environment;
    private int _running;
    private int _peak;

    /// <param name="environment">
    /// Extra environment for every child, applied after the runner's own settings. A null value
    /// removes the variable, which is how a tool can be asked to run without a credential it would
    /// otherwise inherit.
    /// </param>
    public BoundedProcessRunner(
        int maxConcurrent,
        TimeSpan timeout,
        IReadOnlyDictionary<string, string?>? environment = null)
    {
        _slots = new SemaphoreSlim(Math.Max(1, maxConcurrent));
        _timeout = timeout;
        _environment = environment ?? new Dictionary<string, string?>();
    }

    /// <summary>
    /// The most child processes this runner ever had alive at once. Local instrumentation, never
    /// reported anywhere: it is how the global cap is shown to be a cap rather than an intention.
    /// Counted around the child itself, because a caller waiting on the semaphore is not a process.
    /// </summary>
    public int PeakConcurrentProcesses => Volatile.Read(ref _peak);

    /// <summary>Child processes alive right now, as distinct from calls that have been submitted.</summary>
    public int ActiveProcesses => Volatile.Read(ref _running);

    public async Task<ProcessResult> RunAsync(
        string fileName,
        IReadOnlyList<string> arguments,
        string? workingDirectory,
        CancellationToken cancellationToken)
    {
        await _slots.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var running = Interlocked.Increment(ref _running);
            for (var peak = Volatile.Read(ref _peak); running > peak; peak = Volatile.Read(ref _peak))
            {
                Interlocked.CompareExchange(ref _peak, running, peak);
            }

            try
            {
                return await RunCoreAsync(fileName, arguments, workingDirectory, cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                Interlocked.Decrement(ref _running);
            }
        }
        finally
        {
            _slots.Release();
        }
    }

    private async Task<ProcessResult> RunCoreAsync(
        string fileName,
        IReadOnlyList<string> arguments,
        string? workingDirectory,
        CancellationToken cancellationToken)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = fileName,
            WorkingDirectory = workingDirectory ?? string.Empty,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };

        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        // Keep child tools non-interactive: a prompt would otherwise hang the refresh.
        startInfo.Environment["GIT_TERMINAL_PROMPT"] = "0";
        startInfo.Environment["GIT_OPTIONAL_LOCKS"] = "0";

        // Monitored content is data and is never executed — and a repository's own configuration is
        // monitored content. `core.fsmonitor` names a program that `git status` runs, and
        // `diff.external` names one that log and diff run, both from the repository being observed.
        // Passed as configuration environment rather than `-c` so no monitored value ever reaches a
        // command line. These take precedence over the repository's own config files.
        startInfo.Environment["GIT_CONFIG_COUNT"] = "2";
        startInfo.Environment["GIT_CONFIG_KEY_0"] = "core.fsmonitor";
        startInfo.Environment["GIT_CONFIG_VALUE_0"] = "false";
        startInfo.Environment["GIT_CONFIG_KEY_1"] = "diff.external";
        startInfo.Environment["GIT_CONFIG_VALUE_1"] = string.Empty;
        startInfo.Environment["NO_COLOR"] = "1";

        foreach (var (name, value) in _environment)
        {
            if (value is null)
            {
                startInfo.Environment.Remove(name);
            }
            else
            {
                startInfo.Environment[name] = value;
            }
        }

        using var process = new Process { StartInfo = startInfo };

        var stdout = new StringBuilder();
        var stderr = new StringBuilder();
        var outputDone = new TaskCompletionSource();
        var errorDone = new TaskCompletionSource();

        process.OutputDataReceived += (_, e) =>
        {
            if (e.Data is null)
            {
                outputDone.TrySetResult();
            }
            else
            {
                stdout.AppendLine(e.Data);
            }
        };

        process.ErrorDataReceived += (_, e) =>
        {
            if (e.Data is null)
            {
                errorDone.TrySetResult();
            }
            else
            {
                stderr.AppendLine(e.Data);
            }
        };

        try
        {
            if (!process.Start())
            {
                return new ProcessResult(-1, string.Empty, $"Could not start '{fileName}'.", false, NotFound: true);
            }
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            return new ProcessResult(-1, string.Empty, ex.Message, false, NotFound: true);
        }

        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        process.StandardInput.Close();

        using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutSource.CancelAfter(_timeout);

        try
        {
            await process.WaitForExitAsync(timeoutSource.Token).ConfigureAwait(false);
            await Task.WhenAll(outputDone.Task, errorDone.Task).WaitAsync(TimeSpan.FromSeconds(2), CancellationToken.None)
                .ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is OperationCanceledException or TimeoutException)
        {
            TryKill(process);

            if (cancellationToken.IsCancellationRequested)
            {
                throw new OperationCanceledException(cancellationToken);
            }

            return new ProcessResult(-1, stdout.ToString(), stderr.ToString(), TimedOut: true, NotFound: false);
        }

        return new ProcessResult(process.ExitCode, stdout.ToString(), stderr.ToString(), false, false);
    }

    private static void TryKill(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or NotSupportedException or SystemException)
        {
            // The process is already gone; there is nothing left to bound.
        }
    }

    public void Dispose() => _slots.Dispose();
}
