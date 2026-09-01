namespace Tapit.Core.DSP;

/// <summary>
/// First-order DC blocker: <c>y[n] = x[n] - x[n-1] + R·y[n-1]</c>.
/// </summary>
/// <remarks>
/// Phase 1 measurement: a raw, effects-bypassed WASAPI stream carries a stable DC offset
/// (about −42 dBFS on the development machine) because bypassing the APO chain also bypasses
/// its high-pass. Left in place, that offset inflates RMS, drags crest factor toward 1.0 and
/// biases every envelope feature. This runs before anything else in the detector.
/// </remarks>
public sealed class DcBlocker
{
    private readonly float _pole;
    private float _lastInput;
    private float _lastOutput;

    /// <param name="cutoffHz">−3 dB corner. 20 Hz is well below any desk resonance of interest.</param>
    /// <param name="sampleRate">Stream sample rate.</param>
    public DcBlocker(double cutoffHz, int sampleRate)
    {
        if (sampleRate <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(sampleRate), sampleRate, "Sample rate must be positive.");
        }

        _pole = (float)Math.Exp(-2.0 * Math.PI * Math.Max(0.1, cutoffHz) / sampleRate);
    }

    public float Pole => _pole;

    public void Reset()
    {
        _lastInput = 0f;
        _lastOutput = 0f;
    }

    public float Process(float sample)
    {
        float output = sample - _lastInput + (_pole * _lastOutput);
        _lastInput = sample;
        _lastOutput = output;
        return output;
    }

    public void Process(ReadOnlySpan<float> source, Span<float> destination)
    {
        int n = Math.Min(source.Length, destination.Length);
        for (int i = 0; i < n; i++)
        {
            destination[i] = Process(source[i]);
        }
    }

    /// <summary>
    /// Removes DC from an isolated block without carrying filter state, by subtracting the
    /// block mean. Used for analysis windows pulled out of the ring, where a stateful filter
    /// would depend on what happened to precede the window.
    /// </summary>
    public static void RemoveMean(Span<float> samples)
    {
        if (samples.IsEmpty)
        {
            return;
        }

        double sum = 0.0;
        for (int i = 0; i < samples.Length; i++)
        {
            sum += samples[i];
        }

        float mean = (float)(sum / samples.Length);
        if (mean == 0f)
        {
            return;
        }

        for (int i = 0; i < samples.Length; i++)
        {
            samples[i] -= mean;
        }
    }
}
