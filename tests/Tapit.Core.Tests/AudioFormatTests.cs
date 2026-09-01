using Tapit.Core.Audio;

namespace Tapit.Core.Tests;

public class AudioFormatTests
{
    [Fact]
    public void DerivedSizes_AreConsistent()
    {
        var format = new AudioFormat(48000, 2, AudioSampleFormat.Float32);

        Assert.Equal(4, format.BytesPerSample);
        Assert.Equal(32, format.BitsPerSample);
        Assert.Equal(8, format.BlockAlign);
        Assert.Equal(384000, format.AverageBytesPerSecond);
        Assert.Equal(24000.0, format.NyquistHz);
        Assert.True(format.IsFloatingPoint);
    }

    [Theory]
    [InlineData(48000, 90.0, 4320)]
    [InlineData(44100, 90.0, 3969)]
    [InlineData(16000, 90.0, 1440)]
    [InlineData(48000, 12.0, 576)]
    [InlineData(48000, 0.0, 0)]
    public void MillisecondsToFrames_ScalesWithSampleRate(int sampleRate, double ms, int expected)
    {
        // Nothing in Tapit may assume 48 kHz; every DSP constant is expressed in time and
        // converted here.
        var format = new AudioFormat(sampleRate, 1, AudioSampleFormat.Float32);
        Assert.Equal(expected, format.MillisecondsToFrames(ms));
    }

    [Fact]
    public void FramesToMilliseconds_IsInverseOfMillisecondsToFrames()
    {
        var format = new AudioFormat(48000, 1, AudioSampleFormat.Int16);
        Assert.Equal(90.0, format.FramesToMilliseconds(format.MillisecondsToFrames(90.0)), 6);
    }

    [Fact]
    public void WithChannels_PreservesRateAndEncoding()
    {
        var stereo = new AudioFormat(44100, 2, AudioSampleFormat.Int24);
        AudioFormat mono = stereo.WithChannels(1);

        Assert.Equal(1, mono.Channels);
        Assert.Equal(44100, mono.SampleRate);
        Assert.Equal(AudioSampleFormat.Int24, mono.SampleFormat);
    }

    [Fact]
    public void Equality_IsByValue()
    {
        var a = new AudioFormat(48000, 2, AudioSampleFormat.Float32);
        var b = new AudioFormat(48000, 2, AudioSampleFormat.Float32);
        var c = new AudioFormat(48000, 1, AudioSampleFormat.Float32);

        Assert.Equal(a, b);
        Assert.Equal(a.GetHashCode(), b.GetHashCode());
        Assert.NotEqual(a, c);
    }

    [Theory]
    [InlineData(0, 1)]
    [InlineData(-1, 1)]
    [InlineData(48000, 0)]
    [InlineData(48000, -2)]
    public void InvalidArguments_Throw(int sampleRate, int channels) =>
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new AudioFormat(sampleRate, channels, AudioSampleFormat.Float32));
}

public class DeviceSuitabilityTests
{
    private static AudioDeviceInfo Device(int sampleRate, int channels = 2, AudioDeviceState state = AudioDeviceState.Active) =>
        new(
            "id",
            "Test microphone",
            state,
            IsDefault: true,
            IsDefaultCommunications: false,
            sampleRate > 0 ? new AudioFormat(sampleRate, channels, AudioSampleFormat.Float32) : null);

    [Theory]
    [InlineData(48000)]
    [InlineData(44100)]
    [InlineData(96000)]
    [InlineData(32000)]
    public void WideBandDevices_AreGood(int sampleRate) =>
        Assert.Equal(DeviceSuitability.Good, DeviceSuitabilityCheck.Evaluate(Device(sampleRate)).Suitability);

    [Theory]
    [InlineData(16000)]
    [InlineData(22050)]
    public void NarrowBandDevices_AreMarginal(int sampleRate) =>
        Assert.Equal(DeviceSuitability.Marginal, DeviceSuitabilityCheck.Evaluate(Device(sampleRate)).Suitability);

    [Theory]
    [InlineData(8000)]
    [InlineData(11025)]
    public void TelephonyBandDevices_AreUnsuitable(int sampleRate)
    {
        DeviceAssessment assessment = DeviceSuitabilityCheck.Evaluate(Device(sampleRate));

        Assert.Equal(DeviceSuitability.Unsuitable, assessment.Suitability);
        Assert.False(assessment.AllowsCalibration);
    }

    [Theory]
    [InlineData(AudioDeviceState.Unplugged)]
    [InlineData(AudioDeviceState.NotPresent)]
    [InlineData(AudioDeviceState.Disabled)]
    public void InactiveDevices_AreUnsuitableRegardlessOfFormat(AudioDeviceState state) =>
        Assert.Equal(
            DeviceSuitability.Unsuitable,
            DeviceSuitabilityCheck.Evaluate(Device(48000, 2, state)).Suitability);

    [Fact]
    public void UnknownFormat_IsMarginalNotFatal() =>
        Assert.Equal(DeviceSuitability.Marginal, DeviceSuitabilityCheck.Evaluate(Device(0)).Suitability);

    [Fact]
    public void MarginalAndGoodDevices_AllowCalibration()
    {
        Assert.True(DeviceSuitabilityCheck.Evaluate(Device(48000)).AllowsCalibration);
        Assert.True(DeviceSuitabilityCheck.Evaluate(Device(16000)).AllowsCalibration);
    }
}
