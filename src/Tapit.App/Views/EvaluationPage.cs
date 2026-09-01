using System.Drawing;
using System.Windows.Forms;
using Tapit.App.Services;
using Tapit.Core;
using Tapit.Core.Classification;
using Tapit.Core.Evaluation;
using Tapit.Core.Profiles;

namespace Tapit.App.Views;

/// <summary>
/// Held-out evaluation: the only screen that reports accuracy.
/// </summary>
/// <remarks>
/// The trials collected here are never fed back into training, threshold fitting, or feature
/// selection. A model scored on the data that shaped it reports its own memory.
/// </remarks>
internal sealed class EvaluationPage : PageBase
{
    private readonly DeskPanel _desk;
    private readonly Label _instruction;
    private readonly Label _progress;
    private readonly TextBox _report;
    private readonly Button _start;
    private readonly Button _skip;
    private readonly Button _cancel;
    private readonly Button _export;

    private EvaluationSession? _session;
    private EvaluationReport? _lastReport;

    public EvaluationPage(AppServices services)
        : base(services, "Evaluation")
    {
        _desk = new DeskPanel { Dock = DockStyle.Fill };

        _instruction = new Label
        {
            Dock = DockStyle.Top,
            Height = 30,
            Font = Theme.Heading,
            ForeColor = Theme.Text,
            TextAlign = ContentAlignment.MiddleCenter,
            Text = "Held-out evaluation",
        };

        _progress = new Label
        {
            Dock = DockStyle.Top,
            Height = 24,
            Font = Theme.Mono,
            ForeColor = Theme.Accent,
            TextAlign = ContentAlignment.MiddleCenter,
        };

        _report = new TextBox
        {
            Dock = DockStyle.Bottom,
            Height = 240,
            Multiline = true,
            ReadOnly = true,
            ScrollBars = ScrollBars.Vertical,
            Font = Theme.Mono,
            BackColor = Theme.Surface,
            ForeColor = Theme.Text,
            BorderStyle = BorderStyle.FixedSingle,
        };

        _start = Theme.MakeButton("Start", 100);
        _skip = Theme.MakeButton("Skip", 100);
        _cancel = Theme.MakeButton("Cancel", 100);
        _export = Theme.MakeButton("Export CSV", 110);

        var buttons = new FlowLayoutPanel
        {
            Dock = DockStyle.Bottom,
            Height = 42,
            BackColor = Theme.Background,
            Padding = new Padding(0, 6, 0, 0),
        };

        buttons.Controls.AddRange([_start, _skip, _cancel, _export]);

        _start.Click += (_, _) => StartSession();
        _skip.Click += (_, _) => { _session?.Skip(); Refresh2(); };
        _cancel.Click += (_, _) => CancelSession();
        _export.Click += (_, _) => Export();

        var host = new Panel { Dock = DockStyle.Fill, Padding = new Padding(0, 44, 0, 0) };
        host.Controls.Add(_desk);
        host.Controls.Add(_progress);
        host.Controls.Add(_instruction);

        Controls.Add(host);
        Controls.Add(buttons);
        Controls.Add(_report);
        Controls.Add(Title);

        Services.TapProcessed += OnTap;
        ShowHistory();
        UpdateButtons();
    }

    public override void OnDeactivated() => CancelSession();

    private void StartSession()
    {
        if (Services.Model is null)
        {
            _report.Text = "This profile has no calibration yet. Calibrate before evaluating.";
            return;
        }

        if (!Services.IsRunning)
        {
            Services.Start();
        }

        _session = new EvaluationSession();

        // Evaluation measures the classifier, not the desk's media controls.
        Services.SuppressActions = true;

        _lastReport = null;
        _report.Text = "Tap the highlighted zone. Sixty taps, fifteen per zone, in rotation.";
        Refresh2();
    }

    private void CancelSession()
    {
        _session = null;
        Services.SuppressActions = false;
        _desk.PromptZone = null;
        Refresh2();
    }

    private void OnTap(object? sender, TapResult result) => OnUi(() =>
    {
        if (_session is null || _session.IsComplete)
        {
            return;
        }

        // Detector-level rejections still count as trials: refusing to answer is part of
        // the behaviour being measured, and hiding it would flatter the result.
        _session.Record(result.Event, result.Decision, result.LatencyMs);

        if (result.Decision.Zone is Zone predicted)
        {
            _desk.Flash(predicted, result.Accepted);
        }

        if (_session.IsComplete)
        {
            Complete();
        }

        Refresh2();
    });

    private void Complete()
    {
        if (_session is null)
        {
            return;
        }

        Services.SuppressActions = false;

        _lastReport = _session.BuildReport(
            Services.Profile.ClassifierName, Services.Engine?.Source.DeviceName);

        Services.Store.SaveEvaluation(Services.Profile, _lastReport);

        _report.Text = _lastReport.Render();
        _session = null;
        _desk.PromptZone = null;
    }

    private void Export()
    {
        if (_lastReport is null)
        {
            return;
        }

        using var dialog = new SaveFileDialog
        {
            Filter = "CSV files|*.csv",
            FileName = $"tapit-evaluation-{_lastReport.RunAt:yyyyMMdd-HHmmss}.csv",
        };

        if (dialog.ShowDialog() == DialogResult.OK)
        {
            File.WriteAllText(dialog.FileName, _lastReport.ToCsv());
        }
    }

    private void ShowHistory()
    {
        List<EvaluationSummary> history = Services.Profile.EvaluationHistory
            .OrderByDescending(h => h.RunAt)
            .Take(10)
            .ToList();

        if (history.Count == 0)
        {
            _report.Text = string.Join(Environment.NewLine,
                "No evaluation has been run for this profile.",
                string.Empty,
                "An evaluation is 60 taps - 15 per zone - collected separately from calibration",
                "and never used to train anything. It is the only number that says how well",
                "Tapit actually works on this desk.",
                string.Empty,
                "Targets: at least 80% accuracy and under 200 ms median latency. Those are",
                "engineering targets, not guarantees.");

            return;
        }

        _report.Text = "Previous evaluations" + Environment.NewLine + Environment.NewLine +
                       string.Join(Environment.NewLine, history.Select(h =>
                           $"  {h.RunAt:yyyy-MM-dd HH:mm}   {h.Classifier,-20} " +
                           $"{h.Accuracy,7:P1}   {h.Correct}/{h.Events} correct, {h.Rejected} rejected   " +
                           $"{h.MedianLatencyMs:0} ms median"));
    }

    private void Refresh2()
    {
        if (_session is null)
        {
            _instruction.Text = "Held-out evaluation";
            _progress.Text = string.Empty;
            _desk.PromptZone = null;
            _desk.SetStatus(string.Empty, Theme.TextMuted);
        }
        else if (_session.CurrentPrompt is Zone prompt)
        {
            _desk.PromptZone = prompt;
            _instruction.Text = $"Tap {Zones.DisplayName(prompt)}";

            int correct = _session.Trials.Count(t => t.Correct);
            _progress.Text = $"{_session.Completed} / {_session.TotalTrials}        {correct} correct so far";
            _desk.SetStatus("Tap this area naturally", Theme.Accent);
        }

        UpdateButtons();
        _desk.Invalidate();
    }

    private void UpdateButtons()
    {
        bool active = _session is not null;

        _start.Enabled = !active && Services.Model is not null;
        _skip.Enabled = active;
        _cancel.Enabled = active;
        _export.Enabled = _lastReport is not null;
    }
}
