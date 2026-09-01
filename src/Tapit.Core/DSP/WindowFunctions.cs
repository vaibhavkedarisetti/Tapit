namespace Tapit.Core.DSP;

public enum WindowType
{
    Rectangular,
    Hann,
}

/// <summary>
/// Analysis windows, precomputed once per size.
/// </summary>
/// <remarks>
/// Tapit windows the event before its single FFT. Hann is the default: a desk impulse has a
/// steep onset and the sidelobe leakage of a rectangular window would smear that energy
/// across the whole spectrum, contaminating exactly the band structure the classifier uses
/// to tell zones apart.
/// </remarks>
public static class WindowFunctions
{
    public static float[] Create(WindowType type, int length)
    {
        if (length <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(length), length, "Window length must be positive.");
        }

        var window = new float[length];
        Fill(type, window);
        return window;
    }

    public static void Fill(WindowType type, Span<float> window)
    {
        int n = window.Length;
        if (n == 0)
        {
            return;
        }

        if (n == 1)
        {
            window[0] = 1f;
            return;
        }

        double denominator = n - 1;

        for (int i = 0; i < n; i++)
        {
            double x = i / denominator;
            window[i] = type switch
            {
                WindowType.Rectangular => 1f,
                WindowType.Hann => (float)(0.5 - (0.5 * Math.Cos(2.0 * Math.PI * x))),
                _ => throw new ArgumentOutOfRangeException(nameof(type), type, "Unknown window type."),
            };
        }
    }

    /// <summary>Multiplies <paramref name="samples"/> by <paramref name="window"/> in place.</summary>
    public static void Apply(Span<float> samples, ReadOnlySpan<float> window)
    {
        int n = Math.Min(samples.Length, window.Length);
        for (int i = 0; i < n; i++)
        {
            samples[i] *= window[i];
        }
    }

    /// <summary>
    /// Coherent gain - the mean of the window. Dividing by it restores amplitude after
    /// windowing so RMS-style features stay comparable between window types.
    /// </summary>
    public static double CoherentGain(ReadOnlySpan<float> window)
    {
        if (window.IsEmpty)
        {
            return 1.0;
        }

        double sum = 0.0;
        for (int i = 0; i < window.Length; i++)
        {
            sum += window[i];
        }

        return sum / window.Length;
    }
}
