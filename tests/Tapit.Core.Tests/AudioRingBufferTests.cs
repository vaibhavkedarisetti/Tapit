using Tapit.Core.Audio;

namespace Tapit.Core.Tests;

public class AudioRingBufferTests
{
    [Theory]
    [InlineData(1, 1)]
    [InlineData(3, 4)]
    [InlineData(1000, 1024)]
    [InlineData(1024, 1024)]
    [InlineData(1025, 2048)]
    public void Capacity_RoundsUpToPowerOfTwo(int requested, int expected) =>
        Assert.Equal(expected, new AudioRingBuffer(1, requested).Capacity);

    [Fact]
    public void MonoWriteAndRead_RoundTrips()
    {
        var buffer = new AudioRingBuffer(1, 64);
        float[] input = [0.1f, 0.2f, 0.3f, 0.4f];

        buffer.Write(input, 4);

        var output = new float[4];
        Assert.True(buffer.TryReadMono(0, output));
        Assert.Equal(input, output);
        Assert.Equal(4, buffer.WriteIndex);
    }

    [Fact]
    public void MultiChannel_DeinterleavesAndMixesToMono()
    {
        var buffer = new AudioRingBuffer(2, 64);

        // Frames: (1.0, 0.0), (0.0, 1.0), (0.5, -0.5)
        float[] interleaved = [1.0f, 0.0f, 0.0f, 1.0f, 0.5f, -0.5f];
        buffer.Write(interleaved, 3);

        var left = new float[3];
        var right = new float[3];
        var mono = new float[3];

        Assert.True(buffer.TryReadChannel(0, 0, left));
        Assert.True(buffer.TryReadChannel(1, 0, right));
        Assert.True(buffer.TryReadMono(0, mono));

        Assert.Equal([1.0f, 0.0f, 0.5f], left);
        Assert.Equal([0.0f, 1.0f, -0.5f], right);
        Assert.Equal([0.5f, 0.5f, 0.0f], mono);
    }

    [Fact]
    public void SingleChannelBuffer_SharesMonoPlane()
    {
        var buffer = new AudioRingBuffer(1, 16);
        buffer.Write([0.75f, -0.75f], 2);

        var channel = new float[2];
        var mono = new float[2];
        Assert.True(buffer.TryReadChannel(0, 0, channel));
        Assert.True(buffer.TryReadMono(0, mono));

        Assert.Equal(channel, mono);
    }

    [Fact]
    public void AbsoluteIndexing_SurvivesWrap()
    {
        var buffer = new AudioRingBuffer(1, 8);

        // Write 20 frames of a ramp through an 8-frame ring.
        for (int i = 0; i < 20; i++)
        {
            buffer.Write([i], 1);
        }

        Assert.Equal(20, buffer.WriteIndex);
        Assert.Equal(12, buffer.OldestAvailableIndex);

        var output = new float[8];
        Assert.True(buffer.TryReadMono(12, output));
        Assert.Equal([12f, 13f, 14f, 15f, 16f, 17f, 18f, 19f], output);
    }

    [Fact]
    public void ReadingOverwrittenRange_FailsAndIsCounted()
    {
        var buffer = new AudioRingBuffer(1, 8);
        for (int i = 0; i < 20; i++)
        {
            buffer.Write([i], 1);
        }

        var output = new float[4];

        // Frame 0 was lapped long ago. Returning stale audio here would silently corrupt an
        // analysis window, so the read must fail loudly instead.
        Assert.False(buffer.TryReadMono(0, output));
        Assert.Equal(1, buffer.FailedReadCount);
    }

    [Fact]
    public void ReadingUnwrittenRange_Fails()
    {
        var buffer = new AudioRingBuffer(1, 64);
        buffer.Write([1f, 2f], 2);

        var output = new float[4];
        Assert.False(buffer.TryReadMono(0, output));
        Assert.False(buffer.TryReadMono(5, output));
    }

    [Fact]
    public void NegativeStartIndex_Fails()
    {
        var buffer = new AudioRingBuffer(1, 16);
        buffer.Write([1f], 1);
        Assert.False(buffer.TryReadMono(-1, new float[1]));
    }

    [Fact]
    public void EmptyDestination_Succeeds()
    {
        var buffer = new AudioRingBuffer(1, 16);
        Assert.True(buffer.TryReadMono(0, Span<float>.Empty));
    }

    [Fact]
    public void WriteSilence_AdvancesClockAndZeroesAllPlanes()
    {
        var buffer = new AudioRingBuffer(2, 16);
        buffer.Write([1f, 1f, 1f, 1f], 2);
        buffer.WriteSilence(3);

        Assert.Equal(5, buffer.WriteIndex);

        var left = new float[3];
        var mono = new float[3];
        Assert.True(buffer.TryReadChannel(0, 2, left));
        Assert.True(buffer.TryReadMono(2, mono));

        Assert.All(left, v => Assert.Equal(0f, v));
        Assert.All(mono, v => Assert.Equal(0f, v));
    }

    [Fact]
    public void Reset_RestartsFrameClock()
    {
        var buffer = new AudioRingBuffer(1, 16);
        buffer.Write([1f, 2f, 3f], 3);
        buffer.Reset();

        Assert.Equal(0, buffer.WriteIndex);
        Assert.Equal(0, buffer.OverrunCount);
        Assert.Equal(0, buffer.FailedReadCount);
    }

    [Fact]
    public void ShortInterleavedInput_WritesOnlyWholeFrames()
    {
        var buffer = new AudioRingBuffer(2, 16);

        // Claims three frames but only supplies two and a half.
        buffer.Write([1f, 2f, 3f, 4f, 5f], 3);

        Assert.Equal(2, buffer.WriteIndex);
    }

    [Fact]
    public void ChannelIndexOutOfRange_Throws()
    {
        var buffer = new AudioRingBuffer(2, 16);
        Assert.Throws<ArgumentOutOfRangeException>(() => buffer.TryReadChannel(2, 0, new float[1]));
    }

    [Fact]
    public void ReserveIndex_TracksWriteIndexWhenIdle()
    {
        var buffer = new AudioRingBuffer(1, 64);

        Assert.Equal(0, buffer.ReserveIndex);

        buffer.Write([1f, 2f, 3f], 3);
        Assert.Equal(buffer.WriteIndex, buffer.ReserveIndex);

        buffer.WriteSilence(5);
        Assert.Equal(buffer.WriteIndex, buffer.ReserveIndex);
        Assert.Equal(8, buffer.WriteIndex);
    }

    [Fact]
    public void OldestAvailableIndex_ExcludesFramesAnInFlightWriteWillClobber()
    {
        // Regression: the producer copies a packet's samples into their slots *before* it
        // publishes the new write index. A window derived from the write index alone would
        // still advertise the tail frames as readable even though they had already been
        // overwritten, and a consumer there would silently read data one full lap ahead.
        // The readable window must therefore start one capacity behind the reserve index.
        var buffer = new AudioRingBuffer(1, 8);

        for (int i = 0; i < 12; i++)
        {
            buffer.Write([i], 1);
        }

        Assert.Equal(12, buffer.WriteIndex);
        Assert.Equal(buffer.ReserveIndex - buffer.Capacity, buffer.OldestAvailableIndex);

        // With no write in flight the whole capacity is readable.
        var window = new float[8];
        Assert.True(buffer.TryReadMono(buffer.OldestAvailableIndex, window));
        Assert.Equal([4f, 5f, 6f, 7f, 8f, 9f, 10f, 11f], window);
    }

    [Fact]
    public void ConcurrentProducerAndConsumer_NeverYieldTornData()
    {
        // The whole ring design rests on the reserve/write index pair. This drives a real
        // producer/consumer race with the consumer deliberately parked at the tail - the
        // worst case - and asserts that every block it accepts is a contiguous run. A torn
        // read shows up as a value exactly one capacity out.
        const int capacity = 4096;
        const int totalFrames = 1_000_000;
        const int blockFrames = 128;

        var buffer = new AudioRingBuffer(1, capacity);
        var producerDone = new ManualResetEventSlim(false);
        Exception? producerFailure = null;

        var producer = new Thread(() =>
        {
            try
            {
                var packet = new float[64];
                long next = 0;
                while (next < totalFrames)
                {
                    for (int i = 0; i < packet.Length; i++)
                    {
                        packet[i] = next + i;
                    }

                    buffer.Write(packet, packet.Length);
                    next += packet.Length;
                }
            }
            catch (Exception ex)
            {
                producerFailure = ex;
            }
            finally
            {
                producerDone.Set();
            }
        })
        {
            IsBackground = true,
        };

        producer.Start();

        var block = new float[blockFrames];
        long position = 0;
        long verified = 0;

        while (!producerDone.IsSet || buffer.WriteIndex - position >= blockFrames)
        {
            if (buffer.WriteIndex - position < blockFrames)
            {
                Thread.SpinWait(50);
                continue;
            }

            if (!buffer.TryReadMono(position, block))
            {
                // Lapped: legitimate under contention. Resynchronise and keep going.
                position = buffer.OldestAvailableIndex;
                continue;
            }

            for (int i = 0; i < block.Length; i++)
            {
                Assert.Equal(position + i, block[i]);
            }

            position += blockFrames;
            verified += blockFrames;
        }

        producer.Join();

        Assert.Null(producerFailure);
        Assert.True(verified > 0, "the consumer never managed to validate a block");
    }

    [Fact]
    public void ConsumerThatKeepsUp_SeesEveryFrameExactlyOnce()
    {
        // The realistic case: a 4-second ring and a consumer that runs every few
        // milliseconds is never anywhere near the tail, so nothing may be dropped at all.
        const int capacity = 65536;
        const int packetFrames = 480;
        const int packets = 400;
        const int blockFrames = 240;

        var buffer = new AudioRingBuffer(1, capacity);
        var packetReady = new SemaphoreSlim(0);
        var consumed = new SemaphoreSlim(1);
        Exception? producerFailure = null;

        var producer = new Thread(() =>
        {
            try
            {
                var packet = new float[packetFrames];
                for (int p = 0; p < packets; p++)
                {
                    consumed.Wait();
                    for (int i = 0; i < packetFrames; i++)
                    {
                        packet[i] = (p * packetFrames) + i;
                    }

                    buffer.Write(packet, packetFrames);
                    packetReady.Release();
                }
            }
            catch (Exception ex)
            {
                producerFailure = ex;
            }
        })
        {
            IsBackground = true,
        };

        producer.Start();

        var block = new float[blockFrames];
        long position = 0;

        for (int p = 0; p < packets; p++)
        {
            Assert.True(packetReady.Wait(TimeSpan.FromSeconds(10)), "producer stalled");

            while (buffer.WriteIndex - position >= blockFrames)
            {
                Assert.True(buffer.TryReadMono(position, block), "a consumer that keeps up must never be refused");

                for (int i = 0; i < blockFrames; i++)
                {
                    Assert.Equal(position + i, block[i]);
                }

                position += blockFrames;
            }

            consumed.Release();
        }

        producer.Join();

        Assert.Null(producerFailure);
        Assert.Equal(packets * (long)packetFrames, position);
        Assert.Equal(0, buffer.OverrunCount);
        Assert.Equal(0, buffer.FailedReadCount);
    }
}
