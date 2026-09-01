using System.Buffers.Binary;
using Tapit.Core.Audio;

namespace Tapit.Core.Tests;

public class WavRoundTripTests
{
    private static float[] Ramp(int frames, int channels)
    {
        var data = new float[frames * channels];
        for (int f = 0; f < frames; f++)
        {
            for (int c = 0; c < channels; c++)
            {
                data[(f * channels) + c] = (f / (float)frames * 2f - 1f) * (c == 0 ? 1f : 0.5f);
            }
        }

        return data;
    }

    [Fact]
    public void Float32_RoundTripsBitExact()
    {
        var format = new AudioFormat(48000, 1, AudioSampleFormat.Float32);
        float[] input = Ramp(500, 1);

        using var stream = new MemoryStream();
        using (var writer = new WavWriter(stream, format, ownsStream: false))
        {
            writer.WriteFrames(input, 500);
        }

        stream.Position = 0;
        using var reader = new WavReader(stream);

        Assert.Equal(format, reader.Format);
        Assert.Equal(500, reader.TotalFrames);
        Assert.Equal(input, reader.ReadAll());
    }

    [Theory]
    [InlineData(AudioSampleFormat.Int16, 1e-4)]
    [InlineData(AudioSampleFormat.Int24, 1e-6)]
    [InlineData(AudioSampleFormat.Int32, 1e-6)]
    public void IntegerEncodings_RoundTripWithinQuantisationError(AudioSampleFormat sampleFormat, double tolerance)
    {
        var format = new AudioFormat(44100, 1, sampleFormat);
        float[] input = Ramp(256, 1);

        using var stream = new MemoryStream();
        using (var writer = new WavWriter(stream, format, ownsStream: false))
        {
            writer.WriteFrames(input, 256);
        }

        stream.Position = 0;
        using var reader = new WavReader(stream);
        float[] output = reader.ReadAll();

        Assert.Equal(input.Length, output.Length);
        for (int i = 0; i < input.Length; i++)
        {
            Assert.True(
                Math.Abs(input[i] - output[i]) <= tolerance,
                $"sample {i}: expected {input[i]}, got {output[i]}");
        }
    }

    [Fact]
    public void MultiChannel_PreservesInterleaving()
    {
        var format = new AudioFormat(48000, 2, AudioSampleFormat.Float32);
        float[] input = Ramp(300, 2);

        using var stream = new MemoryStream();
        using (var writer = new WavWriter(stream, format, ownsStream: false))
        {
            writer.WriteFrames(input, 300);
        }

        stream.Position = 0;
        using var reader = new WavReader(stream);

        Assert.Equal(2, reader.Format.Channels);
        Assert.Equal(300, reader.TotalFrames);
        Assert.Equal(input, reader.ReadAll());
    }

    [Fact]
    public void HeaderSizes_AreFinalisedOnDispose()
    {
        var format = new AudioFormat(48000, 1, AudioSampleFormat.Int16);

        using var stream = new MemoryStream();
        using (var writer = new WavWriter(stream, format, ownsStream: false))
        {
            writer.WriteFrames(new float[100], 100);
            Assert.Equal(100, writer.FramesWritten);
        }

        byte[] bytes = stream.ToArray();
        Assert.Equal(44 + 200, bytes.Length);
        Assert.Equal((uint)(36 + 200), BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(4)));
        Assert.Equal((uint)200, BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(40)));
    }

    [Fact]
    public void ReadFrames_StreamsInChunksAndStopsAtEnd()
    {
        var format = new AudioFormat(48000, 1, AudioSampleFormat.Float32);
        float[] input = Ramp(1000, 1);

        using var stream = new MemoryStream();
        using (var writer = new WavWriter(stream, format, ownsStream: false))
        {
            writer.WriteFrames(input, 1000);
        }

        stream.Position = 0;
        using var reader = new WavReader(stream);

        var block = new float[128];
        int total = 0;
        int frames;
        while ((frames = reader.ReadFrames(block, 128)) > 0)
        {
            total += frames;
        }

        Assert.Equal(1000, total);
        Assert.Equal(0, reader.FramesRemaining);
        Assert.Equal(0, reader.ReadFrames(block, 128));
    }

    [Fact]
    public void Rewind_RestartsFromTheFirstFrame()
    {
        var format = new AudioFormat(48000, 1, AudioSampleFormat.Float32);

        using var stream = new MemoryStream();
        using (var writer = new WavWriter(stream, format, ownsStream: false))
        {
            writer.WriteFrames([0.25f, 0.5f, 0.75f], 3);
        }

        stream.Position = 0;
        using var reader = new WavReader(stream);

        var first = new float[3];
        reader.ReadFrames(first, 3);
        reader.Rewind();

        var second = new float[3];
        Assert.Equal(3, reader.ReadFrames(second, 3));
        Assert.Equal(first, second);
    }

    [Fact]
    public void UnknownChunks_AreSkipped()
    {
        // Real-world WAV files carry LIST/INFO and other chunks between 'fmt ' and 'data'.
        using var stream = new MemoryStream();
        using (var w = new BinaryWriter(stream, System.Text.Encoding.ASCII, leaveOpen: true))
        {
            const int dataBytes = 8;
            w.Write("RIFF"u8.ToArray());
            w.Write(4 + 24 + 12 + 8 + dataBytes);
            w.Write("WAVE"u8.ToArray());

            w.Write("fmt "u8.ToArray());
            w.Write(16);
            w.Write((ushort)1);      // PCM
            w.Write((ushort)1);      // mono
            w.Write(48000);
            w.Write(96000);
            w.Write((ushort)2);
            w.Write((ushort)16);

            w.Write("LIST"u8.ToArray());
            w.Write(4);
            w.Write("INFO"u8.ToArray());

            w.Write("data"u8.ToArray());
            w.Write(dataBytes);
            w.Write((short)0);
            w.Write((short)16384);
            w.Write((short)-16384);
            w.Write((short)0);
        }

        stream.Position = 0;
        using var reader = new WavReader(stream);

        Assert.Equal(48000, reader.Format.SampleRate);
        Assert.Equal(4, reader.TotalFrames);

        float[] samples = reader.ReadAll();
        Assert.Equal(0f, samples[0]);
        Assert.Equal(0.5f, samples[1], 4);
        Assert.Equal(-0.5f, samples[2], 4);
    }

    [Fact]
    public void ExtensibleFormat_ResolvesSubFormat()
    {
        using var stream = new MemoryStream();
        using (var w = new BinaryWriter(stream, System.Text.Encoding.ASCII, leaveOpen: true))
        {
            const int dataBytes = 8;
            w.Write("RIFF"u8.ToArray());
            w.Write(4 + 48 + 8 + dataBytes);
            w.Write("WAVE"u8.ToArray());

            w.Write("fmt "u8.ToArray());
            w.Write(40);
            w.Write((ushort)0xFFFE); // WAVE_FORMAT_EXTENSIBLE
            w.Write((ushort)1);
            w.Write(48000);
            w.Write(192000);
            w.Write((ushort)4);
            w.Write((ushort)32);
            w.Write((ushort)22);     // cbSize
            w.Write((ushort)32);     // valid bits
            w.Write(4);              // channel mask
            w.Write(new Guid("00000003-0000-0010-8000-00aa00389b71").ToByteArray()); // IEEE float

            w.Write("data"u8.ToArray());
            w.Write(dataBytes);
            w.Write(0.5f);
            w.Write(-0.25f);
        }

        stream.Position = 0;
        using var reader = new WavReader(stream);

        Assert.Equal(AudioSampleFormat.Float32, reader.Format.SampleFormat);
        Assert.Equal([0.5f, -0.25f], reader.ReadAll());
    }

    [Fact]
    public void NonRiffFile_IsRejected()
    {
        using var stream = new MemoryStream(new byte[64]);
        Assert.Throws<InvalidDataException>(() => new WavReader(stream));
    }

    [Fact]
    public void CompressedFormat_IsRejectedRatherThanMisread()
    {
        using var stream = new MemoryStream();
        using (var w = new BinaryWriter(stream, System.Text.Encoding.ASCII, leaveOpen: true))
        {
            w.Write("RIFF"u8.ToArray());
            w.Write(36);
            w.Write("WAVE"u8.ToArray());
            w.Write("fmt "u8.ToArray());
            w.Write(16);
            w.Write((ushort)0x0055); // MPEG Layer-3
            w.Write((ushort)2);
            w.Write(44100);
            w.Write(16000);
            w.Write((ushort)1);
            w.Write((ushort)0);
            w.Write("data"u8.ToArray());
            w.Write(0);
        }

        stream.Position = 0;
        Assert.Throws<NotSupportedException>(() => new WavReader(stream));
    }
}
