namespace Tapit.Core.Audio;

/// <summary>
/// Sample encodings Tapit can consume. Windows capture endpoints realistically hand us
/// 32-bit float or 16-bit PCM, but 24- and 32-bit integer formats appear on interfaces
/// and in WAV files, so all four are handled by <see cref="SampleConverter"/>.
/// </summary>
public enum AudioSampleFormat
{
    Int16,
    Int24,
    Int32,
    Float32,
    Float64,
}

/// <summary>
/// Immutable description of a PCM stream.
/// </summary>
/// <remarks>
/// Nothing in Tapit assumes a particular sample rate. Every DSP parameter is expressed in
/// milliseconds or hertz and converted through this type at runtime, so a 16 kHz webcam
/// microphone and a 48 kHz laptop array run the same code with the same tuning.
/// </remarks>
public sealed class AudioFormat : IEquatable<AudioFormat>
{
    public AudioFormat(int sampleRate, int channels, AudioSampleFormat sampleFormat)
    {
        if (sampleRate <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(sampleRate), sampleRate, "Sample rate must be positive.");
        }

        if (channels <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(channels), channels, "Channel count must be positive.");
        }

        SampleRate = sampleRate;
        Channels = channels;
        SampleFormat = sampleFormat;
    }

    public int SampleRate { get; }

    public int Channels { get; }

    public AudioSampleFormat SampleFormat { get; }

    public int BytesPerSample => SampleConverter.BytesPerSample(SampleFormat);

    public int BitsPerSample => BytesPerSample * 8;

    /// <summary>Bytes occupied by one frame (one sample for every channel).</summary>
    public int BlockAlign => BytesPerSample * Channels;

    public int AverageBytesPerSecond => BlockAlign * SampleRate;

    public double NyquistHz => SampleRate / 2.0;

    public bool IsFloatingPoint =>
        SampleFormat is AudioSampleFormat.Float32 or AudioSampleFormat.Float64;

    /// <summary>Rounds a duration in milliseconds to a whole number of frames.</summary>
    public int MillisecondsToFrames(double milliseconds) =>
        checked((int)Math.Round(milliseconds * SampleRate / 1000.0, MidpointRounding.AwayFromZero));

    public double FramesToMilliseconds(long frames) => frames * 1000.0 / SampleRate;

    public double FramesToSeconds(long frames) => (double)frames / SampleRate;

    /// <summary>
    /// Returns the same format with a different channel count, used when describing the
    /// mono mixdown that the detector actually consumes.
    /// </summary>
    public AudioFormat WithChannels(int channels) => new(SampleRate, channels, SampleFormat);

    public bool Equals(AudioFormat? other) =>
        other is not null &&
        SampleRate == other.SampleRate &&
        Channels == other.Channels &&
        SampleFormat == other.SampleFormat;

    public override bool Equals(object? obj) => Equals(obj as AudioFormat);

    public override int GetHashCode() => HashCode.Combine(SampleRate, Channels, (int)SampleFormat);

    public override string ToString() =>
        $"{SampleRate} Hz, {Channels} ch, {SampleFormat}";
}
