using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;
using Tapit.Core.Classification;

namespace Tapit.App.Views;

/// <summary>
/// The desk map: laptop, four zones, and what just happened.
/// </summary>
/// <remarks>
/// The layout mirrors the physical arrangement exactly - the laptop at the top, the two
/// front zones nearer the viewer, the rear zones further away - so the picture matches what
/// the user is looking at without them having to translate.
/// </remarks>
internal sealed class DeskPanel : Panel
{
    private readonly System.Windows.Forms.Timer _flashTimer;
    private readonly Dictionary<Zone, RectangleF> _zoneBounds = [];

    private Zone? _flashZone;
    private bool _flashAccepted;
    private int _flashLevel;
    private string _statusText = "Not listening";
    private string _detailText = string.Empty;
    private Color _statusColour = Theme.TextMuted;

    public DeskPanel()
    {
        DoubleBuffered = true;
        BackColor = Theme.Background;

        _flashTimer = new System.Windows.Forms.Timer { Interval = 40 };
        _flashTimer.Tick += (_, _) =>
        {
            _flashLevel -= 12;
            if (_flashLevel <= 0)
            {
                _flashLevel = 0;
                _flashZone = null;
                _flashTimer.Stop();
            }

            Invalidate();
        };
    }

    /// <summary>Zone the user is currently being asked to tap, highlighted persistently.</summary>
    public Zone? PromptZone { get; set; }

    public void SetStatus(string text, Color colour, string detail = "")
    {
        _statusText = text;
        _statusColour = colour;
        _detailText = detail;
        Invalidate();
    }

    /// <summary>Flashes a zone. Accepted taps flash the accent colour, rejections flash red.</summary>
    public void Flash(Zone zone, bool accepted)
    {
        _flashZone = zone;
        _flashAccepted = accepted;
        _flashLevel = 100;
        _flashTimer.Start();
        Invalidate();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);

        Graphics g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;

        float width = ClientSize.Width;
        float height = ClientSize.Height;

        if (width < 80 || height < 80)
        {
            return;
        }

        // Laptop
        float laptopWidth = Math.Min(width * 0.44f, 260);
        float laptopHeight = Math.Min(height * 0.26f, 130);
        var laptop = new RectangleF((width - laptopWidth) / 2f, height * 0.06f, laptopWidth, laptopHeight);

        using (var body = new SolidBrush(Theme.Surface))
        using (var pen = new Pen(Theme.Border, 1.5f))
        {
            g.FillRectangle(body, laptop);
            g.DrawRectangle(pen, laptop.X, laptop.Y, laptop.Width, laptop.Height);
        }

        RectangleF screen = RectangleF.Inflate(laptop, -10, -10);
        using (var screenBrush = new SolidBrush(Theme.Background))
        {
            g.FillRectangle(screenBrush, screen);
        }

        using (var brush = new SolidBrush(Theme.TextFaint))
        using (var format = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center })
        {
            g.DrawString("LAPTOP", Theme.ZoneLabel, brush, screen, format);
        }

        // Zones
        float leftX = width * 0.20f;
        float rightX = width * 0.80f;
        float frontY = height * 0.52f;
        float rearY = height * 0.76f;

        DrawZone(g, Zone.LeftFront, leftX, frontY);
        DrawZone(g, Zone.RightFront, rightX, frontY);
        DrawZone(g, Zone.LeftRear, leftX, rearY);
        DrawZone(g, Zone.RightRear, rightX, rearY);

        // Status
        using (var brush = new SolidBrush(_statusColour))
        using (var format = new StringFormat { Alignment = StringAlignment.Center })
        {
            g.DrawString(_statusText, Theme.MonoLarge, brush,
                new RectangleF(0, height * 0.885f, width, 26), format);
        }

        if (!string.IsNullOrEmpty(_detailText))
        {
            using var brush = new SolidBrush(Theme.TextMuted);
            using var format = new StringFormat { Alignment = StringAlignment.Center };
            g.DrawString(_detailText, Theme.Mono, brush,
                new RectangleF(0, height * 0.945f, width, 20), format);
        }
    }

    private void DrawZone(Graphics g, Zone zone, float centreX, float centreY)
    {
        const float radius = 17f;
        var circle = new RectangleF(centreX - radius, centreY - radius, radius * 2, radius * 2);
        _zoneBounds[zone] = circle;

        bool isFlashing = _flashZone == zone && _flashLevel > 0;
        bool isPrompt = PromptZone == zone;

        Color fill = Theme.SurfaceRaised;
        Color outline = Theme.Border;

        if (isPrompt)
        {
            fill = Color.FromArgb(60, Theme.Accent);
            outline = Theme.Accent;
        }

        if (isFlashing)
        {
            Color flash = _flashAccepted ? Theme.Accent : Theme.Bad;
            int alpha = Math.Clamp(_flashLevel * 2, 0, 255);
            fill = Color.FromArgb(alpha, flash);
            outline = flash;

            // Expanding ring, the one piece of motion in the whole interface.
            float ringRadius = radius + ((100 - _flashLevel) * 0.32f);
            using var ringPen = new Pen(Color.FromArgb(Math.Clamp(_flashLevel * 2, 0, 200), flash), 2f);
            g.DrawEllipse(ringPen, centreX - ringRadius, centreY - ringRadius, ringRadius * 2, ringRadius * 2);
        }

        using (var brush = new SolidBrush(fill))
        using (var pen = new Pen(outline, isPrompt || isFlashing ? 2.2f : 1.4f))
        {
            g.FillEllipse(brush, circle);
            g.DrawEllipse(pen, circle);
        }

        using (var brush = new SolidBrush(isPrompt || isFlashing ? Theme.Text : Theme.TextMuted))
        using (var format = new StringFormat { Alignment = StringAlignment.Center })
        {
            g.DrawString(Zones.DisplayName(zone), Theme.ZoneLabel, brush,
                new RectangleF(centreX - 80, centreY + radius + 6, 160, 18), format);
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _flashTimer.Dispose();
        }

        base.Dispose(disposing);
    }
}
