namespace Tapit.Core.Features;

/// <summary>Inter-channel measurements for one event.</summary>
public readonly record struct InterChannelCues(
    double LevelDifferenceDb,
    double LagSamples,
    double LagMicroseconds,
    double Correlation,
    double PeakCorrelation)
{
    /// <summary>
    /// True when the two channels look like the same signal duplicated or matrixed - a
    /// near-perfect correlation at zero lag with no level difference.
    /// </summary>
    /// <remarks>
    /// Windows commonly presents a "stereo" microphone that is really one beamformed signal
    /// copied to both channels. Spatial features computed from such a pair are pure noise,
    /// so this has to be measured rather than assumed.
    /// </remarks>
    public bool LooksDegenerate =>
        PeakCorrelation > 0.999 && Math.Abs(LagSamples) < 0.5 && Math.Abs(LevelDifferenceDb) < 0.2;
}

/// <summary>
/// Spatial cues between two microphone channels.
/// </summary>
/// <remarks>
/// <para>
/// Left-versus-right is the symmetry axis of a centred microphone: two taps equidistant
/// either side of it travel near-identical path lengths and produce near-identical mono
/// spectra. What does differ is which channel the sound reaches first and loudest. For a
/// laptop array with elements ~10 cm apart, a tap well off to one side gives up to ~290 µs
/// of delay - around 14 samples at 48 kHz, which is comfortably measurable.
/// </para>
/// <para>
/// The cues are computed over the attack region only. The direct arrival carries the
/// direction; the later ring is diffuse, reflected, and dominated by the surface's own
/// resonance, which is the same whichever side was struck.
/// </para>
/// </remarks>
public static class InterChannel
{
    /// <summary>Widest delay searched, in samples. ~1.3 ms at 48 kHz covers any laptop array.</summary>
    public const int MaxLagSamples = 64;

    /// <summary>
    /// Measures level difference and arrival delay between two channel windows.
    /// </summary>
    /// <param name="left">First channel.</param>
    /// <param name="right">Second channel.</param>
    /// <param name="sampleRate">Stream sample rate.</param>
    /// <param name="analysisSamples">
    /// Number of samples from the start of the window to analyse. Zero uses the whole window.
    /// </param>
    public static InterChannelCues Measure(
        ReadOnlySpan<float> left,
        ReadOnlySpan<float> right,
        int sampleRate,
        int analysisSamples = 0)
    {
        int length = Math.Min(left.Length, right.Length);
        if (analysisSamples > 0)
        {
            length = Math.Min(length, analysisSamples);
        }

        if (length < 8)
        {
            return default;
        }

        left = left[..length];
        right = right[..length];

        double leftEnergy = 0.0;
        double rightEnergy = 0.0;
        for (int i = 0; i < length; i++)
        {
            leftEnergy += (double)left[i] * left[i];
            rightEnergy += (double)right[i] * right[i];
        }

        double levelDb = leftEnergy > 0 && rightEnergy > 0
            ? 10.0 * Math.Log10(leftEnergy / rightEnergy)
            : 0.0;

        double zeroLag = NormalisedCorrelation(left, right, 0, leftEnergy, rightEnergy);

        int maxLag = Math.Min(MaxLagSamples, length / 4);
        double bestCorrelation = double.NegativeInfinity;
        int bestLag = 0;

        for (int lag = -maxLag; lag <= maxLag; lag++)
        {
            double correlation = NormalisedCorrelation(left, right, lag, leftEnergy, rightEnergy);
            if (correlation > bestCorrelation)
            {
                bestCorrelation = correlation;
                bestLag = lag;
            }
        }

        // Parabolic interpolation around the peak: the true delay rarely lands exactly on a
        // sample, and sub-sample resolution matters when the whole usable range is ~14
        // samples wide.
        double refinedLag = bestLag;
        if (bestLag > -maxLag && bestLag < maxLag)
        {
            double before = NormalisedCorrelation(left, right, bestLag - 1, leftEnergy, rightEnergy);
            double after = NormalisedCorrelation(left, right, bestLag + 1, leftEnergy, rightEnergy);
            double denominator = before - (2 * bestCorrelation) + after;

            if (Math.Abs(denominator) > 1e-12)
            {
                double offset = 0.5 * (before - after) / denominator;
                if (Math.Abs(offset) <= 1.0)
                {
                    refinedLag = bestLag + offset;
                }
            }
        }

        return new InterChannelCues(
            levelDb,
            refinedLag,
            refinedLag * 1_000_000.0 / sampleRate,
            zeroLag,
            bestCorrelation);
    }

    private static double NormalisedCorrelation(
        ReadOnlySpan<float> left, ReadOnlySpan<float> right, int lag, double leftEnergy, double rightEnergy)
    {
        if (leftEnergy <= 0 || rightEnergy <= 0)
        {
            return 0.0;
        }

        double sum = 0.0;
        int start = Math.Max(0, -lag);
        int end = Math.Min(left.Length, right.Length - lag);

        for (int i = start; i < end; i++)
        {
            sum += (double)left[i] * right[i + lag];
        }

        return sum / Math.Sqrt(leftEnergy * rightEnergy);
    }
}
