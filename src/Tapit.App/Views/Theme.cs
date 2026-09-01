using System.Drawing;
using System.Windows.Forms;

namespace Tapit.App.Views;

/// <summary>
/// The application's visual language.
/// </summary>
/// <remarks>
/// Deliberately restrained: a dark neutral ground, one accent, and a monospaced face for
/// anything numeric so columns line up and values do not jitter as they update. No
/// gradients, no animation beyond a single tap flash, no decorative metrics. This is a
/// measurement instrument, and it should look like one.
/// </remarks>
internal static class Theme
{
    public static readonly Color Background = Color.FromArgb(24, 26, 29);
    public static readonly Color Surface = Color.FromArgb(32, 35, 39);
    public static readonly Color SurfaceRaised = Color.FromArgb(42, 46, 51);
    public static readonly Color Border = Color.FromArgb(58, 63, 70);

    public static readonly Color Text = Color.FromArgb(226, 229, 233);
    public static readonly Color TextMuted = Color.FromArgb(140, 148, 158);
    public static readonly Color TextFaint = Color.FromArgb(96, 103, 112);

    public static readonly Color Accent = Color.FromArgb(94, 176, 226);
    public static readonly Color Good = Color.FromArgb(112, 194, 138);
    public static readonly Color Warn = Color.FromArgb(224, 178, 92);
    public static readonly Color Bad = Color.FromArgb(224, 108, 108);

    public static readonly Font Body = new("Segoe UI", 9F);
    public static readonly Font BodyBold = new("Segoe UI", 9F, FontStyle.Bold);
    public static readonly Font Heading = new("Segoe UI", 12F, FontStyle.Regular);
    public static readonly Font Mono = new("Consolas", 9F);
    public static readonly Font MonoLarge = new("Consolas", 14F);
    public static readonly Font ZoneLabel = new("Segoe UI", 8F, FontStyle.Bold);

    public static void ApplyDark(Control control)
    {
        control.BackColor = Background;
        control.ForeColor = Text;
    }

    public static Button MakeButton(string text, int width = 110)
    {
        var button = new Button
        {
            Text = text,
            Width = width,
            Height = 30,
            FlatStyle = FlatStyle.Flat,
            BackColor = SurfaceRaised,
            ForeColor = Text,
            Font = Body,
            UseVisualStyleBackColor = false,
        };

        button.FlatAppearance.BorderColor = Border;
        button.FlatAppearance.MouseOverBackColor = Border;
        return button;
    }

    public static Label MakeLabel(string text, Font? font = null, Color? colour = null) => new()
    {
        Text = text,
        AutoSize = true,
        Font = font ?? Body,
        ForeColor = colour ?? Text,
        BackColor = Color.Transparent,
    };

    public static ComboBox MakeCombo(int width = 260) => new()
    {
        Width = width,
        DropDownStyle = ComboBoxStyle.DropDownList,
        FlatStyle = FlatStyle.Flat,
        BackColor = SurfaceRaised,
        ForeColor = Text,
        Font = Body,
    };

    public static TextBox MakeTextBox(int width = 260) => new()
    {
        Width = width,
        BorderStyle = BorderStyle.FixedSingle,
        BackColor = SurfaceRaised,
        ForeColor = Text,
        Font = Body,
    };

    /// <summary>Colour for a signal-quality figure, so status is readable at a glance.</summary>
    public static Color ForQuality(bool good, bool warn = false) =>
        good ? Good : warn ? Warn : Bad;
}
