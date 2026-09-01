namespace Tapit.Core.DSP;

/// <summary>
/// In-place iterative radix-2 Cooley-Tukey FFT.
/// </summary>
/// <remarks>
/// Deterministic, allocation-free once the twiddle tables are built, and dependency-free.
/// A 90 ms window at 48 kHz is 4320 samples, so transforms are 8192-point - small enough
/// that a straightforward radix-2 implementation is comfortably inside the DSP budget and
/// far easier to verify than anything clever.
/// </remarks>
public static class Fft
{
    /// <summary>Rounds up to the next power of two, which is the only size this transform accepts.</summary>
    public static int NextPowerOfTwo(int value)
    {
        if (value <= 1)
        {
            return 1;
        }

        int result = 1;
        while (result < value)
        {
            result <<= 1;
        }

        return result;
    }

    public static bool IsPowerOfTwo(int value) => value > 0 && (value & (value - 1)) == 0;

    /// <summary>
    /// Forward transform. <paramref name="real"/> and <paramref name="imaginary"/> are
    /// modified in place and must be the same power-of-two length.
    /// </summary>
    public static void Forward(Span<float> real, Span<float> imaginary)
    {
        Transform(real, imaginary, inverse: false);
    }

    public static void Inverse(Span<float> real, Span<float> imaginary)
    {
        Transform(real, imaginary, inverse: true);

        float scale = 1f / real.Length;
        for (int i = 0; i < real.Length; i++)
        {
            real[i] *= scale;
            imaginary[i] *= scale;
        }
    }

    private static void Transform(Span<float> real, Span<float> imaginary, bool inverse)
    {
        int n = real.Length;

        if (n != imaginary.Length)
        {
            throw new ArgumentException("Real and imaginary spans must be the same length.", nameof(imaginary));
        }

        if (!IsPowerOfTwo(n))
        {
            throw new ArgumentException($"FFT length must be a power of two, got {n}.", nameof(real));
        }

        if (n == 1)
        {
            return;
        }

        BitReverseReorder(real, imaginary);

        double sign = inverse ? 1.0 : -1.0;

        for (int span = 1; span < n; span <<= 1)
        {
            int step = span << 1;
            double theta = sign * Math.PI / span;
            double wReal = Math.Cos(theta);
            double wImaginary = Math.Sin(theta);

            for (int start = 0; start < n; start += step)
            {
                // Recomputing the twiddle per butterfly group by recurrence keeps this
                // table-free; the drift over a 8192-point transform is well below the
                // precision the features need.
                double currentReal = 1.0;
                double currentImaginary = 0.0;

                for (int offset = 0; offset < span; offset++)
                {
                    int a = start + offset;
                    int b = a + span;

                    double tReal = (currentReal * real[b]) - (currentImaginary * imaginary[b]);
                    double tImaginary = (currentReal * imaginary[b]) + (currentImaginary * real[b]);

                    real[b] = (float)(real[a] - tReal);
                    imaginary[b] = (float)(imaginary[a] - tImaginary);
                    real[a] = (float)(real[a] + tReal);
                    imaginary[a] = (float)(imaginary[a] + tImaginary);

                    double nextReal = (currentReal * wReal) - (currentImaginary * wImaginary);
                    currentImaginary = (currentReal * wImaginary) + (currentImaginary * wReal);
                    currentReal = nextReal;
                }
            }
        }
    }

    private static void BitReverseReorder(Span<float> real, Span<float> imaginary)
    {
        int n = real.Length;
        for (int i = 1, j = 0; i < n; i++)
        {
            int bit = n >> 1;
            for (; (j & bit) != 0; bit >>= 1)
            {
                j ^= bit;
            }

            j ^= bit;

            if (i < j)
            {
                (real[i], real[j]) = (real[j], real[i]);
                (imaginary[i], imaginary[j]) = (imaginary[j], imaginary[i]);
            }
        }
    }

    /// <summary>
    /// Computes the magnitude spectrum of a real signal.
    /// </summary>
    /// <param name="signal">Input samples; zero-padded up to the transform size.</param>
    /// <param name="scratchReal">Working buffer, power-of-two length ≥ signal length.</param>
    /// <param name="scratchImaginary">Working buffer, same length as <paramref name="scratchReal"/>.</param>
    /// <param name="magnitudes">
    /// Receives bins 0..N/2 inclusive. Length must be <c>scratchReal.Length / 2 + 1</c>.
    /// </param>
    public static void MagnitudeSpectrum(
        ReadOnlySpan<float> signal,
        Span<float> scratchReal,
        Span<float> scratchImaginary,
        Span<float> magnitudes)
    {
        int n = scratchReal.Length;

        if (magnitudes.Length != (n / 2) + 1)
        {
            throw new ArgumentException(
                $"Magnitude span must hold {(n / 2) + 1} bins for a {n}-point transform.", nameof(magnitudes));
        }

        int copy = Math.Min(signal.Length, n);
        signal[..copy].CopyTo(scratchReal);
        scratchReal[copy..].Clear();
        scratchImaginary.Clear();

        Forward(scratchReal, scratchImaginary);

        for (int bin = 0; bin < magnitudes.Length; bin++)
        {
            double re = scratchReal[bin];
            double im = scratchImaginary[bin];
            magnitudes[bin] = (float)Math.Sqrt((re * re) + (im * im));
        }
    }

    /// <summary>Centre frequency of an FFT bin.</summary>
    public static double BinToHertz(int bin, int transformSize, int sampleRate) =>
        (double)bin * sampleRate / transformSize;

    /// <summary>Nearest FFT bin to a frequency.</summary>
    public static int HertzToBin(double hertz, int transformSize, int sampleRate) =>
        (int)Math.Round(hertz * transformSize / sampleRate);
}
