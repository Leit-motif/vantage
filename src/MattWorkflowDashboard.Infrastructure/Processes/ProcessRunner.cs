using System.Diagnostics;
using System.Text;

namespace MattWorkflowDashboard.Infrastructure.Processes;

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
/// The external-process boundary. Tests substitute this to make success, authentication loss,
/// timeout, malformed output, partial data, and cancellation deterministic.
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
/// Runs Git and <c>gh</c> as bounded, cancellable child processes. Arguments are passed as a
/// structured list and never through a shell, so no monitored content can be interpolated into
/// a command line. Concurrency is capped globally so a large refresh cannot swamp the machine.
/// </summary>
public sealed class BoundedProcessRunner : IProcessRunner, IDisposable
{
    private readonly SemaphoreSlim _slots;
    private readonly TimeSpan _timeout;

    public BoundedProcessRunner(int maxConcurrent, TimeSpan timeout)
    {
        _slots = new SemaphoreSlim(Math.Max(1, maxConcurrent));
        _timeout = timeout;
    }

    public async Task<ProcessResult> RunAsync(
        string fileName,
        IReadOnlyList<string> arguments,
        string? workingDirectory,
        CancellationToken cancellationToken)
    {
        await _slots.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await RunCoreAsync(fileName, arguments, workingDirectory, cancellationToken).ConfigureAwait(false);
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
        startInfo.Environment["GH_NO_UPDATE_NOTIFIER"] = "1";
        startInfo.Environment["GH_PROMPT_DISABLED"] = "1";
        startInfo.Environment["NO_COLOR"] = "1";

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
