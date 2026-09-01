using Tapit.Core.Audio;
using Tapit.Core.Calibration;
using Tapit.Core.Classification;
using Tapit.Core.Detection;
using Tapit.Core.Evaluation;
using Tapit.Core.Profiles;

namespace Tapit.Core.Tests;

public class CalibrationSessionTests
{
    private static TapEvent Accepted() => new()
    {
        OnsetSample = 1000,
        WindowStartSample = 400,
        OnsetSeconds = 1.0,
        Accepted = true,
        Rejection = RejectionReason.None,
        Measurements = default,
        NoiseFloorDbfs = -60,
        SnrDb = 30,
        Window = new float[10],
        SampleRate = 48000,
    };

    private static TapEvent Rejected(RejectionReason reason) => new()
    {
        OnsetSample = 1000,
        WindowStartSample = 400,
        OnsetSeconds = 1.0,
        Accepted = false,
        Rejection = reason,
        Measurements = default,
        NoiseFloorDbfs = -60,
        SnrDb = 3,
        Window = new float[10],
        SampleRate = 48000,
    };

    private static float[] Features(int seed) => [seed, seed * 2f, seed * 0.5f];

    private static void FillZone(CalibrationSession session, int count, int seed = 0)
    {
        for (int i = 0; i < count; i++)
        {
            session.Offer(Accepted(), Features(seed + i));
        }
    }

    [Fact]
    public void CollectsTenPerZoneInOrderThenCompletes()
    {
        var session = new CalibrationSession();
        session.Start();

        Assert.Equal(Zone.LeftRear, session.CurrentZone);

        foreach (Zone zone in Zones.All)
        {
            Assert.Equal(zone, session.CurrentZone);
            FillZone(session, 10);
        }

        Assert.Equal(CalibrationState.Complete, session.State);
        Assert.Equal(40, session.TotalAccepted);
        Assert.Null(session.CurrentZone);

        foreach (Zone zone in Zones.All)
        {
            Assert.Equal(10, session.AcceptedFor(zone));
        }
    }

    [Fact]
    public void RejectedEventsDoNotCount()
    {
        var session = new CalibrationSession();
        session.Start();

        CalibrationFeedback feedback = session.Offer(Rejected(RejectionReason.SignalTooWeak), Features(1));

        Assert.False(feedback.Counted);
        Assert.Equal(CalibrationOutcome.RejectedByDetector, feedback.Outcome);
        Assert.Equal("Signal too weak", feedback.Message);
        Assert.Equal(0, session.TotalAccepted);
    }

    [Fact]
    public void SoundsArrivingWhileNotArmedAreIgnored()
    {
        // The whole reason the session has an armed state: a cough between prompts must
        // never become training data.
        var session = new CalibrationSession();

        Assert.False(session.IsArmed);
        Assert.Equal(CalibrationOutcome.NotArmed, session.Offer(Accepted(), Features(1)).Outcome);
        Assert.Equal(0, session.TotalAccepted);

        session.Start();
        session.Pause();

        Assert.False(session.IsArmed);
        Assert.Equal(CalibrationOutcome.NotArmed, session.Offer(Accepted(), Features(2)).Outcome);
        Assert.Equal(0, session.TotalAccepted);

        session.Resume();
        Assert.True(session.Offer(Accepted(), Features(3)).Counted);
    }

    [Fact]
    public void NonFiniteFeaturesAreRefused()
    {
        var session = new CalibrationSession();
        session.Start();

        Assert.Equal(CalibrationOutcome.BadFeatures, session.Offer(Accepted(), [float.NaN, 1f, 2f]).Outcome);
        Assert.Equal(CalibrationOutcome.BadFeatures, session.Offer(Accepted(), null).Outcome);
        Assert.Equal(0, session.TotalAccepted);
    }

    [Fact]
    public void UndoRemovesTheLastSampleAndStepsBack()
    {
        var session = new CalibrationSession();
        session.Start();

        FillZone(session, 10);
        Assert.Equal(Zone.LeftFront, session.CurrentZone);

        Assert.True(session.Undo());

        Assert.Equal(9, session.AcceptedFor(Zone.LeftRear));
        Assert.Equal(Zone.LeftRear, session.CurrentZone);
    }

    [Fact]
    public void UndoOnAnEmptySessionDoesNothing() =>
        Assert.False(new CalibrationSession().Undo());

    [Fact]
    public void RetryZoneClearsOnlyThatZone()
    {
        var session = new CalibrationSession();
        session.Start();

        FillZone(session, 10);      // LeftRear
        FillZone(session, 4);       // LeftFront, partial

        Assert.True(session.RetryZone());

        Assert.Equal(10, session.AcceptedFor(Zone.LeftRear));
        Assert.Equal(0, session.AcceptedFor(Zone.LeftFront));
    }

    [Fact]
    public void RetryZoneReopensACompletedSession()
    {
        var session = new CalibrationSession(samplesPerZone: 2);
        session.Start();

        foreach (Zone unused in Zones.All)
        {
            FillZone(session, 2);
        }

        Assert.Equal(CalibrationState.Complete, session.State);

        session.RetryZone(Zone.RightRear);

        Assert.Equal(CalibrationState.Collecting, session.State);
        Assert.Equal(Zone.RightRear, session.CurrentZone);
    }

    [Fact]
    public void CancelStopsCollection()
    {
        var session = new CalibrationSession();
        session.Start();
        session.Cancel();

        Assert.Equal(CalibrationState.Cancelled, session.State);
        Assert.False(session.IsArmed);
    }

    [Fact]
    public void ProgressReflectsAcceptedSamples()
    {
        var session = new CalibrationSession(samplesPerZone: 5);
        session.Start();

        FillZone(session, 5);

        Assert.Equal(20, session.TotalRequired);
        Assert.Equal(0.25, session.Progress, 4);
    }

    [Fact]
    public void NegativeExamplesAreCollectedSeparately()
    {
        var session = new CalibrationSession();
        session.Start();

        session.AddNegative([1f, 2f, 3f]);
        session.AddNegative([float.NaN, 1f, 2f]);   // refused

        Assert.Single(session.Negatives);
    }

    [Fact]
    public void ReportFlagsUnseparableCalibration()
    {
        var session = new CalibrationSession(samplesPerZone: 6);
        session.Start();

        // Identical features for every zone: nothing can separate these.
        foreach (Zone unused in Zones.All)
        {
            for (int i = 0; i < 6; i++)
            {
                session.Offer(Accepted(), [1f, 1f, 1f]);
            }
        }

        CalibrationReport report = session.BuildReport();

        Assert.True(report.RecommendRecalibration);
        Assert.True(report.Agreement < 0.6, $"agreement was {report.Agreement:P0}");
        Assert.Contains("not separable", report.Advice, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ReportOnSelfConsistentSamplesDoesNotDemandRecalibration()
    {
        var session = new CalibrationSession(samplesPerZone: 6);
        session.Start();

        var random = new Random(5);
        foreach (Zone zone in Zones.All)
        {
            for (int i = 0; i < 6; i++)
            {
                int index = Zones.IndexOf(zone);
                session.Offer(Accepted(),
                [
                    (float)(index * 10 + random.NextDouble() * 0.2),
                    (float)(index * -4 + random.NextDouble() * 0.2),
                    (float)(random.NextDouble() * 0.2),
                ]);
            }
        }

        CalibrationReport report = session.BuildReport();

        Assert.False(report.RecommendRecalibration);
        Assert.Contains("diagnostic", report.Advice, StringComparison.OrdinalIgnoreCase);
    }
}

public class EvaluationSessionTests
{
    private static TapEvent Event(bool accepted = true) => new()
    {
        OnsetSample = 0,
        WindowStartSample = 0,
        OnsetSeconds = 0,
        Accepted = accepted,
        Rejection = accepted ? RejectionReason.None : RejectionReason.SignalTooWeak,
        Measurements = default,
        NoiseFloorDbfs = -60,
        SnrDb = 30,
        Window = [],
        SampleRate = 48000,
    };

    [Fact]
    public void PromptsRotateThroughZonesAndTotalSixty()
    {
        var session = new EvaluationSession();

        Assert.Equal(60, session.TotalTrials);
        Assert.Equal(Zone.LeftRear, session.CurrentPrompt);

        var seen = new List<Zone>();
        while (session.CurrentPrompt is Zone prompt)
        {
            seen.Add(prompt);
            session.Skip();
        }

        Assert.Equal(60, seen.Count);

        foreach (Zone zone in Zones.All)
        {
            Assert.Equal(15, seen.Count(z => z == zone));
        }

        // Round-robin, not blocked: consecutive prompts differ.
        Assert.NotEqual(seen[0], seen[1]);
    }

    [Fact]
    public void PerfectRunReportsFullAccuracy()
    {
        var session = new EvaluationSession(trialsPerZone: 3);

        while (session.CurrentPrompt is Zone prompt)
        {
            session.Record(Event(), new ZoneDecision(prompt, 0.95, 0.5, 1.0, ZoneRejection.None), 120.0);
        }

        EvaluationReport report = session.BuildReport("nearest-neighbour");

        Assert.Equal(1.0, report.OverallAccuracy, 4);
        Assert.Equal(0, report.RejectedCount);
        Assert.Equal(120.0, report.MedianLatencyMs, 1);
        Assert.True(report.MeetsAccuracyTarget);
        Assert.True(report.MeetsLatencyTarget);
    }

    [Fact]
    public void RejectionsCountAgainstOverallAccuracyButNotAgainstAnswered()
    {
        var session = new EvaluationSession(trialsPerZone: 2);
        int index = 0;

        while (session.CurrentPrompt is Zone prompt)
        {
            bool reject = index++ % 2 == 0;

            session.Record(
                Event(!reject),
                reject
                    ? ZoneDecision.Reject(ZoneRejection.LowConfidence)
                    : new ZoneDecision(prompt, 0.9, 0.5, 1.0, ZoneRejection.None),
                100.0);
        }

        EvaluationReport report = session.BuildReport("test");

        Assert.Equal(0.5, report.OverallAccuracy, 4);
        Assert.Equal(1.0, report.AccuracyOfClassified, 4);
        Assert.Equal(4, report.RejectedCount);
    }

    [Fact]
    public void LatencyPercentilesAreReported()
    {
        var session = new EvaluationSession(trialsPerZone: 1);
        double[] latencies = [50, 100, 150, 900];
        int index = 0;

        while (session.CurrentPrompt is Zone prompt)
        {
            session.Record(Event(), new ZoneDecision(prompt, 1, 1, 1, ZoneRejection.None), latencies[index++]);
        }

        EvaluationReport report = session.BuildReport("test");

        Assert.Equal(100.0, report.MedianLatencyMs, 1);
        Assert.Equal(900.0, report.P95LatencyMs, 1);
    }

    [Fact]
    public void RecordingBeyondTheEndThrows()
    {
        var session = new EvaluationSession(trialsPerZone: 1);

        while (session.CurrentPrompt is not null)
        {
            session.Skip();
        }

        Assert.Throws<InvalidOperationException>(() =>
            session.Record(Event(), ZoneDecision.Reject(ZoneRejection.LowConfidence), 10));
    }

    [Fact]
    public void CsvHasARowPerTrial()
    {
        var session = new EvaluationSession(trialsPerZone: 1);

        while (session.CurrentPrompt is Zone prompt)
        {
            session.Record(Event(), new ZoneDecision(prompt, 0.9, 0.4, 1.0, ZoneRejection.None), 80);
        }

        string[] lines = session.BuildReport("test").ToCsv()
            .Split('\n', StringSplitOptions.RemoveEmptyEntries);

        Assert.Equal(5, lines.Length);   // header + 4 trials
        Assert.StartsWith("trial,prompted,predicted", lines[0], StringComparison.Ordinal);
    }
}

public sealed class ProfileStoreTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"tapit-profiles-{Guid.NewGuid():N}");

    private ProfileStore Store => new(_root);

    [Fact]
    public void ProfileRoundTripsThroughDisk()
    {
        var profile = new TapitProfile { Name = "Office Desk" };
        profile.Device = new DeviceBinding("id-1", "Mic", 48000, 2, "Float32", true);
        profile.Actions[Zone.LeftFront] = new ZoneActionBinding("open.url", "https://example.com");
        profile.SetSamples(
            [new LabeledSample(Zone.LeftRear, [1f, 2f]), new LabeledSample(Zone.RightFront, [3f, 4f])],
            ["a", "b"]);

        Store.Save(profile);

        TapitProfile? loaded = Store.Load(profile.Id);

        Assert.NotNull(loaded);
        Assert.Equal("Office Desk", loaded.Name);
        Assert.Equal(48000, loaded.Device!.SampleRate);
        Assert.True(loaded.Device.RawMode);
        Assert.Equal("open.url", loaded.Actions[Zone.LeftFront].ActionId);
        Assert.Equal("https://example.com", loaded.Actions[Zone.LeftFront].Argument);
        Assert.Equal(2, loaded.Samples.Count);
        Assert.Equal([1f, 2f], loaded.Samples[0].Features);
        Assert.Equal(["a", "b"], loaded.FeatureNames);
    }

    [Fact]
    public void ActiveProfileIdPersists()
    {
        ProfileStore store = Store;
        store.ActiveProfileId = "abc123";

        Assert.Equal("abc123", new ProfileStore(_root).ActiveProfileId);
    }

    [Fact]
    public void LoadAllSkipsCorruptProfiles()
    {
        ProfileStore store = Store;
        var good = new TapitProfile { Name = "Good" };
        store.Save(good);

        string badDirectory = Path.Combine(store.ProfilesDirectory, "broken");
        Directory.CreateDirectory(badDirectory);
        File.WriteAllText(Path.Combine(badDirectory, "profile.json"), "{ this is not json");

        IReadOnlyList<TapitProfile> all = store.LoadAll();

        Assert.Single(all);
        Assert.Equal("Good", all[0].Name);
    }

    [Fact]
    public void DeleteRemovesTheProfile()
    {
        ProfileStore store = Store;
        var profile = new TapitProfile();
        store.Save(profile);

        store.Delete(profile.Id);

        Assert.Null(store.Load(profile.Id));
    }

    [Fact]
    public void EvaluationHistoryIsAppendedAndPersisted()
    {
        ProfileStore store = Store;
        var profile = new TapitProfile();
        store.Save(profile);

        var session = new EvaluationSession(trialsPerZone: 1);
        while (session.CurrentPrompt is Zone prompt)
        {
            session.Record(
                new TapEvent
                {
                    OnsetSample = 0, WindowStartSample = 0, OnsetSeconds = 0, Accepted = true,
                    Rejection = RejectionReason.None, Measurements = default, NoiseFloorDbfs = -60,
                    SnrDb = 30, Window = [], SampleRate = 48000,
                },
                new ZoneDecision(prompt, 0.9, 0.4, 1.0, ZoneRejection.None),
                100);
        }

        store.SaveEvaluation(profile, session.BuildReport("nearest-neighbour"));

        TapitProfile reloaded = store.Load(profile.Id)!;

        Assert.Single(reloaded.EvaluationHistory);
        Assert.Equal(1.0, reloaded.EvaluationHistory[0].Accuracy, 4);
        Assert.True(Directory.EnumerateFiles(
            Path.Combine(store.ProfilesDirectory, profile.Id, "evaluations")).Any());
    }

    [Fact]
    public void BuildModelReturnsNullUntilCalibrated()
    {
        var profile = new TapitProfile();
        Assert.Null(profile.BuildModel());

        profile.SetSamples(
            Zones.All.SelectMany(z => Enumerable.Range(0, 3)
                .Select(i => new LabeledSample(z, [Zones.IndexOf(z) * 5f + i * 0.1f, i]))).ToList(),
            ["a", "b"]);

        Assert.NotNull(profile.BuildModel());
    }

    [Fact]
    public void DeviceChangesAreReportedInPlainLanguage()
    {
        var calibrated = new DeviceBinding("id-1", "Built-in Mic", 48000, 2, "Float32", true);

        Assert.Empty(calibrated.DifferencesFrom(calibrated));

        IReadOnlyList<string> differences = calibrated.DifferencesFrom(
            new DeviceBinding("id-2", "USB Mic", 44100, 1, "Int16", false));

        Assert.Equal(5, differences.Count);
        Assert.Contains(differences, d => d.Contains("microphone changed", StringComparison.Ordinal));
        Assert.Contains(differences, d => d.Contains("sample rate", StringComparison.Ordinal));
        Assert.Contains(differences, d => d.Contains("no longer available", StringComparison.Ordinal));
    }

    [Fact]
    public void CompatibilityCheckFlagsAChangedFeatureSet()
    {
        var profile = new TapitProfile
        {
            Device = new DeviceBinding("id-1", "Mic", 48000, 2, "Float32", true),
            FeatureNames = ["a", "b"],
        };

        ProfileCompatibility ok = profile.CheckCompatibility(profile.Device, ["a", "b"]);
        Assert.True(ok.IsCompatible);

        ProfileCompatibility changed = profile.CheckCompatibility(profile.Device, ["a", "b", "c"]);
        Assert.False(changed.IsCompatible);
        Assert.Contains("feature set changed", changed.Summary, StringComparison.Ordinal);
    }

    [Fact]
    public void DetectorSettingsRoundTripThroughOptions()
    {
        var options = new DetectorOptions { WindowMs = 120, MinRiseDb = 7, RefractoryMs = 250 };
        DetectorOptions restored = DetectorSettings.From(options).ToOptions();

        Assert.Equal(120, restored.WindowMs);
        Assert.Equal(7, restored.MinRiseDb);
        Assert.Equal(250, restored.RefractoryMs);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }
}
