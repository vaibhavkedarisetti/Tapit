using Tapit.Core.Audio;
using Tapit.Core.Classification;
using Tapit.Core.Detection;
using Tapit.Core.Features;

namespace Tapit.Core;

/// <summary>A fully processed tap: what was heard, measured, and decided.</summary>
public sealed record TapResult(
    TapEvent Event,
    float[]? Features,
    ZoneDecision Decision,
    double LatencyMs)
{
    /// <summary>True only when the detector accepted it <i>and</i> the zone model accepted it.</summary>
    public bool Accepted => Event.Accepted && Decision.Accepted;

    public Zone? Zone => Decision.Zone;

    /// <summary>The reason, whichever stage refused it.</summary>
    public string Explanation => !Event.Accepted
        ? RejectionReasonText.Describe(Event.Rejection)
        : ZoneRejectionText.Describe(Decision.Rejection);
}

/// <summary>
/// The processing pipeline: capture to decision.
/// </summary>
/// <remarks>
/// <para>
/// Owns the DSP thread. It pulls from the capture source's ring buffer, runs detection,
/// extracts features, and - when a trained model is present - classifies and applies the
/// rejection stack. It then raises <see cref="TapProcessed"/> and stops. It knows nothing
/// about actions, profiles or the UI; the host decides what an accepted result means.
/// </para>
/// <para>
/// Without a model it still runs, reporting detections with no zone. That is the mode
/// calibration and detector tuning use.
/// </para>
/// </remarks>
public sealed class TapitEngine : IDisposable
{
    private readonly IAudioCaptureSource _source;
    private readonly DetectorOptions _detectorOptions;
    private readonly object _gate = new();

    private TapDetector? _detector;
    private TapFeatureExtractor? _extractor;
    private ZoneModel? _model;
    private Thread? _worker;
    private volatile bool _running;
    private volatile bool _paused;
    private bool _disposed;

    public TapitEngine(IAudioCaptureSource source, DetectorOptions? detectorOptions = null, ZoneModel? model = null)
    {
        _source = source ?? throw new ArgumentNullException(nameof(source));
        _detectorOptions = (detectorOptions ?? new DetectorOptions()).Clone();
        _model = model;
    }

    /// <summary>Raised on the DSP thread for every candidate, accepted or not.</summary>
    public event EventHandler<TapResult>? TapProcessed;

    /// <summary>Raised when the capture source changes state.</summary>
    public event EventHandler<CaptureStateChangedEventArgs>? CaptureStateChanged;

    public IAudioCaptureSource Source => _source;

    public AudioFormat? Format => _source.Format;

    public bool IsRunning => _running;

    /// <summary>Paused means the pipeline ignores audio but the stream stays open.</summary>
    public bool IsPaused
    {
        get => _paused;
        set => _paused = value;
    }

    public double NoiseFloorDbfs => _detector?.NoiseFloorDbfs ?? double.NaN;

    public double LevelDbfs => _detector?.LastFrameDbfs ?? double.NaN;

    public bool IsLearningRoom => _detector?.IsLearningRoom ?? false;

    public double RoomLearnProgress => _detector?.RoomLearnProgress ?? 0.0;

    public long CandidateCount => _detector?.CandidateCount ?? 0;

    public long AcceptedCount => _detector?.AcceptedCount ?? 0;

    public TapDetector? Detector => _detector;

    public TapFeatureExtractor? FeatureExtractor => _extractor;

    /// <summary>Swaps the trained model. Safe while running.</summary>
    public ZoneModel? Model
    {
        get => _model;
        set => _model = value;
    }

    public void Start()
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);

            if (_running)
            {
                return;
            }

            _source.StateChanged += OnCaptureStateChanged;
            _source.Start();

            // The detector is sized from the negotiated format, so it cannot be built until
            // the stream is actually open.
            WaitForFormat();

            AudioFormat format = _source.Format
                                 ?? throw new InvalidOperationException("Capture did not produce a format.");

            _detector = new TapDetector(format, _detectorOptions);
            _extractor = new TapFeatureExtractor(format.SampleRate, _detector.WindowSamples);

            _running = true;
            _worker = new Thread(Run)
            {
                Name = "Tapit DSP",
                IsBackground = true,
                Priority = ThreadPriority.AboveNormal,
            };

            _worker.Start();
        }
    }

    private void WaitForFormat()
    {
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (_source.Format is null && DateTime.UtcNow < deadline)
        {
            if (_source.State == CaptureState.Faulted)
            {
                throw new InvalidOperationException("The capture device could not be opened.");
            }

            Thread.Sleep(20);
        }
    }

    public void Stop()
    {
        Thread? worker;

        lock (_gate)
        {
            if (!_running)
            {
                return;
            }

            _running = false;
            worker = _worker;
            _worker = null;
        }

        worker?.Join(3000);

        _source.StateChanged -= OnCaptureStateChanged;
        _source.Stop();
    }

    private void Run()
    {
        while (_running)
        {
            if (!_source.WaitForData(50))
            {
                continue;
            }

            AudioRingBuffer? buffer = _source.Buffer;
            TapDetector? detector = _detector;

            if (buffer is null || detector is null)
            {
                continue;
            }

            IReadOnlyList<TapEvent> events = detector.Process(buffer, _source.StreamGeneration);

            if (_paused || events.Count == 0)
            {
                continue;
            }

            foreach (TapEvent tapEvent in events)
            {
                TapProcessed?.Invoke(this, Evaluate(tapEvent));
            }
        }
    }

    private TapResult Evaluate(TapEvent tapEvent)
    {
        // Latency is measured from the acoustic onset, using the capture clock, not from
        // when this thread happened to wake up.
        double latencyMs = _source.Clock?.AgeMilliseconds(tapEvent.OnsetSample) ?? double.NaN;

        if (!tapEvent.Accepted)
        {
            return new TapResult(tapEvent, null, ZoneDecision.Reject(ZoneRejection.NotTrained), latencyMs);
        }

        float[]? features = _extractor?.Extract(tapEvent);

        if (features is null)
        {
            return new TapResult(tapEvent, null, ZoneDecision.Reject(ZoneRejection.NotTrained), latencyMs);
        }

        ZoneModel? model = _model;
        ZoneDecision decision = model is null
            ? ZoneDecision.Reject(ZoneRejection.NotTrained)
            : model.Predict(features);

        return new TapResult(tapEvent, features, decision, latencyMs);
    }

    private void OnCaptureStateChanged(object? sender, CaptureStateChangedEventArgs e) =>
        CaptureStateChanged?.Invoke(this, e);

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        Stop();
    }
}
