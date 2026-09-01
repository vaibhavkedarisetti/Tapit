using Tapit.Core.Audio;
using Tapit.Core.Detection;
using Tapit.Core.DSP;

namespace Tapit.Core.Features;

/// <summary>
/// The starting feature set: time-domain shape, two spectral moments, and coarse band
/// energies.
/// </summary>
/// <remarks>
/// <para>
/// Small on purpose. With ten calibration examples per zone, a large feature vector
/// overfits, and every extra feature has to earn its place by improving measured separation
/// on real taps - not by sounding useful. Log-mel and MFCC coefficients are deliberately
/// absent until there is evidence they help here.
/// </para>
/// <para>
/// Band energies are normalised against total band energy, so how hard the desk was struck
/// does not dominate the vector. The absolute level features are kept separate so the two
/// choices can be tested independently.
/// </para>
/// </remarks>
public sealed class TapFeatureExtractor
{
    /// <summary>Number of log-spaced energy bands between <see cref="LowestBandHz"/> and Nyquist.</summary>
    public const int BandCount = 6;

    public const double LowestBandHz = 100.0;

    private readonly int _sampleRate;
    private readonly int _transformSize;
    private readonly float[] _windowFunction;
    private readonly float[] _scratchReal;
    private readonly float[] _scratchImaginary;
    private readonly float[] _magnitudes;
    private readonly float[] _windowed;
    private readonly float[] _envelope;
    private readonly int[] _bandEdgeBins;

    public TapFeatureExtractor(int sampleRate, int windowSamples)
    {
        if (sampleRate <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(sampleRate), sampleRate, "Sample rate must be positive.");
        }

        if (windowSamples <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(windowSamples), windowSamples, "Window must be positive.");
        }

        _sampleRate = sampleRate;
        WindowSamples = windowSamples;
        _transformSize = Fft.NextPowerOfTwo(windowSamples);

        _windowFunction = WindowFunctions.Create(WindowType.Hann, windowSamples);
        _windowed = new float[windowSamples];
        _envelope = new float[windowSamples];
        _scratchReal = new float[_transformSize];
        _scratchImaginary = new float[_transformSize];
        _magnitudes = new float[(_transformSize / 2) + 1];

        _bandEdgeBins = BuildBandEdges(sampleRate, _transformSize);
    }

    public int WindowSamples { get; }

    public int TransformSize => _transformSize;

    public static IReadOnlyList<string> Names { get; } =
    [
        "rmsDbfs",
        "peakDbfs",
        "crestDb",
        "attackMs",
        "decayMs",
        "durationMs",
        "zcr",
        "earlyEnergy",
        "centroidHz",
        "bandwidthHz",
        "band0Db",
        "band1Db",
        "band2Db",
        "band3Db",
        "band4Db",
        "band5Db",
        "chLevelDb",
        "chLagUs",
        "chCorr",
    ];

    public static int Count => Names.Count;

    /// <summary>
    /// Extracts a feature vector from an analysis window. The window is not modified.
    /// </summary>
    /// <returns>
    /// A new <see cref="float"/> array of length <see cref="Count"/>. Every value is checked
    /// finite; a non-finite feature invalidates the whole event rather than poisoning a model.
    /// </returns>
    public float[] Extract(ReadOnlySpan<float> window)
    {
        var features = new float[Count];
        Extract(window, features);
        return features;
    }

    public bool Extract(ReadOnlySpan<float> window, Span<float> features) =>
        Extract(window, [], [], features);

    /// <summary>
    /// Extracts features, including inter-channel spatial cues when two channels are supplied.
    /// </summary>
    /// <remarks>
    /// The spatial cues are the only ones that can separate left from right. A centred
    /// microphone sees near-identical path lengths to symmetric points either side of it, so
    /// the mono spectrum of a left tap and a right tap can be almost the same signal. Which
    /// channel the sound reaches first, and by how much, is what differs.
    /// </remarks>
    public bool Extract(
        ReadOnlySpan<float> window,
        ReadOnlySpan<float> left,
        ReadOnlySpan<float> right,
        Span<float> features)
    {
        if (features.Length < Count)
        {
            throw new ArgumentException($"Need room for {Count} features.", nameof(features));
        }

        int length = Math.Min(window.Length, WindowSamples);
        window[..length].CopyTo(_windowed);
        if (length < WindowSamples)
        {
            _windowed.AsSpan(length).Clear();
        }

        // --- time domain, on the unwindowed signal --------------------------------------
        SignalLevels levels = SignalAnalysis.Measure(_windowed);
        Envelope.Follow(_windowed, _envelope, _sampleRate);
        int peakIndex = Envelope.PeakIndex(_windowed);

        int half = WindowSamples / 2;
        double earlyEnergy = Envelope.Energy(_windowed.AsSpan(0, half));
        double totalEnergy = earlyEnergy + Envelope.Energy(_windowed.AsSpan(half));

        int crossings = 0;
        for (int i = 1; i < WindowSamples; i++)
        {
            if ((_windowed[i - 1] < 0f) != (_windowed[i] < 0f))
            {
                crossings++;
            }
        }

        features[0] = (float)SignalAnalysis.ToDbfs(levels.Rms);
        features[1] = (float)SignalAnalysis.ToDbfs(levels.Peak);
        features[2] = (float)(levels.Rms > 0
            ? SignalAnalysis.ToDbfs(levels.Peak) - SignalAnalysis.ToDbfs(levels.Rms)
            : 0.0);
        features[3] = (float)Envelope.AttackMilliseconds(_envelope, peakIndex, _sampleRate);
        features[4] = (float)Envelope.DecayMilliseconds(_envelope, peakIndex, _sampleRate);
        features[5] = (float)Envelope.EffectiveDurationMilliseconds(_envelope, _sampleRate);
        features[6] = (float)(WindowSamples > 1 ? (double)crossings / (WindowSamples - 1) : 0.0);
        features[7] = (float)(totalEnergy > 0 ? earlyEnergy / totalEnergy : 0.0);

        // --- frequency domain -------------------------------------------------------------
        WindowFunctions.Apply(_windowed, _windowFunction);
        Fft.MagnitudeSpectrum(_windowed, _scratchReal, _scratchImaginary, _magnitudes);

        ComputeSpectralMoments(out double centroid, out double bandwidth);
        features[8] = (float)centroid;
        features[9] = (float)bandwidth;

        ComputeBandEnergies(features[10..(10 + BandCount)]);

        ComputeSpatialCues(left, right, peakIndex, features[(10 + BandCount)..]);

        for (int i = 0; i < Count; i++)
        {
            if (!float.IsFinite(features[i]))
            {
                return false;
            }
        }

        return true;
    }

    private void ComputeSpectralMoments(out double centroid, out double bandwidth)
    {
        double weighted = 0.0;
        double total = 0.0;

        for (int bin = 1; bin < _magnitudes.Length; bin++)
        {
            double magnitude = _magnitudes[bin];
            double hz = Fft.BinToHertz(bin, _transformSize, _sampleRate);
            weighted += magnitude * hz;
            total += magnitude;
        }

        if (total <= 0)
        {
            centroid = 0.0;
            bandwidth = 0.0;
            return;
        }

        centroid = weighted / total;

        double variance = 0.0;
        for (int bin = 1; bin < _magnitudes.Length; bin++)
        {
            double hz = Fft.BinToHertz(bin, _transformSize, _sampleRate);
            double delta = hz - centroid;
            variance += _magnitudes[bin] * delta * delta;
        }

        bandwidth = Math.Sqrt(variance / total);
    }

    private void ComputeBandEnergies(Span<float> bands)
    {
        Span<double> energies = stackalloc double[BandCount];
        double total = 0.0;

        for (int b = 0; b < BandCount; b++)
        {
            int start = _bandEdgeBins[b];
            int end = Math.Min(_bandEdgeBins[b + 1], _magnitudes.Length);
            double energy = 0.0;

            for (int bin = start; bin < end; bin++)
            {
                double magnitude = _magnitudes[bin];
                energy += magnitude * magnitude;
            }

            energies[b] = energy;
            total += energy;
        }

        // Relative, in dB: the classifier should key on spectral shape, not on how hard the
        // desk happened to be struck.
        for (int b = 0; b < BandCount; b++)
        {
            double fraction = total > 0 ? energies[b] / total : 0.0;
            bands[b] = (float)(10.0 * Math.Log10(Math.Max(fraction, 1e-10)));
        }
    }

    /// <summary>
    /// Writes level difference (dB), arrival delay (us) and peak correlation between the two
    /// channels. All zero for a mono device, which makes them constant and therefore inert
    /// after standardisation rather than misleading.
    /// </summary>
    private void ComputeSpatialCues(
        ReadOnlySpan<float> left, ReadOnlySpan<float> right, int peakIndex, Span<float> features)
    {
        features[0] = 0f;
        features[1] = 0f;
        features[2] = 0f;

        if (left.Length < 16 || right.Length < 16)
        {
            return;
        }

        // Centre the analysis on the direct arrival: the ring that follows is diffuse and
        // carries the surface's resonance, which is the same whichever side was struck.
        int span = Math.Max(16, _sampleRate / 100);          // 10 ms
        int start = Math.Clamp(peakIndex - (_sampleRate / 1000), 0, Math.Max(0, left.Length - 8));
        int length = Math.Min(span, Math.Min(left.Length, right.Length) - start);

        if (length < 16)
        {
            return;
        }

        InterChannelCues cues = InterChannel.Measure(
            left.Slice(start, length), right.Slice(start, length), _sampleRate);

        features[0] = (float)cues.LevelDifferenceDb;
        features[1] = (float)cues.LagMicroseconds;
        features[2] = (float)cues.PeakCorrelation;
    }

    private static int[] BuildBandEdges(int sampleRate, int transformSize)
    {
        double nyquist = sampleRate / 2.0;
        double low = Math.Min(LowestBandHz, nyquist / 4.0);

        var edges = new int[BandCount + 1];
        int binCount = (transformSize / 2) + 1;

        for (int i = 0; i <= BandCount; i++)
        {
            double hz = low * Math.Pow(nyquist / low, (double)i / BandCount);
            edges[i] = Math.Clamp(Fft.HertzToBin(hz, transformSize, sampleRate), 0, binCount);
        }

        // Guarantee every band has at least one bin, however narrow the spectrum is.
        for (int i = 1; i <= BandCount; i++)
        {
            if (edges[i] <= edges[i - 1])
            {
                edges[i] = Math.Min(edges[i - 1] + 1, binCount);
            }
        }

        return edges;
    }

    /// <summary>Band edge frequencies, for display in the inspector.</summary>
    public double[] BandEdgeFrequencies()
    {
        var edges = new double[BandCount + 1];
        for (int i = 0; i <= BandCount; i++)
        {
            edges[i] = Fft.BinToHertz(_bandEdgeBins[i], _transformSize, _sampleRate);
        }

        return edges;
    }

    /// <summary>Convenience: extract from a detected event.</summary>
    public float[]? Extract(TapEvent tapEvent)
    {
        ArgumentNullException.ThrowIfNull(tapEvent);

        if (tapEvent.Window.Length == 0)
        {
            return null;
        }

        var features = new float[Count];

        ReadOnlySpan<float> left = tapEvent.ChannelWindows.Length >= 2 ? tapEvent.ChannelWindows[0] : [];
        ReadOnlySpan<float> right = tapEvent.ChannelWindows.Length >= 2 ? tapEvent.ChannelWindows[1] : [];

        return Extract(tapEvent.Window, left, right, features) ? features : null;
    }
}
