namespace Tapit.Core.Classification;

/// <summary>The four desk zones.</summary>
public enum Zone
{
    LeftRear = 0,
    LeftFront = 1,
    RightRear = 2,
    RightFront = 3,
}

public static class Zones
{
    /// <summary>Canonical order. Used for score vectors, confusion matrices and persistence.</summary>
    public static readonly Zone[] All =
    [
        Zone.LeftRear,
        Zone.LeftFront,
        Zone.RightRear,
        Zone.RightFront,
    ];

    public const int Count = 4;

    public static string DisplayName(Zone zone) => zone switch
    {
        Zone.LeftRear => "LEFT REAR",
        Zone.LeftFront => "LEFT FRONT",
        Zone.RightRear => "RIGHT REAR",
        Zone.RightFront => "RIGHT FRONT",
        _ => zone.ToString(),
    };

    public static int IndexOf(Zone zone) => (int)zone;

    public static Zone FromIndex(int index) =>
        index >= 0 && index < Count
            ? All[index]
            : throw new ArgumentOutOfRangeException(nameof(index), index, "Zone index out of range.");
}

/// <summary>A calibration or evaluation example: a feature vector with a known zone.</summary>
public sealed class LabeledSample(Zone zone, float[] features)
{
    public Zone Zone { get; } = zone;

    public float[] Features { get; } = features ?? throw new ArgumentNullException(nameof(features));

    /// <summary>When the sample was collected. Kept so a profile can show its own age.</summary>
    public DateTimeOffset CollectedAt { get; init; } = DateTimeOffset.Now;
}

/// <summary>
/// Per-feature standardisation (z-score).
/// </summary>
/// <remarks>
/// Distances and linear weights are meaningless when one feature is measured in hertz
/// (thousands) and another is a fraction (0 to 1) - the hertz feature would dominate every
/// distance regardless of how informative it is. The statistics are computed once from the
/// calibration set and stored in the profile, so scaling at inference is identical to
/// scaling at training.
/// </remarks>
public sealed class FeatureScaler
{
    public FeatureScaler(float[] mean, float[] scale)
    {
        ArgumentNullException.ThrowIfNull(mean);
        ArgumentNullException.ThrowIfNull(scale);

        if (mean.Length != scale.Length)
        {
            throw new ArgumentException("Mean and scale must be the same length.", nameof(scale));
        }

        Mean = mean;
        Scale = scale;
    }

    public float[] Mean { get; }

    public float[] Scale { get; }

    public int Dimension => Mean.Length;

    public static FeatureScaler Fit(IReadOnlyList<LabeledSample> samples)
    {
        ArgumentNullException.ThrowIfNull(samples);

        if (samples.Count == 0)
        {
            throw new ArgumentException("Cannot fit a scaler to zero samples.", nameof(samples));
        }

        int dimension = samples[0].Features.Length;
        var mean = new float[dimension];
        var scale = new float[dimension];

        for (int d = 0; d < dimension; d++)
        {
            double sum = 0.0;
            for (int i = 0; i < samples.Count; i++)
            {
                sum += samples[i].Features[d];
            }

            double m = sum / samples.Count;

            double variance = 0.0;
            for (int i = 0; i < samples.Count; i++)
            {
                double delta = samples[i].Features[d] - m;
                variance += delta * delta;
            }

            variance /= samples.Count;

            mean[d] = (float)m;

            // A constant feature carries no information; a scale of 1 makes it contribute
            // exactly zero after centring instead of exploding to infinity.
            double deviation = Math.Sqrt(variance);
            scale[d] = deviation > 1e-9 ? (float)deviation : 1f;
        }

        return new FeatureScaler(mean, scale);
    }

    public void Transform(ReadOnlySpan<float> features, Span<float> destination)
    {
        if (features.Length != Dimension || destination.Length < Dimension)
        {
            throw new ArgumentException($"Expected {Dimension} features.", nameof(features));
        }

        for (int d = 0; d < Dimension; d++)
        {
            destination[d] = (features[d] - Mean[d]) / Scale[d];
        }
    }

    public float[] Transform(ReadOnlySpan<float> features)
    {
        var result = new float[Dimension];
        Transform(features, result);
        return result;
    }

    public IReadOnlyList<LabeledSample> Transform(IReadOnlyList<LabeledSample> samples) =>
        samples.Select(s => new LabeledSample(s.Zone, Transform(s.Features)) { CollectedAt = s.CollectedAt })
            .ToList();
}
