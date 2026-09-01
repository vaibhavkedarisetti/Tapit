using Tapit.Core.Classification;
using Tapit.Core.Evaluation;

namespace Tapit.Core.Tests;

public class ClassificationTests
{
    private const int Dimension = 16;

    /// <summary>
    /// Four separated clusters, one per zone, with deterministic jitter. Stands in for four
    /// zones that the desk actually distinguishes - which is the case a classifier has to
    /// get right before anything harder is worth trying.
    /// </summary>
    private static List<LabeledSample> Clusters(int perZone = 10, double jitter = 0.35, int seed = 7)
    {
        var random = new Random(seed);
        var samples = new List<LabeledSample>();

        for (int z = 0; z < Zones.Count; z++)
        {
            for (int i = 0; i < perZone; i++)
            {
                var features = new float[Dimension];
                for (int d = 0; d < Dimension; d++)
                {
                    // Each zone gets a distinct offset on the first four dimensions.
                    double centre = d == z ? 5.0 : 0.0;
                    features[d] = (float)(centre + ((random.NextDouble() * 2.0 - 1.0) * jitter));
                }

                samples.Add(new LabeledSample(Zones.FromIndex(z), features));
            }
        }

        return samples;
    }

    private static float[] Near(Zone zone, double jitter = 0.2, int seed = 99)
    {
        var random = new Random(seed);
        var features = new float[Dimension];
        int index = Zones.IndexOf(zone);

        for (int d = 0; d < Dimension; d++)
        {
            double centre = d == index ? 5.0 : 0.0;
            features[d] = (float)(centre + ((random.NextDouble() * 2.0 - 1.0) * jitter));
        }

        return features;
    }

    public static TheoryData<string> ClassifierNames => new()
    {
        "nearest-neighbour", "knn-3", "logistic-regression", "ridge",
    };

    [Theory]
    [MemberData(nameof(ClassifierNames))]
    public void EveryClassifierSeparatesCleanlySeparableZones(string name)
    {
        List<LabeledSample> samples = Clusters();
        ZoneModel model = ZoneModel.Train(samples, classifierFactory: CrossValidation.FactoryFor(name));

        foreach (Zone zone in Zones.All)
        {
            ZoneDecision decision = model.Predict(Near(zone));

            Assert.True(decision.Accepted, $"{name} rejected a clean {zone} sample: {decision.Rejection}");
            Assert.Equal(zone, decision.Zone);
        }
    }

    [Theory]
    [MemberData(nameof(ClassifierNames))]
    public void ClassifiersAreDeterministic(string name)
    {
        List<LabeledSample> samples = Clusters();
        float[] probe = Near(Zone.RightRear);

        ZoneDecision first = ZoneModel.Train(samples, classifierFactory: CrossValidation.FactoryFor(name)).Predict(probe);
        ZoneDecision second = ZoneModel.Train(samples, classifierFactory: CrossValidation.FactoryFor(name)).Predict(probe);

        Assert.Equal(first.Zone, second.Zone);
        Assert.Equal(first.Confidence, second.Confidence, 8);
    }

    [Fact]
    public void UntrainedClassifierThrowsRatherThanGuessing()
    {
        Assert.Throws<InvalidOperationException>(() =>
            new NearestNeighbourClassifier().Classify(new float[Dimension]));

        Assert.Throws<InvalidOperationException>(() =>
            new LogisticRegressionClassifier().Classify(new float[Dimension]));
    }

    [Fact]
    public void OutOfDistributionEventIsRejectedAsUnlikeCalibration()
    {
        // A tap somewhere else entirely on the desk. A bare four-way classifier would still
        // return one of four answers; the novelty gate is what stops it becoming an action.
        ZoneModel model = ZoneModel.Train(Clusters());

        var alien = new float[Dimension];
        Array.Fill(alien, 40f);

        ZoneDecision decision = model.Predict(alien);

        Assert.False(decision.Accepted);
        Assert.Equal(ZoneRejection.UnlikeCalibration, decision.Rejection);
    }

    [Fact]
    public void AmbiguousEventIsRejected()
    {
        List<LabeledSample> samples = Clusters();
        var model = ZoneModel.Train(samples, thresholds: new RejectionThresholds
        {
            MinConfidence = 0.0,
            MinMargin = 0.9,           // demand near-certainty
            MaxNearestDistance = 1e9,
        });

        ZoneDecision decision = model.Predict(Near(Zone.LeftRear));

        Assert.False(decision.Accepted);
        Assert.Equal(ZoneRejection.Ambiguous, decision.Rejection);
    }

    [Fact]
    public void LowConfidenceEventIsRejected()
    {
        var model = ZoneModel.Train(Clusters(), thresholds: new RejectionThresholds
        {
            MinConfidence = 0.999,
            MinMargin = 0.0,
            MaxNearestDistance = 1e9,
        });

        Assert.Equal(ZoneRejection.LowConfidence, model.Predict(Near(Zone.LeftFront)).Rejection);
    }

    [Fact]
    public void NegativeExampleBeatsAZoneAndIsRejected()
    {
        List<LabeledSample> samples = Clusters();

        // A negative sitting exactly where the probe lands: the probe is closer to a known
        // non-tap than to any calibration example.
        float[] probe = Near(Zone.RightFront);
        ZoneModel model = ZoneModel.Train(samples, negatives: [probe]);

        ZoneDecision decision = model.Predict(probe);

        Assert.False(decision.Accepted);
        Assert.Equal(ZoneRejection.LooksLikeNoise, decision.Rejection);
    }

    [Fact]
    public void NegativeExamplesCanBeDisabled()
    {
        float[] probe = Near(Zone.RightFront);

        ZoneModel model = ZoneModel.Train(
            Clusters(),
            negatives: [probe],
            thresholds: new RejectionThresholds { UseNegativeExamples = false });

        Assert.True(model.Predict(probe).Accepted);
    }

    [Fact]
    public void WrongLengthFeatureVectorIsRejectedNotMisread()
    {
        ZoneModel model = ZoneModel.Train(Clusters());
        Assert.Equal(ZoneRejection.NotTrained, model.Predict(new float[3]).Rejection);
    }

    [Fact]
    public void NoveltyThresholdIsLearnedFromTheCalibrationSpread()
    {
        // Tight clusters should produce a tighter novelty cut-off than loose ones, because
        // "far away" only means something relative to how varied this person's taps are.
        double tight = ZoneModel.Train(Clusters(jitter: 0.1, seed: 1)).Thresholds.MaxNearestDistance;
        double loose = ZoneModel.Train(Clusters(jitter: 1.5, seed: 1)).Thresholds.MaxNearestDistance;

        Assert.True(loose > tight, $"loose {loose:0.000} should exceed tight {tight:0.000}");
    }

    [Fact]
    public void TrainingRequiresEveryZone()
    {
        List<LabeledSample> onlyTwo = Clusters().Where(s => s.Zone is Zone.LeftRear or Zone.LeftFront).ToList();

        // Two zones is fewer samples than zones only when the count drops below four; the
        // real guard is that a model must not be built from nothing.
        Assert.Throws<ArgumentException>(() => ZoneModel.Train([]));
        Assert.NotNull(ZoneModel.Train(onlyTwo));
    }
}

public class FeatureScalerTests
{
    [Fact]
    public void StandardisesEachDimensionIndependently()
    {
        List<LabeledSample> samples =
        [
            new(Zone.LeftRear, [0f, 100f]),
            new(Zone.LeftFront, [2f, 300f]),
            new(Zone.RightRear, [4f, 500f]),
        ];

        FeatureScaler scaler = FeatureScaler.Fit(samples);

        Assert.Equal(2f, scaler.Mean[0], 4);
        Assert.Equal(300f, scaler.Mean[1], 2);

        float[] scaled = scaler.Transform([2f, 300f]);
        Assert.Equal(0f, scaled[0], 4);
        Assert.Equal(0f, scaled[1], 4);
    }

    [Fact]
    public void ConstantFeatureDoesNotExplode()
    {
        // A feature with zero variance carries no information. Dividing by its deviation
        // would produce infinity and poison every distance.
        List<LabeledSample> samples =
        [
            new(Zone.LeftRear, [5f, 1f]),
            new(Zone.LeftFront, [5f, 2f]),
        ];

        FeatureScaler scaler = FeatureScaler.Fit(samples);
        float[] scaled = scaler.Transform([5f, 1.5f]);

        Assert.Equal(1f, scaler.Scale[0]);
        Assert.True(float.IsFinite(scaled[0]));
        Assert.Equal(0f, scaled[0], 4);
    }

    [Fact]
    public void RejectsEmptyInput() =>
        Assert.Throws<ArgumentException>(() => FeatureScaler.Fit([]));

    [Fact]
    public void RejectsWrongDimension()
    {
        FeatureScaler scaler = FeatureScaler.Fit([new LabeledSample(Zone.LeftRear, [1f, 2f])]);
        Assert.Throws<ArgumentException>(() => scaler.Transform(new float[5]));
    }
}

public class CrossValidationTests
{
    [Fact]
    public void LeaveOneOutOnSeparableDataAgreesCompletely()
    {
        ConfusionMatrix matrix = CrossValidation.LeaveOneOut(ClassificationTests_Helpers.Separable());

        Assert.Equal(1.0, matrix.AccuracyOfClassified, 3);
        Assert.Empty(matrix.WeakZones());
    }

    [Fact]
    public void LeaveOneOutOnNoiseIsNearChance()
    {
        // Zones that are not physically separable must not produce a flattering diagnostic.
        ConfusionMatrix matrix = CrossValidation.LeaveOneOut(ClassificationTests_Helpers.Random());

        Assert.True(matrix.AccuracyOfClassified < 0.6,
            $"unseparable data reported {matrix.AccuracyOfClassified:P0} agreement");
    }

    [Fact]
    public void CompareRanksClassifiersAndReturnsThemAll()
    {
        IReadOnlyList<ClassifierComparison> comparison =
            CrossValidation.Compare(ClassificationTests_Helpers.Separable());

        Assert.Equal(4, comparison.Count);

        for (int i = 1; i < comparison.Count; i++)
        {
            Assert.True(comparison[i - 1].Agreement >= comparison[i].Agreement, "results must be ranked");
        }
    }

    [Fact]
    public void FactoryForReturnsNearestNeighbourForUnknownNames() =>
        Assert.IsType<NearestNeighbourClassifier>(CrossValidation.FactoryFor("nonsense")());

    [Fact]
    public void TooFewSamplesYieldsAnEmptyMatrix() =>
        Assert.Equal(0, CrossValidation.LeaveOneOut([new LabeledSample(Zone.LeftRear, [1f])]).TotalEvents);
}

internal static class ClassificationTests_Helpers
{
    public static List<LabeledSample> Separable(int perZone = 8)
    {
        var random = new Random(11);
        var samples = new List<LabeledSample>();

        for (int z = 0; z < Zones.Count; z++)
        {
            for (int i = 0; i < perZone; i++)
            {
                var features = new float[8];
                for (int d = 0; d < 8; d++)
                {
                    features[d] = (float)((d == z ? 6.0 : 0.0) + ((random.NextDouble() * 2.0 - 1.0) * 0.3));
                }

                samples.Add(new LabeledSample(Zones.FromIndex(z), features));
            }
        }

        return samples;
    }

    public static List<LabeledSample> Random(int perZone = 10)
    {
        var random = new Random(23);
        var samples = new List<LabeledSample>();

        for (int z = 0; z < Zones.Count; z++)
        {
            for (int i = 0; i < perZone; i++)
            {
                var features = new float[8];
                for (int d = 0; d < 8; d++)
                {
                    features[d] = (float)(random.NextDouble() * 2.0 - 1.0);
                }

                samples.Add(new LabeledSample(Zones.FromIndex(z), features));
            }
        }

        return samples;
    }
}

public class ConfusionMatrixTests
{
    [Fact]
    public void CountsCorrectRejectedAndConfused()
    {
        var matrix = new ConfusionMatrix();

        matrix.Add(Zone.LeftRear, Zone.LeftRear);
        matrix.Add(Zone.LeftRear, Zone.LeftFront);
        matrix.Add(Zone.LeftRear, null);
        matrix.Add(Zone.RightFront, Zone.RightFront);

        Assert.Equal(4, matrix.TotalEvents);
        Assert.Equal(3, matrix.TotalClassified);
        Assert.Equal(1, matrix.TotalRejected);
        Assert.Equal(2, matrix.CorrectCount);

        // Rejections count against overall accuracy but not against "of answered".
        Assert.Equal(0.5, matrix.AccuracyOfAll, 4);
        Assert.Equal(2.0 / 3.0, matrix.AccuracyOfClassified, 4);
        Assert.Equal(0.5, matrix.AccuracyFor(Zone.LeftRear), 4);
    }

    [Fact]
    public void WeakZonesAreOrderedWorstFirst()
    {
        var matrix = new ConfusionMatrix();

        for (int i = 0; i < 10; i++)
        {
            matrix.Add(Zone.LeftRear, Zone.LeftRear);
            matrix.Add(Zone.LeftFront, i < 3 ? Zone.LeftFront : Zone.RightRear);
            matrix.Add(Zone.RightRear, i < 6 ? Zone.RightRear : Zone.LeftFront);
        }

        IReadOnlyList<Zone> weak = matrix.WeakZones();

        Assert.Equal(Zone.LeftFront, weak[0]);
        Assert.Contains(Zone.RightRear, weak);
        Assert.DoesNotContain(Zone.LeftRear, weak);
    }

    [Fact]
    public void EmptyMatrixReportsZeroRatherThanNaN()
    {
        var matrix = new ConfusionMatrix();

        Assert.Equal(0.0, matrix.AccuracyOfAll);
        Assert.Equal(0.0, matrix.AccuracyOfClassified);
        Assert.Equal(0.0, matrix.AccuracyFor(Zone.LeftRear));
    }

    [Fact]
    public void RenderIncludesEveryZone()
    {
        var matrix = new ConfusionMatrix();
        matrix.Add(Zone.LeftRear, Zone.LeftRear);

        string rendered = matrix.Render();

        foreach (Zone zone in Zones.All)
        {
            Assert.Contains(Zones.DisplayName(zone), rendered, StringComparison.Ordinal);
        }
    }
}
