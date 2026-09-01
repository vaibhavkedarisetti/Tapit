using System.Runtime.InteropServices;
using Tapit.Core.Audio;

namespace Tapit.Audio.Wasapi;

/// <summary>
/// Translates between native <c>WAVEFORMATEX</c> / <c>WAVEFORMATEXTENSIBLE</c> memory and
/// the portable <see cref="AudioFormat"/> the rest of Tapit uses.
/// </summary>
internal static class WaveFormatMarshaler
{
    /// <summary>
    /// Reads a native format block. Returns <see langword="null"/> for encodings Tapit does
    /// not decode, rather than guessing.
    /// </summary>
    public static AudioFormat? TryRead(IntPtr formatPointer)
    {
        if (formatPointer == IntPtr.Zero)
        {
            return null;
        }

        WaveFormatEx header = Marshal.PtrToStructure<WaveFormatEx>(formatPointer);
        return TryRead(header, formatPointer);
    }

    private static AudioFormat? TryRead(WaveFormatEx header, IntPtr formatPointer)
    {
        ushort formatTag = header.FormatTag;

        if (formatTag == WasapiConstants.WaveFormatExtensible)
        {
            // WAVE_FORMAT_EXTENSIBLE carries the real encoding in its sub-format GUID. The
            // laptop microphones this application targets almost always report EXTENSIBLE.
            if (header.ExtraSize < 22)
            {
                return null;
            }

            WaveFormatExtensible extensible = Marshal.PtrToStructure<WaveFormatExtensible>(formatPointer);

            if (extensible.SubFormat == WasapiGuids.KsDataFormatSubtypeIeeeFloat)
            {
                formatTag = WasapiConstants.WaveFormatIeeeFloat;
            }
            else if (extensible.SubFormat == WasapiGuids.KsDataFormatSubtypePcm)
            {
                formatTag = WasapiConstants.WaveFormatPcm;
            }
            else
            {
                return null;
            }
        }

        bool isFloat = formatTag == WasapiConstants.WaveFormatIeeeFloat;
        if (formatTag != WasapiConstants.WaveFormatPcm && !isFloat)
        {
            return null;
        }

        if (!SampleConverter.TryGetSampleFormat(header.BitsPerSample, isFloat, out AudioSampleFormat sampleFormat))
        {
            return null;
        }

        if (header.Channels == 0 || header.SamplesPerSecond == 0)
        {
            return null;
        }

        return new AudioFormat((int)header.SamplesPerSecond, header.Channels, sampleFormat);
    }

    /// <summary>
    /// Reads a WAVEFORMATEX blob that came out of the endpoint property store, which is a
    /// copy in our own memory rather than a CoTaskMem allocation.
    /// </summary>
    public static AudioFormat? TryReadBlob(IntPtr blob, int blobSize)
    {
        if (blob == IntPtr.Zero || blobSize < Marshal.SizeOf<WaveFormatEx>())
        {
            return null;
        }

        return TryRead(blob);
    }

    /// <summary>
    /// Describes a native format for diagnostics, including whether the endpoint reports
    /// WAVE_FORMAT_EXTENSIBLE and what channel mask it advertises.
    /// </summary>
    public static string Describe(IntPtr formatPointer)
    {
        if (formatPointer == IntPtr.Zero)
        {
            return "(null)";
        }

        WaveFormatEx header = Marshal.PtrToStructure<WaveFormatEx>(formatPointer);
        string tag = header.FormatTag switch
        {
            WasapiConstants.WaveFormatPcm => "PCM",
            WasapiConstants.WaveFormatIeeeFloat => "IEEE float",
            WasapiConstants.WaveFormatExtensible => "EXTENSIBLE",
            _ => $"tag 0x{header.FormatTag:X4}",
        };

        string mask = string.Empty;
        if (header.FormatTag == WasapiConstants.WaveFormatExtensible && header.ExtraSize >= 22)
        {
            WaveFormatExtensible extensible = Marshal.PtrToStructure<WaveFormatExtensible>(formatPointer);
            string subType = extensible.SubFormat == WasapiGuids.KsDataFormatSubtypeIeeeFloat ? "float"
                : extensible.SubFormat == WasapiGuids.KsDataFormatSubtypePcm ? "PCM"
                : extensible.SubFormat.ToString();
            mask = $", sub={subType}, mask=0x{extensible.ChannelMask:X}";
        }

        return $"{header.SamplesPerSecond} Hz, {header.Channels} ch, {header.BitsPerSample}-bit {tag}{mask}";
    }
}
