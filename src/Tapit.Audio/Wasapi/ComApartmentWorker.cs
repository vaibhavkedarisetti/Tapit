using System.Collections.Concurrent;
using System.Runtime.ExceptionServices;

namespace Tapit.Audio.Wasapi;

/// <summary>
/// A dedicated MTA thread that owns a set of COM objects and executes every call against
/// them.
/// </summary>
/// <remarks>
/// The MMDevice API objects are apartment-affine. A WinUI window runs on an STA thread, a
/// console <c>Main</c> runs MTA, and the audio callback runs on its own thread; letting all
/// three poke the same RCW is how you get intermittent <c>RPC_E_WRONG_THREAD</c> in the
/// field and nowhere else. Pinning the objects to one apartment and marshalling calls to it
/// removes that class of bug outright.
/// </remarks>
internal sealed class ComApartmentWorker : IDisposable
{
    private readonly BlockingCollection<Action> _queue = new(new ConcurrentQueue<Action>());
    private readonly Thread _thread;
    private bool _disposed;

    public ComApartmentWorker(string name)
    {
        _thread = new Thread(Loop)
        {
            Name = name,
            IsBackground = true,
        };

        _thread.SetApartmentState(ApartmentState.MTA);
        _thread.Start();
    }

    private void Loop()
    {
        foreach (Action work in _queue.GetConsumingEnumerable())
        {
            try
            {
                work();
            }
            catch
            {
                // Individual work items capture their own exceptions; anything reaching here
                // came from a fire-and-forget Post and must not take the worker down.
            }
        }
    }

    public void Invoke(Action action) => Invoke(() =>
    {
        action();
        return true;
    });

    public T Invoke<T>(Func<T> action)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        // Already on the worker: run inline, otherwise a nested call would deadlock.
        if (Thread.CurrentThread == _thread)
        {
            return action();
        }

        T result = default!;
        ExceptionDispatchInfo? failure = null;
        using var done = new ManualResetEventSlim(false);

        _queue.Add(() =>
        {
            try
            {
                result = action();
            }
            catch (Exception ex)
            {
                failure = ExceptionDispatchInfo.Capture(ex);
            }
            finally
            {
                done.Set();
            }
        });

        done.Wait();
        failure?.Throw();
        return result;
    }

    /// <summary>Queues work without waiting. Used from OS notification callbacks.</summary>
    public void Post(Action action)
    {
        if (_disposed)
        {
            return;
        }

        try
        {
            _queue.Add(action);
        }
        catch (InvalidOperationException)
        {
            // Worker shut down between the check and the add.
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _queue.CompleteAdding();
        _thread.Join(2000);
        _queue.Dispose();
    }
}
