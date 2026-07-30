namespace CaYaScreenBridge.Windows.System;

/// <summary>
/// Ensures one instance per user session, and lets a second launch bring the running instance's
/// window to the front instead of failing silently.
///
/// This matters more than usual here: two instances would install two low level mouse hooks and each
/// would correct the other's corrections, producing a cursor that vibrates on every screen boundary.
/// </summary>
public sealed class SingleInstance : IDisposable
{
    private const string MutexName = @"Local\CaYaScreenBridge.Instance";
    private const string ShowEventName = @"Local\CaYaScreenBridge.Show";

    private readonly Mutex _mutex;
    private readonly EventWaitHandle _showEvent;
    private readonly CancellationTokenSource _cancellation = new();

    private SingleInstance(Mutex mutex, EventWaitHandle showEvent, bool isFirst)
    {
        _mutex = mutex;
        _showEvent = showEvent;
        IsFirstInstance = isFirst;
    }

    public bool IsFirstInstance { get; }

    /// <summary>Raised on a worker thread when another launch asks for the window.</summary>
    public event Action? ShowRequested;

    public static SingleInstance Acquire()
    {
        var mutex = new Mutex(initiallyOwned: true, MutexName, out bool createdNew);
        var showEvent = new EventWaitHandle(false, EventResetMode.AutoReset, ShowEventName, out _);

        return new SingleInstance(mutex, showEvent, createdNew);
    }

    /// <summary>Starts listening for a "show the window" request from a second launch.</summary>
    public void ListenForShowRequests()
    {
        if (!IsFirstInstance)
        {
            return;
        }

        var thread = new Thread(WaitLoop)
        {
            Name = "CaYaScreenBridge.InstanceListener",
            IsBackground = true,
        };

        thread.Start();
    }

    /// <summary>Signals the already running instance to surface its window.</summary>
    public void RequestShow()
    {
        try
        {
            _showEvent.Set();
        }
        catch
        {
            // The other instance may have exited between the mutex check and here; nothing to do.
        }
    }

    private void WaitLoop()
    {
        WaitHandle[] handles = [_showEvent, _cancellation.Token.WaitHandle];

        while (!_cancellation.IsCancellationRequested)
        {
            int index;
            try
            {
                index = WaitHandle.WaitAny(handles);
            }
            catch (ObjectDisposedException)
            {
                return;
            }

            if (index != 0)
            {
                return;
            }

            try
            {
                ShowRequested?.Invoke();
            }
            catch
            {
                // A failing handler must not kill the listener thread.
            }
        }
    }

    public void Dispose()
    {
        _cancellation.Cancel();

        try
        {
            if (IsFirstInstance)
            {
                _mutex.ReleaseMutex();
            }
        }
        catch (ApplicationException)
        {
            // The mutex was not owned by this thread; releasing is best effort on shutdown.
        }

        _mutex.Dispose();
        _showEvent.Dispose();
        _cancellation.Dispose();
    }
}
