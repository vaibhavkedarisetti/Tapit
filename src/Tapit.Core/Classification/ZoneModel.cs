using Tapit.Core.Evaluation;

namespace Tapit.Core.Classification;

/// <summary>Why a classified event was not allowed to fire an action.</summary>
public enum ZoneRejection
{
    None,
    LowConfidence,
    Ambiguous,
    UnlikeCalibration,
    LooksLikeNoise,
    NotTrained,
}

public static class ZoneRejectionText
{
    public static string Describe(ZoneRejection rejection) => rejection switch
    {
        ZoneRejection.None => "Accepted",
        ZoneRejection.LowConfidence => "Not confident enough",
        ZoneRejection.Ambiguous => "Ambiguous between two zones",
        ZoneRejection.UnlikeCalibration => "Doesn't match your calibration",
        ZoneRejection.LooksLikeNoise => "Looks like typing or background noise",
        ZoneRejection.NotTrained => "No calibration for this profile",
        _ => rejection.ToString(),
    };
}

/// <summary>Final verdict for one detected tap.</summary>
public sealed record ZoneDecision(
    Zone? Zone,
    double Confidence,
    double Margin,
    double NearestDistance,
    ZoneRejection Rejection)
{
    public bool Accepted => Rejection == ZoneRejection.None && Zone is not null;

    public static ZoneDecision Reject(ZoneRejection reason) =>
        new(null, 0, 0, double.NaN, reason);
}

/// <summary>
/// Thresholds for the rejection stack.
/// </summary>
/// <remarks>
/// Derived from the user's own calibration distribution rather than hard-coded, because
/// "far from any example" only means something relative to how spread out that person's
/// taps are on that desk.
/// </remarks>
public sealed class RejectionThresholds
{
    /// <summary><see cref="double.NaN"/> means "learn this from the calibration data".</summary>
    public double MinConfidence { get; set; } = double.NaN;

    /// <summary><see cref="double.NaN"/> means "learn this from the calibration data".</summary>
    public double MinMargin { get; set; } = double.NaN;

    /// <summary>
    /// Distance beyond which an event is treated as unlike anything calibrated.
    /// Positive infinity means "learn this from the calibration spread".
    /// </summary>
    public double MaxNearestDistance { get; set; } = double.PositiveInfinity;

    internal bool NeedsConfidence => double.IsNaN(MinConfidence);

    internal bool NeedsMargin => double.IsNaN(MinMargin);

    internal bool NeedsNovelty => double.IsPositiveInfinity(MaxNearestDistance);

    public bool UseNegativeExamples { get; set; } = true;

    public RejectionThresholds Clone() => (RejectionThresholds)MemberwiseClone();
}

/// <summary>
/// A trained zone model: scaler, classifier, calibration examples, and rejection thresholds.
/// </summary>
/// <remarks>
/// <para>
/// Classification alone is never enough. A four-way classifier always returns one of four
/// answers, including for a cough, a dropped pen, or a tap on a completely different part of
/// the desk. The rejection stack is what turns a classifier into something safe to bind to
/// an action.
/// </para>
/// <para>
/// The ordering is deliberate: quality first (handled upstream by the detector), then
/// confidence, then ambiguity, then novelty, then the negative model. Every gate must pass.
/// </para>
/// </remarks>
public sealed class ZoneModel
{
    private ZoneModel(
        FeatureScaler scaler,
        IZoneClassifier classifier,
        IReadOnlyList<LabeledSample> scaledSamples,
        IReadOnlyList<float[]> scaledNegatives,
        RejectionThresholds thresholds)
    {
        Scaler = scaler;
        Classifier = classifier;
        ScaledSamples = scaledSamples;
        ScaledNegatives = scaledNegatives;
        Thresholds = thresholds;
    }

    public FeatureScaler Scaler { get; }

    public IZoneClassifier Classifier { get; }

    public IReadOnlyList<LabeledSample> ScaledSamples { get; }

    public IReadOnlyList<float[]> ScaledNegatives { get; }

    public RejectionThresholds Thresholds { get; }

    public string ClassifierName => Classifier.Name;

    /// <summary>
    /// Trains a model from raw (unscaled) calibration samples.
    /// </summary>
    /// <param name="samples">Accepted calibration taps.</param>
    /// <param name="negatives">Optional non-tap examples used to improve rejection.</param>
    /// <param name="classifierFactory">Estimator to use. Defaults to nearest neighbour.</param>
    /// <param name="thresholds">Explicit overrides. Anything not supplied is learned.</param>
    /// <param name="learnThresholds">
    /// Set false to skip threshold learning. Used for the inner folds of the learning pass
    /// itself, which would otherwise recurse.
    /// </param>
    public static ZoneModel Train(
        IReadOnlyList<LabeledSample> samples,
        IReadOnlyList<float[]>? negatives = null,
        Func<IZoneClassifier>? classifierFactory = null,
        RejectionThresholds? thresholds = null,
        bool learnThresholds = true)
    {
        ArgumentNullException.ThrowIfNull(samples);

        if (samples.Count == 0)
        {
            throw new ArgumentException("Cannot train a model with no samples.", nameof(samples));
        }

        classifierFactory ??= static () => new NearestNeighbourClassifier();

        FeatureScaler scaler = FeatureScaler.Fit(samples);
        IReadOnlyList<LabeledSample> scaled = scaler.Transform(samples);

        IZoneClassifier classifier = classifierFactory();
        classifier.Train(scaled);

        List<float[]> scaledNegatives = negatives is null
            ? []
            : negatives.Select(n => scaler.Transform(n.AsSpan())).ToList();

        RejectionThresholds effective = thresholds?.Clone() ?? new RejectionThresholds();

        if (effective.NeedsNovelty)
        {
            effective.MaxNearestDistance = LearnNoveltyDistance(scaled);
        }

        // Each gate is learned independently, so overriding one does not silently disable
        // learning for the rest.
        if (learnThresholds && (effective.NeedsConfidence || effective.NeedsMargin))
        {
            LearnDecisionThresholds(samples, classifierFactory, effective);
        }

        // Anything still unset after learning (too few samples to learn from) falls back to
        // a permissive value: the novelty and quality gates are doing the real work, and a
        // guessed confidence cut-off would reject good taps for no measured reason.
        if (effective.NeedsConfidence)
        {
            effective.MinConfidence = 0.0;
        }

        if (effective.NeedsMargin)
        {
            effective.MinMargin = 0.0;
        }

        return new ZoneModel(scaler, classifier, scaled, scaledNegatives, effective);
    }

    /// <summary>
    /// Sets the confidence and margin gates from how this user's own taps actually score.
    /// </summary>
    /// <remarks>
    /// A fixed threshold cannot work across these estimators: a softmax classifier reports
    /// near 1.0 where a distance-based one reports 0.3 on the very same data, so one shared
    /// constant would be far too lax for one and impossibly strict for the other. Instead
    /// each sample is classified by a model trained without it, and the gate is placed below
    /// the bulk of the correct predictions - low enough to admit a normal tap, high enough
    /// to catch something that scores unlike anything seen during calibration.
    /// </remarks>
    private static void LearnDecisionThresholds(
        IReadOnlyList<LabeledSample> samples,
        Func<IZoneClassifier> classifierFactory,
        RejectionThresholds thresholds)
    {
        var confidences = new List<double>();
        var margins = new List<double>();

        for (int held = 0; held < samples.Count; held++)
        {
            var training = new List<LabeledSample>(samples.Count - 1);
            for (int i = 0; i < samples.Count; i++)
            {
                if (i != held)
                {
                    training.Add(samples[i]);
                }
            }

            if (training.Select(s => s.Zone).Distinct().Count() < Zones.Count)
            {
                continue;
            }

            ZoneModel fold = Train(training, classifierFactory: classifierFactory, learnThresholds: false);
            ClassificationResult result = fold.Classifier.Classify(fold.Scaler.Transform(samples[held].Features));

            // Only correct predictions inform the gate. Learning from mistakes would widen
            // it to admit exactly the events it exists to catch.
            if (result.Zone == samples[held].Zone)
            {
                confidences.Add(result.Confidence);
                margins.Add(result.Margin);
            }
        }

        if (confidences.Count < Zones.Count)
        {
            return;
        }

        // Chance for a four-way decision is 0.25; a gate at or below that is no gate at all.
        const double chance = 1.0 / Zones.Count;

        if (thresholds.NeedsConfidence)
        {
            thresholds.MinConfidence = Math.Max(chance * 1.05, Percentile(confidences, 0.10) * 0.85);
        }

        if (thresholds.NeedsMargin)
        {
            thresholds.MinMargin = Math.Max(0.005, Percentile(margins, 0.10) * 0.5);
        }
    }

    private static double Percentile(List<double> values, double fraction)
    {
        values.Sort();
        int index = Math.Clamp((int)(values.Count * fraction), 0, values.Count - 1);
        return values[index];
    }

    /// <summary>
    /// Picks the novelty cut-off from how far the user's own taps sit from each other.
    /// </summary>
    /// <remarks>
    /// For every calibration sample, the distance to its nearest same-zone neighbour is
    /// measured. The cut-off is a generous multiple of the 90th percentile of those
    /// distances: comfortably outside normal variation for this desk, without rejecting a
    /// slightly harder-than-usual tap.
    /// </remarks>
    private static double LearnNoveltyDistance(IReadOnlyList<LabeledSample> scaled)
    {
        var distances = new List<double>(scaled.Count);

        for (int i = 0; i < scaled.Count; i++)
        {
            double nearest = double.PositiveInfinity;
            for (int j = 0; j < scaled.Count; j++)
            {
                if (i == j || scaled[i].Zone != scaled[j].Zone)
                {
                    continue;
                }

                nearest = Math.Min(nearest, Distance.Euclidean(scaled[i].Features, scaled[j].Features));
            }

            if (double.IsFinite(nearest))
            {
                distances.Add(nearest);
            }
        }

        if (distances.Count == 0)
        {
            return double.PositiveInfinity;
        }

        distances.Sort();
        double percentile90 = distances[Math.Min(distances.Count - 1, (int)(distances.Count * 0.9))];

        return Math.Max(percentile90 * 2.0, 1e-3);
    }

    /// <summary>Classifies raw features and applies every rejection gate.</summary>
    public ZoneDecision Predict(ReadOnlySpan<float> rawFeatures)
    {
        if (rawFeatures.Length != Scaler.Dimension)
        {
            return ZoneDecision.Reject(ZoneRejection.NotTrained);
        }

        float[] scaled = Scaler.Transform(rawFeatures);
        ClassificationResult result = Classifier.Classify(scaled);

        double nearest = double.IsNaN(result.NearestDistance)
            ? NearestCalibrationDistance(scaled)
            : result.NearestDistance;

        // Order matters, and not only for correctness: whichever gate fires is what the user
        // is told. Ask "is this the kind of event I know about" before "which zone is it and
        // how sure am I", because an out-of-distribution tap trips a confidence threshold
        // too - and "doesn't match your calibration" is a far more actionable explanation
        // than "not confident enough".
        if (nearest > Thresholds.MaxNearestDistance)
        {
            return new ZoneDecision(null, result.Confidence, result.Margin, nearest, ZoneRejection.UnlikeCalibration);
        }

        if (Thresholds.UseNegativeExamples && ScaledNegatives.Count > 0)
        {
            double nearestNegative = ScaledNegatives.Min(n => Distance.Euclidean(scaled, n));
            if (nearestNegative < nearest)
            {
                return new ZoneDecision(null, result.Confidence, result.Margin, nearest, ZoneRejection.LooksLikeNoise);
            }
        }

        if (result.Confidence < Thresholds.MinConfidence)
        {
            return new ZoneDecision(null, result.Confidence, result.Margin, nearest, ZoneRejection.LowConfidence);
        }

        if (result.Margin < Thresholds.MinMargin)
        {
            return new ZoneDecision(null, result.Confidence, result.Margin, nearest, ZoneRejection.Ambiguous);
        }

        return new ZoneDecision(result.Zone, result.Confidence, result.Margin, nearest, ZoneRejection.None);
    }

    private double NearestCalibrationDistance(ReadOnlySpan<float> scaled)
    {
        double nearest = double.PositiveInfinity;
        foreach (LabeledSample sample in ScaledSamples)
        {
            nearest = Math.Min(nearest, Distance.Euclidean(scaled, sample.Features));
        }

        return nearest;
    }
}

/// <summary>Leave-one-out cross-validation over calibration samples.</summary>
/// <remarks>
/// <b>This is a calibration diagnostic, not an accuracy measurement.</b> High agreement says
/// the samples are self-consistent - that taps labelled the same look alike. It says nothing
/// about how the system behaves on a tap it has never seen, and it must never be reported to
/// a user as accuracy.
/// </remarks>
public static class CrossValidation
{
    public static ConfusionMatrix LeaveOneOut(
        IReadOnlyList<LabeledSample> samples,
        Func<IZoneClassifier>? classifierFactory = null,
        bool applyRejection = false)
    {
        ArgumentNullException.ThrowIfNull(samples);

        var matrix = new ConfusionMatrix();
        if (samples.Count < 2)
        {
            return matrix;
        }

        classifierFactory ??= static () => new NearestNeighbourClassifier();

        for (int held = 0; held < samples.Count; held++)
        {
            var training = new List<LabeledSample>(samples.Count - 1);
            for (int i = 0; i < samples.Count; i++)
            {
                if (i != held)
                {
                    training.Add(samples[i]);
                }
            }

            // Every zone must still be represented, or the fold is meaningless.
            if (training.Select(s => s.Zone).Distinct().Count() < Zones.Count)
            {
                continue;
            }

            ZoneModel model = ZoneModel.Train(training, classifierFactory: classifierFactory, learnThresholds: false);
            ZoneDecision decision = model.Predict(samples[held].Features);

            matrix.Add(samples[held].Zone, applyRejection ? decision.Zone : NearestPrediction(model, samples[held]));
        }

        return matrix;
    }

    private static Zone NearestPrediction(ZoneModel model, LabeledSample sample)
    {
        float[] scaled = model.Scaler.Transform(sample.Features);
        return model.Classifier.Classify(scaled).Zone;
    }

    /// <summary>
    /// Compares candidate classifiers on the same leave-one-out folds and returns them
    /// ranked. The point is to find the simplest estimator that separates the zones, not to
    /// collect implementations.
    /// </summary>
    public static IReadOnlyList<ClassifierComparison> Compare(IReadOnlyList<LabeledSample> samples)
    {
        (string Name, Func<IZoneClassifier> Factory)[] candidates =
        [
            ("nearest-neighbour", static () => new NearestNeighbourClassifier()),
            ("knn-3", static () => new KnnClassifier(3)),
            ("logistic-regression", static () => new LogisticRegressionClassifier()),
            ("ridge", static () => new RidgeClassifier()),
        ];

        return candidates
            .Select(candidate =>
            {
                ConfusionMatrix matrix = LeaveOneOut(samples, candidate.Factory);
                return new ClassifierComparison(candidate.Name, matrix.AccuracyOfClassified, matrix);
            })
            .OrderByDescending(r => r.Agreement)
            .ToList();
    }

    public static Func<IZoneClassifier> FactoryFor(string name) => name switch
    {
        "knn-3" => static () => new KnnClassifier(3),
        "logistic-regression" => static () => new LogisticRegressionClassifier(),
        "ridge" => static () => new RidgeClassifier(),
        _ => static () => new NearestNeighbourClassifier(),
    };
}

public sealed record ClassifierComparison(string Name, double Agreement, ConfusionMatrix Matrix);
