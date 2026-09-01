using System.Text;
using Tapit.Core.Classification;

namespace Tapit.Core.Evaluation;

/// <summary>Counts of predicted zone against true zone, plus rejections.</summary>
public sealed class ConfusionMatrix
{
    private readonly int[,] _counts = new int[Zones.Count, Zones.Count];
    private readonly int[] _rejected = new int[Zones.Count];

    /// <summary>Records a prediction. <paramref name="predicted"/> null means the event was rejected.</summary>
    public void Add(Zone actual, Zone? predicted)
    {
        if (predicted is null)
        {
            _rejected[Zones.IndexOf(actual)]++;
            return;
        }

        _counts[Zones.IndexOf(actual), Zones.IndexOf(predicted.Value)]++;
    }

    public int this[Zone actual, Zone predicted] => _counts[Zones.IndexOf(actual), Zones.IndexOf(predicted)];

    public int RejectedFor(Zone actual) => _rejected[Zones.IndexOf(actual)];

    public int TotalRejected => _rejected.Sum();

    /// <summary>Events that produced a prediction, right or wrong.</summary>
    public int TotalClassified
    {
        get
        {
            int total = 0;
            for (int a = 0; a < Zones.Count; a++)
            {
                for (int p = 0; p < Zones.Count; p++)
                {
                    total += _counts[a, p];
                }
            }

            return total;
        }
    }

    public int TotalEvents => TotalClassified + TotalRejected;

    public int CorrectCount
    {
        get
        {
            int correct = 0;
            for (int i = 0; i < Zones.Count; i++)
            {
                correct += _counts[i, i];
            }

            return correct;
        }
    }

    /// <summary>
    /// Correct predictions over events that produced a prediction. Rejections are excluded,
    /// because a rejection is not a wrong answer - it is a refusal to answer.
    /// </summary>
    public double AccuracyOfClassified => TotalClassified > 0 ? (double)CorrectCount / TotalClassified : 0.0;

    /// <summary>
    /// Correct predictions over <i>all</i> events, counting rejections against the score.
    /// This is the number that reflects what the user actually experiences.
    /// </summary>
    public double AccuracyOfAll => TotalEvents > 0 ? (double)CorrectCount / TotalEvents : 0.0;

    public double AccuracyFor(Zone zone)
    {
        int index = Zones.IndexOf(zone);
        int total = 0;
        for (int p = 0; p < Zones.Count; p++)
        {
            total += _counts[index, p];
        }

        return total > 0 ? (double)_counts[index, index] / total : 0.0;
    }

    public int SamplesFor(Zone zone)
    {
        int index = Zones.IndexOf(zone);
        int total = _rejected[index];
        for (int p = 0; p < Zones.Count; p++)
        {
            total += _counts[index, p];
        }

        return total;
    }

    /// <summary>Zones performing below <paramref name="threshold"/>, worst first.</summary>
    public IReadOnlyList<Zone> WeakZones(double threshold = 0.7) =>
        Zones.All
            .Where(z => SamplesFor(z) > 0 && AccuracyFor(z) < threshold)
            .OrderBy(AccuracyFor)
            .ToList();

    public string Render()
    {
        var sb = new StringBuilder();
        sb.AppendLine("                      predicted");
        sb.Append("  actual        ");
        foreach (Zone zone in Zones.All)
        {
            sb.Append($"{Abbreviate(zone),6}");
        }

        sb.AppendLine($"{"rej",6}{"acc",8}");

        foreach (Zone actual in Zones.All)
        {
            sb.Append($"  {Zones.DisplayName(actual),-14}");
            foreach (Zone predicted in Zones.All)
            {
                sb.Append($"{this[actual, predicted],6}");
            }

            sb.AppendLine($"{RejectedFor(actual),6}{AccuracyFor(actual),7:P0}");
        }

        return sb.ToString();
    }

    private static string Abbreviate(Zone zone) => zone switch
    {
        Zone.LeftRear => "LR",
        Zone.LeftFront => "LF",
        Zone.RightRear => "RR",
        Zone.RightFront => "RF",
        _ => "?",
    };
}
