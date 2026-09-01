using System.Diagnostics;
using System.Runtime.InteropServices;
using Tapit.Audio.Wasapi;
using Tapit.Core.Audio;

namespace Tapit.Audio;

/// <summary>
/// Continuous microphone capture over WASAPI, shared mode, event driven.
/// </summary>
/// <remarks>
/// <para>
/// <b>Realtime contract.</b> The capture thread does exactly three things per packet: convert
/// samples, copy them into the ring, and bump counters. It never allocates (all buffers are
/// sized at stream open), never takes a lock, never logs, never touches the UI and never runs
/// an action. Consumers pull from <see cref="Buffer"/> after
/// <see cref="WaitForData"/> returns.
/// </para>
/// <para>
/// <b>Ownership.</b> One thread owns the entire device lifecycle - activation, format
/// negotiation, the capture loop, reconnect and teardown - and it is an MTA thread. That
/// keeps every COM object in a single apartment and makes the state machine linear code
/// rather than a web of callbacks.
/// </para>
/// <para>
/// <b>Shared mode, not exclusive.</b> Tapit runs in the background; taking exclusive control
/// of the microphone would silently break every call and voice chat on the machine. Shared
/// event-driven mode gives a ~10 ms period, which is well inside the latency budget.
/// </para>
/// </remarks>
public sealed class WasapiCaptureSource : IAudioCaptureSource
{
    private readonly WasapiCaptureOptions _options;
    private readonly ManualResetEventSlim _dataAvailable = new(false);
    private readonly ManualResetEvent _stopEvent = new(false);
    private readonly object _lifecycleLock = new();

    // COM objects and native resources: touched only by the capture thread.
    private IMMDeviceEnumerator? _enumerator;
    private IAudioClient? _audioClient;
    private IAudioCaptureClient? _captureClient;
    private EventWaitHandle? _audioEvent;
    private IntPtr _mmcssHandle = IntPtr.Zero;

    private float[] _scratch = [];
    private int _scratchFrames;
    private int _maxBridgedGapFrames;

    private Thread? _captureThread;
    private volatile bool _stopRequested;
    private volatile CaptureState _state = CaptureState.Stopped;
    private bool _disposed;

    // Statistics. Written by the capture thread, read by diagnostics.
    private long _totalFrames;
    private long _packetCount;
    private long _discontinuityCount;
    private long _silentPacketCount;
    private long _gapFramesInserted;
    private int _maxPacketFrames;
    private double _devicePeriodMs;
    private double _engineBufferMs;
    private double _streamLatencyMs;
    private double _maxServicePassMs;
    private double _lastServicePassMs;
    private volatile bool _rawModeActive;
    private volatile bool _mmcssActive;

    private long _expectedDevicePosition;
    private bool _hasDevicePosition;

    public WasapiCaptureSource(WasapiCaptureOptions? options = null)
    {
        _options = (options ?? new WasapiCaptureOptions()).Clone();
    }

    public AudioFormat? Format { get; private set; }

    public AudioRingBuffer? Buffer { get; private set; }

    public SampleClock? Clock { get; private set; }

    public CaptureState State => _state;

    public int StreamGeneration { get; private set; }

    public string? DeviceId { get; private set; }

    public string? DeviceName { get; private set; }

    /// <summary>
    /// True when the OS granted an effects-bypassed stream. False means AGC, noise
    /// suppression or beamforming may be reshaping the signal.
    /// </summary>
    public bool RawModeActive => _rawModeActive;

    public event EventHandler<CaptureStateChangedEventArgs>? StateChanged;

    public void Start()
    {
        lock (_lifecycleLock)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);

            if (_captureThread is not null)
            {
                return;
            }

            _stopRequested = false;
            _stopEvent.Reset();
            SetState(CaptureState.Starting, "Opening capture device");

            _captureThread = new Thread(CaptureThreadMain)
            {
                Name = "Tapit WASAPI Capture",
                IsBackground = true,
                Priority = ThreadPriority.Highest,
            };

            _captureThread.SetApartmentState(ApartmentState.MTA);
            _captureThread.Start();
        }
    }

    public void Stop()
    {
        Thread? thread;

        lock (_lifecycleLock)
        {
            thread = _captureThread;
            if (thread is null)
            {
                return;
            }

            _captureThread = null;
        }

        _stopRequested = true;
        _stopEvent.Set();
        _dataAvailable.Set();

        if (!thread.Join(4000))
        {
            SetState(CaptureState.Faulted, "Capture thread did not stop cleanly.");
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
        TotalFrames = Interlocked.Read(ref _totalFrames),
        PacketCount = Interlocked.Read(ref _packetCount),
        DiscontinuityCount = Interlocked.Read(ref _discontinuityCount),
        SilentPacketCount = Interlocked.Read(ref _silentPacketCount),
        GapFramesInserted = Interlocked.Read(ref _gapFramesInserted),
        OverrunCount = Buffer?.OverrunCount ?? 0,
        MaxPacketFrames = Volatile.Read(ref _maxPacketFrames),
        DevicePeriodMs = _devicePeriodMs,
        EngineBufferMs = _engineBufferMs,
        StreamLatencyMs = _streamLatencyMs,
        MaxServicePassMs = _maxServicePassMs,
        LastServicePassMs = _lastServicePassMs,
        RawModeActive = _rawModeActive,
        MmcssActive = _mmcssActive,
    };

    // ---------------------------------------------------------------------------------
    // Capture thread
    // ---------------------------------------------------------------------------------

    private void CaptureThreadMain()
    {
        RegisterMmcss();

        int reconnectDelay = _options.InitialReconnectDelayMs;

        try
        {
            while (!_stopRequested)
            {
                try
                {
                    OpenStream();

                    reconnectDelay = _options.InitialReconnectDelayMs;
                    SetState(CaptureState.Running, DescribeStream());

                    RunCaptureLoop();

                    if (_stopRequested)
                    {
                        break;
                    }
                }
                catch (WasapiException ex) when (ex.IsRecoverable && _options.AutoReconnect && !_stopRequested)
                {
                    CloseStream();
                    SetState(CaptureState.Reconnecting, $"{ex.Message}. Retrying in {reconnectDelay} ms.", ex);

                    if (_stopEvent.WaitOne(reconnectDelay))
                    {
                        break;
                    }

                    reconnectDelay = Math.Min(reconnectDelay * 2, _options.MaxReconnectDelayMs);
                    continue;
                }
                catch (Exception ex)
                {
                    CloseStream();
                    SetState(CaptureState.Faulted, ex.Message, ex);
                    return;
                }
                finally
                {
                    CloseStream();
                }
            }
        }
        finally
        {
            CloseStream();
            RevertMmcss();
            SetState(CaptureState.Stopped, "Microphone released");
        }
    }

    private void OpenStream()
    {
        _enumerator ??= (IMMDeviceEnumerator)new MMDeviceEnumeratorComObject();

        IMMDevice device = ResolveDevice(_enumerator);

        try
        {
            DeviceId = EndpointProperties.TryGetId(device);
            DeviceName = EndpointProperties.GetFriendlyName(device);

            AudioDeviceState deviceState = EndpointProperties.GetState(device);
            if (deviceState != AudioDeviceState.Active)
            {
                throw new WasapiException(
                    WasapiConstants.AudclntEDeviceInvalidated,
                    $"Device '{DeviceName}' is {deviceState.ToString().ToLowerInvariant()}");
            }

            // Attempt 0 asks for a raw, effects-bypassed stream. Attempt 1 accepts whatever
            // the driver is willing to give, and records that the signal is processed.
            bool tryRaw = _options.RequestRawMode;

            while (true)
            {
                try
                {
                    InitializeClient(device, tryRaw);
                    _rawModeActive = tryRaw;
                    return;
                }
                catch (WasapiException) when (tryRaw && _options.AllowProcessedFallback)
                {
                    ReleaseClient();
                    tryRaw = false;
                }
            }
        }
        finally
        {
            EndpointProperties.Release(device);
        }
    }

    private IMMDevice ResolveDevice(IMMDeviceEnumerator enumerator)
    {
        int hr;
        IMMDevice? device;

        if (!string.IsNullOrWhiteSpace(_options.DeviceId))
        {
            hr = enumerator.GetDevice(_options.DeviceId, out device);
            if (hr >= 0 && device is not null)
            {
                return device;
            }

            // A configured device that has gone away is a reconnectable condition, not a
            // fatal one: the user may simply have unplugged their USB microphone.
            throw new WasapiException(WasapiConstants.AudclntEDeviceInvalidated, "Resolving the configured capture device");
        }

        hr = enumerator.GetDefaultAudioEndpoint(EDataFlow.Capture, ERole.Console, out device);
        if (hr < 0 || device is null)
        {
            throw new WasapiException(
                hr < 0 ? hr : WasapiConstants.AudclntEDeviceInvalidated,
                "IMMDeviceEnumerator::GetDefaultAudioEndpoint");
        }

        return device;
    }

    private void InitializeClient(IMMDevice device, bool requestRaw)
    {
        Guid audioClientId = WasapiGuids.IAudioClient;
        int hr = device.Activate(ref audioClientId, WasapiConstants.ClsCtxAll, IntPtr.Zero, out object? clientObject);
        WasapiException.ThrowIfFailed(hr, "IMMDevice::Activate(IAudioClient)");

        _audioClient = clientObject as IAudioClient
                       ?? throw new WasapiException(WasapiConstants.ENoInterface, "Casting to IAudioClient");

        if (requestRaw)
        {
            ApplyRawClientProperties(clientObject);
        }

        IntPtr formatPointer = IntPtr.Zero;
        IntPtr closestMatch = IntPtr.Zero;

        try
        {
            // Ask *after* SetClientProperties: on some endpoints the raw format differs from
            // the processed mix format.
            WasapiException.ThrowIfFailed(_audioClient.GetMixFormat(out formatPointer), "IAudioClient::GetMixFormat");

            AudioFormat format = WaveFormatMarshaler.TryRead(formatPointer)
                                 ?? throw new NotSupportedException(
                                     $"The capture endpoint reports a format Tapit cannot decode: " +
                                     $"{WaveFormatMarshaler.Describe(formatPointer)}");

            WasapiException.ThrowIfFailed(
                _audioClient.GetDevicePeriod(out long defaultPeriod, out long minimumPeriod),
                "IAudioClient::GetDevicePeriod");

            _devicePeriodMs = defaultPeriod / WasapiConstants.ReferenceTimesPerMillisecond;

            long requestedBuffer = _options.RequestedBufferMs > 0
                ? (long)(_options.RequestedBufferMs * WasapiConstants.ReferenceTimesPerMillisecond)
                : 0;

            const uint streamFlags = WasapiConstants.StreamFlagsEventCallback | WasapiConstants.StreamFlagsNoPersist;

            hr = _audioClient.Initialize(
                AudioClientShareMode.Shared, streamFlags, requestedBuffer, 0, formatPointer, IntPtr.Zero);

            if (hr == WasapiConstants.AudclntEUnsupportedFormat)
            {
                // Fall back to whatever the engine says is closest to the mix format.
                if (_audioClient.IsFormatSupported(AudioClientShareMode.Shared, formatPointer, out closestMatch) >= 0 &&
                    closestMatch != IntPtr.Zero)
                {
                    AudioFormat? alternative = WaveFormatMarshaler.TryRead(closestMatch);
                    if (alternative is not null)
                    {
                        format = alternative;
                        hr = _audioClient.Initialize(
                            AudioClientShareMode.Shared, streamFlags, requestedBuffer, 0, closestMatch, IntPtr.Zero);
                    }
                }
            }

            if (hr == WasapiConstants.AudclntEBufferSizeNotAligned || hr == WasapiConstants.EInvalidArg)
            {
                // Some drivers reject a zero buffer duration. Retry with the device's own
                // default period, then with a slightly larger one.
                foreach (long candidate in new[] { defaultPeriod, defaultPeriod * 3, minimumPeriod * 4 })
                {
                    if (candidate <= 0)
                    {
                        continue;
                    }

                    hr = _audioClient.Initialize(
                        AudioClientShareMode.Shared, streamFlags, candidate, 0, formatPointer, IntPtr.Zero);

                    if (hr >= 0)
                    {
                        break;
                    }
                }
            }

            WasapiException.ThrowIfFailed(hr, "IAudioClient::Initialize");

            WasapiException.ThrowIfFailed(_audioClient.GetBufferSize(out int bufferFrames), "IAudioClient::GetBufferSize");

            if (_audioClient.GetStreamLatency(out long latency) >= 0)
            {
                _streamLatencyMs = latency / WasapiConstants.ReferenceTimesPerMillisecond;
            }

            Guid captureClientId = WasapiGuids.IAudioCaptureClient;
            WasapiException.ThrowIfFailed(
                _audioClient.GetService(ref captureClientId, out object? captureObject),
                "IAudioClient::GetService(IAudioCaptureClient)");

            _captureClient = captureObject as IAudioCaptureClient
                             ?? throw new WasapiException(WasapiConstants.ENoInterface, "Casting to IAudioCaptureClient");

            _audioEvent = new EventWaitHandle(false, EventResetMode.AutoReset);
            WasapiException.ThrowIfFailed(
                _audioClient.SetEventHandle(_audioEvent.SafeWaitHandle.DangerousGetHandle()),
                "IAudioClient::SetEventHandle");

            AllocateBuffers(format, bufferFrames);

            WasapiException.ThrowIfFailed(_audioClient.Start(), "IAudioClient::Start");
        }
        finally
        {
            if (formatPointer != IntPtr.Zero)
            {
                NativeMethods.CoTaskMemFree(formatPointer);
            }

            if (closestMatch != IntPtr.Zero)
            {
                NativeMethods.CoTaskMemFree(closestMatch);
            }
        }
    }

    private void ApplyRawClientProperties(object? clientObject)
    {
        // IAudioClient2 is present on Windows 8 and later. Its absence is not an error; it
        // only means raw mode cannot be requested on this system.
        if (clientObject is not IAudioClient2 client2)
        {
            _rawModeActive = false;
            return;
        }

        var properties = new AudioClientProperties
        {
            Size = (uint)Marshal.SizeOf<AudioClientProperties>(),
            IsOffload = 0,

            // Deliberately NOT Communications: that category invites OS-side ducking and
            // speech processing, which is exactly what raw mode is trying to avoid.
            Category = AudioStreamCategory.Other,
            Options = AudioClientStreamOptions.Raw,
        };

        int hr = client2.SetClientProperties(ref properties);
        if (hr < 0)
        {
            throw new WasapiException(hr, "IAudioClient2::SetClientProperties(RAW)");
        }
    }

    private void AllocateBuffers(AudioFormat format, int engineBufferFrames)
    {
        Format = format;
        _engineBufferMs = format.FramesToMilliseconds(engineBufferFrames);

        int ringFrames = Math.Max(
            format.MillisecondsToFrames(_options.RingSeconds * 1000.0),
            engineBufferFrames * 4);

        Buffer = new AudioRingBuffer(format.Channels, ringFrames);
        Clock = new SampleClock(format.SampleRate);

        // Every buffer the capture thread will ever need is allocated here, once. A packet
        // larger than the scratch is processed in chunks rather than by reallocating.
        _scratchFrames = Math.Max(engineBufferFrames, format.MillisecondsToFrames(50));
        _scratch = new float[_scratchFrames * format.Channels];

        _maxBridgedGapFrames = format.MillisecondsToFrames(_options.MaxBridgedGapMs);

        _expectedDevicePosition = 0;
        _hasDevicePosition = false;

        Interlocked.Exchange(ref _totalFrames, 0);
        Interlocked.Exchange(ref _packetCount, 0);
        Interlocked.Exchange(ref _discontinuityCount, 0);
        Interlocked.Exchange(ref _silentPacketCount, 0);
        Interlocked.Exchange(ref _gapFramesInserted, 0);
        Volatile.Write(ref _maxPacketFrames, 0);
        _maxServicePassMs = 0;
        _lastServicePassMs = 0;

        StreamGeneration++;
    }

    private void RunCaptureLoop()
    {
        WaitHandle[] handles = [_audioEvent!, _stopEvent];

        // Two device periods, floored at 100 ms: long enough not to spin, short enough that a
        // dead stream is noticed quickly.
        int timeout = Math.Max(100, (int)(_devicePeriodMs * 2));

        while (!_stopRequested)
        {
            int signalled = WaitHandle.WaitAny(handles, timeout);

            if (signalled == 1)
            {
                return;
            }

            if (signalled == WaitHandle.WaitTimeout)
            {
                // A stream that stops firing its event is usually an endpoint that vanished
                // without a notification. Poke the client so the HRESULT tells us.
                int hr = _audioClient!.GetCurrentPadding(out _);
                if (hr < 0)
                {
                    throw new WasapiException(hr, "IAudioClient::GetCurrentPadding");
                }

                continue;
            }

            ServicePackets();
        }
    }

    private void ServicePackets()
    {
        long started = Stopwatch.GetTimestamp();
        IAudioCaptureClient capture = _captureClient!;

        while (!_stopRequested)
        {
            int hr = capture.GetNextPacketSize(out int nextPacketFrames);
            if (hr < 0)
            {
                throw new WasapiException(hr, "IAudioCaptureClient::GetNextPacketSize");
            }

            if (nextPacketFrames == 0)
            {
                break;
            }

            hr = capture.GetBuffer(
                out IntPtr data,
                out int frames,
                out uint flags,
                out ulong devicePosition,
                out ulong qpcPosition);

            if (hr == WasapiConstants.AudclntSBufferEmpty)
            {
                // Documented: nothing was returned and ReleaseBuffer must not be called.
                break;
            }

            if (hr < 0)
            {
                throw new WasapiException(hr, "IAudioCaptureClient::GetBuffer");
            }

            try
            {
                if (frames > 0)
                {
                    ProcessPacket(data, frames, flags, devicePosition, qpcPosition);
                }
            }
            finally
            {
                capture.ReleaseBuffer(frames);
            }
        }

        double elapsedMs = SampleClock.TicksToMilliseconds(Stopwatch.GetTimestamp() - started);
        _lastServicePassMs = elapsedMs;
        if (elapsedMs > _maxServicePassMs)
        {
            _maxServicePassMs = elapsedMs;
        }

        _dataAvailable.Set();
    }

    private void ProcessPacket(IntPtr data, int frames, uint flags, ulong devicePosition, ulong qpcPosition)
    {
        AudioRingBuffer buffer = Buffer!;
        AudioFormat format = Format!;

        // The audio engine always flags the first packet of a stream as discontinuous,
        // because there is nothing before it. Counting that as a glitch would put a
        // permanent 1 in every diagnostic report and hide the real ones.
        bool isFirstPacket = !_hasDevicePosition;

        BridgeGap((long)devicePosition, frames);

        // Anchor the frame clock to the performance counter for the first frame of this
        // packet, so latency is measured from the acoustic event rather than from whenever
        // this thread happened to be scheduled.
        if (qpcPosition != 0)
        {
            Clock!.Anchor(buffer.WriteIndex, SampleClock.QpcHundredNanosecondsToStopwatchTicks(qpcPosition));
        }

        if ((flags & WasapiConstants.BufferFlagsSilent) != 0)
        {
            buffer.WriteSilence(frames);
            Interlocked.Increment(ref _silentPacketCount);
        }
        else
        {
            WriteConverted(data, frames, format, buffer);
        }

        if ((flags & WasapiConstants.BufferFlagsDataDiscontinuity) != 0 && !isFirstPacket)
        {
            Interlocked.Increment(ref _discontinuityCount);
        }

        Interlocked.Add(ref _totalFrames, frames);
        Interlocked.Increment(ref _packetCount);

        if (frames > Volatile.Read(ref _maxPacketFrames))
        {
            Volatile.Write(ref _maxPacketFrames, frames);
        }
    }

    private unsafe void WriteConverted(IntPtr data, int frames, AudioFormat format, AudioRingBuffer buffer)
    {
        int channels = format.Channels;
        int bytesPerFrame = format.BlockAlign;
        byte* source = (byte*)data;
        int remaining = frames;

        // Chunked so an unexpectedly large packet never triggers an allocation here.
        while (remaining > 0)
        {
            int chunk = Math.Min(remaining, _scratchFrames);
            var bytes = new ReadOnlySpan<byte>(source, chunk * bytesPerFrame);

            SampleConverter.ToFloat(bytes, _scratch.AsSpan(0, chunk * channels), format.SampleFormat);
            buffer.Write(_scratch, chunk);

            source += chunk * bytesPerFrame;
            remaining -= chunk;
        }
    }

    private void BridgeGap(long devicePosition, int frames)
    {
        if (_hasDevicePosition)
        {
            long gap = devicePosition - _expectedDevicePosition;

            if (gap > 0 && gap <= _maxBridgedGapFrames)
            {
                // Keep the absolute frame clock aligned with real time; compressing a gap
                // would silently shift every subsequent latency measurement.
                Buffer!.WriteSilence((int)gap);
                Interlocked.Add(ref _gapFramesInserted, gap);
            }
            else if (gap > _maxBridgedGapFrames)
            {
                // Too large to paper over. Restart the clock and let the consumer see a
                // discontinuity rather than injecting seconds of synthetic silence.
                Clock!.Reset();
                Interlocked.Increment(ref _discontinuityCount);
            }
        }

        _expectedDevicePosition = devicePosition + frames;
        _hasDevicePosition = true;
    }

    private void CloseStream()
    {
        try
        {
            _audioClient?.Stop();
        }
        catch (COMException)
        {
            // The device is already gone; nothing to stop.
        }

        ReleaseClient();

        _audioEvent?.Dispose();
        _audioEvent = null;
        _hasDevicePosition = false;
    }

    private void ReleaseClient()
    {
        EndpointProperties.Release(_captureClient);
        _captureClient = null;

        EndpointProperties.Release(_audioClient);
        _audioClient = null;
    }

    private void RegisterMmcss()
    {
        if (!_options.UseMmcss)
        {
            return;
        }

        uint taskIndex = 0;
        _mmcssHandle = NativeMethods.AvSetMmThreadCharacteristics("Pro Audio", ref taskIndex);
        _mmcssActive = _mmcssHandle != IntPtr.Zero;
    }

    private void RevertMmcss()
    {
        if (_mmcssHandle != IntPtr.Zero)
        {
            NativeMethods.AvRevertMmThreadCharacteristics(_mmcssHandle);
            _mmcssHandle = IntPtr.Zero;
        }

        _mmcssActive = false;
    }

    private string DescribeStream()
    {
        string processing = _rawModeActive
            ? "raw (effects bypassed)"
            : "processed (Windows audio enhancements are active)";

        return $"{DeviceName} - {Format} - {processing}";
    }

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

        EndpointProperties.Release(_enumerator);
        _enumerator = null;

        _dataAvailable.Dispose();
        _stopEvent.Dispose();
    }
}
