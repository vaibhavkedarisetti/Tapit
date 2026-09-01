using Tapit.Core.Classification;
using Tapit.Core.Detection;

namespace Tapit.Core.Evaluation;

/// <summary>One held-out trial: what was asked for, what the system said, how long it took.</summary>
public sealed record EvaluationTrial(
    Zone Prompted,
    Zone? Predicted,
    bool Rejected,
    string? RejectionReason,
    double Confidence,
    double LatencyMs)
{
    public bool Correct => !Rejected && Predicted == Prompted;
}

/// <summary>
/// A held-out evaluation run.
/// </summary>
/// <remarks>
/// <para>
/// The data collected here is <b>never</b> used to train the model, fit thresholds, or select
/// features. That separation is the entire point: a model scored on the data that shaped it
/// reports its own memory, not its accuracy.
/// </para>
/// <para>
/// Fifteen taps per zone, sixty total, prompted in a fixed rotation so no zone benefits from
/// being tapped while the user is warmed up.
/// </para>
/// </remarks>
public sealed class EvaluationSession(int trialsPerZone = 15)
{
    private readonly List<EvaluationTrial> _trials = [];
    private readonly List<Zone> _prompts = BuildPrompts(trialsPerZone);

    private int _index;

    public int TrialsPerZone { get; } = trialsPerZone > 0
        ? trialsPerZone
        : throw new ArgumentOutOfRangeException(nameof(trialsPerZone));

    public int TotalTrials => _prompts.Count;

    public int Completed => _trials.Count;

    public bool IsComplete => _index >= _prompts.Count;

    public Zone? CurrentPrompt => _index < _prompts.Count ? _prompts[_index] : null;

    public IReadOnlyList<EvaluationTrial> Trials => _trials;

    public double Progress => TotalTrials > 0 ? (double)Completed / TotalTrials : 0.0;

    private static List<Zone> BuildPrompts(int perZone)
    {
        // Round-robin rather than blocked: a zone tapped only at the end of a long session
        // would be measured under different conditions from one tapped at the start.
        var prompts = new List<Zone>(perZone * Zones.Count);
        for (int round = 0; round < perZone; round++)
        {
            foreach (Zone zone in Zones.All)
            {
                prompts.Add(zone);
            }
        }

        return prompts;
    }

    /// <summary>Records the system's response to the current prompt and advances.</summary>
    public EvaluationTrial Record(TapEvent tapEvent, ZoneDecision decision, double latencyMs)
    {
        ArgumentNullException.ThrowIfNull(tapEvent);
        ArgumentNullException.ThrowIfNull(decision);

        if (CurrentPrompt is not Zone prompt)
        {
            throw new InvalidOperationException("Evaluation is already complete.");
        }

        string? reason = !tapEvent.Accepted
            ? RejectionReasonText.Describe(tapEvent.Rejection)
            : decision.Accepted ? null : ZoneRejectionText.Describe(decision.Rejection);

        var trial = new EvaluationTrial(
            prompt,
            decision.Zone,
            !decision.Accepted,
            reason,
            decision.Confidence,
            latencyMs);

        _trials.Add(trial);
        _index++;

        return trial;
    }

    /// <summary>Skips the current prompt, for a tap the user wants to discard.</summary>
    public void Skip()
    {
        if (_index < _prompts.Count)
        {
            _index++;
        }
    }

    public EvaluationReport BuildReport(string classifierName, string? deviceName = null)
    {
        var matrix = new ConfusionMatrix();
        foreach (EvaluationTrial trial in _trials)
        {
            matrix.Add(trial.Prompted, trial.Rejected ? null : trial.Predicted);
        }

        double[] latencies = _trials.Where(t => double.IsFinite(t.LatencyMs))
            .Select(t => t.LatencyMs)
            .OrderBy(v => v)
            .ToArray();

        return new EvaluationReport(
            DateTimeOffset.Now,
            classifierName,
            deviceName,
            matrix,
            Percentile(latencies, 0.5),
            Percentile(latencies, 0.95),
            _trials.Count == 0 ? 0 : _trials.Average(t => t.Confidence),
            [.. _trials]);
    }

    internal static double Percentile(double[] sorted, double fraction)
    {
        if (sorted.Length == 0)
        {
            return double.NaN;
        }

        int index = (int)Math.Ceiling(fraction * sorted.Length) - 1;
        return sorted[Math.Clamp(index, 0, sorted.Length - 1)];
    }
}

/// <summary>Results of a held-out evaluation.</summary>
public sealed record EvaluationReport(
    DateTimeOffset RunAt,
    string Classifier,
    string? Device,
    ConfusionMatrix Matrix,
    double MedianLatencyMs,
    double P95LatencyMs,
    double MeanConfidence,
    IReadOnlyList<EvaluationTrial> Trials)
{
    /// <summary>Engineering target, not a guarantee.</summary>
    public const double TargetAccuracy = 0.80;

    /// <summary>Engineering target, not a guarantee.</summary>
    public const double TargetMedianLatencyMs = 200.0;

    public double OverallAccuracy => Matrix.AccuracyOfAll;

    public double AccuracyOfClassified => Matrix.AccuracyOfClassified;

    public int RejectedCount => Matrix.TotalRejected;

    public bool MeetsAccuracyTarget => OverallAccuracy >= TargetAccuracy;

    public bool MeetsLatencyTarget => double.IsFinite(MedianLatencyMs) && MedianLatencyMs < TargetMedianLatencyMs;

    public string Render()
    {
        var sb = new System.Text.StringBuilder();

        sb.AppendLine($"  Evaluation - {RunAt:yyyy-MM-dd HH:mm}");
        sb.AppendLine($"  classifier      {Classifier}");
        if (Device is not null)
        {
            sb.AppendLine($"  device          {Device}");
        }

        sb.AppendLine();
        sb.AppendLine($"  events          {Matrix.TotalEvents}");
        sb.AppendLine($"  correct         {Matrix.CorrectCount}");
        sb.AppendLine($"  rejected        {RejectedCount}");
        sb.AppendLine($"  accuracy (all)  {OverallAccuracy:P1}   target {TargetAccuracy:P0} - {(MeetsAccuracyTarget ? "met" : "NOT met")}");
        sb.AppendLine($"  accuracy (answered) {AccuracyOfClassified:P1}");
        sb.AppendLine($"  confidence      {MeanConfidence:P0} mean");
        sb.AppendLine($"  latency         {MedianLatencyMs:0} ms median, {P95LatencyMs:0} ms p95   " +
                      $"target <{TargetMedianLatencyMs:0} ms - {(MeetsLatencyTarget ? "met" : "NOT met")}");
        sb.AppendLine();
        sb.Append(Matrix.Render());
        sb.AppendLine();
        sb.AppendLine("  These are engineering targets measured on one desk, in one room, with one");
        sb.AppendLine("  laptop position. They do not transfer to a different physical setup.");

        return sb.ToString();
    }

    /// <summary>CSV of individual trials, for analysis outside the app.</summary>
    public string ToCsv()
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine("trial,prompted,predicted,rejected,reason,confidence,latencyMs,correct");

        for (int i = 0; i < Trials.Count; i++)
        {
            EvaluationTrial t = Trials[i];
            sb.AppendLine(string.Join(",",
                (i + 1).ToString(System.Globalization.CultureInfo.InvariantCulture),
                t.Prompted,
                t.Predicted?.ToString() ?? string.Empty,
                t.Rejected ? "1" : "0",
                t.RejectionReason?.Replace(',', ';') ?? string.Empty,
                t.Confidence.ToString("0.0000", System.Globalization.CultureInfo.InvariantCulture),
                t.LatencyMs.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture),
                t.Correct ? "1" : "0"));
        }

        return sb.ToString();
    }
}
