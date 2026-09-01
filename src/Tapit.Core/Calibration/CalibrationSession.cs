using Tapit.Core.Classification;
using Tapit.Core.Detection;
using Tapit.Core.Evaluation;

namespace Tapit.Core.Calibration;

public enum CalibrationState
{
    NotStarted,
    Collecting,
    Paused,
    Complete,
    Cancelled,
}

/// <summary>What the session did with an event it was offered.</summary>
public enum CalibrationOutcome
{
    /// <summary>Counted toward the current zone.</summary>
    Accepted,

    /// <summary>The detector rejected it; the reason is passed through to the user.</summary>
    RejectedByDetector,

    /// <summary>Features could not be computed - non-finite values.</summary>
    BadFeatures,

    /// <summary>The session was not armed. Deliberately not counted.</summary>
    NotArmed,
}

public sealed record CalibrationFeedback(
    CalibrationOutcome Outcome,
    Zone Zone,
    int Accepted,
    int Required,
    string Message)
{
    public bool Counted => Outcome == CalibrationOutcome.Accepted;

    public double Progress => Required > 0 ? (double)Accepted / Required : 0.0;
}

/// <summary>
/// Guided four-zone calibration.
/// </summary>
/// <remarks>
/// <para>
/// Collects a fixed number of <i>accepted</i> taps per zone, one zone at a time. Only events
/// arriving while the session is armed are counted, so a cough between prompts, or a tap
/// while the user is reading the next instruction, cannot silently become training data.
/// That is the difference between a calibration set and a pile of audio.
/// </para>
/// <para>
/// This class holds no audio and does no DSP. It is a state machine over feature vectors,
/// which makes it trivially testable and keeps it independent of both the UI and the
/// capture stack.
/// </para>
/// </remarks>
public sealed class CalibrationSession(int samplesPerZone = 10, IReadOnlyList<Zone>? order = null)
{
    private readonly List<LabeledSample> _samples = [];
    private readonly List<float[]> _negatives = [];
    private readonly Zone[] _order = order is { Count: > 0 } ? [.. order] : Zones.All;

    private int _zoneIndex;

    public int SamplesPerZone { get; } = samplesPerZone > 0
        ? samplesPerZone
        : throw new ArgumentOutOfRangeException(nameof(samplesPerZone));

    public CalibrationState State { get; private set; } = CalibrationState.NotStarted;

    /// <summary>The zone the user is being asked to tap, or null when not collecting.</summary>
    public Zone? CurrentZone =>
        State == CalibrationState.Collecting && _zoneIndex < _order.Length ? _order[_zoneIndex] : null;

    public IReadOnlyList<Zone> Order => _order;

    public IReadOnlyList<LabeledSample> Samples => _samples;

    public IReadOnlyList<float[]> Negatives => _negatives;

    public int TotalRequired => SamplesPerZone * _order.Length;

    public int TotalAccepted => _samples.Count;

    public double Progress => TotalRequired > 0 ? (double)TotalAccepted / TotalRequired : 0.0;

    /// <summary>Armed means events offered to <see cref="Offer"/> will be counted.</summary>
    public bool IsArmed => State == CalibrationState.Collecting;

    public int AcceptedFor(Zone zone) => _samples.Count(s => s.Zone == zone);

    public void Start()
    {
        _samples.Clear();
        _negatives.Clear();
        _zoneIndex = 0;
        State = CalibrationState.Collecting;
    }

    public void Pause()
    {
        if (State == CalibrationState.Collecting)
        {
            State = CalibrationState.Paused;
        }
    }

    public void Resume()
    {
        if (State == CalibrationState.Paused)
        {
            State = CalibrationState.Collecting;
        }
    }

    public void Cancel() => State = CalibrationState.Cancelled;

    /// <summary>Removes the most recent accepted sample, stepping back a zone if needed.</summary>
    public bool Undo()
    {
        if (_samples.Count == 0)
        {
            return false;
        }

        LabeledSample last = _samples[^1];
        _samples.RemoveAt(_samples.Count - 1);

        int index = Array.IndexOf(_order, last.Zone);
        if (index >= 0)
        {
            _zoneIndex = index;
        }

        if (State is CalibrationState.Complete or CalibrationState.Cancelled)
        {
            State = CalibrationState.Collecting;
        }

        return true;
    }

    /// <summary>Discards every sample for the current zone and collects it again.</summary>
    public bool RetryZone()
    {
        if (CurrentZone is not Zone zone)
        {
            return false;
        }

        _samples.RemoveAll(s => s.Zone == zone);
        return true;
    }

    public bool RetryZone(Zone zone)
    {
        int removed = _samples.RemoveAll(s => s.Zone == zone);

        int index = Array.IndexOf(_order, zone);
        if (index >= 0)
        {
            _zoneIndex = index;
        }

        if (State == CalibrationState.Complete)
        {
            State = CalibrationState.Collecting;
        }

        return removed > 0;
    }

    /// <summary>
    /// Offers a detected event to the session.
    /// </summary>
    /// <param name="tapEvent">The detector's verdict, including its rejection reason.</param>
    /// <param name="features">Feature vector, or null when extraction failed.</param>
    public CalibrationFeedback Offer(TapEvent tapEvent, float[]? features)
    {
        ArgumentNullException.ThrowIfNull(tapEvent);

        if (CurrentZone is not Zone zone)
        {
            return new CalibrationFeedback(
                CalibrationOutcome.NotArmed, default, 0, SamplesPerZone,
                "Not collecting - this sound was ignored.");
        }

        if (!tapEvent.Accepted)
        {
            return new CalibrationFeedback(
                CalibrationOutcome.RejectedByDetector, zone, AcceptedFor(zone), SamplesPerZone,
                RejectionReasonText.Describe(tapEvent.Rejection));
        }

        if (features is null || features.Any(f => !float.IsFinite(f)))
        {
            return new CalibrationFeedback(
                CalibrationOutcome.BadFeatures, zone, AcceptedFor(zone), SamplesPerZone,
                "Could not measure that tap.");
        }

        _samples.Add(new LabeledSample(zone, features));
        int accepted = AcceptedFor(zone);

        if (accepted >= SamplesPerZone)
        {
            AdvanceZone();
        }

        return new CalibrationFeedback(
            CalibrationOutcome.Accepted, zone, accepted, SamplesPerZone,
            accepted >= SamplesPerZone ? $"{Zones.DisplayName(zone)} done." : "Good.");
    }

    /// <summary>Records an optional non-tap example used to improve rejection.</summary>
    public void AddNegative(float[] features)
    {
        if (features is not null && features.All(float.IsFinite))
        {
            _negatives.Add(features);
        }
    }

    private void AdvanceZone()
    {
        while (_zoneIndex < _order.Length && AcceptedFor(_order[_zoneIndex]) >= SamplesPerZone)
        {
            _zoneIndex++;
        }

        if (_zoneIndex >= _order.Length)
        {
            State = CalibrationState.Complete;
        }
    }

    /// <summary>Runs the post-calibration diagnostic. Valid only once collection is complete.</summary>
    public CalibrationReport BuildReport()
    {
        IReadOnlyList<ClassifierComparison> comparison = CrossValidation.Compare(_samples);
        ClassifierComparison best = comparison[0];

        return new CalibrationReport(
            _samples.Count,
            SamplesPerZone,
            best.Name,
            best.Agreement,
            best.Matrix,
            comparison,
            best.Matrix.WeakZones());
    }
}

/// <summary>
/// Post-calibration diagnostic.
/// </summary>
/// <remarks>
/// <see cref="Agreement"/> is leave-one-out agreement over the calibration set. It measures
/// whether the samples are self-consistent. It is <b>not</b> real-world accuracy, it does not
/// prove the zones are physically separable, and it must be presented to the user in those
/// words. Only a held-out evaluation on the real desk says anything about accuracy.
/// </remarks>
public sealed record CalibrationReport(
    int SampleCount,
    int SamplesPerZone,
    string BestClassifier,
    double Agreement,
    ConfusionMatrix Matrix,
    IReadOnlyList<ClassifierComparison> Comparison,
    IReadOnlyList<Zone> WeakZones)
{
    /// <summary>Agreement below this suggests the zones are not cleanly separable here.</summary>
    public const double RecalibrationThreshold = 0.75;

    public bool RecommendRecalibration => Agreement < RecalibrationThreshold || WeakZones.Count > 0;

    public string Advice
    {
        get
        {
            if (Agreement < 0.5)
            {
                return "The zones are not separable on this surface. A harder, more rigid desk, " +
                       "zones further apart, or a laptop position closer to the taps may help.";
            }

            if (WeakZones.Count > 0)
            {
                return $"Weak {(WeakZones.Count == 1 ? "zone" : "zones")}: " +
                       $"{string.Join(", ", WeakZones.Select(Zones.DisplayName))}. " +
                       "Recalibrating those with more consistent taps may help.";
            }

            return RecommendRecalibration
                ? "Calibration is usable but not strong. Consider recalibrating."
                : "Calibration samples are self-consistent. This is a diagnostic only - " +
                  "run an evaluation to measure real accuracy.";
        }
    }
}
