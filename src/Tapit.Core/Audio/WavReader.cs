using System.Buffers.Binary;

namespace Tapit.Core.Audio;

/// <summary>
/// Streaming RIFF/WAVE reader for uncompressed PCM and IEEE float.
/// </summary>
/// <remarks>
/// Deliberately minimal and strict: it decodes exactly the encodings
/// <see cref="SampleConverter"/> understands and refuses everything else, rather than
/// guessing at a compressed payload and feeding noise into the detector. It exists so the
/// replay tool and the test suite can drive the real pipeline with no microphone attached.
/// </remarks>
public sealed class WavReader : IDisposable
{
    private const int RiffId = 0x46464952;  // "RIFF"
    private const int WaveId = 0x45564157;  // "WAVE"
    private const int FmtId = 0x20746D66;   // "fmt "
    private const int DataId = 0x61746164;  // "data"

    private const ushort WaveFormatPcm = 1;
    private const ushort WaveFormatIeeeFloat = 3;
    private const ushort WaveFormatExtensible = 0xFFFE;

    private readonly Stream _stream;
    private readonly bool _ownsStream;
    private readonly long _dataStart;
    private readonly long _dataLength;
    private byte[] _scratch = [];

    private long _framesRead;

    public WavReader(string path)
        : this(File.Open(path, FileMode.Open, FileAccess.Read, FileShare.Read), ownsStream: true)
    {
    }

    public WavReader(Stream stream, bool ownsStream = false)
    {
        ArgumentNullException.ThrowIfNull(stream);

        _stream = stream;
        _ownsStream = ownsStream;

        (Format, _dataStart, _dataLength) = ParseHeader(stream);
        TotalFrames = _dataLength / Format.BlockAlign;

        stream.Position = _dataStart;
    }

    public AudioFormat Format { get; }

    public long TotalFrames { get; }

    public long FramesRemaining => TotalFrames - _framesRead;

    public double DurationSeconds => Format.FramesToSeconds(TotalFrames);

    /// <summary>
    /// Reads up to <paramref name="frameCount"/> frames into <paramref name="destination"/> as
    /// interleaved normalised floats.
    /// </summary>
    /// <returns>Frames actually read; 0 at end of stream.</returns>
    public int ReadFrames(Span<float> destination, int frameCount)
    {
        int channels = Format.Channels;
        frameCount = (int)Math.Min(frameCount, FramesRemaining);
        frameCount = Math.Min(frameCount, destination.Length / channels);

        if (frameCount <= 0)
        {
            return 0;
        }

        int byteCount = frameCount * Format.BlockAlign;
        if (_scratch.Length < byteCount)
        {
            _scratch = new byte[byteCount];
        }

        int read = ReadExactly(_stream, _scratch.AsSpan(0, byteCount));
        int framesAvailable = read / Format.BlockAlign;
        if (framesAvailable <= 0)
        {
            return 0;
        }

        SampleConverter.ToFloat(
            _scratch.AsSpan(0, framesAvailable * Format.BlockAlign),
            destination[..(framesAvailable * channels)],
            Format.SampleFormat);

        _framesRead += framesAvailable;
        return framesAvailable;
    }

    /// <summary>Reads the whole file into a single interleaved array. Test and tooling helper.</summary>
    public float[] ReadAll()
    {
        var result = new float[checked(TotalFrames * Format.Channels)];
        int offset = 0;
        int frames;

        const int chunkFrames = 8192;
        while ((frames = ReadFrames(result.AsSpan(offset), chunkFrames)) > 0)
        {
            offset += frames * Format.Channels;
        }

        return result;
    }

    public void Rewind()
    {
        _stream.Position = _dataStart;
        _framesRead = 0;
    }

    private static (AudioFormat Format, long DataStart, long DataLength) ParseHeader(Stream stream)
    {
        Span<byte> header = stackalloc byte[12];
        if (ReadExactly(stream, header) != 12)
        {
            throw new InvalidDataException("File is too short to be a WAV file.");
        }

        if (BinaryPrimitives.ReadInt32LittleEndian(header) != RiffId ||
            BinaryPrimitives.ReadInt32LittleEndian(header[8..]) != WaveId)
        {
            throw new InvalidDataException("Not a RIFF/WAVE file.");
        }

        AudioFormat? format = null;
        Span<byte> chunkHeader = stackalloc byte[8];

        while (ReadExactly(stream, chunkHeader) == 8)
        {
            int chunkId = BinaryPrimitives.ReadInt32LittleEndian(chunkHeader);
            uint chunkSize = BinaryPrimitives.ReadUInt32LittleEndian(chunkHeader[4..]);

            if (chunkId == FmtId)
            {
                format = ParseFormatChunk(stream, chunkSize);
            }
            else if (chunkId == DataId)
            {
                if (format is null)
                {
                    throw new InvalidDataException("WAV data chunk appeared before the format chunk.");
                }

                long dataStart = stream.Position;
                long available = stream.Length - dataStart;
                long dataLength = Math.Min(chunkSize, available);

                return (format, dataStart, dataLength);
            }
            else
            {
                // Skip unknown chunks (LIST, fact, bext, ...), honouring RIFF word alignment.
                Skip(stream, chunkSize + (chunkSize & 1));
            }
        }

        throw new InvalidDataException("WAV file contains no data chunk.");
    }

    private static AudioFormat ParseFormatChunk(Stream stream, uint chunkSize)
    {
        if (chunkSize < 16)
        {
            throw new InvalidDataException("WAV format chunk is truncated.");
        }

        Span<byte> fmt = stackalloc byte[16];
        if (ReadExactly(stream, fmt) != 16)
        {
            throw new InvalidDataException("WAV format chunk is truncated.");
        }

        ushort formatTag = BinaryPrimitives.ReadUInt16LittleEndian(fmt);
        ushort channels = BinaryPrimitives.ReadUInt16LittleEndian(fmt[2..]);
        int sampleRate = BinaryPrimitives.ReadInt32LittleEndian(fmt[4..]);
        ushort bitsPerSample = BinaryPrimitives.ReadUInt16LittleEndian(fmt[14..]);

        long consumed = 16;

        if (formatTag == WaveFormatExtensible)
        {
            if (chunkSize < 40)
            {
                throw new InvalidDataException("WAVE_FORMAT_EXTENSIBLE chunk is truncated.");
            }

            Span<byte> extension = stackalloc byte[24];
            if (ReadExactly(stream, extension) != 24)
            {
                throw new InvalidDataException("WAVE_FORMAT_EXTENSIBLE chunk is truncated.");
            }

            consumed += 24;

            // The sub-format GUID's first four bytes carry the underlying format tag.
            formatTag = BinaryPrimitives.ReadUInt16LittleEndian(extension[8..]);
        }

        Skip(stream, chunkSize - consumed + (chunkSize & 1));

        bool isFloat = formatTag == WaveFormatIeeeFloat;
        if (formatTag != WaveFormatPcm && !isFloat)
        {
            throw new NotSupportedException(
                $"WAV format tag 0x{formatTag:X4} is not uncompressed PCM or IEEE float. " +
                "Tapit reads only uncompressed audio.");
        }

        if (!SampleConverter.TryGetSampleFormat(bitsPerSample, isFloat, out AudioSampleFormat sampleFormat))
        {
            throw new NotSupportedException($"{bitsPerSample}-bit {(isFloat ? "float" : "PCM")} is not supported.");
        }

        return new AudioFormat(sampleRate, channels, sampleFormat);
    }

    private static void Skip(Stream stream, long count)
    {
        if (count <= 0)
        {
            return;
        }

        if (stream.CanSeek)
        {
            stream.Position += count;
            return;
        }

        Span<byte> sink = stackalloc byte[256];
        while (count > 0)
        {
            int chunk = (int)Math.Min(count, sink.Length);
            int read = stream.Read(sink[..chunk]);
            if (read <= 0)
            {
                return;
            }

            count -= read;
        }
    }

    private static int ReadExactly(Stream stream, Span<byte> destination)
    {
        int total = 0;
        while (total < destination.Length)
        {
            int read = stream.Read(destination[total..]);
            if (read <= 0)
            {
                break;
            }

            total += read;
        }

        return total;
    }

    public void Dispose()
    {
        if (_ownsStream)
        {
            _stream.Dispose();
        }
    }
}
