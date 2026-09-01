using System.Diagnostics;
using Tapit.Core.Audio;

namespace Tapit.Core.Tests;

public class SampleClockTests
{
    [Fact]
    public void BeforeAnchoring_NoTimestampIsAvailable()
    {
        var clock = new SampleClock(48000);

        Assert.False(clock.IsAnchored);
        Assert.False(clock.TryGetTimestamp(0, out _));
        Assert.True(double.IsNaN(clock.AgeMilliseconds(0)));
    }

    [Fact]
    public void Timestamp_IsLinearInFrameIndex()
    {
        var clock = new SampleClock(48000);
        long anchorTicks = Stopwatch.GetTimestamp();

        clock.Anchor(1000, anchorTicks);

        Assert.True(clock.TryGetTimestamp(1000, out long atAnchor));
        Assert.Equal(anchorTicks, atAnchor);

        // 48000 frames later is exactly one second later.
        Assert.True(clock.TryGetTimestamp(1000 + 48000, out long oneSecondLater));
        Assert.Equal(1000.0, SampleClock.TicksToMilliseconds(oneSecondLater - atAnchor), 3);

        // Extrapolating backwards works too - that is how pre-roll gets timestamped.
        Assert.True(clock.TryGetTimestamp(1000 - 4800, out long earlier));
        Assert.Equal(-100.0, SampleClock.TicksToMilliseconds(earlier - atAnchor), 3);
    }

    [Fact]
    public void AgeMilliseconds_GrowsWithElapsedTime()
    {
        var clock = new SampleClock(48000);

        // Anchor one second in the past: the newest frame should read about 1000 ms old.
        clock.Anchor(0, Stopwatch.GetTimestamp() - Stopwatch.Frequency);

        double age = clock.AgeMilliseconds(0);

        // Lower bound is exact - the anchor really was one second ago. The upper bound is
        // generous on purpose: this thread can be descheduled for an arbitrary while, and a
        // test that fails because the machine was busy tells us nothing about the clock.
        Assert.InRange(age, 999.0, 5000.0);
    }

    [Fact]
    public void Reset_ClearsTheAnchor()
    {
        var clock = new SampleClock(44100);
        clock.Anchor(10, Stopwatch.GetTimestamp());
        Assert.True(clock.IsAnchored);

        clock.Reset();

        Assert.False(clock.IsAnchored);
        Assert.False(clock.TryGetTimestamp(10, out _));
    }

    [Fact]
    public void QpcConversion_TreatsInputAsHundredNanosecondUnits()
    {
        // One second expressed in 100 ns units.
        const ulong oneSecond = 10_000_000UL;

        long ticks = SampleClock.QpcHundredNanosecondsToStopwatchTicks(oneSecond);

        Assert.Equal(Stopwatch.Frequency, ticks);
        Assert.Equal(1000.0, SampleClock.TicksToMilliseconds(ticks), 3);
    }

    [Fact]
    public void ReAnchoring_TracksTheNewestPacket()
    {
        var clock = new SampleClock(48000);
        long start = Stopwatch.GetTimestamp();

        clock.Anchor(0, start);
        clock.Anchor(480, start + (Stopwatch.Frequency / 100)); // 10 ms later

        Assert.True(clock.TryGetTimestamp(480, out long ticks));
        Assert.Equal(10.0, SampleClock.TicksToMilliseconds(ticks - start), 3);
    }

    [Fact]
    public void ConcurrentAnchorAndRead_NeverMixesOldIndexWithNewTimestamp()
    {
        // The anchor is written by the capture thread and read by the DSP thread. A torn
        // pair would silently corrupt every latency figure, so the seqlock is exercised
        // under real contention here.
        var clock = new SampleClock(48000);
        long baseTicks = Stopwatch.GetTimestamp();
        var stop = new ManualResetEventSlim(false);

        var writer = new Thread(() =>
        {
            long frame = 0;
            while (!stop.IsSet)
            {
                clock.Anchor(frame, baseTicks + (long)(frame / 48000.0 * Stopwatch.Frequency));
                frame += 480;
            }
        })
        {
            IsBackground = true,
        };

        writer.Start();

        try
        {
            for (int i = 0; i < 200_000; i++)
            {
                if (clock.TryGetTimestamp(0, out long ticks))
                {
                    // Frame 0 always maps back to the base timestamp, whatever the anchor is,
                    // because the mapping is linear and consistent.
                    Assert.Equal(0.0, SampleClock.TicksToMilliseconds(ticks - baseTicks), 0);
                }
            }
        }
        finally
        {
            stop.Set();
            writer.Join();
        }
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-48000)]
    public void InvalidSampleRate_Throws(int sampleRate) =>
        Assert.Throws<ArgumentOutOfRangeException>(() => new SampleClock(sampleRate));
}
