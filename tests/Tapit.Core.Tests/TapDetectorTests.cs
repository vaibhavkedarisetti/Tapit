using Tapit.Core.Audio;
using Tapit.Core.Detection;
using Tapit.Core.DSP;
using Tapit.Core.Features;

namespace Tapit.Core.Tests;

/// <summary>
/// Synthetic detector checks.
/// </summary>
/// <remarks>
/// These prove the detector's <i>plumbing</i> - that it fires on impulsive energy, refuses
/// sustained and clipped input, honours its refractory period, and produces identical
/// results on replay. They say nothing at all about whether a real desk separates into four
/// zones. That question can only be answered by tapping an actual desk, and no amount of
/// synthetic signal is a substitute for it.
/// </remarks>
public class TapDetectorTests
{
    private const int SampleRate = 48000;

    private static AudioFormat Format => new(SampleRate, 1, AudioSampleFormat.Float32);

    /// <summary>Low-level deterministic room noise, so the floor estimator has something to learn.</summary>
    private static float[] NoiseBed(int samples, double amplitude = 0.0008, int seed = 12345)
    {
        var random = new Random(seed);
        var buffer = new float[samples];
        for (int i = 0; i < samples; i++)
        {
            buffer[i] = (float)((random.NextDouble() * 2.0 - 1.0) * amplitude);
        }

        return buffer;
    }

    /// <summary>A broadband exponentially decaying burst - the shape of a desk impact.</summary>
    private static void AddImpulse(float[] buffer, int at, double peak = 0.3, double decayMs = 8.0, int seed = 7)
    {
        var random = new Random(seed);
        double tau = decayMs * SampleRate / 1000.0;
        int length = (int)(tau * 6);

        for (int i = 0; i < length && at + i < buffer.Length; i++)
        {
            double envelope = Math.Exp(-i / tau);
            double sample = (random.NextDouble() * 2.0 - 1.0) * peak * envelope;
            buffer[at + i] += (float)sample;
        }
    }

    private static List<TapEvent> RunDetector(float[] signal, DetectorOptions? options = null)
    {
        var buffer = new AudioRingBuffer(1, SampleRate * 4);
        var detector = new TapDetector(Format, options);
        var events = new List<TapEvent>();

        const int packet = 480;
        for (int offset = 0; offset < signal.Length; offset += packet)
        {
            int count = Math.Min(packet, signal.Length - offset);
            buffer.Write(signal.AsSpan(offset, count), count);
            events.AddRange(detector.Process(buffer));
        }

        events.AddRange(detector.Process(buffer));
        return events;
    }

    [Fact]
    public void ImpulsesAfterRoomLearning_AreDetectedAndAccepted()
    {
        float[] signal = NoiseBed(SampleRate * 3);

        int[] positions = [SampleRate, (int)(SampleRate * 1.5), SampleRate * 2];
        foreach (int position in positions)
        {
            AddImpulse(signal, position);
        }

        List<TapEvent> events = RunDetector(signal);
        List<TapEvent> accepted = events.Where(e => e.Accepted).ToList();

        Assert.Equal(positions.Length, accepted.Count);

        for (int i = 0; i < positions.Length; i++)
        {
            // The onset should land within a couple of frames of the true impulse start.
            double errorMs = (accepted[i].OnsetSample - positions[i]) * 1000.0 / SampleRate;
            Assert.InRange(errorMs, -1.0, 4.0);
        }
    }

    [Fact]
    public void NothingIsDetectedDuringRoomLearning()
    {
        // An impulse inside the learning period must not become an event: the system is not
        // armed yet, and counting it would let a stray noise poison calibration later.
        float[] signal = NoiseBed(SampleRate * 2);
        AddImpulse(signal, (int)(SampleRate * 0.3));

        List<TapEvent> events = RunDetector(signal);

        Assert.Empty(events);
    }

    [Fact]
    public void QuietRoomProducesNoEvents()
    {
        List<TapEvent> events = RunDetector(NoiseBed(SampleRate * 3));
        Assert.Empty(events);
    }

    [Fact]
    public void SustainedToneIsRejected()
    {
        // A tone that starts abruptly will trip the onset detector; the window validation is
        // what has to notice it never stops.
        float[] signal = NoiseBed(SampleRate * 3);
        int start = SampleRate * 2;

        for (int i = start; i < signal.Length; i++)
        {
            signal[i] += 0.25f * MathF.Sin(2f * MathF.PI * 400f * (i - start) / SampleRate);
        }

        List<TapEvent> events = RunDetector(signal);

        Assert.NotEmpty(events);
        Assert.DoesNotContain(events, e => e.Accepted);
        Assert.Contains(events, e =>
            e.Rejection is RejectionReason.SustainedSound or RejectionReason.LateEnergy
                or RejectionReason.FlatDynamics);
    }

    [Fact]
    public void SlowSwellIsRejected()
    {
        // A gradual rise is not an impact, whatever its final level.
        float[] signal = NoiseBed(SampleRate * 3);
        int start = SampleRate * 2;
        int rampLength = (int)(SampleRate * 0.25);

        for (int i = 0; i < rampLength && start + i < signal.Length; i++)
        {
            double gain = (double)i / rampLength;
            signal[start + i] += (float)(0.3 * gain * Math.Sin(2.0 * Math.PI * 300.0 * i / SampleRate));
        }

        List<TapEvent> events = RunDetector(signal);

        Assert.DoesNotContain(events, e => e.Accepted);
    }

    [Fact]
    public void ClippedImpulseIsRejected()
    {
        float[] signal = NoiseBed(SampleRate * 3);
        int at = SampleRate * 2;

        AddImpulse(signal, at, peak: 3.0);
        for (int i = 0; i < signal.Length; i++)
        {
            signal[i] = Math.Clamp(signal[i], -1f, 1f);
        }

        List<TapEvent> events = RunDetector(signal);

        Assert.NotEmpty(events);
        Assert.DoesNotContain(events, e => e.Accepted);
        Assert.Contains(events, e => e.Rejection == RejectionReason.Clipped);
    }

    [Fact]
    public void WeakImpulseIsRejectedAsTooQuiet()
    {
        var options = new DetectorOptions { MinPeakDbfs = -20.0 };
        float[] signal = NoiseBed(SampleRate * 3);
        AddImpulse(signal, SampleRate * 2, peak: 0.05);

        List<TapEvent> events = RunDetector(signal, options);

        Assert.DoesNotContain(events, e => e.Accepted);
        Assert.Contains(events, e => e.Rejection == RejectionReason.SignalTooWeak);
    }

    [Fact]
    public void RefractoryPeriodCollapsesOneStrikeIntoOneEvent()
    {
        // A real tap rings and can re-cross the threshold; that must not become two actions.
        float[] signal = NoiseBed(SampleRate * 3);
        int at = SampleRate * 2;

        AddImpulse(signal, at);
        AddImpulse(signal, at + (SampleRate / 100));   // +10 ms
        AddImpulse(signal, at + (SampleRate / 50));    // +20 ms

        List<TapEvent> events = RunDetector(signal);

        Assert.Single(events);
    }

    [Fact]
    public void ImpulsesBeyondTheRefractoryPeriodAreSeparateEvents()
    {
        float[] signal = NoiseBed(SampleRate * 3);
        AddImpulse(signal, SampleRate);
        AddImpulse(signal, SampleRate + (int)(SampleRate * 0.25));

        List<TapEvent> events = RunDetector(signal);

        Assert.Equal(2, events.Count(e => e.Accepted));
    }

    [Fact]
    public void NoiseFloorAdaptsToARoomThatGetsLouder()
    {
        var buffer = new AudioRingBuffer(1, SampleRate * 4);
        var detector = new TapDetector(Format);

        float[] quiet = NoiseBed(SampleRate, 0.0005);
        for (int offset = 0; offset < quiet.Length; offset += 480)
        {
            int count = Math.Min(480, quiet.Length - offset);
            buffer.Write(quiet.AsSpan(offset, count), count);
            detector.Process(buffer);
        }

        double quietFloor = detector.NoiseFloorDbfs;

        float[] loud = NoiseBed(SampleRate * 3, 0.01, seed: 999);
        for (int offset = 0; offset < loud.Length; offset += 480)
        {
            int count = Math.Min(480, loud.Length - offset);
            buffer.Write(loud.AsSpan(offset, count), count);
            detector.Process(buffer);
        }

        double loudFloor = detector.NoiseFloorDbfs;

        Assert.True(loudFloor > quietFloor + 6.0,
            $"floor should track the room: {quietFloor:0.0} dBFS -> {loudFloor:0.0} dBFS");
    }

    [Fact]
    public void DcOffsetDoesNotCreatePhantomEvents()
    {
        // Raw WASAPI capture carries a DC offset. Without the DC blocker the constant term
        // alone can hold the detector above its threshold.
        float[] signal = NoiseBed(SampleRate * 3);
        for (int i = 0; i < signal.Length; i++)
        {
            signal[i] += 0.05f;
        }

        List<TapEvent> events = RunDetector(signal);

        Assert.Empty(events);
    }

    [Fact]
    public void DetectionIsDeterministic()
    {
        float[] signal = NoiseBed(SampleRate * 3);
        AddImpulse(signal, SampleRate * 2);

        List<TapEvent> first = RunDetector(signal);
        List<TapEvent> second = RunDetector(signal);

        Assert.Equal(first.Count, second.Count);
        for (int i = 0; i < first.Count; i++)
        {
            Assert.Equal(first[i].OnsetSample, second[i].OnsetSample);
            Assert.Equal(first[i].Accepted, second[i].Accepted);
            Assert.Equal(first[i].Measurements.PeakDbfs, second[i].Measurements.PeakDbfs, 6);
            Assert.Equal(first[i].Window, second[i].Window);
        }
    }

    [Fact]
    public void AcceptedEventCarriesTheFullWindowIncludingPreRoll()
    {
        var options = new DetectorOptions();
        float[] signal = NoiseBed(SampleRate * 3);
        AddImpulse(signal, SampleRate * 2);

        TapEvent tapEvent = RunDetector(signal, options).Single(e => e.Accepted);

        int expected = Format.MillisecondsToFrames(options.WindowMs);
        Assert.Equal(expected, tapEvent.Window.Length);
        Assert.Equal(tapEvent.OnsetSample - Format.MillisecondsToFrames(options.PreRollMs),
            tapEvent.WindowStartSample);

        // The peak must sit after the pre-roll, i.e. the leading edge really was captured.
        int peakIndex = Envelope.PeakIndex(tapEvent.Window);
        Assert.True(peakIndex >= Format.MillisecondsToFrames(options.PreRollMs) - 2,
            $"peak at sample {peakIndex} landed inside the pre-roll");
    }

    [Fact]
    public void WindowLengthIsConfigurable()
    {
        float[] signal = NoiseBed(SampleRate * 3);
        AddImpulse(signal, SampleRate * 2);

        foreach (double windowMs in new[] { 40.0, 90.0, 150.0 })
        {
            var options = new DetectorOptions { WindowMs = windowMs, MaxEffectiveDurationMs = windowMs };
            TapEvent tapEvent = RunDetector(signal, options).Single();

            Assert.Equal(Format.MillisecondsToFrames(windowMs), tapEvent.Window.Length);
        }
    }
}

public class TapFeatureTests
{
    private const int SampleRate = 48000;

    private static float[] Impulse(int length, double peak = 0.3, double decayMs = 8.0, int seed = 3)
    {
        var random = new Random(seed);
        var buffer = new float[length];
        double tau = decayMs * SampleRate / 1000.0;

        for (int i = 0; i < length; i++)
        {
            buffer[i] = (float)((random.NextDouble() * 2.0 - 1.0) * peak * Math.Exp(-i / tau));
        }

        return buffer;
    }

    [Fact]
    public void FeatureVectorIsFullyPopulatedAndFinite()
    {
        var extractor = new TapFeatureExtractor(SampleRate, 4320);
        float[] features = extractor.Extract(Impulse(4320));

        Assert.Equal(TapFeatureExtractor.Count, features.Length);
        Assert.Equal(TapFeatureExtractor.Count, TapFeatureExtractor.Names.Count);
        Assert.All(features, f => Assert.True(float.IsFinite(f)));
    }

    [Fact]
    public void ExtractionIsDeterministic()
    {
        var extractor = new TapFeatureExtractor(SampleRate, 4320);
        float[] window = Impulse(4320);

        Assert.Equal(extractor.Extract(window), extractor.Extract(window));
    }

    [Fact]
    public void SilentWindowStaysFinite()
    {
        var extractor = new TapFeatureExtractor(SampleRate, 4320);
        float[] features = extractor.Extract(new float[4320]);

        Assert.All(features, f => Assert.True(float.IsFinite(f)));
    }

    [Fact]
    public void SpectralCentroidTracksToneFrequency()
    {
        var extractor = new TapFeatureExtractor(SampleRate, 4096);
        int centroidIndex = TapFeatureExtractor.Names.ToList().IndexOf("centroidHz");

        static float[] Tone(double hz)
        {
            var buffer = new float[4096];
            for (int i = 0; i < buffer.Length; i++)
            {
                buffer[i] = 0.5f * MathF.Sin(2f * MathF.PI * (float)hz * i / SampleRate);
            }

            return buffer;
        }

        float low = extractor.Extract(Tone(500))[centroidIndex];
        float high = extractor.Extract(Tone(5000))[centroidIndex];

        Assert.InRange(low, 300f, 900f);
        Assert.InRange(high, 4000f, 6500f);
        Assert.True(high > low);
    }

    [Fact]
    public void BandEnergiesAreRelativeSoLoudnessDoesNotDominate()
    {
        // Two taps of the same shape at different strengths must produce the same spectral
        // shape features; only the absolute level features should move.
        var extractor = new TapFeatureExtractor(SampleRate, 4320);

        float[] quiet = Impulse(4320, peak: 0.05);
        float[] loud = Impulse(4320, peak: 0.5);

        float[] quietFeatures = extractor.Extract(quiet);
        float[] loudFeatures = extractor.Extract(loud);

        for (int band = 0; band < TapFeatureExtractor.BandCount; band++)
        {
            int index = 10 + band;
            Assert.Equal(quietFeatures[index], loudFeatures[index], 2);
        }

        Assert.True(loudFeatures[1] > quietFeatures[1] + 10f, "peak level should differ");
    }

    [Fact]
    public void BandEdgesSpanTheUsableSpectrum()
    {
        var extractor = new TapFeatureExtractor(SampleRate, 4320);
        double[] edges = extractor.BandEdgeFrequencies();

        Assert.Equal(TapFeatureExtractor.BandCount + 1, edges.Length);
        Assert.True(edges[0] < 200);
        Assert.True(edges[^1] > 15000);

        for (int i = 1; i < edges.Length; i++)
        {
            Assert.True(edges[i] > edges[i - 1], "band edges must increase");
        }
    }
}

public class FftTests
{
    [Theory]
    [InlineData(1, 1)]
    [InlineData(3, 4)]
    [InlineData(4096, 4096)]
    [InlineData(4320, 8192)]
    public void NextPowerOfTwo_Rounds(int input, int expected) =>
        Assert.Equal(expected, Fft.NextPowerOfTwo(input));

    [Fact]
    public void ForwardTransformOfASineHasASinglePeak()
    {
        const int n = 1024;
        const int sampleRate = 48000;
        const double frequency = 48000.0 * 64 / n; // exactly bin 64

        var real = new float[n];
        var imaginary = new float[n];
        for (int i = 0; i < n; i++)
        {
            real[i] = MathF.Sin(2f * MathF.PI * (float)frequency * i / sampleRate);
        }

        Fft.Forward(real, imaginary);

        var magnitudes = new double[(n / 2) + 1];
        int peakBin = 0;
        for (int bin = 0; bin < magnitudes.Length; bin++)
        {
            magnitudes[bin] = Math.Sqrt((real[bin] * real[bin]) + (imaginary[bin] * imaginary[bin]));
            if (magnitudes[bin] > magnitudes[peakBin])
            {
                peakBin = bin;
            }
        }

        Assert.Equal(64, peakBin);
    }

    [Fact]
    public void InverseTransformRecoversTheInput()
    {
        const int n = 256;
        var random = new Random(42);

        var original = new float[n];
        for (int i = 0; i < n; i++)
        {
            original[i] = (float)(random.NextDouble() * 2.0 - 1.0);
        }

        var real = (float[])original.Clone();
        var imaginary = new float[n];

        Fft.Forward(real, imaginary);
        Fft.Inverse(real, imaginary);

        for (int i = 0; i < n; i++)
        {
            Assert.Equal(original[i], real[i], 4);
        }
    }

    [Fact]
    public void DcSignalPutsAllEnergyInBinZero()
    {
        const int n = 64;
        var real = new float[n];
        var imaginary = new float[n];
        Array.Fill(real, 1f);

        Fft.Forward(real, imaginary);

        Assert.Equal(64f, real[0], 3);
        for (int bin = 1; bin < n; bin++)
        {
            Assert.Equal(0f, MathF.Sqrt((real[bin] * real[bin]) + (imaginary[bin] * imaginary[bin])), 3);
        }
    }

    [Fact]
    public void NonPowerOfTwoLengthIsRejected() =>
        Assert.Throws<ArgumentException>(() => Fft.Forward(new float[100], new float[100]));

    [Fact]
    public void MismatchedSpansAreRejected() =>
        Assert.Throws<ArgumentException>(() => Fft.Forward(new float[64], new float[32]));

    [Fact]
    public void BinAndHertzConversionsRoundTripWithinOneBin()
    {
        double binWidth = 48000.0 / 4096;
        double recovered = Fft.BinToHertz(Fft.HertzToBin(1000.0, 4096, 48000), 4096, 48000);

        Assert.InRange(recovered, 1000.0 - binWidth, 1000.0 + binWidth);
        Assert.Equal(0.0, Fft.BinToHertz(0, 4096, 48000));
    }
}

public class DcBlockerTests
{
    [Fact]
    public void RemovesAConstantOffset()
    {
        var blocker = new DcBlocker(20.0, 48000);
        var output = new float[48000];
        var input = new float[48000];
        Array.Fill(input, 0.05f);

        blocker.Process(input, output);

        // After the filter settles the offset should be essentially gone.
        SignalLevels tail = SignalAnalysis.Measure(output.AsSpan(24000));
        Assert.True(Math.Abs(tail.Mean) < 0.001f, $"residual DC {tail.Mean}");
    }

    [Fact]
    public void PreservesAudioBandContent()
    {
        var blocker = new DcBlocker(20.0, 48000);
        var input = new float[48000];
        for (int i = 0; i < input.Length; i++)
        {
            input[i] = 0.5f * MathF.Sin(2f * MathF.PI * 1000f * i / 48000f);
        }

        var output = new float[input.Length];
        blocker.Process(input, output);

        SignalLevels before = SignalAnalysis.Measure(input.AsSpan(24000));
        SignalLevels after = SignalAnalysis.Measure(output.AsSpan(24000));

        Assert.Equal(before.Rms, after.Rms, 2);
    }

    [Fact]
    public void RemoveMeanIsStatelessAndExact()
    {
        float[] samples = [1f, 2f, 3f, 4f];
        DcBlocker.RemoveMean(samples);

        Assert.Equal(-1.5f, samples[0], 5);
        Assert.Equal(1.5f, samples[3], 5);
        Assert.Equal(0f, samples.Sum(), 5);
    }

    [Fact]
    public void ResetClearsFilterState()
    {
        var blocker = new DcBlocker(20.0, 48000);
        blocker.Process(1f);
        blocker.Reset();

        Assert.Equal(0f, blocker.Process(0f), 6);
    }
}
