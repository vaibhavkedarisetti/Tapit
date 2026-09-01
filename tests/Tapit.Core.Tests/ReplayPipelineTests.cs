using Tapit.Core.Audio;

namespace Tapit.Core.Tests;

/// <summary>
/// Exercises the offline path the replay tool and every future DSP experiment depend on:
/// a WAV file driving the real ring buffer through the real consumer, with no microphone.
/// </summary>
public sealed class ReplayPipelineTests : IDisposable
{
    private readonly string _path = Path.Combine(
        Path.GetTempPath(),
        $"tapit-test-{Guid.NewGuid():N}.wav");

    private float[] WriteFixture(int frames, int channels, int sampleRate = 48000)
    {
        var format = new AudioFormat(sampleRate, channels, AudioSampleFormat.Float32);
        var data = new float[frames * channels];

        for (int f = 0; f < frames; f++)
        {
            for (int c = 0; c < channels; c++)
            {
                // A per-channel identifiable ramp: any deinterleaving or indexing mistake
                // shows up immediately as a wrong value rather than as plausible audio.
                data[(f * channels) + c] = (f + (c * 0.5f)) / frames;
            }
        }

        using var writer = new WavWriter(_path, format);
        writer.WriteFrames(data, frames);

        return data;
    }

    [Fact]
    public void ManualPacing_DeliversEveryFrameInOrder()
    {
        const int frames = 5000;
        float[] expected = WriteFixture(frames, 1);

        using var source = new FileAudioCaptureSource(_path, ReplayPacing.Manual, packetFrames: 480);
        source.Start();

        Assert.Equal(CaptureState.Running, source.State);
        Assert.Equal(48000, source.Format!.SampleRate);

        var reader = new RingBufferReader(source);
        var block = new float[100];
        var received = new List<float>(frames);

        while (source.Pump())
        {
            while (reader.TryReadNextBlock(block, out long start))
            {
                Assert.Equal(received.Count, start);
                received.AddRange(block);
            }
        }

        while (reader.TryReadNextBlock(block, out _))
        {
            received.AddRange(block);
        }

        Assert.True(source.EndOfStream);
        Assert.Equal(0, reader.DroppedFrames);
        Assert.Equal(frames, received.Count);
        Assert.Equal(expected, received);
    }

    [Fact]
    public void ManualPacing_IsDeterministicAcrossRuns()
    {
        // This is the property that makes DSP tuning measurable rather than anecdotal:
        // the same file must produce identical frame indices and identical samples on
        // every run, so a threshold change is the only variable.
        WriteFixture(3000, 1);

        static List<(long Index, float First)> Replay(string path)
        {
            using var source = new FileAudioCaptureSource(path, ReplayPacing.Manual, packetFrames: 256);
            source.Start();

            var reader = new RingBufferReader(source);
            var block = new float[64];
            var trace = new List<(long, float)>();

            while (source.Pump())
            {
                while (reader.TryReadNextBlock(block, out long start))
                {
                    trace.Add((start, block[0]));
                }
            }

            return trace;
        }

        Assert.Equal(Replay(_path), Replay(_path));
    }

    [Fact]
    public void MultiChannelFile_IsDeinterleavedAndMixedDown()
    {
        const int frames = 1024;
        float[] expected = WriteFixture(frames, 2);

        using var source = new FileAudioCaptureSource(_path, ReplayPacing.Manual, packetFrames: 512);
        source.Start();
        source.PumpToEnd();

        AudioRingBuffer buffer = source.Buffer!;
        Assert.Equal(2, buffer.Channels);
        Assert.Equal(frames, buffer.WriteIndex);

        var left = new float[16];
        var right = new float[16];
        var mono = new float[16];

        Assert.True(buffer.TryReadChannel(0, 100, left));
        Assert.True(buffer.TryReadChannel(1, 100, right));
        Assert.True(buffer.TryReadMono(100, mono));

        for (int i = 0; i < 16; i++)
        {
            int frame = 100 + i;
            Assert.Equal(expected[frame * 2], left[i], 5);
            Assert.Equal(expected[(frame * 2) + 1], right[i], 5);
            Assert.Equal((left[i] + right[i]) / 2f, mono[i], 5);
        }
    }

    [Fact]
    public void Restart_IncrementsStreamGenerationAndResetsTheFrameClock()
    {
        WriteFixture(2000, 1);

        using var source = new FileAudioCaptureSource(_path, ReplayPacing.Manual, packetFrames: 256);

        source.Start();
        int firstGeneration = source.StreamGeneration;
        source.Pump();
        long afterFirstPump = source.Buffer!.WriteIndex;
        source.Stop();

        source.Start();

        Assert.True(source.StreamGeneration > firstGeneration);
        Assert.Equal(0, source.Buffer!.WriteIndex);
        Assert.True(afterFirstPump > 0);
    }

    [Fact]
    public void ReaderResynchronisesWhenTheStreamRestarts()
    {
        WriteFixture(2000, 1);

        using var source = new FileAudioCaptureSource(_path, ReplayPacing.Manual, packetFrames: 512);
        source.Start();

        var reader = new RingBufferReader(source);
        var block = new float[128];

        source.Pump();
        Assert.True(reader.TryReadNextBlock(block, out _));
        long resyncsBefore = reader.ResyncCount;

        source.Stop();
        source.Start();
        source.Pump();

        Assert.True(reader.TryReadNextBlock(block, out long start));
        Assert.Equal(0, start);
        Assert.True(reader.ResyncCount > resyncsBefore);
    }

    [Fact]
    public void ManualPumpOnAPacedSource_IsRejected()
    {
        WriteFixture(100, 1);

        using var source = new FileAudioCaptureSource(_path, ReplayPacing.Realtime);
        source.Start();

        Assert.Throws<InvalidOperationException>(() => source.Pump());
    }

    [Fact]
    public void MissingFile_FaultsRatherThanCrashingLater()
    {
        using var source = new FileAudioCaptureSource(
            Path.Combine(Path.GetTempPath(), $"tapit-missing-{Guid.NewGuid():N}.wav"));

        Assert.ThrowsAny<Exception>(source.Start);
        Assert.Equal(CaptureState.Faulted, source.State);
    }

    [Fact]
    public void WaitForData_SignalsAfterAPump()
    {
        WriteFixture(1000, 1);

        using var source = new FileAudioCaptureSource(_path, ReplayPacing.Manual, packetFrames: 256);
        source.Start();

        Assert.False(source.WaitForData(0));
        source.Pump();
        Assert.True(source.WaitForData(1000));
    }

    public void Dispose()
    {
        if (File.Exists(_path))
        {
            File.Delete(_path);
        }
    }
}

public class RingBufferReaderTests
{
    /// <summary>Minimal source stub: the reader only needs a ring and a generation counter.</summary>
    private sealed class StubSource : IAudioCaptureSource
    {
        public AudioFormat? Format { get; set; } = new(48000, 1, AudioSampleFormat.Float32);

        public AudioRingBuffer? Buffer { get; set; }

        public SampleClock? Clock => null;

        public CaptureState State => CaptureState.Running;

        public int StreamGeneration { get; set; }

        public string? DeviceId => "stub";

        public string? DeviceName => "stub";

        public event EventHandler<CaptureStateChangedEventArgs>? StateChanged;

        public void Start() => StateChanged?.Invoke(this, new CaptureStateChangedEventArgs(CaptureState.Running));

        public void Stop()
        {
        }

        public bool WaitForData(int millisecondsTimeout) => true;

        public CaptureStatistics GetStatistics() => default;

        public void Dispose()
        {
        }
    }

    [Fact]
    public void ReadsSequentialBlocksAndTracksPosition()
    {
        var source = new StubSource { Buffer = new AudioRingBuffer(1, 1024) };
        for (int i = 0; i < 512; i++)
        {
            source.Buffer.Write([i], 1);
        }

        var reader = new RingBufferReader(source);
        var block = new float[128];

        for (int b = 0; b < 4; b++)
        {
            Assert.True(reader.TryReadNextBlock(block, out long start));
            Assert.Equal(b * 128, start);
            Assert.Equal(b * 128, block[0]);
            Assert.Equal((b * 128) + 127, block[127]);
        }

        Assert.False(reader.TryReadNextBlock(block, out _));
        Assert.Equal(512, reader.Position);
        Assert.Equal(0, reader.DroppedFrames);
    }

    [Fact]
    public void PartialBlock_IsNotReturned()
    {
        var source = new StubSource { Buffer = new AudioRingBuffer(1, 256) };
        source.Buffer.Write(new float[100], 100);

        var reader = new RingBufferReader(source);

        Assert.False(reader.TryReadNextBlock(new float[128], out _));
        Assert.Equal(100, reader.Available);
    }

    [Fact]
    public void LappedReader_CountsDroppedFramesAndResynchronises()
    {
        var buffer = new AudioRingBuffer(1, 256);
        var source = new StubSource { Buffer = buffer };
        var reader = new RingBufferReader(source);
        var block = new float[64];

        buffer.Write(new float[64], 64);
        Assert.True(reader.TryReadNextBlock(block, out _));

        // Overrun the reader by writing far more than the ring holds.
        for (int i = 0; i < 20; i++)
        {
            buffer.Write(new float[64], 64);
        }

        Assert.True(reader.TryReadNextBlock(block, out long start));
        Assert.True(reader.DroppedFrames > 0, "lapped frames must be counted, never hidden");
        Assert.Equal(buffer.WriteIndex - buffer.Capacity, start);
    }

    [Fact]
    public void SkipToLatest_DiscardsBacklogAndCountsIt()
    {
        var buffer = new AudioRingBuffer(1, 1024);
        var source = new StubSource { Buffer = buffer };
        buffer.Write(new float[600], 600);

        var reader = new RingBufferReader(source);
        Assert.True(reader.TryReadNextBlock(new float[64], out _));

        reader.SkipToLatest();

        Assert.Equal(600, reader.Position);
        Assert.Equal(536, reader.DroppedFrames);
        Assert.Equal(0, reader.Available);
    }

    [Fact]
    public void NullBuffer_IsHandledRatherThanThrowing()
    {
        var reader = new RingBufferReader(new StubSource { Buffer = null });

        Assert.False(reader.TryReadNextBlock(new float[64], out _));
        Assert.Equal(0, reader.Available);
    }

    [Fact]
    public void Reset_ReturnsTheReaderToItsInitialState()
    {
        var buffer = new AudioRingBuffer(1, 256);
        var source = new StubSource { Buffer = buffer };
        buffer.Write(new float[128], 128);

        var reader = new RingBufferReader(source);
        reader.TryReadNextBlock(new float[64], out _);
        reader.Reset();

        Assert.Equal(0, reader.Position);
        Assert.Equal(0, reader.DroppedFrames);
        Assert.Equal(0, reader.ResyncCount);
    }
}
