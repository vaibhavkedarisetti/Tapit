namespace Tapit.Core.Audio;

/// <summary>Lifecycle of a capture stream.</summary>
public enum CaptureState
{
    Stopped,
    Starting,
    Running,

    /// <summary>The device went away and the source is retrying with backoff.</summary>
    Reconnecting,

    /// <summary>Capture failed in a way retrying will not fix; user action is required.</summary>
    Faulted,
}

public sealed class CaptureStateChangedEventArgs(CaptureState state, string? message = null, Exception? error = null)
    : EventArgs
{
    public CaptureState State { get; } = state;

    public string? Message { get; } = message;

    public Exception? Error { get; } = error;
}

/// <summary>
/// Snapshot of capture health. Fields are written by the capture thread and copied out by
/// diagnostics; a torn read costs one stale number, never a stall on the realtime path.
/// </summary>
public readonly record struct CaptureStatistics
{
    public long TotalFrames { get; init; }

    public long PacketCount { get; init; }

    /// <summary>Packets flagged <c>DATA_DISCONTINUITY</c> by the audio engine.</summary>
    public long DiscontinuityCount { get; init; }

    /// <summary>Packets the engine marked silent (no data was actually transferred).</summary>
    public long SilentPacketCount { get; init; }

    /// <summary>Frames of silence inserted to keep the frame clock aligned across a gap.</summary>
    public long GapFramesInserted { get; init; }

    /// <summary>Times the producer lapped the ring, or a consumer read was refused.</summary>
    public long OverrunCount { get; init; }

    public int MaxPacketFrames { get; init; }

    public double DevicePeriodMs { get; init; }

    public double EngineBufferMs { get; init; }

    public double StreamLatencyMs { get; init; }

    /// <summary>Worst observed time spent inside one capture-thread service pass.</summary>
    public double MaxServicePassMs { get; init; }

    public double LastServicePassMs { get; init; }

    /// <summary>
    /// True when the OS granted a raw stream, bypassing the APO effect chain (AGC, noise
    /// suppression, beamforming). False means the signal has been reshaped for speech and
    /// classification accuracy will suffer.
    /// </summary>
    public bool RawModeActive { get; init; }

    public bool MmcssActive { get; init; }
}

/// <summary>
/// A source of continuous audio feeding the Tapit pipeline.
/// </summary>
/// <remarks>
/// <para>
/// The interface is intentionally pull-based. A source never invokes consumer code on the
/// realtime thread; it writes into <see cref="Buffer"/> and signals. Consumers block in
/// <see cref="WaitForData"/> and then read whatever range they want out of the ring. That
/// keeps the "audio callback never blocks and never executes actions" rule structural
/// rather than a comment someone can violate later.
/// </para>
/// <para>
/// <see cref="StateChanged"/> is the one event, and it fires only on lifecycle transitions
/// (start, device loss, reconnect) - never per buffer.
/// </para>
/// </remarks>
public interface IAudioCaptureSource : IDisposable
{
    /// <summary>Negotiated stream format, available once the source reaches <see cref="CaptureState.Running"/>.</summary>
    AudioFormat? Format { get; }

    /// <summary>Ring of captured audio, allocated at start.</summary>
    AudioRingBuffer? Buffer { get; }

    /// <summary>Maps ring frame indices to wall-clock time for latency accounting.</summary>
    SampleClock? Clock { get; }

    CaptureState State { get; }

    /// <summary>
    /// Incremented every time a new stream is opened, including after a reconnect.
    /// </summary>
    /// <remarks>
    /// A reconnect can hand back a different <see cref="Buffer"/>, a different
    /// <see cref="Format"/>, and a frame clock that restarts at zero. Consumers latch this
    /// value and discard any in-flight detection state when it changes, rather than
    /// silently analysing a window that straddles two different streams.
    /// </remarks>
    int StreamGeneration { get; }

    string? DeviceId { get; }

    string? DeviceName { get; }

    event EventHandler<CaptureStateChangedEventArgs>? StateChanged;

    /// <summary>Starts capture. Returns once the stream is running or has faulted.</summary>
    void Start();

    /// <summary>
    /// Stops capture and releases the device, so the OS microphone-in-use indicator goes out.
    /// </summary>
    void Stop();

    /// <summary>
    /// Blocks until new frames are available or the timeout expires.
    /// </summary>
    /// <returns><see langword="true"/> if data arrived; <see langword="false"/> on timeout.</returns>
    bool WaitForData(int millisecondsTimeout);

    CaptureStatistics GetStatistics();
}
