using Tapit.Core.Audio;

namespace Tapit.Core.Tests;

public class SignalAnalysisTests
{
    [Fact]
    public void Rms_OfSineWave_IsAmplitudeOverRootTwo()
    {
        const int sampleCount = 4800;
        const float amplitude = 0.5f;

        var samples = new float[sampleCount];
        for (int i = 0; i < sampleCount; i++)
        {
            samples[i] = amplitude * MathF.Sin(2f * MathF.PI * 100f * i / 48000f);
        }

        SignalLevels levels = SignalAnalysis.Measure(samples);

        Assert.Equal(amplitude / MathF.Sqrt(2f), levels.Rms, 3);
        Assert.Equal(amplitude, levels.Peak, 3);
        Assert.Equal(0f, levels.DcOffset, 3);
    }

    [Fact]
    public void DcOffset_IsSeparatedFromAcContent()
    {
        // A raw, effects-bypassed capture stream has no high-pass filter in front of it, so
        // this separation is what stops a constant bias from masquerading as signal.
        const float dc = 0.2f;
        const float amplitude = 0.1f;

        var samples = new float[4800];
        for (int i = 0; i < samples.Length; i++)
        {
            samples[i] = dc + (amplitude * MathF.Sin(2f * MathF.PI * 200f * i / 48000f));
        }

        SignalLevels levels = SignalAnalysis.Measure(samples);

        Assert.Equal(dc, levels.DcOffset, 3);
        Assert.Equal(amplitude / MathF.Sqrt(2f), levels.AcRms, 3);
        Assert.True(levels.Rms > levels.AcRms, "uncorrected RMS should be inflated by the DC term");
    }

    [Fact]
    public void ConstantSignal_HasZeroAcRms()
    {
        var samples = new float[128];
        Array.Fill(samples, 0.3f);

        SignalLevels levels = SignalAnalysis.Measure(samples);

        Assert.Equal(0.3f, levels.DcOffset, 5);
        Assert.Equal(0f, levels.AcRms, 5);
    }

    [Fact]
    public void ClippedSamples_AreCounted()
    {
        float[] samples = [0.1f, 1.0f, -1.0f, 0.9f, 0.9995f];

        SignalLevels levels = SignalAnalysis.Measure(samples);

        Assert.Equal(3, levels.ClippedSamples);
        Assert.Equal(5, levels.SampleCount);
        Assert.Equal(0.6, levels.ClippedFraction, 6);
    }

    [Fact]
    public void NonFiniteSamples_AreTreatedAsClippedNotPropagated()
    {
        float[] samples = [0.1f, float.NaN, 0.2f, float.PositiveInfinity, -0.1f];

        SignalLevels levels = SignalAnalysis.Measure(samples);

        Assert.True(float.IsFinite(levels.Rms));
        Assert.True(float.IsFinite(levels.Peak));
        Assert.True(float.IsFinite(levels.Mean));
        Assert.Equal(2, levels.ClippedSamples);
    }

    [Fact]
    public void EmptyInput_ReturnsEmptyLevels()
    {
        SignalLevels levels = SignalAnalysis.Measure([]);

        Assert.Equal(SignalLevels.Empty, levels);
        Assert.Equal(0.0, levels.ClippedFraction);
        Assert.Equal(0.0, levels.CrestFactor);
    }

    [Fact]
    public void CrestFactor_IsPeakOverRms()
    {
        float[] samples = new float[100];
        Array.Fill(samples, 0.1f);
        samples[0] = 1.0f;

        SignalLevels levels = SignalAnalysis.Measure(samples);

        Assert.Equal(levels.Peak / levels.Rms, levels.CrestFactor, 5);
    }

    [Theory]
    [InlineData(1.0, 0.0)]
    [InlineData(0.5, -6.0206)]
    [InlineData(0.1, -20.0)]
    [InlineData(0.001, -60.0)]
    public void ToDbfs_ConvertsAmplitude(double amplitude, double expectedDb) =>
        Assert.Equal(expectedDb, SignalAnalysis.ToDbfs(amplitude), 3);

    [Theory]
    [InlineData(0.0)]
    [InlineData(-1.0)]
    [InlineData(double.NaN)]
    public void ToDbfs_ReturnsFiniteFloorForSilenceAndGarbage(double amplitude) =>
        Assert.Equal(SignalAnalysis.MinimumDbfs, SignalAnalysis.ToDbfs(amplitude));

    [Fact]
    public void FromDbfs_RoundTripsToDbfs()
    {
        double amplitude = SignalAnalysis.FromDbfs(-20.0);
        Assert.Equal(0.1, amplitude, 6);
        Assert.Equal(-20.0, SignalAnalysis.ToDbfs(amplitude), 6);
    }
}
