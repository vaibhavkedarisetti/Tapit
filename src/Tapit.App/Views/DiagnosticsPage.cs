using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;
using Tapit.App.Services;
using Tapit.Core;
using Tapit.Core.Audio;
using Tapit.Core.Classification;
using Tapit.Core.DSP;

namespace Tapit.App.Views;

/// <summary>
/// The instrument panel: what the microphone is doing and what the detector made of it.
/// </summary>
/// <remarks>
/// Exists to answer the questions that come up when a tap is not detected - is there signal,
/// is it clipping, is the floor too high, did the detector see it and reject it, and if so
/// on which gate. Every number here is measured, none are decorative.
/// </remarks>
internal sealed class DiagnosticsPage : PageBase
{
    private readonly Label _text;
    private readonly WaveformPanel _waveform;
    private readonly SpectrumPanel _spectrum;
    private readonly System.Windows.Forms.Timer _timer;

    private TapResult? _lastResult;

    public DiagnosticsPage(AppServices services)
        : base(services, "Diagnostics")
    {
        _text = new Label
        {
            Dock = DockStyle.Left,
            Width = 430,
            Font = Theme.Mono,
            ForeColor = Theme.Text,
            Padding = new Padding(0, 44, 0, 0),
        };

        _waveform = new WaveformPanel { Dock = DockStyle.Top, Height = 170 };
        _spectrum = new SpectrumPanel { Dock = DockStyle.Fill };

        var plots = new Panel { Dock = DockStyle.Fill, Padding = new Padding(16, 44, 0, 0) };
        plots.Controls.Add(_spectrum);
        plots.Controls.Add(_waveform);

        Controls.Add(plots);
        Controls.Add(_text);
        Controls.Add(Title);

        Services.TapProcessed += (_, result) => OnUi(() =>
        {
            _lastResult = result;
            _waveform.Show(result);
            _spectrum.Show(result);
        });

        _timer = new System.Windows.Forms.Timer { Interval = 200 };
        _timer.Tick += (_, _) => UpdateText();
    }

    public override void OnActivated()
    {
        UpdateText();
        _timer.Start();
    }

    public override void OnDeactivated() => _timer.Stop();

    private void UpdateText()
    {
        TapitEngine? engine = Services.Engine;
        IAudioCaptureSource? source = engine?.Source;
        CaptureStatistics stats = source?.GetStatistics() ?? default;

        var lines = new List<string>
        {
            "MICROPHONE",
            $"  Device        {source?.DeviceName ?? "-"}",
            $"  Format        {source?.Format?.ToString() ?? "-"}",
            $"  Processing    {(stats.RawModeActive ? "raw (effects bypassed)" : "PROCESSED - enhancements active")}",
            $"  MMCSS         {(stats.MmcssActive ? "Pro Audio" : "not registered")}",
            $"  State         {source?.State.ToString() ?? "stopped"}",
            string.Empty,
            "SIGNAL",
            $"  Noise floor   {Format(engine?.NoiseFloorDbfs)} dBFS",
            $"  Level         {Format(engine?.LevelDbfs)} dBFS",
            $"  Onset at      {Format(engine is null ? null : engine.NoiseFloorDbfs + engine.Detector?.Options.OnsetThresholdDb ?? 0)} dBFS",
            string.Empty,
            "DETECTOR",
            $"  State         {DetectorState(engine)}",
            $"  Window        {engine?.Detector?.WindowMs ?? 0:0.#} ms " +
            $"(pre-roll {engine?.Detector?.Options.PreRollMs ?? 0:0.#} ms)",
            $"  Candidates    {engine?.CandidateCount ?? 0}",
            $"  Accepted      {engine?.AcceptedCount ?? 0}",
            $"  Dropped       {engine?.Detector?.FramesDropped ?? 0} frames",
            string.Empty,
            "CAPTURE",
            $"  Device period {stats.DevicePeriodMs:0.0} ms",
            $"  Engine buffer {stats.EngineBufferMs:0.0} ms",
            $"  Packets       {stats.PacketCount:N0} (max {stats.MaxPacketFrames})",
            $"  Discontinuity {stats.DiscontinuityCount}",
            $"  Ring overruns {stats.OverrunCount}",
            $"  Service pass  {stats.LastServicePassMs:0.000} ms (max {stats.MaxServicePassMs:0.000})",
            string.Empty,
            "LAST EVENT",
        };

        if (_lastResult is { } result)
        {
            lines.AddRange(
            [
                $"  Time          {result.Event.OnsetSeconds:0.000} s",
                $"  Verdict       {(result.Accepted ? "ACCEPTED" : "rejected")} - {result.Explanation}",
                $"  Peak          {result.Event.Measurements.PeakDbfs,7:0.0} dBFS",
                $"  RMS           {result.Event.Measurements.RmsDbfs,7:0.0} dBFS",
                $"  SNR           {result.Event.SnrDb,7:0.0} dB",
                $"  Crest         {result.Event.Measurements.CrestDb,7:0.0} dB",
                $"  Attack        {result.Event.Measurements.AttackMs,7:0.00} ms",
                $"  Decay         {result.Event.Measurements.DecayMs,7:0.0} ms",
                $"  Duration      {result.Event.Measurements.EffectiveDurationMs,7:0.0} ms",
                $"  Early energy  {result.Event.Measurements.EarlyEnergyFraction,7:0.00}",
                $"  Clipped       {result.Event.Measurements.ClippedSamples}",
                string.Empty,
                "  CLASSIFIER",
                $"  Prediction    {(result.Decision.Zone is Zone z ? Zones.DisplayName(z) : "-")}",
                $"  Confidence    {result.Decision.Confidence:P1}",
                $"  Margin        {result.Decision.Margin:0.000}",
                $"  Nearest dist  {result.Decision.NearestDistance:0.000}",
                $"  Latency       {result.LatencyMs:0.0} ms",
            ]);
        }
        else
        {
            lines.Add("  (none yet)");
        }

        _text.Text = string.Join(Environment.NewLine, lines);
    }

    private static string Format(double? value) =>
        value is null || double.IsNaN(value.Value) ? "     - " : $"{value.Value,7:0.0}";

    private static string DetectorState(TapitEngine? engine)
    {
        if (engine is null || !engine.IsRunning)
        {
            return "stopped";
        }

        return engine.IsLearningRoom
            ? $"learning room ({engine.RoomLearnProgress * 100:0}%)"
            : engine.IsPaused ? "paused" : "listening";
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _timer.Dispose();
        }

        base.Dispose(disposing);
    }
}

/// <summary>Draws the last analysis window, with the onset marked.</summary>
internal sealed class WaveformPanel : Panel
{
    private float[] _samples = [];
    private int _preRollSamples;
    private bool _accepted;

    public WaveformPanel()
    {
        DoubleBuffered = true;
        BackColor = Theme.Surface;
    }

    public void Show(TapResult result)
    {
        _samples = result.Event.Window;
        _accepted = result.Accepted;
        _preRollSamples = (int)(result.Event.OnsetSample - result.Event.WindowStartSample);
        Invalidate();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        Graphics g = e.Graphics;

        using (var border = new Pen(Theme.Border))
        {
            g.DrawRectangle(border, 0, 0, Width - 1, Height - 1);
        }

        using (var label = new SolidBrush(Theme.TextFaint))
        {
            g.DrawString("WAVEFORM - analysis window", Theme.ZoneLabel, label, 8, 6);
        }

        if (_samples.Length < 2)
        {
            return;
        }

        float midline = Height / 2f;
        using (var axis = new Pen(Theme.Border))
        {
            g.DrawLine(axis, 0, midline, Width, midline);
        }

        // Peak-normalised so a quiet tap is still legible; the numeric level is in the
        // text panel, this plot is about shape.
        float peak = 0f;
        foreach (float sample in _samples)
        {
            peak = Math.Max(peak, Math.Abs(sample));
        }

        if (peak <= 0f)
        {
            return;
        }

        float scale = (Height / 2f - 22f) / peak;
        g.SmoothingMode = SmoothingMode.AntiAlias;

        using var pen = new Pen(_accepted ? Theme.Accent : Theme.Bad, 1f);
        var points = new List<PointF>(Width);

        for (int x = 0; x < Width; x++)
        {
            int start = (int)((long)x * _samples.Length / Width);
            int end = (int)((long)(x + 1) * _samples.Length / Width);
            end = Math.Min(Math.Max(end, start + 1), _samples.Length);

            float extreme = 0f;
            for (int i = start; i < end; i++)
            {
                if (Math.Abs(_samples[i]) > Math.Abs(extreme))
                {
                    extreme = _samples[i];
                }
            }

            points.Add(new PointF(x, midline - (extreme * scale)));
        }

        g.DrawLines(pen, [.. points]);

        // Onset marker: everything left of this line is pre-roll.
        if (_preRollSamples > 0 && _preRollSamples < _samples.Length)
        {
            float x = (float)_preRollSamples / _samples.Length * Width;
            using var marker = new Pen(Theme.Warn) { DashStyle = DashStyle.Dot };
            g.DrawLine(marker, x, 18, x, Height - 4);

            using var brush = new SolidBrush(Theme.Warn);
            g.DrawString("onset", Theme.ZoneLabel, brush, x + 3, 20);
        }
    }
}

/// <summary>Log-frequency magnitude spectrum of the last analysis window.</summary>
internal sealed class SpectrumPanel : Panel
{
    private float[] _magnitudes = [];
    private int _sampleRate = 48000;
    private int _transformSize = 8192;

    public SpectrumPanel()
    {
        DoubleBuffered = true;
        BackColor = Theme.Surface;
    }

    public void Show(TapResult result)
    {
        float[] window = result.Event.Window;
        if (window.Length < 8)
        {
            _magnitudes = [];
            Invalidate();
            return;
        }

        _sampleRate = result.Event.SampleRate;
        _transformSize = Fft.NextPowerOfTwo(window.Length);

        var windowed = new float[window.Length];
        window.CopyTo(windowed, 0);
        WindowFunctions.Apply(windowed, WindowFunctions.Create(WindowType.Hann, window.Length));

        var real = new float[_transformSize];
        var imaginary = new float[_transformSize];
        var magnitudes = new float[(_transformSize / 2) + 1];

        Fft.MagnitudeSpectrum(windowed, real, imaginary, magnitudes);

        _magnitudes = magnitudes;
        Invalidate();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        Graphics g = e.Graphics;

        using (var border = new Pen(Theme.Border))
        {
            g.DrawRectangle(border, 0, 0, Width - 1, Height - 1);
        }

        using (var label = new SolidBrush(Theme.TextFaint))
        {
            g.DrawString("SPECTRUM - 100 Hz to Nyquist, log frequency", Theme.ZoneLabel, label, 8, 6);
        }

        if (_magnitudes.Length < 4 || Width < 40 || Height < 40)
        {
            return;
        }

        const double lowHz = 100.0;
        double highHz = _sampleRate / 2.0;
        float top = 24f;
        float bottom = Height - 20f;

        double peak = 0.0;
        foreach (float magnitude in _magnitudes)
        {
            peak = Math.Max(peak, magnitude);
        }

        if (peak <= 0)
        {
            return;
        }

        // 70 dB of range below the peak: enough to show band structure without drawing noise.
        const double rangeDb = 70.0;
        using var pen = new Pen(Theme.Accent, 1f);
        var points = new List<PointF>(Width);

        for (int x = 0; x < Width; x++)
        {
            double hz = lowHz * Math.Pow(highHz / lowHz, (double)x / Width);
            int bin = Math.Clamp(Fft.HertzToBin(hz, _transformSize, _sampleRate), 0, _magnitudes.Length - 1);

            double db = 20.0 * Math.Log10(Math.Max(_magnitudes[bin], 1e-12) / peak);
            double normalised = Math.Clamp((db + rangeDb) / rangeDb, 0.0, 1.0);

            points.Add(new PointF(x, (float)(bottom - (normalised * (bottom - top)))));
        }

        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.DrawLines(pen, [.. points]);

        // Decade gridlines.
        using var grid = new Pen(Theme.Border) { DashStyle = DashStyle.Dot };
        using var gridText = new SolidBrush(Theme.TextFaint);

        foreach (double hz in new[] { 250.0, 1000.0, 4000.0, 10000.0 })
        {
            if (hz >= highHz)
            {
                continue;
            }

            float x = (float)(Math.Log(hz / lowHz) / Math.Log(highHz / lowHz) * Width);
            g.DrawLine(grid, x, top, x, bottom);
            g.DrawString(hz >= 1000 ? $"{hz / 1000:0.#}k" : $"{hz:0}", Theme.ZoneLabel, gridText, x + 2, bottom + 2);
        }
    }
}
