using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using Tapit.Audio;
using Tapit.Audio.Wasapi;
using Tapit.Core.Audio;
using Tapit.Core.Detection;

namespace Tapit.MicCheck;

/// <summary>
/// Phase 1 verification console for Tapit.
/// </summary>
/// <remarks>
/// The point of this tool is that Phase 1 is <i>measured</i> rather than assumed: it reports
/// the negotiated format, whether Windows granted a raw effects-bypassed stream, the true
/// capture latency from the performance counter, and every dropped or discontinuous frame.
/// Those numbers are what the detector's budget will be built on.
/// </remarks>
internal static class Program
{
    private static volatile bool _stopRequested;

    /// <summary>Set by Ctrl+C. Long-running commands poll this rather than being killed.</summary>
    internal static bool StopRequested => _stopRequested;

    private static int Main(string[] args)
    {
        Console.OutputEncoding = Encoding.UTF8;

        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            _stopRequested = true;
        };

        try
        {
            var options = CommandLine.Parse(args);

            return options.Command switch
            {
                "help" => PrintUsage(),
                "devices" => ListDevices(options),
                "listen" => Listen(options),
                "replay" => Replay(options),
                "record" => Listen(options),
                "detect" => DetectCommand.Run(options),
                _ => PrintUsage(),
            };
        }
        catch (CommandLineException ex)
        {
            Console.Error.WriteLine($"tapit-miccheck: {ex.Message}");
            Console.Error.WriteLine("Run 'Tapit.MicCheck help' for usage.");
            return 2;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"tapit-miccheck: {ex.Message}");
            return 1;
        }
    }

    private static int PrintUsage()
    {
        Console.WriteLine("""
            Tapit microphone check - Phase 1 capture verification

            USAGE
              Tapit.MicCheck devices
              Tapit.MicCheck detect  [options]
              Tapit.MicCheck detect  --file <in.wav> [options]
              Tapit.MicCheck listen  [options]
              Tapit.MicCheck replay  <file.wav> [options]
              Tapit.MicCheck record  <file.wav> [options]

            COMMANDS
              devices   List capture endpoints with format and suitability.
              detect    Listen for desk taps. Shows the noise floor, the live level, and
                        every candidate event with the reason it was accepted or rejected.
                        With --file the identical detector runs over a WAV instead.
              listen    Open the microphone and report live signal and capture health.
              replay    Feed a WAV through the consumer path, no detection.
              record    Listen, and also write raw audio to a WAV file.

            CAPTURE OPTIONS
              --device <id>     Endpoint ID to use (default: system default capture device).
              --seconds <n>     Stop after n seconds (default: run until Ctrl+C).
              --block <ms>      Consumer block size in milliseconds (default: 10).
              --ring <s>        Ring buffer depth in seconds (default: 4).
              --no-raw          Do not request an effects-bypassed stream.
              --strict-raw      Fail if a raw stream cannot be granted.
              --no-mmcss        Do not register the capture thread with MMCSS.
              --json            Print a machine-readable summary when finished.

            DETECT OPTIONS
              --file <wav>      Run the detector over a WAV file instead of the microphone.
              --save <dir>      Write each detected window as a WAV, plus events.csv holding
                                measurements and features. Nothing is written unless given.
              --save-rejected   Also save rejected events - useful for tuning thresholds.
              --features        Print the feature vector for each event (--file mode).
              --channels        Print inter-channel level and delay for each accepted tap.
                                This is the measurement that decides whether left-vs-right
                                is separable at all on your microphone.

            DETECTOR TUNING   (all of these are experiments, not constants)
              --window <ms>        Analysis window length (default 90).
              --preroll <ms>       Window portion before the onset (default 12).
              --threshold <dB>     Onset rise above the noise floor (default 12).
              --min-onset <dB>     Absolute onset gate, dBFS (default -55).
              --min-rise <dB>      Required jump from the previous frame (default 9).
              --min-peak <dB>      Reject windows peaking below this, dBFS (default -48).
              --refractory <ms>    Minimum gap between events (default 180).
              --max-attack <ms>    Reject slower attacks (default 10).
              --max-duration <ms>  Reject longer effective durations (default 55).
              --learn <s>          Room-learning period before detecting (default 0.75).

            PRIVACY
              'record' and 'detect --save' are the only things that write audio to disk.
              Everything else keeps audio in memory and discards it.
            """);

        return 0;
    }

    private static int ListDevices(CommandLine options)
    {
        using var enumerator = new WasapiDeviceEnumerator();
        IReadOnlyList<AudioDeviceInfo> devices = enumerator.GetCaptureDevices();

        if (devices.Count == 0)
        {
            Console.WriteLine("No capture devices found.");
            return 1;
        }

        if (options.Json)
        {
            Console.WriteLine(JsonSerializer.Serialize(
                devices.Select(d => new
                {
                    d.Id,
                    d.FriendlyName,
                    State = d.State.ToString(),
                    d.IsDefault,
                    d.IsDefaultCommunications,
                    SampleRate = d.MixFormat?.SampleRate,
                    Channels = d.MixFormat?.Channels,
                    SampleFormat = d.MixFormat?.SampleFormat.ToString(),
                    d.FormFactor,
                    Suitability = DeviceSuitabilityCheck.Evaluate(d).Suitability.ToString(),
                }),
                new JsonSerializerOptions { WriteIndented = true }));

            return 0;
        }

        Console.WriteLine();
        Console.WriteLine("CAPTURE DEVICES");
        Console.WriteLine();

        foreach (AudioDeviceInfo device in devices)
        {
            DeviceAssessment assessment = DeviceSuitabilityCheck.Evaluate(device);

            var markers = new List<string>();
            if (device.IsDefault)
            {
                markers.Add("default");
            }

            if (device.IsDefaultCommunications)
            {
                markers.Add("default comms");
            }

            if (device.State != AudioDeviceState.Active)
            {
                markers.Add(device.State.ToString().ToLowerInvariant());
            }

            string marker = markers.Count > 0 ? $"  [{string.Join(", ", markers)}]" : string.Empty;

            Console.WriteLine($"  {device.FriendlyName}{marker}");
            Console.WriteLine($"    id           {device.Id}");
            Console.WriteLine($"    format       {device.MixFormat?.ToString() ?? "unknown"}");

            if (device.FormFactor is not null)
            {
                Console.WriteLine($"    form factor  {device.FormFactor}");
            }

            Console.WriteLine($"    suitability  {assessment.Suitability}: {assessment.Reason}");
            Console.WriteLine();
        }

        Console.WriteLine("  Tip: the built-in laptop microphone is normally the right choice - it is");
        Console.WriteLine("  mechanically coupled to the same surface the taps travel through.");
        Console.WriteLine();

        return 0;
    }

    private static int Listen(CommandLine options)
    {
        var captureOptions = new WasapiCaptureOptions
        {
            DeviceId = options.DeviceId,
            RequestRawMode = !options.NoRaw,
            AllowProcessedFallback = !options.StrictRaw,
            UseMmcss = !options.NoMmcss,
            RingSeconds = options.RingSeconds,
        };

        using var source = new WasapiCaptureSource(captureOptions);
        return Run(source, options, isRecording: options.Command == "record");
    }

    private static int Replay(CommandLine options)
    {
        if (options.FilePath is null || !File.Exists(options.FilePath))
        {
            throw new CommandLineException($"WAV file not found: {options.FilePath}");
        }

        using var source = new FileAudioCaptureSource(
            options.FilePath,
            ReplayPacing.Realtime,
            packetFrames: 0,
            ringSeconds: options.RingSeconds);

        source.Completed += (_, _) => _stopRequested = true;

        return Run(source, options, isRecording: false);
    }

    private static int Run(IAudioCaptureSource source, CommandLine options, bool isRecording)
    {
        var stateLog = new List<string>();
        var stateLock = new object();

        source.StateChanged += (_, e) =>
        {
            lock (stateLock)
            {
                string line = $"{DateTime.Now:HH:mm:ss}  {e.State}" +
                              (string.IsNullOrEmpty(e.Message) ? string.Empty : $" - {e.Message}");
                stateLog.Add(line);
                if (stateLog.Count > 6)
                {
                    stateLog.RemoveAt(0);
                }
            }
        };

        source.Start();

        // Wait for the format to be negotiated before sizing the consumer.
        var startupTimer = Stopwatch.StartNew();
        while (source.Format is null && source.State is CaptureState.Starting or CaptureState.Stopped &&
               startupTimer.ElapsedMilliseconds < 5000 && !_stopRequested)
        {
            Thread.Sleep(20);
        }

        if (source.State == CaptureState.Faulted || source.Format is null)
        {
            Console.Error.WriteLine();
            Console.Error.WriteLine("Capture could not be started.");
            lock (stateLock)
            {
                foreach (string line in stateLog)
                {
                    Console.Error.WriteLine("  " + line);
                }
            }

            return 1;
        }

        AudioFormat format = source.Format;
        int blockFrames = Math.Max(16, format.MillisecondsToFrames(options.BlockMs));
        int recentBlocks = Math.Max(1, (int)(1000.0 / Math.Max(1.0, options.BlockMs)));

        WavWriter? recorder = null;
        if (isRecording)
        {
            if (options.FilePath is null)
            {
                throw new CommandLineException("record requires an output WAV path.");
            }

            recorder = new WavWriter(options.FilePath, format.WithChannels(1));
        }

        try
        {
            var monitor = new SignalMonitor(source, blockFrames, recentBlocks, recorder);
            RunLoop(source, monitor, options, stateLog, stateLock, isRecording);

            if (options.Json)
            {
                PrintJsonSummary(source, monitor);
            }
        }
        finally
        {
            recorder?.Dispose();
            source.Stop();
        }

        return 0;
    }

    private static void RunLoop(
        IAudioCaptureSource source,
        SignalMonitor monitor,
        CommandLine options,
        List<string> stateLog,
        object stateLock,
        bool isRecording)
    {
        bool interactive = !Console.IsOutputRedirected;
        var runtime = Stopwatch.StartNew();
        var lastRender = Stopwatch.StartNew();

        if (interactive)
        {
            Console.Clear();
            Console.CursorVisible = false;
        }

        try
        {
            while (!_stopRequested)
            {
                if (options.Seconds > 0 && runtime.Elapsed.TotalSeconds >= options.Seconds)
                {
                    break;
                }

                source.WaitForData(50);
                monitor.Pump();

                if (lastRender.ElapsedMilliseconds >= 100)
                {
                    lastRender.Restart();

                    string frame = BuildFrame(source, monitor, runtime.Elapsed, isRecording, stateLog, stateLock);

                    if (interactive)
                    {
                        Console.SetCursorPosition(0, 0);
                        Console.Write(frame);
                    }
                    else if (runtime.ElapsedMilliseconds % 1000 < 120)
                    {
                        Console.Write(frame);
                    }
                }
            }
        }
        finally
        {
            if (interactive)
            {
                Console.CursorVisible = true;
                Console.SetCursorPosition(0, 0);
                Console.Write(BuildFrame(source, monitor, runtime.Elapsed, isRecording, stateLog, stateLock));
                Console.WriteLine();
            }
        }
    }

    private static string BuildFrame(
        IAudioCaptureSource source,
        SignalMonitor monitor,
        TimeSpan elapsed,
        bool isRecording,
        List<string> stateLog,
        object stateLock)
    {
        CaptureStatistics stats = source.GetStatistics();
        MonitorSnapshot snapshot = monitor.Snapshot();
        AudioFormat? format = source.Format;

        var sb = new StringBuilder(2048);
        var culture = CultureInfo.InvariantCulture;

        void Line(string text = "") => sb.Append(text.PadRight(78)).Append('\n');
        void Field(string name, string value) => Line($"  {name,-16}{value}");

        Line("  TAPIT - MICROPHONE CHECK");
        Line("  " + new string('─', 60));
        Line();

        Line("  DEVICE");
        Field("Name", source.DeviceName ?? "(unknown)");
        Field("Endpoint", Truncate(source.DeviceId ?? "(unknown)", 58));
        Field("Format", format?.ToString() ?? "(negotiating)");
        Field("Processing", stats.RawModeActive
            ? "raw - Windows audio effects bypassed"
            : "PROCESSED - AGC/noise suppression may be active");
        Field("MMCSS", stats.MmcssActive ? "Pro Audio" : "not registered");
        Field("State", source.State.ToString());
        Line();

        Line("  SIGNAL");
        Field("RMS (AC)", $"{snapshot.AcRmsDbfs,7:0.0} dBFS   {Meter(snapshot.AcRmsDbfs)}");
        Field("Peak", $"{snapshot.PeakDbfs,7:0.0} dBFS   {Meter(snapshot.PeakDbfs)}");
        Field("DC offset", $"{snapshot.DcOffset,7:0.0000}   ({SignalAnalysis.ToDbfs(Math.Abs(snapshot.DcOffset)):0.0} dBFS)");
        Field("Peak hold", $"{snapshot.PeakHoldDbfs,7:0.0} dBFS");
        Field("Quietest 1 s", $"{snapshot.QuietestBlockDbfs,7:0.0} dBFS");
        Field("Crest", $"{snapshot.CrestFactorDb,7:0.0} dB");
        Field("Clipped", snapshot.ClippedSamples.ToString("N0", culture) + " samples");
        Line();

        Line("  CAPTURE");
        Field("Elapsed", $"{elapsed.TotalSeconds,7:0.0} s");
        Field("Frames", stats.TotalFrames.ToString("N0", culture));
        Field("Packets", $"{stats.PacketCount:N0} (max {stats.MaxPacketFrames} frames)");
        Field("Device period", $"{stats.DevicePeriodMs,7:0.0} ms");
        Field("Engine buffer", $"{stats.EngineBufferMs,7:0.0} ms");
        Field("Stream latency", $"{stats.StreamLatencyMs,7:0.0} ms");
        Field("Newest frame", double.IsNaN(snapshot.NewestFrameAgeMs)
            ? "      - "
            : $"{snapshot.NewestFrameAgeMs,7:0.0} ms old  (capture latency)");
        Field("Service pass", $"{stats.LastServicePassMs,7:0.000} ms  (max {stats.MaxServicePassMs:0.000} ms)");
        Line();

        Line("  INTEGRITY");
        Field("Discontinuity", stats.DiscontinuityCount.ToString("N0", culture));
        Field("Silent packets", stats.SilentPacketCount.ToString("N0", culture));
        Field("Gap bridged", $"{stats.GapFramesInserted:N0} frames");
        Field("Ring overruns", stats.OverrunCount.ToString("N0", culture));
        Field("Dropped", $"{snapshot.DroppedFrames:N0} frames");
        Field("Resyncs", snapshot.Resyncs.ToString("N0", culture));
        Field("Blocks read", snapshot.BlocksProcessed.ToString("N0", culture));
        Line();

        Line("  HEALTH");
        foreach (string note in Diagnose(stats, snapshot, elapsed))
        {
            Line("    " + Truncate(note, 72));
        }

        Line();

        if (isRecording)
        {
            Line("  ●  RECORDING TO DISK - raw audio is being written to a file.");
            Line();
        }

        Line("  RECENT EVENTS");
        lock (stateLock)
        {
            if (stateLog.Count == 0)
            {
                Line("    (none)");
            }
            else
            {
                foreach (string line in stateLog)
                {
                    Line("    " + Truncate(line, 72));
                }
            }
        }

        for (int i = stateLog.Count; i < 6; i++)
        {
            Line();
        }

        Line();
        Line("  Ctrl+C to stop.");

        return sb.ToString();
    }

    /// <summary>
    /// Turns the raw counters into the handful of statements that actually decide whether
    /// this microphone can drive tap classification.
    /// </summary>
    private static IEnumerable<string> Diagnose(
        CaptureStatistics stats, MonitorSnapshot snapshot, TimeSpan elapsed)
    {
        var notes = new List<string>();

        // Measured on a Realtek array: with the effects chain engaged, ambient sound is
        // gated to exact digital silence while the raw stream on the same device at the
        // same moment carries a live signal. Tapit would detect literally nothing.
        if (elapsed.TotalSeconds > 1.5 && snapshot.PeakHoldDbfs <= SignalAnalysis.MinimumDbfs)
        {
            notes.Add(stats.RawModeActive
                ? "FAIL  Stream is digitally silent. Check the microphone is not muted."
                : "FAIL  Stream is digitally silent - Windows audio processing is gating");
            if (!stats.RawModeActive)
            {
                notes.Add("      this microphone. Tapit needs raw mode to see desk taps.");
            }
        }
        else if (!stats.RawModeActive)
        {
            notes.Add("WARN  Processed stream. AGC and noise suppression reshape transients;");
            notes.Add("      expect degraded zone separation. Disable audio enhancements.");
        }
        else
        {
            notes.Add("OK    Raw stream - Windows audio effects are bypassed.");
        }

        if (Math.Abs(snapshot.DcOffset) > 0.002)
        {
            // Raw capture has no high-pass in front of it. Phase 2 must remove this before
            // any amplitude or envelope feature is computed.
            notes.Add($"NOTE  DC offset {snapshot.DcOffset:0.0000} - the detector must high-pass first.");
        }

        if (!stats.MmcssActive)
        {
            notes.Add("WARN  Capture thread is not MMCSS-scheduled; expect more glitching.");
        }

        if (stats.OverrunCount > 0 || snapshot.DroppedFrames > 0)
        {
            notes.Add($"FAIL  {snapshot.DroppedFrames:N0} frames dropped - the consumer cannot keep up.");
        }

        if (stats.DiscontinuityCount > 0)
        {
            notes.Add($"WARN  {stats.DiscontinuityCount:N0} capture discontinuities reported by the engine.");
        }

        if (snapshot.ClippedSamples > 0)
        {
            notes.Add($"WARN  {snapshot.ClippedSamples:N0} clipped samples - input gain is too high.");
        }

        if (stats.MaxServicePassMs > stats.DevicePeriodMs && stats.DevicePeriodMs > 0)
        {
            notes.Add($"WARN  Slowest service pass {stats.MaxServicePassMs:0.00} ms exceeds the " +
                      $"{stats.DevicePeriodMs:0.0} ms period.");
        }

        return notes;
    }

    /// <summary>Simple 30-cell meter spanning -60 dBFS to 0 dBFS.</summary>
    private static string Meter(double dbfs)
    {
        const int width = 30;
        double normalised = Math.Clamp((dbfs + 60.0) / 60.0, 0.0, 1.0);
        int filled = (int)Math.Round(normalised * width);
        return "[" + new string('#', filled) + new string('.', width - filled) + "]";
    }

    private static string Truncate(string value, int max) =>
        value.Length <= max ? value : value[..(max - 1)] + "…";

    private static void PrintJsonSummary(IAudioCaptureSource source, SignalMonitor monitor)
    {
        CaptureStatistics stats = source.GetStatistics();
        MonitorSnapshot snapshot = monitor.Snapshot();

        Console.WriteLine();
        Console.WriteLine(JsonSerializer.Serialize(
            new
            {
                device = source.DeviceName,
                deviceId = source.DeviceId,
                sampleRate = source.Format?.SampleRate,
                channels = source.Format?.Channels,
                sampleFormat = source.Format?.SampleFormat.ToString(),
                rawModeActive = stats.RawModeActive,
                mmcssActive = stats.MmcssActive,
                devicePeriodMs = stats.DevicePeriodMs,
                engineBufferMs = stats.EngineBufferMs,
                streamLatencyMs = stats.StreamLatencyMs,
                captureLatencyMs = snapshot.NewestFrameAgeMs,
                maxServicePassMs = stats.MaxServicePassMs,
                totalFrames = stats.TotalFrames,
                packets = stats.PacketCount,
                discontinuities = stats.DiscontinuityCount,
                silentPackets = stats.SilentPacketCount,
                gapFramesInserted = stats.GapFramesInserted,
                ringOverruns = stats.OverrunCount,
                droppedFrames = snapshot.DroppedFrames,
                resyncs = snapshot.Resyncs,
                rmsDbfs = snapshot.RmsDbfs,
                acRmsDbfs = snapshot.AcRmsDbfs,
                dcOffset = snapshot.DcOffset,
                peakDbfs = snapshot.PeakDbfs,
                crestDb = snapshot.CrestFactorDb,
                peakHoldDbfs = snapshot.PeakHoldDbfs,
                quietestBlockDbfs = snapshot.QuietestBlockDbfs,
                clippedSamples = snapshot.ClippedSamples,
            },
            new JsonSerializerOptions { WriteIndented = true }));
    }
}

internal sealed class CommandLineException(string message) : Exception(message);

internal sealed class CommandLine
{
    public string Command { get; private set; } = "listen";

    public string? FilePath { get; private set; }

    public string? DeviceId { get; private set; }

    public double Seconds { get; private set; }

    public double BlockMs { get; private set; } = 10.0;

    public double RingSeconds { get; private set; } = 4.0;

    public bool NoRaw { get; private set; }

    public bool StrictRaw { get; private set; }

    public bool NoMmcss { get; private set; }

    public bool Json { get; private set; }

    public string? SaveDirectory { get; private set; }

    public bool SaveRejected { get; private set; }

    public bool ShowFeatures { get; private set; }

    public bool ShowChannels { get; private set; }

    public double? WindowMs { get; private set; }

    public double? PreRollMs { get; private set; }

    public double? OnsetThresholdDb { get; private set; }

    public double? MinOnsetDbfs { get; private set; }

    public double? MinRiseDb { get; private set; }

    public double? MinPeakDbfs { get; private set; }

    public double? RefractoryMs { get; private set; }

    public double? MaxAttackMs { get; private set; }

    public double? MaxDurationMs { get; private set; }

    public double? RoomLearnSeconds { get; private set; }

    /// <summary>
    /// Builds detector settings, leaving anything the user did not override at its default.
    /// Every one of these is meant to be swept, which is why they are all on the command line.
    /// </summary>
    public DetectorOptions BuildDetectorOptions()
    {
        var options = new DetectorOptions();

        if (WindowMs is { } window)
        {
            options.WindowMs = window;
        }

        if (PreRollMs is { } preRoll)
        {
            options.PreRollMs = preRoll;
        }

        if (OnsetThresholdDb is { } threshold)
        {
            options.OnsetThresholdDb = threshold;
        }

        if (MinOnsetDbfs is { } minOnset)
        {
            options.MinOnsetDbfs = minOnset;
        }

        if (MinRiseDb is { } minRise)
        {
            options.MinRiseDb = minRise;
        }

        if (MinPeakDbfs is { } minPeak)
        {
            options.MinPeakDbfs = minPeak;
        }

        if (RefractoryMs is { } refractory)
        {
            options.RefractoryMs = refractory;
        }

        if (MaxAttackMs is { } attack)
        {
            options.MaxAttackMs = attack;
        }

        if (MaxDurationMs is { } duration)
        {
            options.MaxEffectiveDurationMs = duration;
        }

        if (RoomLearnSeconds is { } learn)
        {
            options.RoomLearnSeconds = learn;
        }

        return options;
    }

    public static CommandLine Parse(string[] args)
    {
        var result = new CommandLine();

        if (args.Length == 0)
        {
            result.Command = "help";
            return result;
        }

        int index = 0;
        string first = args[0];

        if (!first.StartsWith('-'))
        {
            result.Command = first.ToLowerInvariant() switch
            {
                "devices" or "listen" or "replay" or "record" or "detect" or "help" => first.ToLowerInvariant(),
                _ => throw new CommandLineException($"unknown command '{first}'"),
            };

            index = 1;

            if (result.Command is "replay" or "record")
            {
                if (index >= args.Length || args[index].StartsWith('-'))
                {
                    throw new CommandLineException($"{result.Command} requires a file path");
                }

                result.FilePath = args[index++];
            }
        }

        for (; index < args.Length; index++)
        {
            string arg = args[index];

            switch (arg)
            {
                case "--device":
                    result.DeviceId = RequireValue(args, ref index, arg);
                    break;
                case "--seconds":
                    result.Seconds = ParseDouble(RequireValue(args, ref index, arg), arg);
                    break;
                case "--block":
                    result.BlockMs = ParseDouble(RequireValue(args, ref index, arg), arg);
                    break;
                case "--ring":
                    result.RingSeconds = ParseDouble(RequireValue(args, ref index, arg), arg);
                    break;
                case "--no-raw":
                    result.NoRaw = true;
                    break;
                case "--strict-raw":
                    result.StrictRaw = true;
                    break;
                case "--no-mmcss":
                    result.NoMmcss = true;
                    break;
                case "--json":
                    result.Json = true;
                    break;
                case "--file":
                    result.FilePath = RequireValue(args, ref index, arg);
                    break;
                case "--save":
                    result.SaveDirectory = RequireValue(args, ref index, arg);
                    break;
                case "--save-rejected":
                    result.SaveRejected = true;
                    break;
                case "--features":
                    result.ShowFeatures = true;
                    break;
                case "--channels":
                    result.ShowChannels = true;
                    break;
                case "--window":
                    result.WindowMs = ParseDouble(RequireValue(args, ref index, arg), arg);
                    break;
                case "--preroll":
                    result.PreRollMs = ParseDouble(RequireValue(args, ref index, arg), arg);
                    break;
                case "--threshold":
                    result.OnsetThresholdDb = ParseDouble(RequireValue(args, ref index, arg), arg);
                    break;
                case "--min-onset":
                    result.MinOnsetDbfs = ParseDouble(RequireValue(args, ref index, arg), arg);
                    break;
                case "--min-rise":
                    result.MinRiseDb = ParseDouble(RequireValue(args, ref index, arg), arg);
                    break;
                case "--min-peak":
                    result.MinPeakDbfs = ParseDouble(RequireValue(args, ref index, arg), arg);
                    break;
                case "--refractory":
                    result.RefractoryMs = ParseDouble(RequireValue(args, ref index, arg), arg);
                    break;
                case "--max-attack":
                    result.MaxAttackMs = ParseDouble(RequireValue(args, ref index, arg), arg);
                    break;
                case "--max-duration":
                    result.MaxDurationMs = ParseDouble(RequireValue(args, ref index, arg), arg);
                    break;
                case "--learn":
                    result.RoomLearnSeconds = ParseDouble(RequireValue(args, ref index, arg), arg);
                    break;
                case "-h" or "--help":
                    result.Command = "help";
                    break;
                default:
                    throw new CommandLineException($"unknown option '{arg}'");
            }
        }

        return result;
    }

    private static string RequireValue(string[] args, ref int index, string option)
    {
        if (index + 1 >= args.Length)
        {
            throw new CommandLineException($"{option} requires a value");
        }

        return args[++index];
    }

    private static double ParseDouble(string value, string option) =>
        double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out double parsed)
            ? parsed
            : throw new CommandLineException($"{option} expects a number, got '{value}'");
}
