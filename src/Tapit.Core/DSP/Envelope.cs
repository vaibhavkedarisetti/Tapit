namespace Tapit.Core.DSP;

/// <summary>
/// Amplitude-envelope helpers used by both the detector and the feature extractor.
/// </summary>
public static class Envelope
{
    /// <summary>
    /// Rectified one-pole envelope follower with separate attack and release. Fast attack
    /// preserves the leading edge of an impact - which is the most location-sensitive part
    /// of the event - while a slower release traces the decay.
    /// </summary>
    public static void Follow(
        ReadOnlySpan<float> samples,
        Span<float> envelope,
        int sampleRate,
        double attackMs = 0.5,
        double releaseMs = 12.0)
    {
        int n = Math.Min(samples.Length, envelope.Length);
        if (n == 0)
        {
            return;
        }

        float attack = Coefficient(attackMs, sampleRate);
        float release = Coefficient(releaseMs, sampleRate);
        float state = 0f;

        for (int i = 0; i < n; i++)
        {
            float magnitude = Math.Abs(samples[i]);
            float coefficient = magnitude > state ? attack : release;
            state += coefficient * (magnitude - state);
            envelope[i] = state;
        }
    }

    private static float Coefficient(double milliseconds, int sampleRate)
    {
        if (milliseconds <= 0)
        {
            return 1f;
        }

        double samples = milliseconds * sampleRate / 1000.0;
        return (float)(1.0 - Math.Exp(-1.0 / Math.Max(1.0, samples)));
    }

    /// <summary>Index of the largest absolute sample.</summary>
    public static int PeakIndex(ReadOnlySpan<float> samples)
    {
        int index = 0;
        float peak = -1f;

        for (int i = 0; i < samples.Length; i++)
        {
            float magnitude = Math.Abs(samples[i]);
            if (magnitude > peak)
            {
                peak = magnitude;
                index = i;
            }
        }

        return index;
    }

    /// <summary>
    /// Time from the 10 % crossing to the 90 % crossing of the peak, in milliseconds.
    /// A desk impact rises in well under a millisecond; speech and mechanical noise do not.
    /// </summary>
    public static double AttackMilliseconds(ReadOnlySpan<float> envelope, int peakIndex, int sampleRate)
    {
        if (envelope.IsEmpty || peakIndex <= 0)
        {
            return 0.0;
        }

        float peak = envelope[peakIndex];
        if (peak <= 0f)
        {
            return 0.0;
        }

        float low = 0.1f * peak;
        float high = 0.9f * peak;

        int lowIndex = 0;
        for (int i = peakIndex; i >= 0; i--)
        {
            if (envelope[i] <= low)
            {
                lowIndex = i;
                break;
            }
        }

        int highIndex = peakIndex;
        for (int i = lowIndex; i <= peakIndex; i++)
        {
            if (envelope[i] >= high)
            {
                highIndex = i;
                break;
            }
        }

        return Math.Max(0, highIndex - lowIndex) * 1000.0 / sampleRate;
    }

    /// <summary>
    /// Time from the peak until the envelope falls <paramref name="dropDb"/> below it. When
    /// it never does inside the window, the full remaining window length is returned - the
    /// caller can compare that against the window to recognise a sustained sound.
    /// </summary>
    public static double DecayMilliseconds(
        ReadOnlySpan<float> envelope, int peakIndex, int sampleRate, double dropDb = 20.0)
    {
        if (envelope.IsEmpty || peakIndex >= envelope.Length - 1)
        {
            return 0.0;
        }

        float peak = envelope[peakIndex];
        if (peak <= 0f)
        {
            return 0.0;
        }

        float threshold = (float)(peak * Math.Pow(10.0, -dropDb / 20.0));

        for (int i = peakIndex; i < envelope.Length; i++)
        {
            if (envelope[i] <= threshold)
            {
                return (i - peakIndex) * 1000.0 / sampleRate;
            }
        }

        return (envelope.Length - 1 - peakIndex) * 1000.0 / sampleRate;
    }

    /// <summary>
    /// Effective duration: the span, in milliseconds, over which the envelope stays above a
    /// fraction of its peak. A short impulse is compact here; a fan or a vowel is not.
    /// </summary>
    public static double EffectiveDurationMilliseconds(
        ReadOnlySpan<float> envelope, int sampleRate, double fractionOfPeak = 0.1)
    {
        if (envelope.IsEmpty)
        {
            return 0.0;
        }

        float peak = 0f;
        for (int i = 0; i < envelope.Length; i++)
        {
            if (envelope[i] > peak)
            {
                peak = envelope[i];
            }
        }

        if (peak <= 0f)
        {
            return 0.0;
        }

        float threshold = (float)(peak * fractionOfPeak);
        int count = 0;
        for (int i = 0; i < envelope.Length; i++)
        {
            if (envelope[i] >= threshold)
            {
                count++;
            }
        }

        return count * 1000.0 / sampleRate;
    }

    /// <summary>
    /// Energy-weighted mean time, in milliseconds from the window start. Impacts put their
    /// mass at the front; sustained sounds spread it out.
    /// </summary>
    public static double TemporalCentroidMilliseconds(ReadOnlySpan<float> samples, int sampleRate)
    {
        double weighted = 0.0;
        double total = 0.0;

        for (int i = 0; i < samples.Length; i++)
        {
            double energy = (double)samples[i] * samples[i];
            weighted += energy * i;
            total += energy;
        }

        return total > 0 ? weighted / total * 1000.0 / sampleRate : 0.0;
    }

    /// <summary>Sum of squares over a span.</summary>
    public static double Energy(ReadOnlySpan<float> samples)
    {
        double total = 0.0;
        for (int i = 0; i < samples.Length; i++)
        {
            total += (double)samples[i] * samples[i];
        }

        return total;
    }

    /// <summary>
    /// Fraction of total energy in each of <paramref name="segments"/> equal-length slices.
    /// This is the temporal energy distribution the impact validator and the classifier use.
    /// </summary>
    public static void EnergyDistribution(ReadOnlySpan<float> samples, Span<double> segments)
    {
        int count = segments.Length;
        if (count == 0 || samples.IsEmpty)
        {
            segments.Clear();
            return;
        }

        double total = 0.0;
        for (int s = 0; s < count; s++)
        {
            int start = (int)((long)s * samples.Length / count);
            int end = (int)((long)(s + 1) * samples.Length / count);
            double energy = Energy(samples[start..end]);
            segments[s] = energy;
            total += energy;
        }

        if (total <= 0)
        {
            segments.Clear();
            return;
        }

        for (int s = 0; s < count; s++)
        {
            segments[s] /= total;
        }
    }
}
