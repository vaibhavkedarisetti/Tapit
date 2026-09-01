namespace Tapit.Core.Detection;

/// <summary>Why an event was not accepted. <see cref="None"/> means it passed every gate.</summary>
public enum RejectionReason
{
    None,
    SignalTooWeak,
    LowSignalToNoise,
    Clipped,
    SlowAttack,
    SustainedSound,
    LateEnergy,
    FlatDynamics,
    WindowUnavailable,
}

public static class RejectionReasonText
{
    public static string Describe(RejectionReason reason) => reason switch
    {
        RejectionReason.None => "Accepted",
        RejectionReason.SignalTooWeak => "Signal too weak",
        RejectionReason.LowSignalToNoise => "Too close to the noise floor",
        RejectionReason.Clipped => "Clipped",
        RejectionReason.SlowAttack => "Attack too slow for an impact",
        RejectionReason.SustainedSound => "Sustained sound, not an impact",
        RejectionReason.LateEnergy => "Energy arrives too late in the window",
        RejectionReason.FlatDynamics => "Too flat - no impulsive peak",
        RejectionReason.WindowUnavailable => "Audio window was lost before analysis",
        _ => reason.ToString(),
    };
}

/// <summary>Plain measurements of a candidate window. No interpretation, just numbers.</summary>
public readonly record struct TapMeasurements(
    float Rms,
    float Peak,
    double CrestDb,
    double AttackMs,
    double DecayMs,
    double EffectiveDurationMs,
    double EarlyEnergyFraction,
    double ZeroCrossingRate,
    int ClippedSamples,
    double PeakDbfs,
    double RmsDbfs);

/// <summary>
/// A candidate impact found by the detector, accepted or rejected.
/// </summary>
/// <remarks>
/// Rejected events are still reported. Seeing <i>why</i> something was rejected is how the
/// thresholds get tuned; silently discarding them would make the detector unfalsifiable.
/// </remarks>
public sealed class TapEvent
{
    public required long OnsetSample { get; init; }

    public required long WindowStartSample { get; init; }

    /// <summary>Onset position in seconds from the start of the stream.</summary>
    public required double OnsetSeconds { get; init; }

    public required bool Accepted { get; init; }

    public required RejectionReason Rejection { get; init; }

    public required TapMeasurements Measurements { get; init; }

    /// <summary>Noise floor at the moment of detection, in dBFS.</summary>
    public required double NoiseFloorDbfs { get; init; }

    public required double SnrDb { get; init; }

    /// <summary>
    /// The analysis window itself, DC-removed. Held so the tool can save it as a WAV and so
    /// features can be recomputed offline with different parameters.
    /// </summary>
    public required float[] Window { get; init; }

    /// <summary>
    /// Per-channel analysis windows, when the device exposes more than one channel.
    /// Empty for a mono stream.
    /// </summary>
    /// <remarks>
    /// Kept because left-versus-right is the symmetry axis of a centred microphone: two taps
    /// equidistant either side of it travel near-identical paths and are close to
    /// indistinguishable once mixed to mono. Any hope of separating them lives in the
    /// difference *between* channels, which the mixdown destroys.
    /// </remarks>
    public float[][] ChannelWindows { get; init; } = [];

    public required int SampleRate { get; init; }

    /// <summary>Time from the acoustic onset to this event being produced, where known.</summary>
    public double DetectionLatencyMs { get; init; } = double.NaN;

    public string Summary =>
        $"{OnsetSeconds,7:0.000}s  {(Accepted ? "ACCEPT" : "reject")}  " +
        $"peak {Measurements.PeakDbfs,6:0.0} dBFS  snr {SnrDb,5:0.0} dB  " +
        $"attack {Measurements.AttackMs,5:0.00} ms  dur {Measurements.EffectiveDurationMs,5:0.0} ms  " +
        $"early {Measurements.EarlyEnergyFraction,4:0.00}" +
        (Accepted ? string.Empty : $"  - {RejectionReasonText.Describe(Rejection)}");
}
