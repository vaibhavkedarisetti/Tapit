using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.Versioning;
using Tapit.Core.Classification;

namespace Tapit.Actions;

/// <summary>One dispatched action and how it went.</summary>
public sealed record ActionOutcome(
    Zone Zone,
    string ActionId,
    string ActionName,
    bool Succeeded,
    string? Error,
    double LatencyMs,
    DateTimeOffset At);

/// <summary>
/// Runs actions off the realtime path.
/// </summary>
/// <remarks>
/// <para>
/// A bounded queue and a single worker thread. Bounded because an action that hangs - a
/// process that will not start, a clipboard held by another app - must not let work pile up
/// without limit; when the queue is full the <i>oldest</i> pending action is dropped and
/// counted, so the most recent tap is always the one that runs.
/// </para>
/// <para>
/// <see cref="Dispatch"/> never blocks and never throws, because it is called from the DSP
/// thread.
/// </para>
/// </remarks>
[SupportedOSPlatform("windows")]
public sealed class ActionDispatcher : IDisposable
{
    private readonly ActionRegistry _registry;
    private readonly IActionFeedback _feedback;
    private readonly BlockingCollection<QueuedAction> _queue;
    private readonly Thread _worker;
    private readonly int _capacity;

    private long _dropped;
    private long _executed;
    private long _failed;
    private bool _disposed;

    public ActionDispatcher(ActionRegistry registry, IActionFeedback? feedback = null, int capacity = 8)
    {
        _registry = registry ?? throw new ArgumentNullException(nameof(registry));
        _feedback = feedback ?? NullActionFeedback.Instance;
        _capacity = Math.Max(1, capacity);
        _queue = new BlockingCollection<QueuedAction>(new ConcurrentQueue<QueuedAction>(), _capacity);

        _worker = new Thread(Run)
        {
            Name = "Tapit Action Dispatcher",
            IsBackground = true,
        };

        _worker.Start();
    }

    public long ExecutedCount => Interlocked.Read(ref _executed);

    public long FailedCount => Interlocked.Read(ref _failed);

    public long DroppedCount => Interlocked.Read(ref _dropped);

    /// <summary>Most recent outcomes, newest last. Bounded so it cannot grow without limit.</summary>
    public IReadOnlyList<ActionOutcome> Recent
    {
        get
        {
            lock (_recent)
            {
                return [.. _recent];
            }
        }
    }

    private readonly List<ActionOutcome> _recent = [];

    public event EventHandler<ActionOutcome>? ActionCompleted;

    /// <summary>
    /// Queues an action for an accepted tap. Returns false if it could not be queued.
    /// </summary>
    /// <remarks>
    /// Only ever call this for an event that passed every gate. There is deliberately no
    /// overload that takes a rejected event.
    /// </remarks>
    public bool Dispatch(Zone zone, string actionId, string? argument, double confidence)
    {
        if (_disposed)
        {
            return false;
        }

        var queued = new QueuedAction(zone, actionId, argument, confidence, Stopwatch.GetTimestamp());

        if (_queue.TryAdd(queued))
        {
            return true;
        }

        // Full: drop the oldest so the newest tap still runs.
        if (_queue.TryTake(out _))
        {
            Interlocked.Increment(ref _dropped);
            return _queue.TryAdd(queued);
        }

        Interlocked.Increment(ref _dropped);
        return false;
    }

    private void Run()
    {
        foreach (QueuedAction queued in _queue.GetConsumingEnumerable())
        {
            IAction action = _registry.Resolve(queued.ActionId);
            var context = new ActionContext(
                queued.Zone, queued.Confidence, DateTimeOffset.Now, queued.Argument, _feedback);

            bool succeeded = true;
            string? error = null;

            try
            {
                action.ExecuteAsync(context).GetAwaiter().GetResult();
                Interlocked.Increment(ref _executed);
            }
            catch (Exception ex)
            {
                // A failing action must never take the dispatcher down: the next tap still
                // has to work.
                succeeded = false;
                error = ex.Message;
                Interlocked.Increment(ref _failed);
                _feedback.Notify($"{action.DisplayName} failed: {ex.Message}");
            }

            double latencyMs = (Stopwatch.GetTimestamp() - queued.QueuedAt) * 1000.0 / Stopwatch.Frequency;

            var outcome = new ActionOutcome(
                queued.Zone, action.Id, action.DisplayName, succeeded, error, latencyMs, DateTimeOffset.Now);

            lock (_recent)
            {
                _recent.Add(outcome);
                if (_recent.Count > 20)
                {
                    _recent.RemoveAt(0);
                }
            }

            ActionCompleted?.Invoke(this, outcome);
        }
    }

    /// <summary>
    /// Runs an action immediately on the calling thread. This is what a Test button uses:
    /// explicit, synchronous, and never routed through tap detection.
    /// </summary>
    public async Task<ActionOutcome> TestAsync(Zone zone, string actionId, string? argument)
    {
        IAction action = _registry.Resolve(actionId);
        long started = Stopwatch.GetTimestamp();

        try
        {
            await action.ExecuteAsync(
                new ActionContext(zone, 1.0, DateTimeOffset.Now, argument, _feedback)).ConfigureAwait(false);

            return new ActionOutcome(zone, action.Id, action.DisplayName, true, null,
                (Stopwatch.GetTimestamp() - started) * 1000.0 / Stopwatch.Frequency, DateTimeOffset.Now);
        }
        catch (Exception ex)
        {
            return new ActionOutcome(zone, action.Id, action.DisplayName, false, ex.Message,
                (Stopwatch.GetTimestamp() - started) * 1000.0 / Stopwatch.Frequency, DateTimeOffset.Now);
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
        _worker.Join(3000);
        _queue.Dispose();
    }

    private readonly record struct QueuedAction(
        Zone Zone, string ActionId, string? Argument, double Confidence, long QueuedAt);
}
