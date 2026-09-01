using System.Diagnostics;
using Tapit.Audio;
using Tapit.Audio.Wasapi;
using Tapit.Core.Audio;

namespace Tapit.Audio.Tests;

/// <summary>
/// Integration tests that touch the real audio stack.
/// </summary>
/// <remarks>
/// <para>
/// These open the microphone. Exclude them with
/// <c>dotnet test --filter Category!=Hardware</c> on a machine where that is not wanted.
/// </para>
/// <para>
/// They assert on behaviour that must hold for <i>any</i> endpoint - the stream either runs
/// or reports why - rather than on this developer's particular sound card, so they stay
/// meaningful on other hardware.
/// </para>
/// </remarks>
[Trait("Category", "Hardware")]
public class WasapiDeviceEnumeratorTests
{
    [Fact]
    public void Enumeration_ReturnsWellFormedDevices()
    {
        using var enumerator = new WasapiDeviceEnumerator();
        IReadOnlyList<AudioDeviceInfo> devices = enumerator.GetCaptureDevices();

        Assert.All(devices, device =>
        {
            Assert.False(string.IsNullOrWhiteSpace(device.Id));
            Assert.False(string.IsNullOrWhiteSpace(device.FriendlyName));

            if (device.MixFormat is not null)
            {
                Assert.True(device.MixFormat.SampleRate > 0);
                Assert.True(device.MixFormat.Channels > 0);
            }
        });

        Assert.Equal(devices.Select(d => d.Id).Distinct().Count(), devices.Count);
        Assert.True(devices.Count(d => d.IsDefault) <= 1, "there can be at most one default capture device");
    }

    [Fact]
    public void DefaultDevice_IsAlsoPresentInTheFullList()
    {
        using var enumerator = new WasapiDeviceEnumerator();

        AudioDeviceInfo? defaultDevice = enumerator.GetDefaultCaptureDevice();
        if (defaultDevice is null)
        {
            return; // No capture hardware on this machine; nothing to assert.
        }

        Assert.Contains(enumerator.GetCaptureDevices(), d => d.Id == defaultDevice.Id);
        Assert.Equal(AudioDeviceState.Active, defaultDevice.State);
    }

    [Fact]
    public void GetDevice_RoundTripsAnEnumeratedId()
    {
        using var enumerator = new WasapiDeviceEnumerator();

        AudioDeviceInfo? first = enumerator.GetCaptureDevices().FirstOrDefault();
        if (first is null)
        {
            return;
        }

        AudioDeviceInfo? resolved = enumerator.GetDevice(first.Id);

        Assert.NotNull(resolved);
        Assert.Equal(first.Id, resolved.Id);
        Assert.Equal(first.FriendlyName, resolved.FriendlyName);
    }

    [Fact]
    public void GetDevice_ReturnsNullForAnUnknownId()
    {
        using var enumerator = new WasapiDeviceEnumerator();
        Assert.Null(enumerator.GetDevice("{0.0.1.00000000}.{00000000-0000-0000-0000-000000000000}"));
    }

    [Fact]
    public void GetDevice_RejectsEmptyIds()
    {
        using var enumerator = new WasapiDeviceEnumerator();
        Assert.Throws<ArgumentException>(() => enumerator.GetDevice("  "));
    }

    [Fact]
    public void Dispose_IsIdempotent()
    {
        var enumerator = new WasapiDeviceEnumerator();
        enumerator.GetCaptureDevices();

        enumerator.Dispose();
        enumerator.Dispose();
    }

    [Fact]
    public void WorksFromAnStaThread()
    {
        // WinUI runs on an STA thread. The MMDevice objects are apartment-affine, so the
        // enumerator must marshal to its own MTA worker rather than failing here.
        Exception? failure = null;
        int deviceCount = -1;

        var thread = new Thread(() =>
        {
            try
            {
                using var enumerator = new WasapiDeviceEnumerator();
                deviceCount = enumerator.GetCaptureDevices().Count;
            }
            catch (Exception ex)
            {
                failure = ex;
            }
        });

        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(20)), "STA enumeration deadlocked");

        Assert.Null(failure);
        Assert.True(deviceCount >= 0);
    }
}

[Trait("Category", "Hardware")]
public class WasapiCaptureSourceTests
{
    private static bool HasActiveCaptureDevice()
    {
        using var enumerator = new WasapiDeviceEnumerator();
        return enumerator.GetDefaultCaptureDevice() is { State: AudioDeviceState.Active };
    }

    private static bool RunUntil(IAudioCaptureSource source, Func<bool> condition, int timeoutMs)
    {
        var timer = Stopwatch.StartNew();
        while (timer.ElapsedMilliseconds < timeoutMs)
        {
            if (condition())
            {
                return true;
            }

            Thread.Sleep(20);
        }

        return condition();
    }

    [Fact]
    public void Capture_ProducesContiguousAudioAndStopsCleanly()
    {
        if (!HasActiveCaptureDevice())
        {
            return;
        }

        using var source = new WasapiCaptureSource(new WasapiCaptureOptions { RingSeconds = 2.0 });
        source.Start();

        Assert.True(
            RunUntil(source, () => source.State is CaptureState.Running or CaptureState.Faulted, 5000),
            "capture never left the Starting state");

        if (source.State == CaptureState.Faulted)
        {
            return; // Microphone access denied or device busy: not a code defect.
        }

        Assert.NotNull(source.Format);
        Assert.NotNull(source.Buffer);
        Assert.NotNull(source.Clock);
        Assert.True(source.Format.SampleRate >= 8000);
        Assert.True(source.StreamGeneration >= 1);

        var reader = new RingBufferReader(source);
        var block = new float[source.Format.MillisecondsToFrames(10)];
        int blocksRead = 0;

        var timer = Stopwatch.StartNew();
        while (timer.ElapsedMilliseconds < 1500)
        {
            source.WaitForData(100);
            while (reader.TryReadNextBlock(block, out _))
            {
                Assert.All(block, sample => Assert.True(float.IsFinite(sample)));
                blocksRead++;
            }
        }

        CaptureStatistics stats = source.GetStatistics();

        Assert.True(blocksRead > 10, $"expected a steady block stream, got {blocksRead}");
        Assert.True(stats.TotalFrames > 0);
        Assert.True(stats.PacketCount > 0);
        Assert.Equal(0, reader.DroppedFrames);
        Assert.Equal(0, stats.OverrunCount);

        // The capture thread must stay far inside its period; this is the realtime budget.
        Assert.True(stats.MaxServicePassMs < stats.DevicePeriodMs * 5,
            $"service pass {stats.MaxServicePassMs:0.00} ms is too slow for a {stats.DevicePeriodMs:0.0} ms period");

        source.Stop();
        Assert.Equal(CaptureState.Stopped, source.State);
    }

    [Fact]
    public void FrameClock_IsAnchoredToRealTime()
    {
        if (!HasActiveCaptureDevice())
        {
            return;
        }

        using var source = new WasapiCaptureSource();
        source.Start();

        if (!RunUntil(source, () => source.State == CaptureState.Running, 5000))
        {
            return;
        }

        RunUntil(source, () => source.Buffer!.WriteIndex > source.Format!.SampleRate / 4, 3000);

        Assert.True(source.Clock!.IsAnchored);

        double age = source.Clock.AgeMilliseconds(source.Buffer!.WriteIndex - 1);

        // The newest frame in the ring should be very recent. A wildly wrong figure means
        // the QPC-to-Stopwatch conversion is broken and every latency report would lie.
        Assert.False(double.IsNaN(age));
        Assert.InRange(age, -50.0, 500.0);
    }

    [Fact]
    public void RestartingTheStream_IncrementsGeneration()
    {
        if (!HasActiveCaptureDevice())
        {
            return;
        }

        using var source = new WasapiCaptureSource();

        source.Start();
        if (!RunUntil(source, () => source.State == CaptureState.Running, 5000))
        {
            return;
        }

        int firstGeneration = source.StreamGeneration;
        source.Stop();

        source.Start();
        if (!RunUntil(source, () => source.State == CaptureState.Running, 5000))
        {
            return;
        }

        Assert.True(source.StreamGeneration > firstGeneration);
        source.Stop();
    }

    [Fact]
    public void UnknownDeviceId_ReportsAProblemInsteadOfHanging()
    {
        var options = new WasapiCaptureOptions
        {
            DeviceId = "{0.0.1.00000000}.{deadbeef-0000-0000-0000-000000000000}",
            AutoReconnect = false,
        };

        using var source = new WasapiCaptureSource(options);
        source.Start();

        Assert.True(
            RunUntil(source, () => source.State is CaptureState.Faulted or CaptureState.Stopped, 5000),
            "a missing device must fault promptly, not hang");
    }

    [Fact]
    public void StartIsIdempotentAndStopWithoutStartIsSafe()
    {
        using var source = new WasapiCaptureSource();

        source.Stop(); // never started

        if (!HasActiveCaptureDevice())
        {
            return;
        }

        source.Start();
        source.Start();

        RunUntil(source, () => source.State is CaptureState.Running or CaptureState.Faulted, 5000);

        source.Stop();
        source.Stop();
    }

    [Fact]
    public void ProcessedFallback_CanBeDisabled()
    {
        if (!HasActiveCaptureDevice())
        {
            return;
        }

        // With strict raw mode the source either gets a clean stream or refuses; it must
        // never silently hand back speech-processed audio.
        using var source = new WasapiCaptureSource(new WasapiCaptureOptions
        {
            RequestRawMode = true,
            AllowProcessedFallback = false,
            AutoReconnect = false,
        });

        source.Start();
        RunUntil(source, () => source.State is CaptureState.Running or CaptureState.Faulted, 5000);

        if (source.State == CaptureState.Running)
        {
            Assert.True(source.RawModeActive);
        }

        source.Stop();
    }
}
