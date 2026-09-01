using System.Diagnostics;
using System.Globalization;
using System.Text;
using Tapit.Audio;
using Tapit.Core.Audio;
using Tapit.Core.Detection;
using Tapit.Core.Features;

namespace Tapit.MicCheck;

/// <summary>
/// The tap-detection experiment harness.
/// </summary>
/// <remarks>
/// Live capture and WAV replay run through <b>the same</b> <see cref="TapDetector"/> against
/// the same ring buffer. Nothing is simulated on either path, so an event saved from the
/// microphone and then replayed produces the identical decision - which is what makes
/// threshold tuning an experiment rather than an argument.
/// </remarks>
internal static class DetectCommand
{
    private const int EventLogLines = 12;

    public static int Run(CommandLine options)
    {
        DetectorOptions detectorOptions = options.BuildDetectorOptions();

        return options.FilePath is not null
            ? RunFile(options, detectorOptions)
            : RunLive(options, detectorOptions);
    }

    // -----------------------------------------------------------------------------------
    // Live microphone
    // -----------------------------------------------------------------------------------

    private static int RunLive(CommandLine options, DetectorOptions detectorOptions)
    {
        using var source = new WasapiCaptureSource(new WasapiCaptureOptions
        {
            DeviceId = options.DeviceId,
            RequestRawMode = !options.NoRaw,
            UseMmcss = !options.NoMmcss,
            RingSeconds = options.RingSeconds,
        });

        var recentStates = new List<string>();
        source.StateChanged += (_, e) =>
        {
            lock (recentStates)
            {
                recentStates.Add($"{DateTime.Now:HH:mm:ss}  {e.State}" +
                                 (string.IsNullOrEmpty(e.Message) ? string.Empty : $" - {e.Message}"));
                if (recentStates.Count > 3)
                {
                    recentStates.RemoveAt(0);
                }
            }
        };

        source.Start();

        var startup = Stopwatch.StartNew();
        while (source.Format is null && source.State != CaptureState.Faulted &&
               startup.ElapsedMilliseconds < 5000 && !Program.StopRequested)
        {
            Thread.Sleep(20);
        }

        if (source.Format is null || source.State == CaptureState.Faulted)
        {
            Console.Error.WriteLine("Capture could not be started.");
            lock (recentStates)
            {
                foreach (string line in recentStates)
                {
                    Console.Error.WriteLine("  " + line);
                }
            }

            return 1;
        }

        AudioFormat format = source.Format;
        var detector = new TapDetector(format, detectorOptions);
        var extractor = new TapFeatureExtractor(format.SampleRate, detector.WindowSamples);
        using var recorder = EventRecorder.Create(options.SaveDirectory, format.SampleRate, extractor);

        var log = new List<string>();
        var stats = new SessionStats();

        bool interactive = !Console.IsOutputRedirected;
        if (interactive)
        {
            Console.Clear();
            Console.CursorVisible = false;
        }

        var runtime = Stopwatch.StartNew();
        var lastRender = Stopwatch.StartNew();

        try
        {
            while (!Program.StopRequested)
            {
                if (options.Seconds > 0 && runtime.Elapsed.TotalSeconds >= options.Seconds)
                {
                    break;
                }

                source.WaitForData(50);

                foreach (TapEvent tapEvent in detector.Process(source.Buffer!, source.StreamGeneration))
                {
                    HandleEvent(tapEvent, extractor, recorder, log, stats, options);
                }

                if (lastRender.ElapsedMilliseconds >= 100)
                {
                    lastRender.Restart();
                    string frame = BuildLiveFrame(source, detector, stats, log, runtime.Elapsed, recorder);

                    if (interactive)
                    {
                        Console.SetCursorPosition(0, 0);
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
                Console.Write(BuildLiveFrame(source, detector, stats, log, runtime.Elapsed, recorder));
                Console.WriteLine();
            }

            source.Stop();
        }

        PrintSummary(stats, detector, recorder);
        return 0;
    }

    // -----------------------------------------------------------------------------------
    // WAV replay - identical detector, no microphone
    // -----------------------------------------------------------------------------------

    private static int RunFile(CommandLine options, DetectorOptions detectorOptions)
    {
        string path = options.FilePath!;
        if (!File.Exists(path))
        {
            Console.Error.WriteLine($"WAV file not found: {path}");
            return 2;
        }

        using var source = new FileAudioCaptureSource(path, ReplayPacing.Manual, packetFrames: 0, ringSeconds: options.RingSeconds);
        source.Start();

        AudioFormat format = source.Format!;
        var detector = new TapDetector(format, detectorOptions);
        var extractor = new TapFeatureExtractor(format.SampleRate, detector.WindowSamples);
        using var recorder = EventRecorder.Create(options.SaveDirectory, format.SampleRate, extractor);

        var log = new List<string>();
        var stats = new SessionStats();
        var timer = Stopwatch.StartNew();

        Console.WriteLine();
        Console.WriteLine($"  REPLAY  {Path.GetFileName(path)}");
        Console.WriteLine($"  {format}  -  {source.TotalFramesInFile} frames, " +
                          $"{format.FramesToSeconds(source.TotalFramesInFile):0.00} s");
        Console.WriteLine($"  window {detector.WindowMs:0.#} ms  pre-roll {detectorOptions.PreRollMs:0.#} ms  " +
                          $"onset +{detectorOptions.OnsetThresholdDb:0.#} dB  refractory {detectorOptions.RefractoryMs:0} ms");
        Console.WriteLine();

        while (source.Pump())
        {
            foreach (TapEvent tapEvent in detector.Process(source.Buffer!, source.StreamGeneration))
            {
                HandleEvent(tapEvent, extractor, recorder, log, stats, options);
                Console.WriteLine("  " + tapEvent.Summary);

                if (options.ShowFeatures)
                {
                    PrintFeatures(extractor, tapEvent);
                }
            }
        }

        // Drain anything whose window completed on the final packet.
        foreach (TapEvent tapEvent in detector.Process(source.Buffer!, source.StreamGeneration))
        {
            HandleEvent(tapEvent, extractor, recorder, log, stats, options);
            Console.WriteLine("  " + tapEvent.Summary);

            if (options.ShowFeatures)
            {
                PrintFeatures(extractor, tapEvent);
            }
        }

        timer.Stop();
        Console.WriteLine();
        Console.WriteLine($"  processed in {timer.Elapsed.TotalMilliseconds:0.0} ms " +
                          $"({format.FramesToSeconds(source.TotalFramesInFile) * 1000.0 / Math.Max(1.0, timer.Elapsed.TotalMilliseconds):0.0}x realtime)");

        PrintSummary(stats, detector, recorder);
        return 0;
    }

    // -----------------------------------------------------------------------------------

    /// <summary>
    /// Prints the spatial cues between channels for one event.
    /// </summary>
    /// <remarks>
    /// This is the measurement that decides whether left-versus-right is recoverable at all
    /// on a given microphone. If the two channels turn out to be one signal duplicated, no
    /// amount of feature engineering will separate the two sides.
    /// </remarks>
    internal static string DescribeChannels(TapEvent tapEvent)
    {
        if (tapEvent.ChannelWindows.Length < 2)
        {
            return "      channels: mono - no spatial information available";
        }

        // Attack region only: the direct arrival carries direction, the ring does not.
        int attack = Math.Max(16, tapEvent.SampleRate / 100);   // 10 ms

        InterChannelCues cues = InterChannel.Measure(
            tapEvent.ChannelWindows[0], tapEvent.ChannelWindows[1], tapEvent.SampleRate, attack);

        string side = Math.Abs(cues.LagMicroseconds) < 20 && Math.Abs(cues.LevelDifferenceDb) < 0.5
            ? "centre/none"
            : cues.LagSamples < 0 || cues.LevelDifferenceDb > 0 ? "ch0 leads" : "ch1 leads";

        return $"      channels: level {cues.LevelDifferenceDb,6:+0.00;-0.00} dB   " +
               $"lag {cues.LagMicroseconds,8:+0.0;-0.0} us   " +
               $"corr {cues.PeakCorrelation,5:0.000}   {side}" +
               (cues.LooksDegenerate ? "   [DEGENERATE - channels are duplicates]" : string.Empty);
    }

    private static void HandleEvent(
        TapEvent tapEvent,
        TapFeatureExtractor extractor,
        EventRecorder? recorder,
        List<string> log,
        SessionStats stats,
        CommandLine options)
    {
        stats.Record(tapEvent);

        if (options.ShowChannels && tapEvent.Accepted)
        {
            Console.WriteLine($"  {tapEvent.OnsetSeconds,7:0.000}s  ACCEPT");
            Console.WriteLine(DescribeChannels(tapEvent));
        }

        log.Add(tapEvent.Summary);
        if (log.Count > EventLogLines)
        {
            log.RemoveAt(0);
        }

        if (recorder is not null && (tapEvent.Accepted || options.SaveRejected))
        {
            recorder.Save(tapEvent);
        }
    }

    private static void PrintFeatures(TapFeatureExtractor extractor, TapEvent tapEvent)
    {
        float[]? features = extractor.Extract(tapEvent);
        if (features is null)
        {
            Console.WriteLine("      features: (unavailable)");
            return;
        }

        var sb = new StringBuilder("      ");
        for (int i = 0; i < features.Length; i++)
        {
            sb.Append(CultureInfo.InvariantCulture, $"{TapFeatureExtractor.Names[i]}={features[i]:0.###}  ");
            if ((i + 1) % 4 == 0 && i + 1 < features.Length)
            {
                sb.Append("\n      ");
            }
        }

        Console.WriteLine(sb.ToString().TrimEnd());
    }

    private static string BuildLiveFrame(
        IAudioCaptureSource source,
        TapDetector detector,
        SessionStats stats,
        List<string> log,
        TimeSpan elapsed,
        EventRecorder? recorder)
    {
        CaptureStatistics captureStats = source.GetStatistics();
        var sb = new StringBuilder(4096);

        void Line(string text = "") => sb.Append(text.PadRight(100)).Append('\n');

        Line("  TAPIT - TAP DETECTOR");
        Line("  " + new string('─', 84));
        Line($"  {source.DeviceName}   {source.Format}   " +
             (captureStats.RawModeActive ? "raw" : "PROCESSED - enhancements active"));
        Line();

        if (detector.IsLearningRoom)
        {
            Line($"  LEARNING ROOM   {detector.RoomLearnProgress * 100:0}%   - stay quiet");
        }
        else
        {
            Line("  LISTENING       tap the desk");
        }

        Line();
        Line($"  Noise floor  {detector.NoiseFloorDbfs,7:0.0} dBFS   {Meter(detector.NoiseFloorDbfs)}");
        Line($"  Level        {detector.LastFrameDbfs,7:0.0} dBFS   {Meter(detector.LastFrameDbfs)}");
        Line($"  Onset at     {detector.NoiseFloorDbfs + detector.Options.OnsetThresholdDb,7:0.0} dBFS   " +
             $"(floor +{detector.Options.OnsetThresholdDb:0.#} dB, min {detector.Options.MinOnsetDbfs:0.#} dBFS)");
        Line();

        Line($"  Elapsed {elapsed.TotalSeconds,6:0.0} s     " +
             $"candidates {stats.Total}     accepted {stats.Accepted}     rejected {stats.Rejected}" +
             (recorder is not null ? $"     saved {recorder.SavedCount}" : string.Empty));

        if (stats.Rejected > 0)
        {
            Line("  Rejections: " + stats.RejectionBreakdown());
        }
        else
        {
            Line();
        }

        Line();
        Line("  EVENTS");

        if (log.Count == 0)
        {
            Line("    (none yet)");
        }
        else
        {
            foreach (string line in log)
            {
                Line("    " + line);
            }
        }

        for (int i = log.Count; i < EventLogLines; i++)
        {
            Line();
        }

        Line();
        Line("  Ctrl+C to stop.");

        return sb.ToString();
    }

    private static string Meter(double dbfs)
    {
        const int width = 34;
        double normalised = Math.Clamp((dbfs + 80.0) / 80.0, 0.0, 1.0);
        int filled = (int)Math.Round(normalised * width);
        return "[" + new string('#', filled) + new string('.', width - filled) + "]";
    }

    private static void PrintSummary(SessionStats stats, TapDetector detector, EventRecorder? recorder)
    {
        Console.WriteLine();
        Console.WriteLine("  SUMMARY");
        Console.WriteLine($"    candidates      {stats.Total}");
        Console.WriteLine($"    accepted        {stats.Accepted}");
        Console.WriteLine($"    rejected        {stats.Rejected}");

        if (stats.Rejected > 0)
        {
            Console.WriteLine($"    reasons         {stats.RejectionBreakdown()}");
        }

        Console.WriteLine($"    noise floor     {detector.NoiseFloorDbfs:0.0} dBFS");
        Console.WriteLine($"    frames dropped  {detector.FramesDropped}");

        if (stats.PeakDbfs > -3.0)
        {
            // An overdriven capture distorts the waveform, which corrupts attack time and
            // duration for every event -- not only the ones actually flagged as clipped.
            Console.WriteLine();
            Console.WriteLine($"    !!  INPUT OVERLOADING - loudest window peaked at {stats.PeakDbfs:0.0} dBFS.");
            Console.WriteLine("        Lower the microphone level in Windows sound settings, or tap");
            Console.WriteLine("        more softly. Distortion corrupts every measurement, not just");
            Console.WriteLine("        the events reported as clipped.");
        }

        if (recorder is not null)
        {
            Console.WriteLine($"    saved           {recorder.SavedCount} events → {recorder.Directory}");
            Console.WriteLine($"    features        {recorder.CsvPath}");
        }

        Console.WriteLine();
    }
}

internal sealed class SessionStats
{
    private readonly Dictionary<RejectionReason, int> _reasons = [];

    public int Total { get; private set; }

    public int Accepted { get; private set; }

    /// <summary>Loudest window peak seen, for detecting an overdriven input.</summary>
    public double PeakDbfs { get; private set; } = double.NegativeInfinity;

    public int Rejected => Total - Accepted;

    public void Record(TapEvent tapEvent)
    {
        Total++;

        if (tapEvent.Measurements.PeakDbfs > PeakDbfs)
        {
            PeakDbfs = tapEvent.Measurements.PeakDbfs;
        }

        if (tapEvent.Accepted)
        {
            Accepted++;
            return;
        }

        _reasons.TryGetValue(tapEvent.Rejection, out int count);
        _reasons[tapEvent.Rejection] = count + 1;
    }

    public string RejectionBreakdown() =>
        string.Join(", ", _reasons.OrderByDescending(p => p.Value).Select(p => $"{p.Key}×{p.Value}"));
}

/// <summary>
/// Writes each detected window to its own WAV, plus one CSV of measurements and features.
/// </summary>
/// <remarks>
/// This is the data-collection path for real-desk experiments. It only exists when the user
/// passes <c>--save</c>; nothing is written to disk otherwise.
/// </remarks>
internal sealed class EventRecorder : IDisposable
{
    private readonly AudioFormat _format;
    private readonly TapFeatureExtractor _extractor;
    private readonly StreamWriter _csv;
    private int _index;

    private EventRecorder(string directory, AudioFormat format, TapFeatureExtractor extractor)
    {
        Directory = directory;
        _format = format;
        _extractor = extractor;

        System.IO.Directory.CreateDirectory(directory);
        CsvPath = Path.Combine(directory, "events.csv");

        bool exists = File.Exists(CsvPath);
        _csv = new StreamWriter(CsvPath, append: true);

        if (!exists)
        {
            // Feature columns are prefixed so they never collide with the measurement
            // columns of the same name - otherwise anything reading this by header breaks.
            _csv.WriteLine(
                "index,file,onsetSeconds,accepted,rejection,noiseFloorDbfs,snrDb," +
                "rmsDbfs,peakDbfs,crestDb,attackMs,decayMs,durationMs,earlyEnergy,zcr,clipped," +
                string.Join(",", TapFeatureExtractor.Names.Select(n => "f_" + n)));
        }
    }

    public static EventRecorder? Create(string? directory, int sampleRate, TapFeatureExtractor extractor) =>
        string.IsNullOrWhiteSpace(directory)
            ? null
            : new EventRecorder(directory, new AudioFormat(sampleRate, 1, AudioSampleFormat.Float32), extractor);

    public string Directory { get; }

    public string CsvPath { get; }

    public int SavedCount { get; private set; }

    public void Save(TapEvent tapEvent)
    {
        if (tapEvent.Window.Length == 0)
        {
            return;
        }

        _index++;
        string label = tapEvent.Accepted ? "accept" : $"reject-{tapEvent.Rejection}";
        string name = $"tap-{_index:000}-{label}-{tapEvent.OnsetSeconds:0.000}s.wav";
        string path = Path.Combine(Directory, name);

        using (var writer = new WavWriter(path, _format))
        {
            writer.WriteFrames(tapEvent.Window, tapEvent.Window.Length);
        }

        float[]? features = _extractor.Extract(tapEvent);
        TapMeasurements m = tapEvent.Measurements;
        var culture = CultureInfo.InvariantCulture;

        _csv.WriteLine(string.Join(",",
            _index.ToString(culture),
            name,
            tapEvent.OnsetSeconds.ToString("0.0000", culture),
            tapEvent.Accepted ? "1" : "0",
            tapEvent.Rejection.ToString(),
            tapEvent.NoiseFloorDbfs.ToString("0.00", culture),
            tapEvent.SnrDb.ToString("0.00", culture),
            m.RmsDbfs.ToString("0.00", culture),
            m.PeakDbfs.ToString("0.00", culture),
            m.CrestDb.ToString("0.00", culture),
            m.AttackMs.ToString("0.000", culture),
            m.DecayMs.ToString("0.000", culture),
            m.EffectiveDurationMs.ToString("0.00", culture),
            m.EarlyEnergyFraction.ToString("0.0000", culture),
            m.ZeroCrossingRate.ToString("0.0000", culture),
            m.ClippedSamples.ToString(culture),
            features is null
                ? string.Join(",", Enumerable.Repeat("", TapFeatureExtractor.Count))
                : string.Join(",", features.Select(f => f.ToString("0.0000", culture)))));

        _csv.Flush();
        SavedCount++;
    }

    public void Dispose() => _csv.Dispose();
}
