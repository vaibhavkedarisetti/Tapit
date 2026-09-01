using System.Diagnostics;

namespace Tapit.Core.Audio;

/// <summary>
/// Maps absolute frame indices onto wall-clock timestamps.
/// </summary>
/// <remarks>
/// <para>
/// Latency has to be measured from the <i>acoustic</i> event, not from the moment a managed
/// thread happened to wake up. WASAPI hands us a performance-counter position for the first
/// frame of every packet; the capture layer converts that to <see cref="Stopwatch"/> ticks
/// and anchors this clock. Everything downstream can then ask "how old is frame n?" and get
/// an answer that includes the driver and engine buffering, not just our own scheduling.
/// </para>
/// <para>
/// The anchor is written by the capture thread and read by the DSP thread. Because the two
/// fields are updated together but read separately, the reader takes a version stamp before
/// and after (a small seqlock) so it never combines an old index with a new timestamp.
/// </para>
/// </remarks>
public sealed class SampleClock
{
    private readonly double _ticksPerFrame;

    private long _version;
    private long _anchorFrame;
    private long _anchorTicks;
    private bool _anchored;

    public SampleClock(int sampleRate)
    {
        if (sampleRate <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(sampleRate), sampleRate, "Sample rate must be positive.");
        }

        SampleRate = sampleRate;
        _ticksPerFrame = (double)Stopwatch.Frequency / sampleRate;
    }

    public int SampleRate { get; }

    public bool IsAnchored => Volatile.Read(ref _anchored);

    /// <summary>
    /// Records that frame <paramref name="frameIndex"/> was captured at
    /// <paramref name="stopwatchTicks"/>. Called once per WASAPI packet.
    /// </summary>
    public void Anchor(long frameIndex, long stopwatchTicks)
    {
        Interlocked.Increment(ref _version);
        Volatile.Write(ref _anchorFrame, frameIndex);
        Volatile.Write(ref _anchorTicks, stopwatchTicks);
        Volatile.Write(ref _anchored, true);
        Interlocked.Increment(ref _version);
    }

    /// <summary>
    /// Converts an absolute frame index into a <see cref="Stopwatch"/> timestamp.
    /// Returns <see langword="false"/> until the clock has been anchored at least once.
    /// </summary>
    public bool TryGetTimestamp(long frameIndex, out long stopwatchTicks)
    {
        for (int attempt = 0; attempt < 4; attempt++)
        {
            long before = Volatile.Read(ref _version);
            if ((before & 1) != 0)
            {
                continue;
            }

            long anchorFrame = Volatile.Read(ref _anchorFrame);
            long anchorTicks = Volatile.Read(ref _anchorTicks);
            bool anchored = Volatile.Read(ref _anchored);

            if (Volatile.Read(ref _version) != before)
            {
                continue;
            }

            if (!anchored)
            {
                stopwatchTicks = 0;
                return false;
            }

            stopwatchTicks = anchorTicks + (long)((frameIndex - anchorFrame) * _ticksPerFrame);
            return true;
        }

        stopwatchTicks = 0;
        return false;
    }

    /// <summary>
    /// Milliseconds elapsed between the capture of <paramref name="frameIndex"/> and now.
    /// This is the number that belongs in a latency report.
    /// </summary>
    public double AgeMilliseconds(long frameIndex)
    {
        if (!TryGetTimestamp(frameIndex, out long ticks))
        {
            return double.NaN;
        }

        return (Stopwatch.GetTimestamp() - ticks) * 1000.0 / Stopwatch.Frequency;
    }

    public void Reset()
    {
        Interlocked.Increment(ref _version);
        Volatile.Write(ref _anchored, false);
        Volatile.Write(ref _anchorFrame, 0);
        Volatile.Write(ref _anchorTicks, 0);
        Interlocked.Increment(ref _version);
    }

    public static double TicksToMilliseconds(long ticks) => ticks * 1000.0 / Stopwatch.Frequency;

    /// <summary>
    /// Converts a WASAPI QPC position (100-nanosecond units) into <see cref="Stopwatch"/>
    /// ticks so the two clocks can be compared directly.
    /// </summary>
    public static long QpcHundredNanosecondsToStopwatchTicks(ulong qpc100Ns) =>
        (long)((qpc100Ns / 10_000_000.0) * Stopwatch.Frequency);
}
