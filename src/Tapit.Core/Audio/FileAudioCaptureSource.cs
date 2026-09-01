using System.Diagnostics;

namespace Tapit.Core.Audio;

/// <summary>How a <see cref="FileAudioCaptureSource"/> advances through the file.</summary>
public enum ReplayPacing
{
    /// <summary>
    /// The caller drives the clock with <see cref="FileAudioCaptureSource.Pump"/>. Fully
    /// deterministic: the same WAV file always produces the same frame indices, so detector
    /// behaviour is reproducible run to run. This is the mode the replay tool and the test
    /// suite use.
    /// </summary>
    Manual,

    /// <summary>A background thread paces packets at true wall-clock speed.</summary>
    Realtime,

    /// <summary>
    /// A background thread pushes packets as fast as the consumer keeps up, throttling only
    /// to avoid lapping the ring.
    /// </summary>
    Fast,
}

/// <summary>
/// An <see cref="IAudioCaptureSource"/> backed by a WAV file instead of a microphone.
/// </summary>
/// <remarks>
/// This is what makes the DSP work measurable rather than anecdotal. Because the detector,
/// feature extractor and classifier consume the <see cref="AudioRingBuffer"/> and nothing
/// else, feeding that ring from a file exercises exactly the production code path with no
/// microphone, no device jitter, and no UI.
/// </remarks>
public sealed class FileAudioCaptureSource : IAudioCaptureSource
{
    private readonly string _path;
    private readonly ReplayPacing _pacing;
    private readonly double _ringSeconds;
    private readonly ManualResetEventSlim _dataAvailable = new(false);
    private readonly object _lifecycleLock = new();

    private WavReader? _reader;
    private float[] _packet = [];
    private int _packetFrames;
    private long _virtualStartTicks;
    private double _ticksPerFrame;

    private Thread? _pumpThread;
    private volatile bool _stopRequested;
    private volatile bool _endOfStream;
    private CaptureState _state = CaptureState.Stopped;
    private bool _disposed;

    private long _totalFrames;
    private long _packetCount;
    private int _maxPacketFrames;

    public FileAudioCaptureSource(
        string path,
        ReplayPacing pacing = ReplayPacing.Manual,
        int packetFrames = 0,
        double ringSeconds = 4.0)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        _path = path;
        _pacing = pacing;
        _packetFrames = packetFrames;
        _ringSeconds = ringSeconds > 0 ? ringSeconds : 4.0;
    }

    public AudioFormat? Format { get; private set; }

    public AudioRingBuffer? Buffer { get; private set; }

    public SampleClock? Clock { get; private set; }

    public CaptureState State
    {
        get
        {
            lock (_lifecycleLock)
            {
                return _state;
            }
        }
    }

    public int StreamGeneration { get; private set; }

    public string? DeviceId => _path;

    public string? DeviceName => Path.GetFileName(_path);

    /// <summary>True once every frame in the file has been pushed into the ring.</summary>
    public bool EndOfStream => _endOfStream;

    public long TotalFramesInFile => _reader?.TotalFrames ?? 0;

    public event EventHandler<CaptureStateChangedEventArgs>? StateChanged;

    /// <summary>Raised once when the file has been fully replayed.</summary>
    public event EventHandler? Completed;

    public void Start()
    {
        lock (_lifecycleLock)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);

            if (_state is CaptureState.Running or CaptureState.Starting)
            {
                return;
            }

            SetState(CaptureState.Starting, $"Opening {DeviceName}");

            try
            {
                _reader = new WavReader(_path);
                Format = _reader.Format;

                // Default packet size mirrors a typical WASAPI shared-mode period (10 ms), so
                // replayed audio arrives in the same granularity as live capture.
                if (_packetFrames <= 0)
                {
                    _packetFrames = Math.Max(1, Format.MillisecondsToFrames(10));
                }

                _packet = new float[_packetFrames * Format.Channels];
                Buffer = new AudioRingBuffer(Format.Channels, Format.MillisecondsToFrames(_ringSeconds * 1000.0));
                Clock = new SampleClock(Format.SampleRate);

                _ticksPerFrame = (double)Stopwatch.Frequency / Format.SampleRate;
                _virtualStartTicks = Stopwatch.GetTimestamp();
                _stopRequested = false;
                _endOfStream = false;
                _totalFrames = 0;
                _packetCount = 0;
                _maxPacketFrames = 0;
                StreamGeneration++;

                SetState(CaptureState.Running, $"Replaying {DeviceName}");
            }
            catch (Exception ex)
            {
                SetState(CaptureState.Faulted, $"Could not open {DeviceName}: {ex.Message}", ex);
                throw;
            }

            if (_pacing != ReplayPacing.Manual)
            {
                _pumpThread = new Thread(PumpLoop)
                {
                    Name = "Tapit File Replay",
                    IsBackground = true,
                    Priority = ThreadPriority.AboveNormal,
                };

                _pumpThread.Start();
            }
        }
    }

    /// <summary>
    /// Pushes one packet of audio into the ring. Manual pacing only.
    /// </summary>
    /// <returns><see langword="false"/> once the file is exhausted.</returns>
    public bool Pump(int maxFrames = 0)
    {
        if (_pacing != ReplayPacing.Manual)
        {
            throw new InvalidOperationException("Pump is only valid when pacing is Manual.");
        }

        return PumpOnce(maxFrames);
    }

    /// <summary>Replays the entire file through the ring, invoking a callback after each packet.</summary>
    public void PumpToEnd(Action<AudioRingBuffer>? onPacket = null)
    {
        while (PumpOnce(0))
        {
            if (onPacket is not null && Buffer is not null)
            {
                onPacket(Buffer);
            }
        }
    }

    private bool PumpOnce(int maxFrames)
    {
        WavReader? reader = _reader;
        AudioRingBuffer? buffer = Buffer;
        AudioFormat? format = Format;

        if (reader is null || buffer is null || format is null || _endOfStream)
        {
            return false;
        }

        int request = maxFrames > 0 ? Math.Min(maxFrames, _packetFrames) : _packetFrames;
        int frames = reader.ReadFrames(_packet, request);

        if (frames <= 0)
        {
            _endOfStream = true;
            _dataAvailable.Set();
            Completed?.Invoke(this, EventArgs.Empty);
            return false;
        }

        long startFrame = buffer.WriteIndex;

        // Anchor on a virtual clock so replay latency numbers are deterministic and
        // independent of how fast the host machine happens to run the loop.
        Clock?.Anchor(startFrame, _virtualStartTicks + (long)(startFrame * _ticksPerFrame));

        buffer.Write(_packet, frames);

        _totalFrames += frames;
        _packetCount++;
        if (frames > _maxPacketFrames)
        {
            _maxPacketFrames = frames;
        }

        _dataAvailable.Set();
        return true;
    }

    private void PumpLoop()
    {
        var stopwatch = Stopwatch.StartNew();
        long framesPushed = 0;
        AudioFormat format = Format!;

        try
        {
            while (!_stopRequested)
            {
                if (_pacing == ReplayPacing.Realtime)
                {
                    double dueMs = format.FramesToMilliseconds(framesPushed);
                    double nowMs = stopwatch.Elapsed.TotalMilliseconds;
                    if (dueMs > nowMs)
                    {
                        int sleep = (int)Math.Min(50, dueMs - nowMs);
                        if (sleep > 0)
                        {
                            Thread.Sleep(sleep);
                        }
                        else
                        {
                            Thread.SpinWait(64);
                        }

                        continue;
                    }
                }

                long before = Buffer!.WriteIndex;
                if (!PumpOnce(0))
                {
                    break;
                }

                framesPushed += Buffer.WriteIndex - before;

                if (_pacing == ReplayPacing.Fast)
                {
                    // Never outrun the ring: a replay that laps its own consumer is not a
                    // faithful reproduction of live capture.
                    Thread.Yield();
                }
            }
        }
        catch (Exception ex)
        {
            SetState(CaptureState.Faulted, $"Replay failed: {ex.Message}", ex);
        }
    }

    public void Stop()
    {
        Thread? pump;

        lock (_lifecycleLock)
        {
            if (_state == CaptureState.Stopped)
            {
                return;
            }

            _stopRequested = true;
            pump = _pumpThread;
            _pumpThread = null;
        }

        _dataAvailable.Set();
        pump?.Join(2000);

        lock (_lifecycleLock)
        {
            _reader?.Dispose();
            _reader = null;
            SetState(CaptureState.Stopped, "Replay stopped");
        }
    }

    public bool WaitForData(int millisecondsTimeout)
    {
        if (!_dataAvailable.Wait(millisecondsTimeout))
        {
            return false;
        }

        _dataAvailable.Reset();
        return true;
    }

    public CaptureStatistics GetStatistics() => new()
    {
        TotalFrames = _totalFrames,
        PacketCount = _packetCount,
        MaxPacketFrames = _maxPacketFrames,
        OverrunCount = Buffer?.OverrunCount ?? 0,
        DevicePeriodMs = Format is null ? 0 : Format.FramesToMilliseconds(_packetFrames),
        EngineBufferMs = Format is null || Buffer is null ? 0 : Format.FramesToMilliseconds(Buffer.Capacity),
        RawModeActive = true,
        MmcssActive = false,
    };

    private void SetState(CaptureState state, string? message = null, Exception? error = null)
    {
        _state = state;
        StateChanged?.Invoke(this, new CaptureStateChangedEventArgs(state, message, error));
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        Stop();
        _dataAvailable.Dispose();
    }
}
