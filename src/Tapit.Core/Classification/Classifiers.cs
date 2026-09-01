namespace Tapit.Core.Classification;

/// <summary>Outcome of classifying one feature vector. No accept/reject decision here.</summary>
public readonly record struct ClassificationResult(
    Zone Zone,
    double Confidence,
    double Margin,
    double NearestDistance,
    double[] Scores)
{
    /// <summary>Second-best zone, which is what the ambiguity gate cares about.</summary>
    public Zone RunnerUp
    {
        get
        {
            int best = Zones.IndexOf(Zone);
            int runnerUp = -1;

            for (int i = 0; i < Scores.Length; i++)
            {
                if (i != best && (runnerUp < 0 || Scores[i] > Scores[runnerUp]))
                {
                    runnerUp = i;
                }
            }

            return runnerUp >= 0 ? Zones.FromIndex(runnerUp) : Zone;
        }
    }
}

/// <summary>
/// A locally-trained zone classifier.
/// </summary>
/// <remarks>
/// Trained only from the user's own calibration taps. Nothing is pre-trained, downloaded,
/// or shared, and there is no learned representation anywhere - these are all textbook
/// estimators over the hand-computed features.
/// </remarks>
public interface IZoneClassifier
{
    string Name { get; }

    void Train(IReadOnlyList<LabeledSample> scaledSamples);

    ClassificationResult Classify(ReadOnlySpan<float> scaledFeatures);
}

public static class Distance
{
    public static double Euclidean(ReadOnlySpan<float> a, ReadOnlySpan<float> b)
    {
        double sum = 0.0;
        int n = Math.Min(a.Length, b.Length);

        for (int i = 0; i < n; i++)
        {
            double delta = a[i] - b[i];
            sum += delta * delta;
        }

        return Math.Sqrt(sum);
    }
}

/// <summary>
/// Nearest neighbour. The starting classifier, and the reference every other one has to beat.
/// </summary>
/// <remarks>
/// With ten examples per zone this is a genuinely reasonable estimator, not a placeholder:
/// it makes no distributional assumption, needs no fitting, and its distance to the nearest
/// calibration example is exactly the number the novelty gate needs anyway.
/// </remarks>
public sealed class NearestNeighbourClassifier : IZoneClassifier
{
    private LabeledSample[] _samples = [];

    public string Name => "nearest-neighbour";

    public void Train(IReadOnlyList<LabeledSample> scaledSamples)
    {
        ArgumentNullException.ThrowIfNull(scaledSamples);
        _samples = [.. scaledSamples];
    }

    public ClassificationResult Classify(ReadOnlySpan<float> scaledFeatures)
    {
        if (_samples.Length == 0)
        {
            throw new InvalidOperationException("Classifier has not been trained.");
        }

        // Closest example per zone: the gap between the best two is a meaningful margin,
        // whereas the gap between the two closest examples overall often is not.
        var best = new double[Zones.Count];
        Array.Fill(best, double.PositiveInfinity);

        foreach (LabeledSample sample in _samples)
        {
            double distance = Distance.Euclidean(scaledFeatures, sample.Features);
            int index = Zones.IndexOf(sample.Zone);

            if (distance < best[index])
            {
                best[index] = distance;
            }
        }

        int winner = 0;
        for (int i = 1; i < best.Length; i++)
        {
            if (best[i] < best[winner])
            {
                winner = i;
            }
        }

        double nearest = best[winner];
        double runnerUp = double.PositiveInfinity;
        for (int i = 0; i < best.Length; i++)
        {
            if (i != winner && best[i] < runnerUp)
            {
                runnerUp = best[i];
            }
        }

        // Inverse-distance weighting, matching KnnClassifier so the two report comparable
        // confidences. The obvious 1/(1+d) mapping is badly behaved here: as distances grow
        // it compresses every zone toward 0.25, so a clean, well-separated tap can score
        // below a confidence threshold that a genuinely ambiguous one also fails.
        var scores = new double[Zones.Count];
        double total = 0.0;
        for (int i = 0; i < best.Length; i++)
        {
            scores[i] = 1.0 / (1e-6 + best[i]);
            total += scores[i];
        }

        if (total > 0)
        {
            for (int i = 0; i < scores.Length; i++)
            {
                scores[i] /= total;
            }
        }

        double margin = double.IsPositiveInfinity(runnerUp) || runnerUp + nearest <= 0
            ? 1.0
            : (runnerUp - nearest) / (runnerUp + nearest);

        return new ClassificationResult(Zones.FromIndex(winner), scores[winner], margin, nearest, scores);
    }
}

/// <summary>k-nearest neighbour with distance weighting.</summary>
public sealed class KnnClassifier(int k = 3) : IZoneClassifier
{
    private LabeledSample[] _samples = [];

    public int K { get; } = Math.Max(1, k);

    public string Name => $"knn-{K}";

    public void Train(IReadOnlyList<LabeledSample> scaledSamples)
    {
        ArgumentNullException.ThrowIfNull(scaledSamples);
        _samples = [.. scaledSamples];
    }

    public ClassificationResult Classify(ReadOnlySpan<float> scaledFeatures)
    {
        if (_samples.Length == 0)
        {
            throw new InvalidOperationException("Classifier has not been trained.");
        }

        var distances = new (double Distance, Zone Zone)[_samples.Length];
        for (int i = 0; i < _samples.Length; i++)
        {
            distances[i] = (Distance.Euclidean(scaledFeatures, _samples[i].Features), _samples[i].Zone);
        }

        Array.Sort(distances, static (a, b) => a.Distance.CompareTo(b.Distance));

        int neighbours = Math.Min(K, distances.Length);
        var scores = new double[Zones.Count];
        double total = 0.0;

        for (int i = 0; i < neighbours; i++)
        {
            double weight = 1.0 / (1e-6 + distances[i].Distance);
            scores[Zones.IndexOf(distances[i].Zone)] += weight;
            total += weight;
        }

        if (total > 0)
        {
            for (int i = 0; i < scores.Length; i++)
            {
                scores[i] /= total;
            }
        }

        int winner = ArgMax(scores);
        double runnerUp = SecondBest(scores, winner);

        return new ClassificationResult(
            Zones.FromIndex(winner),
            scores[winner],
            scores[winner] - runnerUp,
            distances[0].Distance,
            scores);
    }

    internal static int ArgMax(double[] values)
    {
        int best = 0;
        for (int i = 1; i < values.Length; i++)
        {
            if (values[i] > values[best])
            {
                best = i;
            }
        }

        return best;
    }

    internal static double SecondBest(double[] values, int exclude)
    {
        double best = double.NegativeInfinity;
        for (int i = 0; i < values.Length; i++)
        {
            if (i != exclude && values[i] > best)
            {
                best = values[i];
            }
        }

        return double.IsNegativeInfinity(best) ? 0.0 : best;
    }
}

/// <summary>
/// L2-regularised multinomial logistic regression, fitted by batch gradient descent.
/// </summary>
/// <remarks>
/// Deterministic: fixed iteration count, fixed learning rate, weights initialised to zero,
/// no randomness anywhere. Regularisation matters more than the optimiser here - forty
/// samples in a sixteen-dimensional space is a setting where an unregularised fit will
/// separate the training data perfectly and mean nothing.
/// </remarks>
public sealed class LogisticRegressionClassifier(
    double regularisation = 0.1, double learningRate = 0.5, int iterations = 600) : IZoneClassifier
{
    private double[][] _weights = [];
    private double[] _bias = [];
    private int _dimension;

    public string Name => "logistic-regression";

    public double Regularisation { get; } = regularisation;

    public void Train(IReadOnlyList<LabeledSample> scaledSamples)
    {
        ArgumentNullException.ThrowIfNull(scaledSamples);

        if (scaledSamples.Count == 0)
        {
            throw new ArgumentException("Cannot train on zero samples.", nameof(scaledSamples));
        }

        _dimension = scaledSamples[0].Features.Length;
        _weights = new double[Zones.Count][];
        _bias = new double[Zones.Count];

        for (int c = 0; c < Zones.Count; c++)
        {
            _weights[c] = new double[_dimension];
        }

        int n = scaledSamples.Count;
        var probabilities = new double[Zones.Count];

        for (int iteration = 0; iteration < iterations; iteration++)
        {
            var weightGradient = new double[Zones.Count][];
            var biasGradient = new double[Zones.Count];
            for (int c = 0; c < Zones.Count; c++)
            {
                weightGradient[c] = new double[_dimension];
            }

            foreach (LabeledSample sample in scaledSamples)
            {
                Softmax(sample.Features, probabilities);
                int label = Zones.IndexOf(sample.Zone);

                for (int c = 0; c < Zones.Count; c++)
                {
                    double error = probabilities[c] - (c == label ? 1.0 : 0.0);
                    for (int d = 0; d < _dimension; d++)
                    {
                        weightGradient[c][d] += error * sample.Features[d];
                    }

                    biasGradient[c] += error;
                }
            }

            for (int c = 0; c < Zones.Count; c++)
            {
                for (int d = 0; d < _dimension; d++)
                {
                    double gradient = (weightGradient[c][d] / n) + (Regularisation * _weights[c][d]);
                    _weights[c][d] -= learningRate * gradient;
                }

                _bias[c] -= learningRate * biasGradient[c] / n;
            }
        }
    }

    private void Softmax(ReadOnlySpan<float> features, Span<double> probabilities)
    {
        double max = double.NegativeInfinity;
        for (int c = 0; c < Zones.Count; c++)
        {
            double score = _bias[c];
            for (int d = 0; d < _dimension && d < features.Length; d++)
            {
                score += _weights[c][d] * features[d];
            }

            probabilities[c] = score;
            if (score > max)
            {
                max = score;
            }
        }

        double total = 0.0;
        for (int c = 0; c < Zones.Count; c++)
        {
            probabilities[c] = Math.Exp(probabilities[c] - max);
            total += probabilities[c];
        }

        if (total > 0)
        {
            for (int c = 0; c < Zones.Count; c++)
            {
                probabilities[c] /= total;
            }
        }
    }

    public ClassificationResult Classify(ReadOnlySpan<float> scaledFeatures)
    {
        if (_weights.Length == 0)
        {
            throw new InvalidOperationException("Classifier has not been trained.");
        }

        var scores = new double[Zones.Count];
        Softmax(scaledFeatures, scores);

        int winner = KnnClassifier.ArgMax(scores);
        double runnerUp = KnnClassifier.SecondBest(scores, winner);

        return new ClassificationResult(
            Zones.FromIndex(winner),
            scores[winner],
            scores[winner] - runnerUp,
            double.NaN,
            scores);
    }
}

/// <summary>
/// Regularised linear discriminant, fitted one-vs-rest by ridge regression.
/// </summary>
/// <remarks>
/// Solved in closed form from the normal equations, so there is no optimiser to tune and
/// the result is exactly reproducible. With sixteen features the matrix is trivial to solve.
/// </remarks>
public sealed class RidgeClassifier(double regularisation = 1.0) : IZoneClassifier
{
    private double[][] _weights = [];
    private int _dimension;

    public string Name => "ridge";

    public void Train(IReadOnlyList<LabeledSample> scaledSamples)
    {
        ArgumentNullException.ThrowIfNull(scaledSamples);

        if (scaledSamples.Count == 0)
        {
            throw new ArgumentException("Cannot train on zero samples.", nameof(scaledSamples));
        }

        _dimension = scaledSamples[0].Features.Length;
        int size = _dimension + 1; // + intercept

        // Gram matrix, shared by every one-vs-rest target.
        var gram = new double[size, size];
        var row = new double[size];

        foreach (LabeledSample sample in scaledSamples)
        {
            for (int d = 0; d < _dimension; d++)
            {
                row[d] = sample.Features[d];
            }

            row[_dimension] = 1.0;

            for (int i = 0; i < size; i++)
            {
                for (int j = 0; j < size; j++)
                {
                    gram[i, j] += row[i] * row[j];
                }
            }
        }

        // Regularise everything except the intercept.
        for (int i = 0; i < _dimension; i++)
        {
            gram[i, i] += regularisation;
        }

        _weights = new double[Zones.Count][];

        for (int c = 0; c < Zones.Count; c++)
        {
            var target = new double[size];
            foreach (LabeledSample sample in scaledSamples)
            {
                double y = Zones.IndexOf(sample.Zone) == c ? 1.0 : -1.0;
                for (int d = 0; d < _dimension; d++)
                {
                    target[d] += sample.Features[d] * y;
                }

                target[_dimension] += y;
            }

            _weights[c] = LinearSolver.Solve((double[,])gram.Clone(), target);
        }
    }

    public ClassificationResult Classify(ReadOnlySpan<float> scaledFeatures)
    {
        if (_weights.Length == 0)
        {
            throw new InvalidOperationException("Classifier has not been trained.");
        }

        var raw = new double[Zones.Count];
        for (int c = 0; c < Zones.Count; c++)
        {
            double score = _weights[c][_dimension];
            for (int d = 0; d < _dimension && d < scaledFeatures.Length; d++)
            {
                score += _weights[c][d] * scaledFeatures[d];
            }

            raw[c] = score;
        }

        // Softmax purely to express the decision on a comparable 0..1 scale.
        var scores = new double[Zones.Count];
        double max = raw.Max();
        double total = 0.0;
        for (int c = 0; c < Zones.Count; c++)
        {
            scores[c] = Math.Exp(raw[c] - max);
            total += scores[c];
        }

        for (int c = 0; c < Zones.Count; c++)
        {
            scores[c] /= total;
        }

        int winner = KnnClassifier.ArgMax(scores);
        double runnerUp = KnnClassifier.SecondBest(scores, winner);

        return new ClassificationResult(
            Zones.FromIndex(winner),
            scores[winner],
            scores[winner] - runnerUp,
            double.NaN,
            scores);
    }
}

internal static class LinearSolver
{
    /// <summary>Gaussian elimination with partial pivoting. Destroys <paramref name="a"/>.</summary>
    public static double[] Solve(double[,] a, double[] b)
    {
        int n = b.Length;
        var x = (double[])b.Clone();

        for (int column = 0; column < n; column++)
        {
            int pivot = column;
            for (int row = column + 1; row < n; row++)
            {
                if (Math.Abs(a[row, column]) > Math.Abs(a[pivot, column]))
                {
                    pivot = row;
                }
            }

            if (pivot != column)
            {
                for (int k = 0; k < n; k++)
                {
                    (a[column, k], a[pivot, k]) = (a[pivot, k], a[column, k]);
                }

                (x[column], x[pivot]) = (x[pivot], x[column]);
            }

            double diagonal = a[column, column];
            if (Math.Abs(diagonal) < 1e-12)
            {
                // Singular: the ridge term should prevent this, but never divide by zero.
                continue;
            }

            for (int row = column + 1; row < n; row++)
            {
                double factor = a[row, column] / diagonal;
                if (factor == 0)
                {
                    continue;
                }

                for (int k = column; k < n; k++)
                {
                    a[row, k] -= factor * a[column, k];
                }

                x[row] -= factor * x[column];
            }
        }

        for (int row = n - 1; row >= 0; row--)
        {
            double sum = x[row];
            for (int k = row + 1; k < n; k++)
            {
                sum -= a[row, k] * x[k];
            }

            double diagonal = a[row, row];
            x[row] = Math.Abs(diagonal) < 1e-12 ? 0.0 : sum / diagonal;
        }

        return x;
    }
}
