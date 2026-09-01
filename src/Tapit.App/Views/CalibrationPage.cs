using System.Drawing;
using System.Windows.Forms;
using Tapit.App.Services;
using Tapit.Core;
using Tapit.Core.Calibration;
using Tapit.Core.Classification;
using Tapit.Core.Detection;

namespace Tapit.App.Views;

/// <summary>
/// Guided four-zone calibration.
/// </summary>
/// <remarks>
/// Actions are suppressed for the whole session - a tap collected as a training sample must
/// not also skip a track - and the session is only armed while it is actually collecting, so
/// a cough between prompts cannot become training data.
/// </remarks>
internal sealed class CalibrationPage : PageBase
{
    private readonly DeskPanel _desk;
    private readonly Label _instruction;
    private readonly Label _progress;
    private readonly Label _feedback;
    private readonly Button _start;
    private readonly Button _pause;
    private readonly Button _undo;
    private readonly Button _retry;
    private readonly Button _cancel;
    private readonly TextBox _report;

    private CalibrationSession? _session;

    public CalibrationPage(AppServices services)
        : base(services, "Calibration")
    {
        _desk = new DeskPanel { Dock = DockStyle.Fill };

        _instruction = new Label
        {
            Dock = DockStyle.Top,
            Height = 30,
            Font = Theme.Heading,
            ForeColor = Theme.Text,
            TextAlign = ContentAlignment.MiddleCenter,
            Text = "Ready to calibrate",
        };

        _progress = new Label
        {
            Dock = DockStyle.Top,
            Height = 24,
            Font = Theme.Mono,
            ForeColor = Theme.Accent,
            TextAlign = ContentAlignment.MiddleCenter,
        };

        _feedback = new Label
        {
            Dock = DockStyle.Bottom,
            Height = 26,
            Font = Theme.Mono,
            ForeColor = Theme.TextMuted,
            TextAlign = ContentAlignment.MiddleCenter,
        };

        _report = new TextBox
        {
            Dock = DockStyle.Bottom,
            Height = 150,
            Multiline = true,
            ReadOnly = true,
            ScrollBars = ScrollBars.Vertical,
            Font = Theme.Mono,
            BackColor = Theme.Surface,
            ForeColor = Theme.Text,
            BorderStyle = BorderStyle.FixedSingle,
            Visible = false,
        };

        _start = Theme.MakeButton("Start", 100);
        _pause = Theme.MakeButton("Pause", 100);
        _undo = Theme.MakeButton("Undo", 100);
        _retry = Theme.MakeButton("Retry zone", 110);
        _cancel = Theme.MakeButton("Cancel", 100);

        var buttons = new FlowLayoutPanel
        {
            Dock = DockStyle.Bottom,
            Height = 42,
            FlowDirection = FlowDirection.LeftToRight,
            BackColor = Theme.Background,
            Padding = new Padding(0, 6, 0, 0),
        };

        buttons.Controls.AddRange([_start, _pause, _undo, _retry, _cancel]);

        _start.Click += (_, _) => StartSession();
        _pause.Click += (_, _) => TogglePause();
        _undo.Click += (_, _) => { _session?.Undo(); Refresh2(); };
        _retry.Click += (_, _) => { _session?.RetryZone(); Refresh2(); };
        _cancel.Click += (_, _) => CancelSession();

        var host = new Panel { Dock = DockStyle.Fill, Padding = new Padding(0, 44, 0, 0) };
        host.Controls.Add(_desk);
        host.Controls.Add(_progress);
        host.Controls.Add(_instruction);

        Controls.Add(host);
        Controls.Add(_feedback);
        Controls.Add(buttons);
        Controls.Add(_report);
        Controls.Add(Title);

        Services.TapProcessed += OnTap;
        UpdateButtons();
    }

    public override void OnDeactivated() => CancelSession();

    private void StartSession()
    {
        if (!Services.IsRunning)
        {
            Services.Start();
        }

        if (!Services.IsRunning)
        {
            _feedback.ForeColor = Theme.Bad;
            _feedback.Text = Services.LastError ?? "Microphone unavailable.";
            return;
        }

        _session = new CalibrationSession();
        _session.Start();

        // A tap that is training data must not also fire an action.
        Services.SuppressActions = true;

        _report.Visible = false;
        _feedback.Text = string.Empty;
        Refresh2();
    }

    private void TogglePause()
    {
        if (_session is null)
        {
            return;
        }

        if (_session.State == CalibrationState.Paused)
        {
            _session.Resume();
        }
        else
        {
            _session.Pause();
        }

        Refresh2();
    }

    private void CancelSession()
    {
        _session?.Cancel();
        _session = null;
        Services.SuppressActions = false;
        _desk.PromptZone = null;
        Refresh2();
    }

    private void OnTap(object? sender, TapResult result) => OnUi(() =>
    {
        if (_session is null || !_session.IsArmed)
        {
            return;
        }

        CalibrationFeedback feedback = _session.Offer(result.Event, result.Features);

        _feedback.ForeColor = feedback.Counted ? Theme.Good : Theme.Warn;
        _feedback.Text = feedback.Counted
            ? $"{Zones.DisplayName(feedback.Zone)}  {feedback.Accepted} / {feedback.Required}   {feedback.Message}"
            : $"Not counted - {feedback.Message}{Measured(result.Event)}";

        if (result.Event.Accepted)
        {
            _desk.Flash(feedback.Zone, feedback.Counted);
        }

        if (_session.State == CalibrationState.Complete)
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

        CalibrationReport report = _session.BuildReport();

        Services.Profile.SetSamples(_session.Samples, Core.Features.TapFeatureExtractor.Names);
        Services.Profile.Negatives = [.. _session.Negatives];
        Services.Profile.ClassifierName = report.BestClassifier;
        Services.SaveProfile();
        Services.ReloadModel();

        _report.Visible = true;
        _report.Text = string.Join(Environment.NewLine,
            $"Calibration complete - {report.SampleCount} samples, {report.SamplesPerZone} per zone.",
            string.Empty,
            $"Best classifier      {report.BestClassifier}",
            $"Leave-one-out agreement  {report.Agreement:P1}",
            string.Empty,
            "Classifier comparison (leave-one-out, calibration data only):",
            string.Join(Environment.NewLine,
                report.Comparison.Select(c => $"   {c.Name,-22} {c.Agreement,7:P1}")),
            string.Empty,
            report.Matrix.Render(),
            report.Advice,
            string.Empty,
            "Leave-one-out agreement is a calibration diagnostic. It shows the samples are",
            "self-consistent. It is NOT accuracy, and it does not prove the zones are",
            "physically separable. Run Evaluation to measure that.");

        _session = null;
    }

    /// <summary>
    /// The measurement that actually failed, and the limit it failed against.
    /// </summary>
    /// <remarks>
    /// A bare reason ("sustained sound") tells the user nothing they can act on. The number
    /// does: it says immediately whether the tap was borderline or nowhere near, and whether
    /// the fault is the tap or the threshold.
    /// </remarks>
    private string Measured(TapEvent tapEvent)
    {
        DetectorOptions options = Services.Engine?.Detector?.Options ?? new DetectorOptions();
        TapMeasurements m = tapEvent.Measurements;

        return tapEvent.Rejection switch
        {
            RejectionReason.SustainedSound =>
                $"   (duration {m.EffectiveDurationMs:0} ms, limit {options.MaxEffectiveDurationMs:0} ms)",
            RejectionReason.SlowAttack =>
                $"   (attack {m.AttackMs:0.0} ms, limit {options.MaxAttackMs:0.0} ms)",
            RejectionReason.SignalTooWeak =>
                $"   (peak {m.PeakDbfs:0.0} dBFS, needs {options.MinPeakDbfs:0.0})",
            RejectionReason.LowSignalToNoise =>
                $"   (SNR {tapEvent.SnrDb:0.0} dB, needs {options.MinSnrDb:0.0} dB)",
            RejectionReason.FlatDynamics =>
                $"   (crest {m.CrestDb:0.0} dB, needs {options.MinCrestFactorDb:0.0} dB)",
            RejectionReason.LateEnergy =>
                $"   (early energy {m.EarlyEnergyFraction:0.00}, needs {options.MinEarlyEnergyFraction:0.00})",
            RejectionReason.Clipped =>
                $"   ({m.ClippedSamples} clipped samples - tap more softly)",
            _ => string.Empty,
        };
    }

    private void Refresh2()
    {
        Zone? zone = _session?.CurrentZone;
        _desk.PromptZone = zone;

        if (_session is null)
        {
            _instruction.Text = Services.Profile.IsCalibrated
                ? $"Calibrated - {Services.Profile.Samples.Count} samples"
                : "Ready to calibrate";
            _progress.Text = string.Empty;
            _desk.SetStatus(string.Empty, Theme.TextMuted);
        }
        else if (_session.State == CalibrationState.Paused)
        {
            _instruction.Text = "Paused";
            _desk.SetStatus("Paused", Theme.Warn);
        }
        else if (zone is Zone current)
        {
            _instruction.Text = $"Tap {Zones.DisplayName(current)}";
            _progress.Text = $"{_session.AcceptedFor(current)} / {_session.SamplesPerZone}" +
                             $"        overall {_session.TotalAccepted} / {_session.TotalRequired}";
            _desk.SetStatus("Tap this area naturally", Theme.Accent);
        }

        UpdateButtons();
        _desk.Invalidate();
    }

    private void UpdateButtons()
    {
        bool active = _session is not null;

        _start.Enabled = !active;
        _pause.Enabled = active;
        _undo.Enabled = active && _session!.TotalAccepted > 0;
        _retry.Enabled = active;
        _cancel.Enabled = active;

        _pause.Text = _session?.State == CalibrationState.Paused ? "Resume" : "Pause";
    }
}
