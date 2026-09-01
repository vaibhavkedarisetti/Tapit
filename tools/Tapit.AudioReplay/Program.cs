using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using Tapit.Core.Audio;
using Tapit.Core.Classification;
using Tapit.Core.Detection;
using Tapit.Core.Evaluation;
using Tapit.Core.Features;
using Tapit.Core.Profiles;

namespace Tapit.AudioReplay;

/// <summary>
/// Offline replay harness.
/// </summary>
/// <remarks>
/// <para>
/// Runs recorded audio through <b>the production code</b> - the same
/// <see cref="TapDetector"/>, the same <see cref="TapFeatureExtractor"/>, the same
/// <see cref="ZoneModel"/> and the same rejection stack the live application uses - by
/// substituting a WAV file for the microphone behind <c>IAudioCaptureSource</c>.
/// </para>
/// <para>
/// This is what makes DSP work repeatable. Every threshold in the detector is meant to be
/// chosen by sweeping it over a recorded corpus and reading the result, not by argument.
/// </para>
/// </remarks>
internal static class Program
{
    private static int Main(string[] args)
    {
        Console.OutputEncoding = Encoding.UTF8;

        try
        {
            Options options = Options.Parse(args);

            return options.Command switch
            {
                "help" => Usage(),
                "run" => Run(options),
                "sweep" => Sweep(options),
                "features" => DumpFeatures(options),
                _ => Usage(),
            };
        }
        catch (OptionException ex)
        {
            Console.Error.WriteLine($"tapit-replay: {ex.Message}");
            return 2;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"tapit-replay: {ex.Message}");
            return 1;
        }
    }

    private static int Usage()
    {
        Console.WriteLine("""
            Tapit audio replay - offline DSP harness

            USAGE
              Tapit.AudioReplay run      <file.wav|directory> [options]
              Tapit.AudioReplay features <file.wav|directory> --out <features.csv> [options]
              Tapit.AudioReplay sweep    <file.wav|directory> --param <name> --values <a,b,c>

            COMMANDS
              run       Detect, classify and report every event.
              features  Write a feature vector per detected event to CSV.
              sweep     Re-run one parameter across several values and compare.

            OPTIONS
              --profile <path>   profile.json to classify with. Without it, detection only.
              --label <zone>     True zone for these files, enabling accuracy scoring.
                                 One of LeftRear, LeftFront, RightRear, RightFront.
              --out <path>       Output CSV.
              --json             Machine-readable summary.
              --quiet            Suppress the per-event listing.

            DETECTOR TUNING
              --window <ms>  --preroll <ms>  --threshold <dB>  --min-rise <dB>
              --min-onset <dB>  --min-peak <dB>  --refractory <ms>  --max-attack <ms>
              --max-duration <ms>  --learn <s>

            SWEEPABLE PARAMETERS
              window, preroll, threshold, min-rise, min-onset, min-peak, refractory,
              max-attack, max-duration

            LABELLING BY FILENAME
              A file or folder whose name contains a zone (for example
              'left-front-taps.wav' or a folder 'RightRear/') is labelled automatically,
              so a corpus can be scored without passing --label for every run.
            """);

        return 0;
    }

    // -----------------------------------------------------------------------------------

    private static int Run(Options options)
    {
        IReadOnlyList<string> files = options.ResolveFiles();
        if (files.Count == 0)
        {
            Console.Error.WriteLine("No WAV files found.");
            return 2;
        }

        ZoneModel? model = options.LoadModel();
        var matrix = new ConfusionMatrix();
        var all = new List<ReplayEvent>();
        var stopwatch = Stopwatch.StartNew();

        foreach (string file in files)
        {
            Zone? label = options.Label ?? ZoneFromPath(file);
            IReadOnlyList<ReplayEvent> events = Replay(file, options.BuildDetectorOptions(), model);

            all.AddRange(events);

            if (!options.Quiet)
            {
                Console.WriteLine();
                Console.WriteLine($"  {Path.GetFileName(file)}" +
                                  (label is Zone z ? $"   [labelled {Zones.DisplayName(z)}]" : string.Empty));

                foreach (ReplayEvent replay in events)
                {
                    Console.WriteLine("    " + Describe(replay));
                }

                if (events.Count == 0)
                {
                    Console.WriteLine("    (no events)");
                }
            }

            if (label is Zone actual && model is not null)
            {
                foreach (ReplayEvent replay in events.Where(e => e.Event.Accepted))
                {
                    matrix.Add(actual, replay.Decision?.Accepted == true ? replay.Decision.Zone : null);
                }
            }
        }

        stopwatch.Stop();

        Console.WriteLine();
        Console.WriteLine("  SUMMARY");
        Console.WriteLine($"    files              {files.Count}");
        Console.WriteLine($"    candidates         {all.Count}");
        Console.WriteLine($"    detector accepted  {all.Count(e => e.Event.Accepted)}");
        Console.WriteLine($"    detector rejected  {all.Count(e => !e.Event.Accepted)}");

        foreach (IGrouping<RejectionReason, ReplayEvent> group in all
                     .Where(e => !e.Event.Accepted)
                     .GroupBy(e => e.Event.Rejection)
                     .OrderByDescending(g => g.Count()))
        {
            Console.WriteLine($"      {group.Key,-22} {group.Count()}");
        }

        if (model is not null)
        {
            int classified = all.Count(e => e.Decision?.Accepted == true);
            Console.WriteLine($"    model accepted     {classified}");
            Console.WriteLine($"    model rejected     {all.Count(e => e.Event.Accepted && e.Decision?.Accepted != true)}");
        }

        if (matrix.TotalEvents > 0)
        {
            Console.WriteLine();
            Console.WriteLine($"    accuracy (all)      {matrix.AccuracyOfAll:P1}");
            Console.WriteLine($"    accuracy (answered) {matrix.AccuracyOfClassified:P1}");
            Console.WriteLine();
            Console.Write(matrix.Render());
        }

        Console.WriteLine();
        Console.WriteLine($"    processed in {stopwatch.Elapsed.TotalMilliseconds:0} ms");

        if (options.Json)
        {
            Console.WriteLine(JsonSerializer.Serialize(new
            {
                files = files.Count,
                candidates = all.Count,
                detectorAccepted = all.Count(e => e.Event.Accepted),
                modelAccepted = all.Count(e => e.Decision?.Accepted == true),
                accuracy = matrix.TotalEvents > 0 ? matrix.AccuracyOfAll : (double?)null,
                rejections = all.Where(e => !e.Event.Accepted)
                    .GroupBy(e => e.Event.Rejection.ToString())
                    .ToDictionary(g => g.Key, g => g.Count()),
            }, new JsonSerializerOptions { WriteIndented = true }));
        }

        return 0;
    }

    private static int DumpFeatures(Options options)
    {
        IReadOnlyList<string> files = options.ResolveFiles();
        string output = options.Output ?? "features.csv";

        ZoneModel? model = options.LoadModel();
        var sb = new StringBuilder();

        sb.AppendLine("file,onsetSeconds,label,accepted,rejection,predicted,confidence," +
                      string.Join(",", TapFeatureExtractor.Names));

        int rows = 0;

        foreach (string file in files)
        {
            Zone? label = options.Label ?? ZoneFromPath(file);

            foreach (ReplayEvent replay in Replay(file, options.BuildDetectorOptions(), model))
            {
                if (replay.Features is null)
                {
                    continue;
                }

                sb.AppendLine(string.Join(",",
                    Path.GetFileName(file),
                    replay.Event.OnsetSeconds.ToString("0.0000", CultureInfo.InvariantCulture),
                    label?.ToString() ?? string.Empty,
                    replay.Event.Accepted ? "1" : "0",
                    replay.Event.Rejection.ToString(),
                    replay.Decision?.Zone?.ToString() ?? string.Empty,
                    (replay.Decision?.Confidence ?? 0).ToString("0.0000", CultureInfo.InvariantCulture),
                    string.Join(",", replay.Features.Select(f => f.ToString("0.0000", CultureInfo.InvariantCulture)))));

                rows++;
            }
        }

        File.WriteAllText(output, sb.ToString());
        Console.WriteLine($"  wrote {rows} feature rows to {output}");

        return 0;
    }

    private static int Sweep(Options options)
    {
        if (options.SweepParameter is null || options.SweepValues.Count == 0)
        {
            throw new OptionException("sweep needs --param and --values");
        }

        IReadOnlyList<string> files = options.ResolveFiles();
        ZoneModel? model = options.LoadModel();

        Console.WriteLine();
        Console.WriteLine($"  SWEEP  {options.SweepParameter}");
        Console.WriteLine();
        Console.WriteLine("    value    candidates  accepted  rejected  accuracy");

        foreach (double value in options.SweepValues)
        {
            DetectorOptions detector = options.BuildDetectorOptions();
            Apply(detector, options.SweepParameter, value);

            var matrix = new ConfusionMatrix();
            int candidates = 0;
            int accepted = 0;

            foreach (string file in files)
            {
                Zone? label = options.Label ?? ZoneFromPath(file);

                foreach (ReplayEvent replay in Replay(file, detector, model))
                {
                    candidates++;
                    if (replay.Event.Accepted)
                    {
                        accepted++;
                    }

                    if (label is Zone actual && model is not null && replay.Event.Accepted)
                    {
                        matrix.Add(actual, replay.Decision?.Accepted == true ? replay.Decision.Zone : null);
                    }
                }
            }

            string accuracy = matrix.TotalEvents > 0 ? $"{matrix.AccuracyOfAll,8:P1}" : "       -";
            Console.WriteLine($"    {value,7:0.##}  {candidates,10}  {accepted,8}  {candidates - accepted,8}  {accuracy}");
        }

        Console.WriteLine();
        Console.WriteLine("    Pick the value that maximises what you care about, then set it as the");
        Console.WriteLine("    default. A sweep over one recording is not a general result.");

        return 0;
    }

    private static void Apply(DetectorOptions options, string parameter, double value)
    {
        switch (parameter.ToLowerInvariant())
        {
            case "window": options.WindowMs = value; break;
            case "preroll": options.PreRollMs = value; break;
            case "threshold": options.OnsetThresholdDb = value; break;
            case "min-rise": options.MinRiseDb = value; break;
            case "min-onset": options.MinOnsetDbfs = value; break;
            case "min-peak": options.MinPeakDbfs = value; break;
            case "refractory": options.RefractoryMs = value; break;
            case "max-attack": options.MaxAttackMs = value; break;
            case "max-duration": options.MaxEffectiveDurationMs = value; break;
            default: throw new OptionException($"unknown sweep parameter '{parameter}'");
        }
    }

    // -----------------------------------------------------------------------------------

    private static IReadOnlyList<ReplayEvent> Replay(string file, DetectorOptions options, ZoneModel? model)
    {
        using var source = new FileAudioCaptureSource(file, ReplayPacing.Manual);
        source.Start();

        AudioFormat format = source.Format!;
        var detector = new TapDetector(format, options);
        var extractor = new TapFeatureExtractor(format.SampleRate, detector.WindowSamples);
        var results = new List<ReplayEvent>();

        void Drain()
        {
            foreach (TapEvent tapEvent in detector.Process(source.Buffer!, source.StreamGeneration))
            {
                float[]? features = tapEvent.Accepted ? extractor.Extract(tapEvent) : null;
                ZoneDecision? decision = model is not null && features is not null
                    ? model.Predict(features)
                    : null;

                results.Add(new ReplayEvent(tapEvent, features, decision));
            }
        }

        while (source.Pump())
        {
            Drain();
        }

        Drain();
        return results;
    }

    private static string Describe(ReplayEvent replay)
    {
        string line = replay.Event.Summary;

        if (replay.Decision is { } decision)
        {
            line += decision.Accepted
                ? $"  →  {Zones.DisplayName(decision.Zone!.Value)} ({decision.Confidence:P0})"
                : $"  →  rejected: {ZoneRejectionText.Describe(decision.Rejection)}";
        }

        return line;
    }

    /// <summary>Infers a zone label from a file or folder name, so corpora self-describe.</summary>
    internal static Zone? ZoneFromPath(string path)
    {
        string normalised = path.Replace('\\', '/').ToLowerInvariant().Replace("_", "-").Replace(" ", "-");

        // Longest names first so "left-front" is not shadowed by "left".
        if (normalised.Contains("left-front") || normalised.Contains("leftfront"))
        {
            return Zone.LeftFront;
        }

        if (normalised.Contains("left-rear") || normalised.Contains("leftrear"))
        {
            return Zone.LeftRear;
        }

        if (normalised.Contains("right-front") || normalised.Contains("rightfront"))
        {
            return Zone.RightFront;
        }

        if (normalised.Contains("right-rear") || normalised.Contains("rightrear"))
        {
            return Zone.RightRear;
        }

        return null;
    }
}

internal sealed record ReplayEvent(TapEvent Event, float[]? Features, ZoneDecision? Decision);

internal sealed class OptionException(string message) : Exception(message);

internal sealed class Options
{
    public string Command { get; private set; } = "help";

    public string? Path { get; private set; }

    public string? ProfilePath { get; private set; }

    public string? Output { get; private set; }

    public Zone? Label { get; private set; }

    public bool Json { get; private set; }

    public bool Quiet { get; private set; }

    public string? SweepParameter { get; private set; }

    public List<double> SweepValues { get; } = [];

    private readonly Dictionary<string, double> _overrides = [];

    public static Options Parse(string[] args)
    {
        var result = new Options();

        if (args.Length == 0)
        {
            return result;
        }

        int index = 0;
        string first = args[0];

        if (!first.StartsWith('-'))
        {
            result.Command = first.ToLowerInvariant() switch
            {
                "run" or "features" or "sweep" or "help" => first.ToLowerInvariant(),
                _ => throw new OptionException($"unknown command '{first}'"),
            };

            index = 1;

            if (result.Command != "help" && index < args.Length && !args[index].StartsWith('-'))
            {
                result.Path = args[index++];
            }
        }

        for (; index < args.Length; index++)
        {
            string arg = args[index];

            switch (arg)
            {
                case "--profile": result.ProfilePath = Value(args, ref index, arg); break;
                case "--out": result.Output = Value(args, ref index, arg); break;
                case "--json": result.Json = true; break;
                case "--quiet": result.Quiet = true; break;
                case "--param": result.SweepParameter = Value(args, ref index, arg); break;
                case "-h" or "--help": result.Command = "help"; break;

                case "--label":
                    result.Label = Enum.TryParse(Value(args, ref index, arg), ignoreCase: true, out Zone zone)
                        ? zone
                        : throw new OptionException("--label must be LeftRear, LeftFront, RightRear or RightFront");
                    break;

                case "--values":
                    foreach (string part in Value(args, ref index, arg).Split(',', StringSplitOptions.RemoveEmptyEntries))
                    {
                        result.SweepValues.Add(ParseNumber(part, arg));
                    }

                    break;

                case "--window" or "--preroll" or "--threshold" or "--min-rise" or "--min-onset"
                    or "--min-peak" or "--refractory" or "--max-attack" or "--max-duration" or "--learn":
                    result._overrides[arg[2..]] = ParseNumber(Value(args, ref index, arg), arg);
                    break;

                default:
                    throw new OptionException($"unknown option '{arg}'");
            }
        }

        if (result.Command != "help" && result.Path is null)
        {
            throw new OptionException("a WAV file or directory is required");
        }

        return result;
    }

    private static string Value(string[] args, ref int index, string option) =>
        index + 1 < args.Length ? args[++index] : throw new OptionException($"{option} requires a value");

    private static double ParseNumber(string text, string option) =>
        double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out double value)
            ? value
            : throw new OptionException($"{option} expects a number, got '{text}'");

    public IReadOnlyList<string> ResolveFiles()
    {
        if (Path is null)
        {
            return [];
        }

        if (File.Exists(Path))
        {
            return [Path];
        }

        if (Directory.Exists(Path))
        {
            return Directory.EnumerateFiles(Path, "*.wav", SearchOption.AllDirectories)
                .OrderBy(f => f, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        throw new OptionException($"not found: {Path}");
    }

    public ZoneModel? LoadModel()
    {
        if (ProfilePath is null)
        {
            return null;
        }

        string path = File.Exists(ProfilePath)
            ? ProfilePath
            : System.IO.Path.Combine(ProfilePath, "profile.json");

        if (!File.Exists(path))
        {
            throw new OptionException($"profile not found: {ProfilePath}");
        }

        TapitProfile profile = JsonSerializer.Deserialize<TapitProfile>(
            File.ReadAllText(path),
            new JsonSerializerOptions
            {
                Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() },
            }) ?? throw new OptionException("profile could not be read");

        return profile.BuildModel() ?? throw new OptionException("profile has no calibration");
    }

    public DetectorOptions BuildDetectorOptions()
    {
        var options = new DetectorOptions();

        foreach ((string name, double value) in _overrides)
        {
            switch (name)
            {
                case "window": options.WindowMs = value; break;
                case "preroll": options.PreRollMs = value; break;
                case "threshold": options.OnsetThresholdDb = value; break;
                case "min-rise": options.MinRiseDb = value; break;
                case "min-onset": options.MinOnsetDbfs = value; break;
                case "min-peak": options.MinPeakDbfs = value; break;
                case "refractory": options.RefractoryMs = value; break;
                case "max-attack": options.MaxAttackMs = value; break;
                case "max-duration": options.MaxEffectiveDurationMs = value; break;
                case "learn": options.RoomLearnSeconds = value; break;
            }
        }

        return options;
    }
}
