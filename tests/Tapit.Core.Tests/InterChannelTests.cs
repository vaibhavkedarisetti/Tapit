using Tapit.Core.Features;

namespace Tapit.Core.Tests;

/// <summary>
/// Inter-channel spatial cues.
/// </summary>
/// <remarks>
/// These are the only features that can separate left from right. A centred microphone sees
/// near-identical path lengths to symmetric points either side of it, so the mono spectra of
/// a left tap and a right tap are close to the same signal; the difference lives entirely in
/// which channel hears it first and loudest.
/// </remarks>
public class InterChannelTests
{
    private const int SampleRate = 48000;

    /// <summary>A decaying broadband burst, optionally delayed and attenuated.</summary>
    private static (float[] Left, float[] Right) Pair(
        int delaySamples, double rightGain = 1.0, int length = 4320, int seed = 5)
    {
        var random = new Random(seed);
        var source = new float[length + 256];
        double tau = 8.0 * SampleRate / 1000.0;

        for (int i = 0; i < source.Length; i++)
        {
            source[i] = (float)((random.NextDouble() * 2.0 - 1.0) * 0.4 * Math.Exp(-i / tau));
        }

        var left = new float[length];
        var right = new float[length];

        for (int i = 0; i < length; i++)
        {
            left[i] = source[i + 128];

            int shifted = i + 128 - delaySamples;
            right[i] = shifted >= 0 && shifted < source.Length
                ? (float)(source[shifted] * rightGain)
                : 0f;
        }

        return (left, right);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(5)]
    [InlineData(-5)]
    [InlineData(14)]
    [InlineData(-14)]
    public void ArrivalDelayIsRecoveredWithTheCorrectSign(int delaySamples)
    {
        (float[] left, float[] right) = Pair(delaySamples);

        InterChannelCues cues = InterChannel.Measure(left, right, SampleRate);

        // Sub-sample interpolation lands within a few hundredths of a sample; asserting to
        // more precision than that would be testing floating-point noise.
        Assert.Equal(delaySamples, cues.LagSamples, 1);
        Assert.InRange(cues.LagMicroseconds, (delaySamples - 0.05) * 1_000_000.0 / SampleRate,
            (delaySamples + 0.05) * 1_000_000.0 / SampleRate);
    }

    [Fact]
    public void DelayResolutionIsSubSample()
    {
        // A 10 cm array gives at most ~14 samples of delay at 48 kHz, so whole-sample
        // resolution would quantise the entire usable range into 29 steps.
        (float[] left, float[] right) = Pair(7);

        InterChannelCues cues = InterChannel.Measure(left, right, SampleRate);

        Assert.Equal(7.0, cues.LagSamples, 1);
        Assert.NotEqual(Math.Round(cues.LagSamples), cues.LagSamples, 6);
    }

    [Fact]
    public void LevelDifferenceIsSignedTowardTheLouderChannel()
    {
        (float[] quietRight, float[] right) = Pair(0, rightGain: 0.5);

        InterChannelCues cues = InterChannel.Measure(quietRight, right, SampleRate);

        // Left is twice the amplitude: +6 dB.
        Assert.Equal(6.02, cues.LevelDifferenceDb, 1);
    }

    [Fact]
    public void IdenticalChannelsAreReportedAsDegenerate()
    {
        // Windows often presents one beamformed signal copied to both channels. Spatial
        // features from such a pair are pure noise, so this has to be detected, not assumed.
        (float[] left, _) = Pair(0);

        InterChannelCues cues = InterChannel.Measure(left, left, SampleRate);

        Assert.True(cues.LooksDegenerate);
        Assert.Equal(1.0, cues.PeakCorrelation, 3);
        Assert.Equal(0.0, cues.LagSamples, 3);
        Assert.Equal(0.0, cues.LevelDifferenceDb, 3);
    }

    [Fact]
    public void GenuinelyIndependentChannelsAreNotDegenerate()
    {
        (float[] left, float[] right) = Pair(9, rightGain: 0.7);

        Assert.False(InterChannel.Measure(left, right, SampleRate).LooksDegenerate);
    }

    [Fact]
    public void SilenceAndShortInputAreHandled()
    {
        Assert.Equal(default, InterChannel.Measure(new float[4], new float[4], SampleRate));

        InterChannelCues silent = InterChannel.Measure(new float[512], new float[512], SampleRate);
        Assert.Equal(0.0, silent.LevelDifferenceDb);
        Assert.True(double.IsFinite(silent.Correlation));
    }

    [Fact]
    public void MonoExtractionLeavesSpatialFeaturesInert()
    {
        var extractor = new TapFeatureExtractor(SampleRate, 4320);
        (float[] mono, _) = Pair(0);

        float[] features = extractor.Extract(mono);

        int spatial = TapFeatureExtractor.Names.ToList().IndexOf("chLevelDb");
        Assert.Equal(0f, features[spatial]);
        Assert.Equal(0f, features[spatial + 1]);
        Assert.Equal(0f, features[spatial + 2]);
    }

    [Fact]
    public void SpatialFeaturesDistinguishOppositeSides()
    {
        // The whole point: two events that are near-identical in mono but arrive from
        // opposite sides must land in different places in feature space.
        var extractor = new TapFeatureExtractor(SampleRate, 4320);

        (float[] leftA, float[] rightA) = Pair(12, rightGain: 0.6);    // arrives left-first
        (float[] leftB, float[] rightB) = Pair(-12, rightGain: 1.0 / 0.6); // arrives right-first

        var a = new float[TapFeatureExtractor.Count];
        var b = new float[TapFeatureExtractor.Count];

        Assert.True(extractor.Extract(leftA, leftA, rightA, a));
        Assert.True(extractor.Extract(leftB, leftB, rightB, b));

        int lag = TapFeatureExtractor.Names.ToList().IndexOf("chLagUs");
        int level = TapFeatureExtractor.Names.ToList().IndexOf("chLevelDb");

        Assert.True(a[lag] > 100f, $"expected a clear positive lag, got {a[lag]:0.0} us");
        Assert.True(b[lag] < -100f, $"expected a clear negative lag, got {b[lag]:0.0} us");
        Assert.True(a[level] > 0f && b[level] < 0f, "level difference should flip sign");
    }
}
