using System.Buffers.Binary;

namespace Tapit.Core.Audio;

/// <summary>
/// Minimal RIFF/WAVE writer used only by the opt-in debug recorder and by tests that need a
/// fixture on disk.
/// </summary>
/// <remarks>
/// Raw audio persistence is <b>off by default</b> in Tapit. Anything that constructs this
/// type is responsible for surfacing a visible recording indicator for as long as it lives.
/// </remarks>
public sealed class WavWriter : IDisposable
{
    private readonly Stream _stream;
    private readonly bool _ownsStream;
    private readonly AudioFormat _format;
    private byte[] _scratch = [];
    private long _framesWritten;
    private bool _disposed;

    public WavWriter(string path, AudioFormat format)
        : this(File.Open(path, FileMode.Create, FileAccess.Write, FileShare.Read), format, ownsStream: true)
    {
    }

    public WavWriter(Stream stream, AudioFormat format, bool ownsStream = false)
    {
        ArgumentNullException.ThrowIfNull(stream);
        ArgumentNullException.ThrowIfNull(format);

        if (!stream.CanSeek)
        {
            throw new ArgumentException("WavWriter needs a seekable stream to finalise chunk sizes.", nameof(stream));
        }

        _stream = stream;
        _ownsStream = ownsStream;
        _format = format;

        WriteHeader(placeholderSizes: true);
    }

    public long FramesWritten => _framesWritten;

    public double DurationSeconds => _format.FramesToSeconds(_framesWritten);

    /// <summary>Appends interleaved normalised float samples, converting to the target encoding.</summary>
    public void WriteFrames(ReadOnlySpan<float> interleaved, int frameCount)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        int channels = _format.Channels;
        frameCount = Math.Min(frameCount, interleaved.Length / channels);
        if (frameCount <= 0)
        {
            return;
        }

        int sampleCount = frameCount * channels;
        int byteCount = frameCount * _format.BlockAlign;

        if (_scratch.Length < byteCount)
        {
            _scratch = new byte[byteCount];
        }

        Span<byte> destination = _scratch.AsSpan(0, byteCount);

        switch (_format.SampleFormat)
        {
            case AudioSampleFormat.Int16:
                for (int i = 0; i < sampleCount; i++)
                {
                    BinaryPrimitives.WriteInt16LittleEndian(destination[(i * 2)..], ToInt16(interleaved[i]));
                }

                break;

            case AudioSampleFormat.Int24:
                for (int i = 0; i < sampleCount; i++)
                {
                    int value = ToInt24(interleaved[i]);
                    int offset = i * 3;
                    destination[offset] = (byte)value;
                    destination[offset + 1] = (byte)(value >> 8);
                    destination[offset + 2] = (byte)(value >> 16);
                }

                break;

            case AudioSampleFormat.Int32:
                for (int i = 0; i < sampleCount; i++)
                {
                    BinaryPrimitives.WriteInt32LittleEndian(destination[(i * 4)..], ToInt32(interleaved[i]));
                }

                break;

            case AudioSampleFormat.Float32:
                for (int i = 0; i < sampleCount; i++)
                {
                    BinaryPrimitives.WriteSingleLittleEndian(destination[(i * 4)..], interleaved[i]);
                }

                break;

            default:
                throw new NotSupportedException($"{_format.SampleFormat} output is not supported.");
        }

        _stream.Write(destination);
        _framesWritten += frameCount;
    }

    private static short ToInt16(float sample) =>
        (short)Math.Clamp(MathF.Round(sample * 32767f), short.MinValue, short.MaxValue);

    private static int ToInt24(float sample) =>
        (int)Math.Clamp(MathF.Round(sample * 8388607f), -8388608f, 8388607f);

    private static int ToInt32(float sample) =>
        (int)Math.Clamp((double)MathF.Round(sample * 2147483647f), int.MinValue, int.MaxValue);

    private void WriteHeader(bool placeholderSizes)
    {
        long dataBytes = placeholderSizes ? 0 : _framesWritten * _format.BlockAlign;

        _stream.Position = 0;
        Span<byte> header = stackalloc byte[44];

        BinaryPrimitives.WriteInt32LittleEndian(header, 0x46464952);                        // "RIFF"
        BinaryPrimitives.WriteUInt32LittleEndian(header[4..], (uint)(36 + dataBytes));
        BinaryPrimitives.WriteInt32LittleEndian(header[8..], 0x45564157);                   // "WAVE"
        BinaryPrimitives.WriteInt32LittleEndian(header[12..], 0x20746D66);                  // "fmt "
        BinaryPrimitives.WriteUInt32LittleEndian(header[16..], 16);
        BinaryPrimitives.WriteUInt16LittleEndian(header[20..], (ushort)(_format.IsFloatingPoint ? 3 : 1));
        BinaryPrimitives.WriteUInt16LittleEndian(header[22..], (ushort)_format.Channels);
        BinaryPrimitives.WriteUInt32LittleEndian(header[24..], (uint)_format.SampleRate);
        BinaryPrimitives.WriteUInt32LittleEndian(header[28..], (uint)_format.AverageBytesPerSecond);
        BinaryPrimitives.WriteUInt16LittleEndian(header[32..], (ushort)_format.BlockAlign);
        BinaryPrimitives.WriteUInt16LittleEndian(header[34..], (ushort)_format.BitsPerSample);
        BinaryPrimitives.WriteInt32LittleEndian(header[36..], 0x61746164);                  // "data"
        BinaryPrimitives.WriteUInt32LittleEndian(header[40..], (uint)dataBytes);

        _stream.Write(header);

        if (!placeholderSizes)
        {
            _stream.Position = _stream.Length;
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        try
        {
            _stream.Flush();
            WriteHeader(placeholderSizes: false);
            _stream.Flush();
        }
        finally
        {
            if (_ownsStream)
            {
                _stream.Dispose();
            }
        }
    }
}
