using System.Drawing;
using System.Windows.Forms;
using Tapit.App.Services;
using Tapit.App.Views;

namespace Tapit.App;

internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        // One instance only: two copies would fight over the microphone and both would be
        // worse for it.
        using var single = new Mutex(true, @"Local\Tapit.SingleInstance", out bool created);
        if (!created)
        {
            MessageBox.Show("Tapit is already running. Look for it in the notification area.",
                "Tapit", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        ApplicationConfiguration.Initialize();
        Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);

        bool startHidden = args.Any(a => string.Equals(a, "--tray", StringComparison.OrdinalIgnoreCase));

        using var services = new AppServices();
        using var context = new TapitApplicationContext(services, startHidden);

        Application.Run(context);
    }
}

/// <summary>
/// Keeps Tapit alive in the notification area, with or without a window.
/// </summary>
/// <remarks>
/// Microphone state is deliberately impossible to misread: the tray icon changes colour, its
/// tooltip states it in words, and the menu's first line repeats it. A background process
/// that holds a microphone should never leave a user guessing.
/// </remarks>
internal sealed class TapitApplicationContext : ApplicationContext
{
    private readonly AppServices _services;
    private readonly NotifyIcon _tray;
    private readonly ToolStripMenuItem _statusItem;
    private readonly ToolStripMenuItem _pauseItem;
    private readonly Icon _listeningIcon;
    private readonly Icon _pausedIcon;

    private MainForm? _window;

    public TapitApplicationContext(AppServices services, bool startHidden)
    {
        _services = services;

        _listeningIcon = TrayIcons.Create(Theme.Accent);
        _pausedIcon = TrayIcons.Create(Theme.TextFaint);

        _statusItem = new ToolStripMenuItem("Starting…") { Enabled = false };
        _pauseItem = new ToolStripMenuItem("Pause microphone", null, (_, _) => TogglePause());

        var menu = new ContextMenuStrip();
        menu.Items.Add(new ToolStripMenuItem("Tapit") { Enabled = false, Font = Theme.BodyBold });
        menu.Items.Add(_statusItem);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(new ToolStripMenuItem("Desk", null, (_, _) => Open("Desk")));
        menu.Items.Add(new ToolStripMenuItem("Calibration", null, (_, _) => Open("Calibration")));
        menu.Items.Add(new ToolStripMenuItem("Actions", null, (_, _) => Open("Actions")));
        menu.Items.Add(new ToolStripMenuItem("Diagnostics", null, (_, _) => Open("Diagnostics")));
        menu.Items.Add(new ToolStripMenuItem("Evaluation", null, (_, _) => Open("Evaluation")));
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(_pauseItem);
        menu.Items.Add(new ToolStripMenuItem("Quit", null, (_, _) => Quit()));

        _tray = new NotifyIcon
        {
            Icon = _pausedIcon,
            Text = "Tapit",
            Visible = true,
            ContextMenuStrip = menu,
        };

        _tray.DoubleClick += (_, _) => Open("Desk");

        _services.StateChanged += (_, _) => UpdateTray();
        _services.Start();

        if (!startHidden)
        {
            Open("Desk");
        }

        UpdateTray();
    }

    private void Open(string page)
    {
        if (_window is null || _window.IsDisposed)
        {
            _window = new MainForm(_services);
            _window.FormClosed += (_, _) => _window = null;
        }

        _window.Show(page);
        _window.Visible = true;
        _window.WindowState = FormWindowState.Normal;
        _window.Activate();
    }

    private void TogglePause()
    {
        _services.TogglePaused();
        UpdateTray();
    }

    private void UpdateTray()
    {
        if (_tray.ContextMenuStrip?.InvokeRequired == true)
        {
            _tray.ContextMenuStrip.BeginInvoke(UpdateTray);
            return;
        }

        bool listening = _services.IsRunning;

        _tray.Icon = listening ? _listeningIcon : _pausedIcon;
        _statusItem.Text = listening ? "● Listening" : "○ Microphone paused";
        _pauseItem.Text = listening ? "Pause microphone" : "Resume microphone";

        string calibration = _services.Model is null ? "not calibrated" : _services.Profile.Name;

        // NotifyIcon tooltips are capped at 63 characters by Windows.
        string tooltip = listening
            ? $"Tapit - listening ({calibration})"
            : "Tapit - microphone paused";

        _tray.Text = tooltip.Length > 63 ? tooltip[..63] : tooltip;
    }

    private void Quit()
    {
        if (_window is not null)
        {
            _window.ExitRequested = true;
            _window.Close();
        }

        _tray.Visible = false;
        ExitThread();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _tray.Visible = false;
            _tray.Dispose();
            _listeningIcon.Dispose();
            _pausedIcon.Dispose();
        }

        base.Dispose(disposing);
    }
}
