namespace Tapit.Core.Audio;

/// <summary>
/// Sequential block reader over an <see cref="IAudioCaptureSource"/>'s ring buffer.
/// </summary>
/// <remarks>
/// <para>
/// Every consumer in the pipeline - the detector, the diagnostics meter, the replay
/// harness - needs the same three behaviours: walk forward in fixed blocks, notice when the
/// producer has lapped it, and start over cleanly when the stream is reopened. Putting that
/// in one place means a reconnect cannot leave a half-read analysis window in some
/// downstream component's state.
/// </para>
/// <para>
/// Dropped frames are counted, never hidden. A gap in the audio a consumer never saw is a
/// missed tap, and Tapit would rather report that than invent silence.
/// </para>
/// </remarks>
public sealed class RingBufferReader(IAudioCaptureSource source)
{
    private readonly IAudioCaptureSource _source = source ?? throw new ArgumentNullException(nameof(source));

    private long _position;
    private int _generation = -1;
    private long _droppedFrames;
    private long _resyncCount;

    /// <summary>Absolute frame index of the next block to be returned.</summary>
    public long Position => _position;

    /// <summary>Frames the producer overwrote before this reader got to them.</summary>
    public long DroppedFrames => _droppedFrames;

    /// <summary>Times the reader had to jump forward, including stream restarts.</summary>
    public long ResyncCount => _resyncCount;

    public int Generation => _generation;

    /// <summary>Frames written but not yet consumed.</summary>
    public long Available
    {
        get
        {
            AudioRingBuffer? buffer = _source.Buffer;
            return buffer is null ? 0 : Math.Max(0, buffer.WriteIndex - _position);
        }
    }

    /// <summary>
    /// Reads the next contiguous block of the mono mixdown.
    /// </summary>
    /// <param name="destination">Exactly this many frames are read, or nothing is.</param>
    /// <param name="startIndex">Absolute frame index of the first sample returned.</param>
    /// <returns><see langword="false"/> when a whole block is not yet available.</returns>
    public bool TryReadNextBlock(Span<float> destination, out long startIndex) =>
        TryReadNextBlock(destination, channel: -1, out startIndex);

    /// <summary>
    /// Reads the next contiguous block from one channel, or from the mono mixdown when
    /// <paramref name="channel"/> is negative.
    /// </summary>
    public bool TryReadNextBlock(Span<float> destination, int channel, out long startIndex)
    {
        startIndex = 0;

        AudioRingBuffer? buffer = _source.Buffer;
        if (buffer is null || destination.IsEmpty)
        {
            return false;
        }

        int generation = _source.StreamGeneration;
        if (generation != _generation)
        {
            // A new stream: different format, different ring, frame clock back at zero.
            // Start from the oldest audio the new stream has, and say so.
            _generation = generation;
            _position = buffer.OldestAvailableIndex;
            _resyncCount++;
        }

        if (!EnsureNotLapped(buffer, destination.Length))
        {
            return false;
        }

        bool read = channel < 0
            ? buffer.TryReadMono(_position, destination)
            : buffer.TryReadChannel(channel, _position, destination);

        if (!read)
        {
            // The producer lapped us mid-copy. Resynchronise to whatever is still valid.
            long oldest = buffer.OldestAvailableIndex;
            if (oldest > _position)
            {
                _droppedFrames += oldest - _position;
                _position = oldest;
                _resyncCount++;
            }

            return false;
        }

        startIndex = _position;
        _position += destination.Length;
        return true;
    }

    private bool EnsureNotLapped(AudioRingBuffer buffer, int blockLength)
    {
        long write = buffer.WriteIndex;
        long oldest = Math.Max(0, write - buffer.Capacity);

        if (_position < oldest)
        {
            _droppedFrames += oldest - _position;
            _position = oldest;
            _resyncCount++;
        }

        return write - _position >= blockLength;
    }

    /// <summary>Jumps to the newest audio, discarding anything not yet consumed.</summary>
    public void SkipToLatest()
    {
        AudioRingBuffer? buffer = _source.Buffer;
        if (buffer is null)
        {
            return;
        }

        long write = buffer.WriteIndex;
        if (write > _position)
        {
            _droppedFrames += write - _position;
        }

        _position = write;
        _resyncCount++;
    }

    public void Reset()
    {
        _position = 0;
        _generation = -1;
        _droppedFrames = 0;
        _resyncCount = 0;
    }
}
