using System.Drawing;
using System.Windows.Forms;
using Tapit.App.Services;

namespace Tapit.App.Views;

/// <summary>
/// The application window: a left rail of sections and one page at a time.
/// </summary>
/// <remarks>
/// Closing the window hides it rather than exiting - Tapit is a tray utility, and a user who
/// closes the window expects it to keep listening. Quit is an explicit tray action.
/// </remarks>
internal sealed class MainForm : Form
{
    private readonly AppServices _services;
    private readonly Panel _content;
    private readonly Dictionary<string, PageBase> _pages = [];
    private readonly Dictionary<string, Button> _navButtons = [];
    private readonly Label _statusStrip;

    private PageBase? _current;

    public MainForm(AppServices services)
    {
        _services = services;

        Text = "Tapit";
        ClientSize = new Size(1000, 700);
        MinimumSize = new Size(840, 600);
        StartPosition = FormStartPosition.CenterScreen;
        BackColor = Theme.Background;
        ForeColor = Theme.Text;
        Font = Theme.Body;
        Icon = TrayIcons.Create(Theme.Accent);

        _content = new Panel { Dock = DockStyle.Fill, BackColor = Theme.Background };

        var rail = new FlowLayoutPanel
        {
            Dock = DockStyle.Left,
            Width = 170,
            FlowDirection = FlowDirection.TopDown,
            WrapContents = false,
            BackColor = Theme.Surface,
            Padding = new Padding(0, 18, 0, 0),
        };

        _statusStrip = new Label
        {
            Dock = DockStyle.Bottom,
            Height = 28,
            Font = Theme.Mono,
            ForeColor = Theme.TextMuted,
            BackColor = Theme.Surface,
            TextAlign = ContentAlignment.MiddleLeft,
            Padding = new Padding(12, 0, 0, 0),
        };

        AddPage(rail, "Desk", new DeskPage(services));
        AddPage(rail, "Calibration", new CalibrationPage(services));
        AddPage(rail, "Actions", new ActionsPage(services));
        AddPage(rail, "Diagnostics", new DiagnosticsPage(services));
        AddPage(rail, "Evaluation", new EvaluationPage(services));
        AddPage(rail, "Settings", new SettingsPage(services));

        Controls.Add(_content);
        Controls.Add(rail);
        Controls.Add(_statusStrip);

        _services.StateChanged += (_, _) => BeginInvokeSafe(UpdateStatus);
        _services.Feedback.Notified += (_, message) => BeginInvokeSafe(() => _statusStrip.Text = message);

        Show("Desk");
        UpdateStatus();
    }

    private void AddPage(Control rail, string name, PageBase page)
    {
        Button button = Theme.MakeButton(name, 170);
        button.Height = 38;
        button.TextAlign = ContentAlignment.MiddleLeft;
        button.Padding = new Padding(16, 0, 0, 0);
        button.BackColor = Theme.Surface;
        button.FlatAppearance.BorderSize = 0;
        button.Margin = new Padding(0);
        button.Click += (_, _) => Show(name);

        rail.Controls.Add(button);
        _navButtons[name] = button;
        _pages[name] = page;
    }

    public void Show(string pageName)
    {
        if (!_pages.TryGetValue(pageName, out PageBase? page))
        {
            return;
        }

        _current?.OnDeactivated();

        _content.Controls.Clear();
        _content.Controls.Add(page);
        _current = page;

        foreach ((string name, Button button) in _navButtons)
        {
            bool active = name == pageName;
            button.BackColor = active ? Theme.SurfaceRaised : Theme.Surface;
            button.ForeColor = active ? Theme.Accent : Theme.Text;
        }

        page.OnActivated();
    }

    private void UpdateStatus()
    {
        string microphone = _services.IsRunning ? "Listening" : "Microphone paused";
        string profile = _services.Profile.Name;
        string calibration = _services.Model is null ? "not calibrated" : _services.Profile.ClassifierName;

        _statusStrip.Text = $"{microphone}    ·    {profile}    ·    {calibration}";
        _statusStrip.ForeColor = _services.IsRunning ? Theme.Good : Theme.TextMuted;

        if (_services.LastError is { } error)
        {
            _statusStrip.Text = error;
            _statusStrip.ForeColor = Theme.Bad;
        }
    }

    private void BeginInvokeSafe(Action action)
    {
        if (IsDisposed || !IsHandleCreated)
        {
            return;
        }

        try
        {
            BeginInvoke(action);
        }
        catch (ObjectDisposedException)
        {
        }
        catch (InvalidOperationException)
        {
        }
    }

    /// <summary>True when the user asked to exit, rather than merely closing the window.</summary>
    public bool ExitRequested { get; set; }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        if (!ExitRequested && e.CloseReason == CloseReason.UserClosing)
        {
            e.Cancel = true;
            Hide();
            _current?.OnDeactivated();
            return;
        }

        base.OnFormClosing(e);
    }
}

/// <summary>Draws tray and window icons at runtime, so the app ships no image assets.</summary>
internal static class TrayIcons
{
    public static Icon Create(Color colour)
    {
        using var bitmap = new Bitmap(32, 32);
        using (Graphics g = Graphics.FromImage(bitmap))
        {
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            g.Clear(Color.Transparent);

            // A dot with two rings: a tap and its radiating impulse.
            using var ring = new Pen(Color.FromArgb(150, colour), 2f);
            g.DrawEllipse(ring, 3, 3, 25, 25);
            g.DrawEllipse(ring, 8, 8, 15, 15);

            using var centre = new SolidBrush(colour);
            g.FillEllipse(centre, 12, 12, 8, 8);
        }

        IntPtr handle = bitmap.GetHicon();
        try
        {
            using var temporary = Icon.FromHandle(handle);
            return (Icon)temporary.Clone();
        }
        finally
        {
            NativeIcon.DestroyIcon(handle);
        }
    }
}

internal static class NativeIcon
{
    [System.Runtime.InteropServices.DllImport("user32.dll", SetLastError = true)]
    [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
    public static extern bool DestroyIcon(IntPtr handle);
}
