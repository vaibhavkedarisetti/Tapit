using Tapit.Core.Audio;
using Tapit.Core.DSP;

namespace Tapit.Core.Detection;

/// <summary>
/// Real-time acoustic impulse detector.
/// </summary>
/// <remarks>
/// <para>
/// Deliberately one class and one pass. It walks the ring in short frames, tracks an
/// adaptive noise floor, fires on a sharp rise above it, then validates the full window
/// before calling anything a tap.
/// </para>
/// <para>
/// It runs on the DSP thread, never the capture thread, and pulls from the ring buffer - so
/// exactly the same code runs against a live microphone and against a WAV file. That is the
/// whole point: a detector you cannot replay is a detector you cannot tune.
/// </para>
/// </remarks>
public sealed class TapDetector
{
    private const float FloorEpsilon = 1e-7f;   // about -140 dBFS

    private readonly AudioFormat _format;
    private readonly DetectorOptions _options;
    private readonly DcBlocker _dcBlocker;

    private readonly int _frameSamples;
    private readonly int _windowSamples;
    private readonly int _preRollSamples;
    private readonly int _refractorySamples;
    private readonly long _roomLearnSamples;

    private readonly float[] _frame;
    private readonly float[] _filtered;
    private readonly float[] _window;
    private readonly float[] _envelope;
    private float[][] _channelWindows = [];
    private readonly List<TapEvent> _pendingEvents = [];

    private readonly float _fallCoefficient;
    private readonly float _riseCoefficient;
    private readonly float _onsetRatio;
    private readonly float _riseRatio;
    private readonly float _minOnsetAmplitude;

    private long _position;

    // Nullable rather than a sentinel: "_position - long.MinValue" overflows and wraps
    // negative, which silently makes the refractory test true forever.
    private long? _lastEventOnset;
    private long? _pendingOnset;
    private double _roomLearnSum;
    private long _roomLearnFrames;

    private float _noiseFloor;
    private float _lastFrameRms;
    private float _previousFrameRms;
    private int _generation = -1;

    public TapDetector(AudioFormat format, DetectorOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(format);

        _format = format;
        _options = (options ?? new DetectorOptions()).Clone();

        _frameSamples = Math.Max(1, format.MillisecondsToFrames(_options.FrameMs));
        _windowSamples = Math.Max(_frameSamples * 2, format.MillisecondsToFrames(_options.WindowMs));
        _preRollSamples = Math.Clamp(format.MillisecondsToFrames(_options.PreRollMs), 0, _windowSamples - 1);
        _refractorySamples = Math.Max(_frameSamples, format.MillisecondsToFrames(_options.RefractoryMs));
        _roomLearnSamples = (long)(_options.RoomLearnSeconds * format.SampleRate);

        _frame = new float[_frameSamples];
        _filtered = new float[_frameSamples];
        _window = new float[_windowSamples];
        _envelope = new float[_windowSamples];

        _dcBlocker = new DcBlocker(_options.DcBlockerHz, format.SampleRate);

        _fallCoefficient = SmoothingCoefficient(_options.NoiseFallMs);
        _riseCoefficient = SmoothingCoefficient(_options.NoiseRiseMs);
        _onsetRatio = (float)Math.Pow(10.0, _options.OnsetThresholdDb / 20.0);
        _riseRatio = (float)Math.Pow(10.0, _options.MinRiseDb / 20.0);
        _minOnsetAmplitude = (float)SignalAnalysis.FromDbfs(_options.MinOnsetDbfs);

        _noiseFloor = FloorEpsilon;
    }

    public DetectorOptions Options => _options;

    public int WindowSamples => _windowSamples;

    public int PreRollSamples => _preRollSamples;

    public double WindowMs => _format.FramesToMilliseconds(_windowSamples);

    /// <summary>Current adaptive noise floor.</summary>
    public double NoiseFloorDbfs => SignalAnalysis.ToDbfs(_noiseFloor);

    /// <summary>Level of the most recently examined frame.</summary>
    public double LastFrameDbfs => SignalAnalysis.ToDbfs(_lastFrameRms);

    /// <summary>True while still seeding the noise floor and not yet detecting.</summary>
    public bool IsLearningRoom => _position < _roomLearnSamples;

    public double RoomLearnProgress =>
        _roomLearnSamples <= 0 ? 1.0 : Math.Clamp((double)_position / _roomLearnSamples, 0.0, 1.0);

    /// <summary>Absolute frame index the detector has consumed up to.</summary>
    public long Position => _position;

    public long FramesDropped { get; private set; }

    public long CandidateCount { get; private set; }

    public long AcceptedCount { get; private set; }

    /// <summary>
    /// Consumes whatever audio is available and returns any events completed by this call.
    /// The returned list is reused between calls; copy anything you intend to keep.
    /// </summary>
    public IReadOnlyList<TapEvent> Process(AudioRingBuffer buffer, int streamGeneration = 0)
    {
        ArgumentNullException.ThrowIfNull(buffer);

        _pendingEvents.Clear();

        if (streamGeneration != _generation)
        {
            ResetForNewStream(buffer, streamGeneration);
        }

        while (true)
        {
            // A pending onset completes as soon as its window tail has arrived.
            if (_pendingOnset is long onset)
            {
                long windowStart = onset - _preRollSamples;
                if (windowStart + _windowSamples > buffer.WriteIndex)
                {
                    break;
                }

                CompletePending(buffer, onset, windowStart);
                continue;
            }

            if (_position + _frameSamples > buffer.WriteIndex)
            {
                break;
            }

            if (!buffer.TryReadMono(_position, _frame))
            {
                // Lapped. Skip forward and say so rather than analysing a corrupt window.
                long oldest = buffer.OldestAvailableIndex;
                FramesDropped += Math.Max(0, oldest - _position);
                _position = oldest;
                _dcBlocker.Reset();
                continue;
            }

            ScanFrame();
            _position += _frameSamples;
        }

        return _pendingEvents;
    }

    private void ScanFrame()
    {
        // Raw capture carries a DC offset (measured ~-42 dBFS); remove it before any
        // energy measurement, or the offset alone can hold the detector above threshold.
        _dcBlocker.Process(_frame, _filtered);

        float rms = SignalAnalysis.Measure(_filtered).Rms;
        _lastFrameRms = rms;

        if (_position < _roomLearnSamples)
        {
            _roomLearnSum += rms;
            _roomLearnFrames++;
            _noiseFloor = Math.Max(FloorEpsilon, (float)(_roomLearnSum / _roomLearnFrames));
            return;
        }

        // Test against the floor as it stood before this frame, then let the floor follow.
        // Asymmetric tracking does the work: it drops toward quiet quickly but rises with a
        // ~1.5 s constant, so a 50 ms tap barely moves it while a fan that switches on is
        // eventually absorbed. Freezing the floor instead would leave the detector jammed
        // above threshold forever once the room got louder.
        float floor = _noiseFloor;
        float coefficient = rms < floor ? _fallCoefficient : _riseCoefficient;
        _noiseFloor = Math.Max(FloorEpsilon, floor + (coefficient * (rms - floor)));

        float previous = _previousFrameRms;
        _previousFrameRms = rms;

        // Three conditions, cheapest first: a jump from the previous frame, a margin over
        // the noise floor, and an absolute level. The rise condition is what separates an
        // impact from room noise that merely drifts upward.
        bool aboveThreshold = rms > previous * _riseRatio
                              && rms > floor * _onsetRatio
                              && rms >= _minOnsetAmplitude;

        if (!aboveThreshold)
        {
            return;
        }

        // Two separate timers, because the two failure modes pull in opposite directions.
        //
        //   * Refracting on every candidate lets a burst of room noise mask a real tap that
        //     arrives milliseconds later.
        //   * Refracting only on accepted events lets one physical strike produce several
        //     events: if the first window is rejected, the same strike's ringing can be
        //     picked up again a few frames later and accepted.
        //
        // So: every candidate consumes its own analysis window (handled in CompletePending,
        // which advances past it), and an *accepted* tap additionally holds off for the full
        // refractory period.
        if (_lastEventOnset is long last && _position - last < _refractorySamples)
        {
            return;
        }

        _pendingOnset = _position;
        CandidateCount++;
    }

    private void CompletePending(AudioRingBuffer buffer, long onset, long windowStart)
    {
        _pendingOnset = null;

        ReadChannelWindows(buffer, windowStart);

        if (windowStart < 0 || !buffer.TryReadMono(windowStart, _window))
        {
            AdvancePast(windowStart + _windowSamples);
            _pendingEvents.Add(BuildEvent(onset, windowStart, default, RejectionReason.WindowUnavailable));
            return;
        }

        // Stateless DC removal so the window's analysis does not depend on what preceded it.
        DcBlocker.RemoveMean(_window);

        TapMeasurements measurements = Measure(_window);
        RejectionReason rejection = Validate(measurements);

        TapEvent tapEvent = BuildEvent(onset, windowStart, measurements, rejection);
        if (tapEvent.Accepted)
        {
            AcceptedCount++;
        }

        // Refractory follows any acoustically *loud* candidate, not merely an accepted one.
        //
        // Measured: a single hard strike that was rejected for clipping produced nine events
        // - the strike, then seven detections of its own decaying ring at -15 to -27 dBFS,
        // then a tail. Nothing suppressed them, because refractory was tied to acceptance and
        // the strike had been rejected on shape.
        //
        // Loudness, not verdict, is what says a real physical event just happened. Quiet room
        // noise still cannot open a refractory window and mask a genuine tap behind it.
        if (tapEvent.Accepted || IsLoudPhysicalEvent(tapEvent, measurements))
        {
            _lastEventOnset = onset;
        }

        if (!tapEvent.Accepted)
        {
            // Frames inside a candidate's window are never scanned, so in a persistently
            // noisy room the floor tracker would see almost no audio and stay stuck low -
            // leaving the detector churning out candidates it always rejects. A rejected
            // window is by definition not a tap, so fold its level into the floor as if
            // those frames had been scanned. An accepted tap must never raise the floor.
            AbsorbIntoNoiseFloor(measurements.Rms, _windowSamples / _frameSamples);
        }

        // Resume scanning past the window just analysed. Two candidates cannot share audio:
        // whatever is inside this window has already been judged, accepted or not.
        AdvancePast(windowStart + _windowSamples);

        _pendingEvents.Add(tapEvent);
    }

    /// <summary>
    /// True when the window carried enough level to be a genuine impact, whatever the shape
    /// gates decided. Clipping counts: an overdriven window is loud by definition.
    /// </summary>
    private bool IsLoudPhysicalEvent(TapEvent tapEvent, in TapMeasurements measurements) =>
        measurements.ClippedSamples > 0 ||
        (measurements.PeakDbfs >= _options.MinPeakDbfs && tapEvent.SnrDb >= _options.MinSnrDb);

    /// <summary>
    /// Pulls each channel's window alongside the mono mixdown, so inter-channel cues survive.
    /// Allocated once per stream, reused thereafter.
    /// </summary>
    private void ReadChannelWindows(AudioRingBuffer buffer, long windowStart)
    {
        if (buffer.Channels < 2 || windowStart < 0)
        {
            return;
        }

        if (_channelWindows.Length != buffer.Channels)
        {
            _channelWindows = new float[buffer.Channels][];
            for (int c = 0; c < buffer.Channels; c++)
            {
                _channelWindows[c] = new float[_windowSamples];
            }
        }

        for (int c = 0; c < buffer.Channels; c++)
        {
            if (!buffer.TryReadChannel(c, windowStart, _channelWindows[c]))
            {
                Array.Clear(_channelWindows[c]);
            }
        }
    }

    private TapEvent BuildEvent(
        long onset, long windowStart, TapMeasurements measurements, RejectionReason rejection)
    {
        double snr = measurements.PeakDbfs - SignalAnalysis.ToDbfs(_noiseFloor);

        return new TapEvent
        {
            OnsetSample = onset,
            WindowStartSample = windowStart,
            OnsetSeconds = _format.FramesToSeconds(onset),
            Accepted = rejection == RejectionReason.None,
            Rejection = rejection,
            Measurements = measurements,
            NoiseFloorDbfs = SignalAnalysis.ToDbfs(_noiseFloor),
            SnrDb = snr,
            Window = rejection == RejectionReason.WindowUnavailable ? [] : _window.AsSpan().ToArray(),
            ChannelWindows = rejection == RejectionReason.WindowUnavailable
                ? []
                : _channelWindows.Select(static c => c.AsSpan().ToArray()).ToArray(),
            SampleRate = _format.SampleRate,
        };
    }

    private TapMeasurements Measure(ReadOnlySpan<float> window)
    {
        SignalLevels levels = SignalAnalysis.Measure(window, _options.ClipThreshold);

        Envelope.Follow(window, _envelope, _format.SampleRate);
        int peakIndex = Envelope.PeakIndex(window);

        double attackMs = Envelope.AttackMilliseconds(_envelope, peakIndex, _format.SampleRate);
        double decayMs = Envelope.DecayMilliseconds(_envelope, peakIndex, _format.SampleRate);
        double durationMs = Envelope.EffectiveDurationMilliseconds(
            _envelope, _format.SampleRate, _options.DurationFractionOfPeak);

        int half = window.Length / 2;
        double earlyEnergy = Envelope.Energy(window[..half]);
        double totalEnergy = earlyEnergy + Envelope.Energy(window[half..]);
        double earlyFraction = totalEnergy > 0 ? earlyEnergy / totalEnergy : 0.0;

        int crossings = 0;
        for (int i = 1; i < window.Length; i++)
        {
            if ((window[i - 1] < 0f) != (window[i] < 0f))
            {
                crossings++;
            }
        }

        double zcr = window.Length > 1 ? (double)crossings / (window.Length - 1) : 0.0;
        double crestDb = levels.Rms > 0 ? SignalAnalysis.ToDbfs(levels.Peak) - SignalAnalysis.ToDbfs(levels.Rms) : 0.0;

        return new TapMeasurements(
            levels.Rms,
            levels.Peak,
            crestDb,
            attackMs,
            decayMs,
            durationMs,
            earlyFraction,
            zcr,
            levels.ClippedSamples,
            SignalAnalysis.ToDbfs(levels.Peak),
            SignalAnalysis.ToDbfs(levels.Rms));
    }

    /// <summary>
    /// Gates applied in the order that costs least to evaluate and rejects most confidently.
    /// </summary>
    private RejectionReason Validate(in TapMeasurements m)
    {
        if (m.ClippedSamples > _options.MaxClippedFraction * _windowSamples)
        {
            return RejectionReason.Clipped;
        }

        if (m.PeakDbfs < _options.MinPeakDbfs)
        {
            return RejectionReason.SignalTooWeak;
        }

        if (m.PeakDbfs - SignalAnalysis.ToDbfs(_noiseFloor) < _options.MinSnrDb)
        {
            return RejectionReason.LowSignalToNoise;
        }

        if (m.CrestDb < _options.MinCrestFactorDb)
        {
            return RejectionReason.FlatDynamics;
        }

        if (m.AttackMs > _options.MaxAttackMs)
        {
            return RejectionReason.SlowAttack;
        }

        if (m.EffectiveDurationMs > _options.MaxEffectiveDurationMs)
        {
            return RejectionReason.SustainedSound;
        }

        if (m.EarlyEnergyFraction < _options.MinEarlyEnergyFraction)
        {
            return RejectionReason.LateEnergy;
        }

        return RejectionReason.None;
    }

    /// <summary>
    /// Applies <paramref name="frames"/> frames' worth of noise-floor tracking toward
    /// <paramref name="level"/> in one step.
    /// </summary>
    private void AbsorbIntoNoiseFloor(float level, int frames)
    {
        if (frames <= 0 || !float.IsFinite(level))
        {
            return;
        }

        float coefficient = level < _noiseFloor ? _fallCoefficient : _riseCoefficient;
        double compounded = 1.0 - Math.Pow(1.0 - coefficient, frames);

        _noiseFloor = (float)Math.Max(FloorEpsilon, _noiseFloor + (compounded * (level - _noiseFloor)));
    }

    /// <summary>
    /// Skips the scan position forward, without disturbing the DC blocker.
    /// </summary>
    /// <remarks>
    /// The blocker tracks a near-constant offset, so its state stays valid across a skip;
    /// resetting it instead would pass one frame of raw DC straight through and read as a
    /// large level jump - a phantom onset immediately after every event.
    /// </remarks>
    private void AdvancePast(long index)
    {
        if (index <= _position)
        {
            return;
        }

        _position = index;

        // The previous frame is no longer adjacent, so a rise comparison against it would be
        // meaningless. Fall back to the noise floor as a neutral reference.
        _previousFrameRms = _noiseFloor;
    }

    private void ResetForNewStream(AudioRingBuffer buffer, int generation)
    {
        _generation = generation;
        _position = buffer.OldestAvailableIndex;
        _pendingOnset = null;
        _lastEventOnset = null;
        _roomLearnSum = 0;
        _roomLearnFrames = 0;
        _noiseFloor = FloorEpsilon;
        _previousFrameRms = 0f;
        _dcBlocker.Reset();
    }

    private float SmoothingCoefficient(double milliseconds)
    {
        if (milliseconds <= 0)
        {
            return 1f;
        }

        double frames = milliseconds / _options.FrameMs;
        return (float)(1.0 - Math.Exp(-1.0 / Math.Max(1.0, frames)));
    }
}
