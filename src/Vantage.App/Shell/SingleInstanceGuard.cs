namespace Vantage.App.Shell;

/// <summary>
/// One overlay, one indexer. The claim is a named kernel object in the session's namespace, so a
/// second launch discovers the first no matter how it was started.
/// </summary>
public sealed class SingleInstanceGuard : IDisposable
{
    public const string DefaultName = @"Local\Vantage.SingleInstance";

    private Mutex? _held;

    public SingleInstanceGuard(string? name = null)
    {
        var mutex = new Mutex(initiallyOwned: true, name ?? DefaultName, out var isOnlyInstance);
        IsOnlyInstance = isOnlyInstance;

        if (isOnlyInstance)
        {
            _held = mutex;
        }
        else
        {
            // Someone else owns the session; nothing is held, so nothing has to be released.
            mutex.Dispose();
        }
    }

    /// <summary>Whether this process is the one that may open the dashboard.</summary>
    public bool IsOnlyInstance { get; }

    public void Dispose()
    {
        if (_held is null)
        {
            return;
        }

        try
        {
            _held.ReleaseMutex();
        }
        catch (ApplicationException)
        {
            // Released from another thread, or never really held: closing the handle is enough.
        }

        _held.Dispose();
        _held = null;
    }
}
