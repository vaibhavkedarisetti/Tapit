using System.Buffers.Binary;
using System.Runtime.InteropServices;

namespace Tapit.Core.Audio;

/// <summary>
/// Converts raw device bytes into normalised <see cref="float"/> samples in [-1, 1].
/// </summary>
/// <remarks>
/// <para>
/// This runs on the realtime capture thread. Every method is allocation-free and takes
/// caller-provided spans; nothing here may throw on the hot path for ordinary input.
/// </para>
/// <para>
/// Integer formats are scaled by 2^(bits-1) rather than (2^(bits-1) - 1). That is the
/// convention that maps integer zero to float zero exactly and keeps the conversion a
/// pure shift in magnitude, at the cost of full-scale negative reaching exactly -1.0 and
/// full-scale positive reaching 1.0 - 1 ulp. Clip detection accounts for this.
/// </para>
/// </remarks>
public static class SampleConverter
{
    private const float Int16Scale = 1f / 32768f;
    private const float Int24Scale = 1f / 8388608f;
    private const float Int32Scale = 1f / 2147483648f;

    public static int BytesPerSample(AudioSampleFormat format) => format switch
    {
        AudioSampleFormat.Int16 => 2,
        AudioSampleFormat.Int24 => 3,
        AudioSampleFormat.Int32 => 4,
        AudioSampleFormat.Float32 => 4,
        AudioSampleFormat.Float64 => 8,
        _ => throw new ArgumentOutOfRangeException(nameof(format), format, "Unsupported sample format."),
    };

    /// <summary>
    /// Number of whole samples represented by <paramref name="byteCount"/> bytes.
    /// </summary>
    public static int SampleCount(int byteCount, AudioSampleFormat format) =>
        byteCount / BytesPerSample(format);

    /// <summary>
    /// Converts <paramref name="source"/> into <paramref name="destination"/>, returning the
    /// number of samples written. Conversion stops at whichever span runs out first, so a
    /// short or misaligned device packet degrades to fewer samples instead of an exception.
    /// </summary>
    public static int ToFloat(ReadOnlySpan<byte> source, Span<float> destination, AudioSampleFormat format)
    {
        int bytesPerSample = BytesPerSample(format);
        int count = Math.Min(source.Length / bytesPerSample, destination.Length);
        if (count == 0)
        {
            return 0;
        }

        switch (format)
        {
            case AudioSampleFormat.Int16:
                ConvertInt16(source, destination, count);
                break;
            case AudioSampleFormat.Int24:
                ConvertInt24(source, destination, count);
                break;
            case AudioSampleFormat.Int32:
                ConvertInt32(source, destination, count);
                break;
            case AudioSampleFormat.Float32:
                ConvertFloat32(source, destination, count);
                break;
            case AudioSampleFormat.Float64:
                ConvertFloat64(source, destination, count);
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(format), format, "Unsupported sample format.");
        }

        return count;
    }

    private static void ConvertInt16(ReadOnlySpan<byte> source, Span<float> destination, int count)
    {
        if (BitConverter.IsLittleEndian)
        {
            ReadOnlySpan<short> samples = MemoryMarshal.Cast<byte, short>(source)[..count];
            for (int i = 0; i < count; i++)
            {
                destination[i] = samples[i] * Int16Scale;
            }

            return;
        }

        for (int i = 0; i < count; i++)
        {
            destination[i] = BinaryPrimitives.ReadInt16LittleEndian(source.Slice(i * 2, 2)) * Int16Scale;
        }
    }

    private static void ConvertInt24(ReadOnlySpan<byte> source, Span<float> destination, int count)
    {
        for (int i = 0; i < count; i++)
        {
            int offset = i * 3;

            // Little-endian 24-bit two's complement: sign-extend through the top byte.
            int value = source[offset]
                        | (source[offset + 1] << 8)
                        | ((sbyte)source[offset + 2] << 16);

            destination[i] = value * Int24Scale;
        }
    }

    private static void ConvertInt32(ReadOnlySpan<byte> source, Span<float> destination, int count)
    {
        if (BitConverter.IsLittleEndian)
        {
            ReadOnlySpan<int> samples = MemoryMarshal.Cast<byte, int>(source)[..count];
            for (int i = 0; i < count; i++)
            {
                destination[i] = samples[i] * Int32Scale;
            }

            return;
        }

        for (int i = 0; i < count; i++)
        {
            destination[i] = BinaryPrimitives.ReadInt32LittleEndian(source.Slice(i * 4, 4)) * Int32Scale;
        }
    }

    private static void ConvertFloat32(ReadOnlySpan<byte> source, Span<float> destination, int count)
    {
        if (BitConverter.IsLittleEndian)
        {
            MemoryMarshal.Cast<byte, float>(source)[..count].CopyTo(destination);
            return;
        }

        for (int i = 0; i < count; i++)
        {
            destination[i] = BinaryPrimitives.ReadSingleLittleEndian(source.Slice(i * 4, 4));
        }
    }

    private static void ConvertFloat64(ReadOnlySpan<byte> source, Span<float> destination, int count)
    {
        for (int i = 0; i < count; i++)
        {
            double value = BitConverter.IsLittleEndian
                ? MemoryMarshal.Cast<byte, double>(source)[i]
                : BinaryPrimitives.ReadDoubleLittleEndian(source.Slice(i * 8, 8));

            destination[i] = (float)value;
        }
    }

    /// <summary>
    /// Maps a WAV / WAVEFORMATEX description onto an <see cref="AudioSampleFormat"/>.
    /// Returns <see langword="false"/> for encodings Tapit will not attempt to decode,
    /// which is preferable to silently misinterpreting compressed audio as PCM.
    /// </summary>
    public static bool TryGetSampleFormat(int bitsPerSample, bool isFloatingPoint, out AudioSampleFormat format)
    {
        if (isFloatingPoint)
        {
            switch (bitsPerSample)
            {
                case 32:
                    format = AudioSampleFormat.Float32;
                    return true;
                case 64:
                    format = AudioSampleFormat.Float64;
                    return true;
                default:
                    format = default;
                    return false;
            }
        }

        switch (bitsPerSample)
        {
            case 16:
                format = AudioSampleFormat.Int16;
                return true;
            case 24:
                format = AudioSampleFormat.Int24;
                return true;
            case 32:
                format = AudioSampleFormat.Int32;
                return true;
            default:
                format = default;
                return false;
        }
    }
}
