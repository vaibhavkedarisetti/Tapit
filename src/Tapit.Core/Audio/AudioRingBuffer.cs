using System.Numerics;
using System.Runtime.CompilerServices;

namespace Tapit.Core.Audio;

/// <summary>
/// Lock-free single-producer / single-consumer ring of planar float audio, addressed by a
/// monotonically increasing 64-bit absolute frame index.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why absolute indexing.</b> The onset detector fires a few frames <i>after</i> the
/// physical strike, so the analysis window has to start before the detection point. With a
/// wrapped read/write offset that is awkward and easy to get wrong; with an absolute frame
/// clock, "give me the 90 ms around event <i>n</i>" is a total function, overrun is exactly
/// detectable, and an offline WAV replay produces bit-identical indices to a live
/// microphone. That last property is what makes DSP work repeatable.
/// </para>
/// <para>
/// <b>Realtime contract.</b> The producer (WASAPI capture thread) only ever copies samples
/// and publishes indices with release stores. It never allocates, never locks and never
/// calls back into consumer code.
/// </para>
/// <para>
/// <b>Two indices, not one.</b> A single published write index is not sufficient, and the
/// bug it hides is subtle: the producer copies a packet's samples into their slots
/// <i>before</i> it publishes the new index. During that window the published index still
/// advertises the oldest <c>capacity</c> frames as readable, but the slots at the tail -
/// which alias the frames being written - have already been overwritten. A consumer sitting
/// near the tail then reads data exactly one lap in the future and has no way to tell.
/// </para>
/// <para>
/// So the producer publishes <see cref="ReserveIndex"/> (where the in-flight write will end)
/// <i>before</i> touching any samples, and <see cref="WriteIndex"/> (where valid data ends)
/// after. The readable window is <c>[ReserveIndex - Capacity, WriteIndex)</c>, which stays
/// honest while a write is in progress. Usable history is therefore the capacity minus one
/// in-flight packet.
/// </para>
/// <para>
/// When the stream has more than one channel the buffer also maintains a mono mixdown
/// plane, computed once during the write pass, because every consumer in the pipeline wants
/// it and recomputing it per read would be wasted work.
/// </para>
/// </remarks>
public sealed class AudioRingBuffer
{
    private readonly float[][] _planes;
    private readonly float[] _mono;
    private readonly int _capacity;
    private readonly int _mask;
    private readonly float _monoScale;

    private long _writeIndex;
    private long _reserveIndex;
    private long _overrunCount;
    private long _failedReadCount;

    /// <param name="channels">Number of interleaved channels the producer will write.</param>
    /// <param name="minimumCapacityFrames">
    /// Requested history depth in frames. Rounded up to the next power of two so the wrap
    /// is a mask rather than a modulo.
    /// </param>
    public AudioRingBuffer(int channels, int minimumCapacityFrames)
    {
        if (channels <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(channels), channels, "Channel count must be positive.");
        }

        if (minimumCapacityFrames <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(minimumCapacityFrames), minimumCapacityFrames, "Capacity must be positive.");
        }

        _capacity = (int)BitOperations.RoundUpToPowerOf2((uint)minimumCapacityFrames);
        _mask = _capacity - 1;

        Channels = channels;
        _planes = new float[channels][];
        for (int c = 0; c < channels; c++)
        {
            _planes[c] = new float[_capacity];
        }

        // A single-channel stream shares its plane with the mono view: no copy, no drift.
        _mono = channels == 1 ? _planes[0] : new float[_capacity];
        _monoScale = 1f / channels;
    }

    public int Channels { get; }

    /// <summary>History depth in frames. Always a power of two.</summary>
    public int Capacity => _capacity;

    /// <summary>
    /// Total frames ever written. This is the absolute frame clock; it does not wrap for
    /// six million years at 48 kHz.
    /// </summary>
    public long WriteIndex => Volatile.Read(ref _writeIndex);

    /// <summary>
    /// Where the producer's in-flight write will end. Equal to <see cref="WriteIndex"/> when
    /// no write is in progress, ahead of it while one is.
    /// </summary>
    public long ReserveIndex => Volatile.Read(ref _reserveIndex);

    /// <summary>
    /// Oldest absolute frame index that is still safe to read. Derived from
    /// <see cref="ReserveIndex"/> so that frames currently being overwritten are excluded.
    /// </summary>
    public long OldestAvailableIndex => Math.Max(0, ReserveIndex - _capacity);

    /// <summary>Number of times a write lapped data the consumer had not yet read.</summary>
    public long OverrunCount => Interlocked.Read(ref _overrunCount);

    /// <summary>Number of reads refused because the requested range was gone or not yet written.</summary>
    public long FailedReadCount => Interlocked.Read(ref _failedReadCount);

    /// <summary>
    /// Producer entry point. Deinterleaves <paramref name="interleaved"/> into the planar
    /// stores, updates the mono mixdown, then publishes the new write index.
    /// </summary>
    public void Write(ReadOnlySpan<float> interleaved, int frameCount)
    {
        if (frameCount <= 0)
        {
            return;
        }

        int channels = Channels;
        int required = frameCount * channels;
        if (interleaved.Length < required)
        {
            frameCount = interleaved.Length / channels;
            if (frameCount <= 0)
            {
                return;
            }
        }

        long write = _writeIndex;
        int start = (int)(write & _mask);

        // Announce the range about to be clobbered before touching a single sample. The
        // full fence matters: a consumer must never observe the overwritten slots without
        // also observing the reservation that invalidates them.
        Interlocked.Exchange(ref _reserveIndex, write + frameCount);

        if (channels == 1)
        {
            WriteMonoPlane(interleaved, frameCount, start);
        }
        else
        {
            WriteMultiChannel(interleaved, frameCount, start, channels);
        }

        // Release store: everything above is visible before the consumer sees the new index.
        Volatile.Write(ref _writeIndex, write + frameCount);

        if (frameCount > _capacity)
        {
            Interlocked.Increment(ref _overrunCount);
        }
    }

    private void WriteMonoPlane(ReadOnlySpan<float> source, int frameCount, int start)
    {
        float[] plane = _planes[0];
        int firstChunk = Math.Min(frameCount, _capacity - start);

        source[..firstChunk].CopyTo(plane.AsSpan(start, firstChunk));

        int remaining = frameCount - firstChunk;
        if (remaining > 0)
        {
            source.Slice(firstChunk, remaining).CopyTo(plane.AsSpan(0, remaining));
        }
    }

    private void WriteMultiChannel(ReadOnlySpan<float> source, int frameCount, int start, int channels)
    {
        float[][] planes = _planes;
        float[] mono = _mono;
        float scale = _monoScale;

        int src = 0;
        int slot = start;

        for (int f = 0; f < frameCount; f++)
        {
            float sum = 0f;
            for (int c = 0; c < channels; c++)
            {
                float sample = source[src++];
                planes[c][slot] = sample;
                sum += sample;
            }

            mono[slot] = sum * scale;
            slot = (slot + 1) & _mask;
        }
    }

    /// <summary>
    /// Writes <paramref name="frameCount"/> frames of digital silence. Used to bridge a
    /// reported capture discontinuity so the absolute frame clock stays aligned with real
    /// time instead of quietly compressing the gap.
    /// </summary>
    public void WriteSilence(int frameCount)
    {
        if (frameCount <= 0)
        {
            return;
        }

        long write = _writeIndex;
        int slot = (int)(write & _mask);
        int remaining = frameCount;

        Interlocked.Exchange(ref _reserveIndex, write + frameCount);

        while (remaining > 0)
        {
            int chunk = Math.Min(remaining, _capacity - slot);
            for (int c = 0; c < Channels; c++)
            {
                Array.Clear(_planes[c], slot, chunk);
            }

            if (!ReferenceEquals(_mono, _planes[0]))
            {
                Array.Clear(_mono, slot, chunk);
            }

            slot = (slot + chunk) & _mask;
            remaining -= chunk;
        }

        Volatile.Write(ref _writeIndex, write + frameCount);
    }

    /// <summary>
    /// Copies the frames starting at absolute index <paramref name="startIndex"/> from one
    /// channel into <paramref name="destination"/>.
    /// </summary>
    /// <returns>
    /// <see langword="false"/> if the range has already been overwritten, has not been
    /// written yet, or was lapped by the producer during the copy. A refused read is
    /// counted and must be treated as a dropped event, never as silence.
    /// </returns>
    public bool TryReadChannel(int channel, long startIndex, Span<float> destination)
    {
        if ((uint)channel >= (uint)Channels)
        {
            throw new ArgumentOutOfRangeException(nameof(channel), channel, "Channel index out of range.");
        }

        return TryReadPlane(_planes[channel], startIndex, destination);
    }

    /// <summary>Copies the mono mixdown for the requested absolute range.</summary>
    public bool TryReadMono(long startIndex, Span<float> destination) =>
        TryReadPlane(_mono, startIndex, destination);

    private bool TryReadPlane(float[] plane, long startIndex, Span<float> destination)
    {
        int length = destination.Length;
        if (length == 0)
        {
            return true;
        }

        if (startIndex < 0)
        {
            Interlocked.Increment(ref _failedReadCount);
            return false;
        }

        // Acquire loads: pair with the release store in Write. The readable window ends at
        // the write index and begins one capacity behind the *reserve* index, so frames the
        // producer is in the middle of overwriting are excluded.
        long write = Volatile.Read(ref _writeIndex);
        long oldest = Volatile.Read(ref _reserveIndex) - _capacity;

        if (startIndex + length > write || startIndex < oldest)
        {
            Interlocked.Increment(ref _failedReadCount);
            return false;
        }

        int slot = (int)(startIndex & _mask);
        int firstChunk = Math.Min(length, _capacity - slot);

        plane.AsSpan(slot, firstChunk).CopyTo(destination);

        int remaining = length - firstChunk;
        if (remaining > 0)
        {
            plane.AsSpan(0, remaining).CopyTo(destination[firstChunk..]);
        }

        // Re-validate: the producer may have lapped the region while we were copying, in
        // which case the bytes we just read are a mixture of two eras and must be discarded.
        // The barrier keeps the sample loads above from being reordered past this check.
        Interlocked.MemoryBarrier();

        if (startIndex < Volatile.Read(ref _reserveIndex) - _capacity)
        {
            Interlocked.Increment(ref _overrunCount);
            Interlocked.Increment(ref _failedReadCount);
            return false;
        }

        return true;
    }

    /// <summary>
    /// Number of frames available to a consumer positioned at <paramref name="readIndex"/>.
    /// Negative when the consumer has already been lapped.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public long AvailableFrom(long readIndex) => WriteIndex - readIndex;

    /// <summary>
    /// Clears the buffer and restarts the frame clock. Only legal while the producer is
    /// stopped; it is called when a stream is (re)started, not during capture.
    /// </summary>
    public void Reset()
    {
        for (int c = 0; c < Channels; c++)
        {
            Array.Clear(_planes[c]);
        }

        if (!ReferenceEquals(_mono, _planes[0]))
        {
            Array.Clear(_mono);
        }

        Interlocked.Exchange(ref _overrunCount, 0);
        Interlocked.Exchange(ref _failedReadCount, 0);
        Interlocked.Exchange(ref _reserveIndex, 0);
        Volatile.Write(ref _writeIndex, 0);
    }
}
