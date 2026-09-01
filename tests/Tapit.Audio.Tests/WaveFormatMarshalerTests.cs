using System.Runtime.InteropServices;
using Tapit.Audio.Wasapi;
using Tapit.Core.Audio;

namespace Tapit.Audio.Tests;

/// <summary>
/// Parses real native format blocks. <c>GetMixFormat</c> hands back exactly this memory, so
/// getting it wrong means capturing at the wrong rate or misreading every sample.
/// </summary>
public class WaveFormatMarshalerTests
{
    private static IntPtr AllocWaveFormatEx(
        ushort formatTag, ushort channels, uint sampleRate, ushort bits, ushort extraSize = 0)
    {
        var header = new WaveFormatEx
        {
            FormatTag = formatTag,
            Channels = channels,
            SamplesPerSecond = sampleRate,
            AverageBytesPerSecond = sampleRate * channels * (uint)(bits / 8),
            BlockAlign = (ushort)(channels * (bits / 8)),
            BitsPerSample = bits,
            ExtraSize = extraSize,
        };

        IntPtr memory = Marshal.AllocCoTaskMem(Marshal.SizeOf<WaveFormatExtensible>());
        Marshal.StructureToPtr(header, memory, fDeleteOld: false);
        return memory;
    }

    private static IntPtr AllocExtensible(uint sampleRate, ushort channels, ushort bits, Guid subFormat)
    {
        IntPtr memory = AllocWaveFormatEx(0xFFFE, channels, sampleRate, bits, extraSize: 22);

        var extensible = Marshal.PtrToStructure<WaveFormatExtensible>(memory);
        extensible.ValidBitsPerSample = bits;
        extensible.ChannelMask = channels == 1 ? 4u : 3u;
        extensible.SubFormat = subFormat;
        Marshal.StructureToPtr(extensible, memory, fDeleteOld: false);

        return memory;
    }

    [Fact]
    public void PlainPcm_IsParsed()
    {
        IntPtr memory = AllocWaveFormatEx(1, 2, 44100, 16);
        try
        {
            AudioFormat? format = WaveFormatMarshaler.TryRead(memory);

            Assert.NotNull(format);
            Assert.Equal(44100, format.SampleRate);
            Assert.Equal(2, format.Channels);
            Assert.Equal(AudioSampleFormat.Int16, format.SampleFormat);
        }
        finally
        {
            Marshal.FreeCoTaskMem(memory);
        }
    }

    [Fact]
    public void PlainIeeeFloat_IsParsed()
    {
        IntPtr memory = AllocWaveFormatEx(3, 1, 48000, 32);
        try
        {
            AudioFormat? format = WaveFormatMarshaler.TryRead(memory);

            Assert.NotNull(format);
            Assert.Equal(AudioSampleFormat.Float32, format.SampleFormat);
        }
        finally
        {
            Marshal.FreeCoTaskMem(memory);
        }
    }

    [Fact]
    public void Extensible_ResolvesFloatSubFormat()
    {
        // This is the shape a real Windows laptop microphone reports.
        IntPtr memory = AllocExtensible(48000, 2, 32, WasapiGuids.KsDataFormatSubtypeIeeeFloat);
        try
        {
            AudioFormat? format = WaveFormatMarshaler.TryRead(memory);

            Assert.NotNull(format);
            Assert.Equal(48000, format.SampleRate);
            Assert.Equal(2, format.Channels);
            Assert.Equal(AudioSampleFormat.Float32, format.SampleFormat);
        }
        finally
        {
            Marshal.FreeCoTaskMem(memory);
        }
    }

    [Fact]
    public void Extensible_ResolvesPcmSubFormat()
    {
        IntPtr memory = AllocExtensible(48000, 2, 24, WasapiGuids.KsDataFormatSubtypePcm);
        try
        {
            AudioFormat? format = WaveFormatMarshaler.TryRead(memory);

            Assert.NotNull(format);
            Assert.Equal(AudioSampleFormat.Int24, format.SampleFormat);
        }
        finally
        {
            Marshal.FreeCoTaskMem(memory);
        }
    }

    [Fact]
    public void UnknownSubFormat_IsRejectedRatherThanGuessed()
    {
        IntPtr memory = AllocExtensible(48000, 2, 32, Guid.NewGuid());
        try
        {
            Assert.Null(WaveFormatMarshaler.TryRead(memory));
        }
        finally
        {
            Marshal.FreeCoTaskMem(memory);
        }
    }

    [Fact]
    public void CompressedFormatTag_IsRejected()
    {
        IntPtr memory = AllocWaveFormatEx(0x0055, 2, 44100, 0); // MPEG Layer-3
        try
        {
            Assert.Null(WaveFormatMarshaler.TryRead(memory));
        }
        finally
        {
            Marshal.FreeCoTaskMem(memory);
        }
    }

    [Theory]
    [InlineData((ushort)8)]
    [InlineData((ushort)0)]
    public void UnsupportedBitDepths_AreRejected(ushort bits)
    {
        IntPtr memory = AllocWaveFormatEx(1, 1, 48000, bits);
        try
        {
            Assert.Null(WaveFormatMarshaler.TryRead(memory));
        }
        finally
        {
            Marshal.FreeCoTaskMem(memory);
        }
    }

    [Fact]
    public void ZeroChannelsOrRate_IsRejected()
    {
        IntPtr noChannels = AllocWaveFormatEx(1, 0, 48000, 16);
        IntPtr noRate = AllocWaveFormatEx(1, 2, 0, 16);

        try
        {
            Assert.Null(WaveFormatMarshaler.TryRead(noChannels));
            Assert.Null(WaveFormatMarshaler.TryRead(noRate));
        }
        finally
        {
            Marshal.FreeCoTaskMem(noChannels);
            Marshal.FreeCoTaskMem(noRate);
        }
    }

    [Fact]
    public void ExtensibleWithTruncatedExtension_IsRejected()
    {
        // Claims EXTENSIBLE but does not carry the 22-byte extension.
        IntPtr memory = AllocWaveFormatEx(0xFFFE, 2, 48000, 32, extraSize: 0);
        try
        {
            Assert.Null(WaveFormatMarshaler.TryRead(memory));
        }
        finally
        {
            Marshal.FreeCoTaskMem(memory);
        }
    }

    [Fact]
    public void NullPointer_IsHandled()
    {
        Assert.Null(WaveFormatMarshaler.TryRead(IntPtr.Zero));
        Assert.Equal("(null)", WaveFormatMarshaler.Describe(IntPtr.Zero));
        Assert.Null(WaveFormatMarshaler.TryReadBlob(IntPtr.Zero, 40));
    }

    [Fact]
    public void UndersizedBlob_IsRejected()
    {
        IntPtr memory = AllocWaveFormatEx(1, 2, 48000, 16);
        try
        {
            Assert.Null(WaveFormatMarshaler.TryReadBlob(memory, 4));
        }
        finally
        {
            Marshal.FreeCoTaskMem(memory);
        }
    }

    [Fact]
    public void Describe_ReportsTheKeyFieldsForDiagnostics()
    {
        IntPtr memory = AllocExtensible(48000, 2, 32, WasapiGuids.KsDataFormatSubtypeIeeeFloat);
        try
        {
            string description = WaveFormatMarshaler.Describe(memory);

            Assert.Contains("48000 Hz", description, StringComparison.Ordinal);
            Assert.Contains("2 ch", description, StringComparison.Ordinal);
            Assert.Contains("EXTENSIBLE", description, StringComparison.Ordinal);
            Assert.Contains("float", description, StringComparison.Ordinal);
        }
        finally
        {
            Marshal.FreeCoTaskMem(memory);
        }
    }
}
