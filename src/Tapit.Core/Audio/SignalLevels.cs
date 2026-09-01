namespace Tapit.Core.Audio;

/// <summary>
/// Level measurement over a block of samples.
/// </summary>
public readonly record struct SignalLevels(
    float Rms,
    float Peak,
    int ClippedSamples,
    int SampleCount,
    float Mean = 0f)
{
    public static SignalLevels Empty => new(0f, 0f, 0, 0);

    /// <summary>
    /// DC offset. Worth watching: a raw, effects-bypassed capture stream has no high-pass
    /// filter in front of it, so many endpoints deliver a substantial constant bias that
    /// inflates RMS, distorts crest factor, and would poison every amplitude feature if it
    /// were not removed before analysis.
    /// </summary>
    public float DcOffset => Mean;

    /// <summary>RMS with the DC component removed - the number that describes actual sound.</summary>
    public float AcRms
    {
        get
        {
            double variance = (double)Rms * Rms - (double)Mean * Mean;
            return variance > 0 ? (float)Math.Sqrt(variance) : 0f;
        }
    }

    public double RmsDbfs => SignalAnalysis.ToDbfs(Rms);

    public double PeakDbfs => SignalAnalysis.ToDbfs(Peak);

    /// <summary>Crest factor (peak / RMS), the classic "is this a transient" ratio.</summary>
    public double CrestFactor => Rms > 0f ? Peak / Rms : 0.0;

    public double ClippedFraction => SampleCount > 0 ? (double)ClippedSamples / SampleCount : 0.0;
}

/// <summary>
/// Basic block statistics, deliberately kept off the capture thread: the ring buffer holds
/// everything, so the consumer can measure the same samples without stealing realtime budget
/// from the producer.
/// </summary>
public static class SignalAnalysis
{
    /// <summary>Amplitude at or above which a sample is treated as clipped.</summary>
    public const float DefaultClipThreshold = 0.999f;

    /// <summary>Floor for dBFS reporting, so silence yields a finite number.</summary>
    public const double MinimumDbfs = -120.0;

    public static SignalLevels Measure(ReadOnlySpan<float> samples, float clipThreshold = DefaultClipThreshold)
    {
        if (samples.IsEmpty)
        {
            return SignalLevels.Empty;
        }

        double sumOfSquares = 0.0;
        double sum = 0.0;
        float peak = 0f;
        int clipped = 0;

        for (int i = 0; i < samples.Length; i++)
        {
            float sample = samples[i];

            // A non-finite sample means the input path is broken. Report it as clipping
            // rather than propagating NaN into every downstream statistic.
            if (!float.IsFinite(sample))
            {
                clipped++;
                continue;
            }

            float magnitude = Math.Abs(sample);
            sumOfSquares += (double)sample * sample;
            sum += sample;

            if (magnitude > peak)
            {
                peak = magnitude;
            }

            if (magnitude >= clipThreshold)
            {
                clipped++;
            }
        }

        float rms = (float)Math.Sqrt(sumOfSquares / samples.Length);
        float mean = (float)(sum / samples.Length);
        return new SignalLevels(rms, peak, clipped, samples.Length, mean);
    }

    public static double ToDbfs(double amplitude)
    {
        if (amplitude <= 0.0 || !double.IsFinite(amplitude))
        {
            return MinimumDbfs;
        }

        double db = 20.0 * Math.Log10(amplitude);
        return db < MinimumDbfs ? MinimumDbfs : db;
    }

    public static double FromDbfs(double dbfs) => Math.Pow(10.0, dbfs / 20.0);
}
