using System.Buffers.Binary;
using Tapit.Core.Audio;

namespace Tapit.Core.Tests;

public class SampleConverterTests
{
    [Theory]
    [InlineData(AudioSampleFormat.Int16, 2)]
    [InlineData(AudioSampleFormat.Int24, 3)]
    [InlineData(AudioSampleFormat.Int32, 4)]
    [InlineData(AudioSampleFormat.Float32, 4)]
    [InlineData(AudioSampleFormat.Float64, 8)]
    public void BytesPerSample_MatchesEncoding(AudioSampleFormat format, int expected) =>
        Assert.Equal(expected, SampleConverter.BytesPerSample(format));

    [Fact]
    public void Int16_ConvertsFullScaleAndZero()
    {
        var source = new byte[8];
        BinaryPrimitives.WriteInt16LittleEndian(source.AsSpan(0), 0);
        BinaryPrimitives.WriteInt16LittleEndian(source.AsSpan(2), short.MaxValue);
        BinaryPrimitives.WriteInt16LittleEndian(source.AsSpan(4), short.MinValue);
        BinaryPrimitives.WriteInt16LittleEndian(source.AsSpan(6), -16384);

        var destination = new float[4];
        int written = SampleConverter.ToFloat(source, destination, AudioSampleFormat.Int16);

        Assert.Equal(4, written);
        Assert.Equal(0f, destination[0]);
        Assert.Equal(0.99997f, destination[1], 4);
        Assert.Equal(-1.0f, destination[2]);
        Assert.Equal(-0.5f, destination[3]);
    }

    [Fact]
    public void Int24_SignExtendsNegativeValues()
    {
        // -1, -8388608 (full negative), +8388607 (full positive), little-endian 24-bit.
        byte[] source =
        [
            0xFF, 0xFF, 0xFF,
            0x00, 0x00, 0x80,
            0xFF, 0xFF, 0x7F,
        ];

        var destination = new float[3];
        int written = SampleConverter.ToFloat(source, destination, AudioSampleFormat.Int24);

        Assert.Equal(3, written);
        Assert.Equal(-1f / 8388608f, destination[0], 9);
        Assert.Equal(-1.0f, destination[1]);
        Assert.Equal(0.99999988f, destination[2], 6);
    }

    [Fact]
    public void Int32_ScalesToUnitRange()
    {
        var source = new byte[12];
        BinaryPrimitives.WriteInt32LittleEndian(source.AsSpan(0), int.MinValue);
        BinaryPrimitives.WriteInt32LittleEndian(source.AsSpan(4), 0);
        BinaryPrimitives.WriteInt32LittleEndian(source.AsSpan(8), int.MaxValue);

        var destination = new float[3];
        SampleConverter.ToFloat(source, destination, AudioSampleFormat.Int32);

        Assert.Equal(-1.0f, destination[0]);
        Assert.Equal(0f, destination[1]);
        Assert.True(destination[2] is > 0.999f and <= 1.0f);
    }

    [Fact]
    public void Float32_PassesThroughBitExact()
    {
        float[] values = [0f, 0.5f, -0.25f, 1f, -1f, 0.123456789f];
        var source = new byte[values.Length * 4];
        for (int i = 0; i < values.Length; i++)
        {
            BinaryPrimitives.WriteSingleLittleEndian(source.AsSpan(i * 4), values[i]);
        }

        var destination = new float[values.Length];
        SampleConverter.ToFloat(source, destination, AudioSampleFormat.Float32);

        Assert.Equal(values, destination);
    }

    [Fact]
    public void Float64_NarrowsToSingle()
    {
        var source = new byte[16];
        BinaryPrimitives.WriteDoubleLittleEndian(source.AsSpan(0), 0.25);
        BinaryPrimitives.WriteDoubleLittleEndian(source.AsSpan(8), -0.75);

        var destination = new float[2];
        SampleConverter.ToFloat(source, destination, AudioSampleFormat.Float64);

        Assert.Equal(0.25f, destination[0]);
        Assert.Equal(-0.75f, destination[1]);
    }

    [Fact]
    public void PartialTrailingSample_IsIgnoredRatherThanThrowing()
    {
        // A short or misaligned device packet must degrade to fewer samples. Throwing here
        // would take down the capture thread.
        var source = new byte[5]; // two whole Int16 samples plus one stray byte
        var destination = new float[4];

        int written = SampleConverter.ToFloat(source, destination, AudioSampleFormat.Int16);

        Assert.Equal(2, written);
    }

    [Fact]
    public void DestinationSmallerThanSource_StopsAtDestination()
    {
        var source = new byte[20];
        var destination = new float[3];

        int written = SampleConverter.ToFloat(source, destination, AudioSampleFormat.Int16);

        Assert.Equal(3, written);
    }

    [Fact]
    public void EmptySource_WritesNothing()
    {
        var destination = new float[4];
        Assert.Equal(0, SampleConverter.ToFloat([], destination, AudioSampleFormat.Float32));
    }

    [Theory]
    [InlineData(16, false, AudioSampleFormat.Int16)]
    [InlineData(24, false, AudioSampleFormat.Int24)]
    [InlineData(32, false, AudioSampleFormat.Int32)]
    [InlineData(32, true, AudioSampleFormat.Float32)]
    [InlineData(64, true, AudioSampleFormat.Float64)]
    public void TryGetSampleFormat_MapsSupportedEncodings(int bits, bool isFloat, AudioSampleFormat expected)
    {
        Assert.True(SampleConverter.TryGetSampleFormat(bits, isFloat, out AudioSampleFormat format));
        Assert.Equal(expected, format);
    }

    [Theory]
    [InlineData(8, false)]
    [InlineData(12, false)]
    [InlineData(16, true)]
    [InlineData(0, false)]
    public void TryGetSampleFormat_RejectsUnsupportedEncodings(int bits, bool isFloat) =>
        Assert.False(SampleConverter.TryGetSampleFormat(bits, isFloat, out _));
}
