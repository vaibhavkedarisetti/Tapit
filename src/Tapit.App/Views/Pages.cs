using System.Diagnostics;
using System.Drawing;
using System.Windows.Forms;
using Tapit.App.Services;
using Tapit.Actions;
using Tapit.Core;
using Tapit.Core.Audio;
using Tapit.Core.Classification;
using Tapit.Core.Profiles;

namespace Tapit.App.Views;

/// <summary>Common plumbing for every page: dark ground, a title, and safe UI marshalling.</summary>
internal abstract class PageBase : Panel
{
    protected PageBase(AppServices services, string title)
    {
        Services = services;
        Dock = DockStyle.Fill;
        BackColor = Theme.Background;
        Padding = new Padding(20, 16, 20, 16);

        Title = new Label
        {
            Text = title,
            Font = Theme.Heading,
            ForeColor = Theme.Text,
            AutoSize = true,
            Location = new Point(20, 14),
        };
    }

    protected AppServices Services { get; }

    protected Label Title { get; }

    /// <summary>Runs an update on the UI thread, ignoring races with window teardown.</summary>
    protected void OnUi(Action action)
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
            // The window closed between the check and the call.
        }
        catch (InvalidOperationException)
        {
        }
    }

    /// <summary>Called when the page becomes visible, so pages only work while shown.</summary>
    public virtual void OnActivated()
    {
    }

    public virtual void OnDeactivated()
    {
    }
}

/// <summary>The main view: the desk, and what just happened on it.</summary>
internal sealed class DeskPage : PageBase
{
    private readonly DeskPanel _desk;
    private readonly Label _lastEvent;

    public DeskPage(AppServices services)
        : base(services, "Desk")
    {
        _desk = new DeskPanel { Dock = DockStyle.Fill };

        _lastEvent = new Label
        {
            Dock = DockStyle.Bottom,
            Height = 68,
            Font = Theme.Mono,
            ForeColor = Theme.TextMuted,
            TextAlign = ContentAlignment.MiddleCenter,
            Text = string.Empty,
        };

        Controls.Add(_desk);
        Controls.Add(_lastEvent);
        Controls.Add(Title);

        Services.TapProcessed += OnTap;
        Services.StateChanged += (_, _) => OnUi(UpdateStatus);
    }

    public override void OnActivated() => UpdateStatus();

    private void UpdateStatus()
    {
        if (Services.LastError is { } error)
        {
            _desk.SetStatus("Microphone unavailable", Theme.Bad, error);
            return;
        }

        if (!Services.IsRunning)
        {
            _desk.SetStatus("Paused", Theme.TextMuted, "Microphone released");
            return;
        }

        if (Services.Engine?.IsLearningRoom == true)
        {
            _desk.SetStatus("Learning the room…", Theme.Warn,
                $"{Services.Engine.RoomLearnProgress * 100:0}%  - stay quiet");
            return;
        }

        if (Services.Model is null)
        {
            _desk.SetStatus("Not calibrated", Theme.Warn,
                Services.CalibrationInvalidated ?? "Run Calibration to teach Tapit your four zones");
            return;
        }

        _desk.SetStatus("Listening", Theme.Good,
            $"noise floor {Services.Engine?.NoiseFloorDbfs ?? 0:0.0} dBFS");
    }

    private void OnTap(object? sender, TapResult result) => OnUi(() =>
    {
        if (result.Accepted && result.Zone is Zone zone)
        {
            _desk.Flash(zone, accepted: true);
            _lastEvent.ForeColor = Theme.Good;
            _lastEvent.Text =
                $"{Zones.DisplayName(zone)}\n\nAccepted     confidence {result.Decision.Confidence:P0}     " +
                $"latency {result.LatencyMs:0} ms";
        }
        else
        {
            // Rejections are shown, not hidden. Silence would make the system unfalsifiable.
            if (result.Decision.Zone is Zone guess)
            {
                _desk.Flash(guess, accepted: false);
            }

            _lastEvent.ForeColor = Theme.TextMuted;
            _lastEvent.Text = $"Tap rejected\n\n{result.Explanation}";
        }

        UpdateStatus();
    });
}

/// <summary>Zone-to-action bindings, each with an explicit Test button.</summary>
internal sealed class ActionsPage : PageBase
{
    private readonly Dictionary<Zone, (ComboBox Action, TextBox Argument, Label Status)> _rows = [];

    public ActionsPage(AppServices services)
        : base(services, "Actions")
    {
        var layout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 5,
            RowCount = Zones.Count + 2,
            Padding = new Padding(0, 44, 0, 0),
            BackColor = Theme.Background,
            AutoScroll = true,
        };

        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 120));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 230));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 260));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 90));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));

        int row = 0;
        foreach (Zone zone in Zones.All)
        {
            layout.Controls.Add(Theme.MakeLabel(Zones.DisplayName(zone), Theme.BodyBold), 0, row);

            ComboBox actions = Theme.MakeCombo(220);
            foreach (IAction action in Services.Registry.All.OrderBy(a => a.Category).ThenBy(a => a.DisplayName))
            {
                actions.Items.Add(new ActionItem(action));
            }

            TextBox argument = Theme.MakeTextBox(250);
            Label status = Theme.MakeLabel(string.Empty, Theme.Body, Theme.TextMuted);

            ZoneActionBinding binding = Services.Profile.Actions.TryGetValue(zone, out ZoneActionBinding? existing)
                ? existing
                : ZoneActionBinding.None;

            SelectAction(actions, binding.ActionId);
            argument.Text = binding.Argument ?? string.Empty;

            Zone captured = zone;
            actions.SelectedIndexChanged += (_, _) => Save(captured);
            argument.Leave += (_, _) => Save(captured);

            Button test = Theme.MakeButton("Test", 80);
            test.Click += async (_, _) => await TestAsync(captured).ConfigureAwait(true);

            layout.Controls.Add(actions, 1, row);
            layout.Controls.Add(argument, 2, row);
            layout.Controls.Add(test, 3, row);
            layout.Controls.Add(status, 4, row);

            _rows[zone] = (actions, argument, status);
            row++;
        }

        Label note = Theme.MakeLabel(
            "Test runs the action immediately and does not involve tap detection.\n" +
            "Actions only ever fire for a tap that passed every rejection gate.",
            Theme.Body, Theme.TextFaint);

        layout.Controls.Add(note, 0, row + 1);
        layout.SetColumnSpan(note, 5);

        Controls.Add(layout);
        Controls.Add(Title);

        UpdateValidation();
    }

    private static void SelectAction(ComboBox combo, string actionId)
    {
        for (int i = 0; i < combo.Items.Count; i++)
        {
            if (combo.Items[i] is ActionItem item &&
                string.Equals(item.Action.Id, actionId, StringComparison.OrdinalIgnoreCase))
            {
                combo.SelectedIndex = i;
                return;
            }
        }

        if (combo.Items.Count > 0)
        {
            combo.SelectedIndex = 0;
        }
    }

    private void Save(Zone zone)
    {
        (ComboBox actions, TextBox argument, _) = _rows[zone];

        if (actions.SelectedItem is not ActionItem item)
        {
            return;
        }

        Services.Profile.Actions[zone] = new ZoneActionBinding(
            item.Action.Id,
            string.IsNullOrWhiteSpace(argument.Text) ? null : argument.Text.Trim());

        Services.SaveProfile();
        UpdateValidation();
    }

    private void UpdateValidation()
    {
        foreach ((Zone zone, (ComboBox actions, TextBox argument, Label status)) in _rows)
        {
            if (actions.SelectedItem is not ActionItem item)
            {
                continue;
            }

            argument.Enabled = item.Action.RequiresArgument || item.Action.ArgumentHint is not null;
            argument.PlaceholderText = item.Action.ArgumentHint ?? string.Empty;

            ActionValidation validation = Services.Registry.Validate(
                item.Action.Id, string.IsNullOrWhiteSpace(argument.Text) ? null : argument.Text.Trim());

            status.Text = validation.IsValid ? string.Empty : validation.Message;
            status.ForeColor = Theme.Warn;
        }
    }

    private async Task TestAsync(Zone zone)
    {
        (ComboBox actions, TextBox argument, Label status) = _rows[zone];

        if (actions.SelectedItem is not ActionItem item)
        {
            return;
        }

        ActionOutcome outcome = await Services.Dispatcher.TestAsync(
            zone, item.Action.Id, string.IsNullOrWhiteSpace(argument.Text) ? null : argument.Text.Trim())
            .ConfigureAwait(true);

        status.ForeColor = outcome.Succeeded ? Theme.Good : Theme.Bad;
        status.Text = outcome.Succeeded
            ? $"ran in {outcome.LatencyMs:0} ms"
            : outcome.Error ?? "failed";
    }

    private sealed record ActionItem(IAction Action)
    {
        public override string ToString() => $"{Action.Category} - {Action.DisplayName}";
    }
}

/// <summary>Microphone choice, profiles, and the privacy-relevant switches.</summary>
internal sealed class SettingsPage : PageBase
{
    private readonly ComboBox _devices = Theme.MakeCombo(420);
    private readonly ComboBox _profiles = Theme.MakeCombo(280);
    private readonly ComboBox _classifiers = Theme.MakeCombo(280);
    private readonly Label _deviceInfo = Theme.MakeLabel(string.Empty, Theme.Mono, Theme.TextMuted);
    private readonly CheckBox _startWithWindows;

    public SettingsPage(AppServices services)
        : base(services, "Settings")
    {
        var layout = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.TopDown,
            WrapContents = false,
            AutoScroll = true,
            Padding = new Padding(0, 44, 0, 0),
            BackColor = Theme.Background,
        };

        _startWithWindows = new CheckBox
        {
            Text = "Start Tapit when I sign in",
            ForeColor = Theme.Text,
            Font = Theme.Body,
            AutoSize = true,
            Checked = StartupRegistration.IsEnabled,
        };

        _startWithWindows.CheckedChanged += (_, _) => StartupRegistration.SetEnabled(_startWithWindows.Checked);

        layout.Controls.Add(Theme.MakeLabel("MICROPHONE", Theme.BodyBold, Theme.TextMuted));
        layout.Controls.Add(_devices);
        layout.Controls.Add(_deviceInfo);
        layout.Controls.Add(Spacer());

        layout.Controls.Add(Theme.MakeLabel("PROFILE", Theme.BodyBold, Theme.TextMuted));
        layout.Controls.Add(_profiles);

        var profileButtons = new FlowLayoutPanel { AutoSize = true, FlowDirection = FlowDirection.LeftToRight };
        Button newProfile = Theme.MakeButton("New profile", 120);
        Button renameProfile = Theme.MakeButton("Rename", 100);
        Button deleteProfile = Theme.MakeButton("Delete", 100);
        profileButtons.Controls.AddRange([newProfile, renameProfile, deleteProfile]);
        layout.Controls.Add(profileButtons);

        layout.Controls.Add(Theme.MakeLabel(
            "A profile is bound to one laptop, one desk, one laptop position and one room.\n" +
            "Change any of those and it must be recalibrated.", Theme.Body, Theme.TextFaint));
        layout.Controls.Add(Spacer());

        layout.Controls.Add(Theme.MakeLabel("CLASSIFIER", Theme.BodyBold, Theme.TextMuted));
        _classifiers.Items.AddRange(["nearest-neighbour", "knn-3", "logistic-regression", "ridge"]);
        _classifiers.SelectedItem = Services.Profile.ClassifierName;
        _classifiers.SelectedIndexChanged += (_, _) =>
        {
            Services.Profile.ClassifierName = (string)_classifiers.SelectedItem!;
            Services.SaveProfile();
            Services.ReloadModel();
        };

        layout.Controls.Add(_classifiers);
        layout.Controls.Add(Spacer());

        layout.Controls.Add(Theme.MakeLabel("STARTUP", Theme.BodyBold, Theme.TextMuted));
        layout.Controls.Add(_startWithWindows);
        layout.Controls.Add(Spacer());

        layout.Controls.Add(Theme.MakeLabel("PRIVACY", Theme.BodyBold, Theme.TextMuted));
        layout.Controls.Add(Theme.MakeLabel(
            "Audio is processed in memory and discarded. Profiles store feature vectors and\n" +
            "model parameters - never recordings. Nothing is uploaded: no telemetry, no\n" +
            "analytics, no crash reporting, no update check.\n\n" +
            "Pausing the microphone genuinely stops the stream, so the Windows in-use\n" +
            "indicator goes out.",
            Theme.Body, Theme.TextMuted));
        layout.Controls.Add(Spacer());

        layout.Controls.Add(Theme.MakeLabel("ABOUT", Theme.BodyBold, Theme.TextMuted));
        layout.Controls.Add(Theme.MakeLabel("Tapit", Theme.Heading));
        layout.Controls.Add(Theme.MakeLabel("Built by Vaibhav Kedarisetti", Theme.Body, Theme.TextMuted));
        layout.Controls.Add(MakeLinkedInLink());

        newProfile.Click += (_, _) => CreateProfile();
        renameProfile.Click += (_, _) => RenameProfile();
        deleteProfile.Click += (_, _) => DeleteProfile();
        _devices.SelectedIndexChanged += (_, _) => ApplyDevice();
        _profiles.SelectedIndexChanged += (_, _) => ApplyProfile();

        Controls.Add(layout);
        Controls.Add(Title);
    }

    private static Control Spacer() => new Panel { Height = 14, Width = 10 };

    private const string LinkedInUrl = "https://www.linkedin.com/in/vaibhav-kedarisetti";

    private static LinkLabel MakeLinkedInLink()
    {
        var link = new LinkLabel
        {
            Text = "linkedin.com/in/vaibhav-kedarisetti",
            AutoSize = true,
            Font = Theme.Body,
            BackColor = Color.Transparent,
            LinkColor = Theme.Accent,
            ActiveLinkColor = Theme.Text,
            VisitedLinkColor = Theme.Accent,
            LinkBehavior = LinkBehavior.HoverUnderline,
        };

        link.LinkClicked += (_, _) =>
        {
            try
            {
                Process.Start(new ProcessStartInfo(LinkedInUrl) { UseShellExecute = true })?.Dispose();
            }
            catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
            {
                // No default browser, or the shell refused. Not worth interrupting the user.
            }
        };

        return link;
    }

    public override void OnActivated()
    {
        ReloadDevices();
        ReloadProfiles();
        _classifiers.SelectedItem = Services.Profile.ClassifierName;
    }

    private bool _suppress;

    private void ReloadDevices()
    {
        _suppress = true;
        _devices.Items.Clear();

        _devices.Items.Add(new DeviceItem(null, "System default capture device"));

        foreach (AudioDeviceInfo device in Services.Devices.GetCaptureDevices()
                     .Where(d => d.State == AudioDeviceState.Active))
        {
            _devices.Items.Add(new DeviceItem(device.Id, device.FriendlyName));
        }

        string? configured = Services.Profile.Device?.DeviceId;
        _devices.SelectedIndex = 0;

        for (int i = 0; i < _devices.Items.Count; i++)
        {
            if (_devices.Items[i] is DeviceItem item && item.Id == configured)
            {
                _devices.SelectedIndex = i;
                break;
            }
        }

        _suppress = false;
        UpdateDeviceInfo();
    }

    private void UpdateDeviceInfo()
    {
        DeviceBinding? current = Services.CurrentBinding;

        if (current is null)
        {
            _deviceInfo.Text = "Not capturing.";
            _deviceInfo.ForeColor = Theme.TextMuted;
            return;
        }

        _deviceInfo.Text =
            $"{current.SampleRate} Hz, {current.Channels} ch, {current.SampleFormat}   " +
            (current.RawMode
                ? "raw - Windows audio effects bypassed"
                : "PROCESSED - enhancements active, accuracy will suffer");

        _deviceInfo.ForeColor = current.RawMode ? Theme.Good : Theme.Warn;
    }

    private void ReloadProfiles()
    {
        _suppress = true;
        _profiles.Items.Clear();

        foreach (TapitProfile profile in Services.Store.LoadAll())
        {
            _profiles.Items.Add(new ProfileItem(profile));
            if (profile.Id == Services.Profile.Id)
            {
                _profiles.SelectedIndex = _profiles.Items.Count - 1;
            }
        }

        _suppress = false;
    }

    private void ApplyDevice()
    {
        if (_suppress || _devices.SelectedItem is not DeviceItem item)
        {
            return;
        }

        // Changing microphone invalidates calibration, so say so before doing it.
        if (Services.Profile.IsCalibrated &&
            Services.Profile.Device?.DeviceId != item.Id &&
            MessageBox.Show(
                "Changing the microphone invalidates this profile's calibration.\n\nContinue?",
                "Tapit", MessageBoxButtons.OKCancel, MessageBoxIcon.Warning) != DialogResult.OK)
        {
            ReloadDevices();
            return;
        }

        Services.Profile.Device = item.Id is null
            ? null
            : new DeviceBinding(item.Id, item.Name, 0, 0, string.Empty, false);

        Services.SaveProfile();
        Services.Stop();
        Services.Start();
        UpdateDeviceInfo();
    }

    private void ApplyProfile()
    {
        if (_suppress || _profiles.SelectedItem is not ProfileItem item)
        {
            return;
        }

        Services.SwitchProfile(item.Profile);
        OnActivated();
    }

    private void CreateProfile()
    {
        string? name = Prompt.Show("New profile", "Name this physical setup:", "Office Desk");
        if (string.IsNullOrWhiteSpace(name))
        {
            return;
        }

        var profile = new TapitProfile { Name = name.Trim() };
        Services.Store.Save(profile);
        Services.SwitchProfile(profile);
        OnActivated();
    }

    private void RenameProfile()
    {
        string? name = Prompt.Show("Rename profile", "New name:", Services.Profile.Name);
        if (string.IsNullOrWhiteSpace(name))
        {
            return;
        }

        Services.Profile.Name = name.Trim();
        Services.SaveProfile();
        ReloadProfiles();
    }

    private void DeleteProfile()
    {
        if (Services.Store.LoadAll().Count <= 1)
        {
            MessageBox.Show("At least one profile is required.", "Tapit");
            return;
        }

        if (MessageBox.Show(
                $"Delete '{Services.Profile.Name}' and its calibration?",
                "Tapit", MessageBoxButtons.OKCancel, MessageBoxIcon.Warning) != DialogResult.OK)
        {
            return;
        }

        Services.Store.Delete(Services.Profile.Id);
        TapitProfile next = Services.Store.LoadAll()[0];
        Services.SwitchProfile(next);
        OnActivated();
    }

    private sealed record DeviceItem(string? Id, string Name)
    {
        public override string ToString() => Name;
    }

    private sealed record ProfileItem(TapitProfile Profile)
    {
        public override string ToString() =>
            Profile.IsCalibrated ? Profile.Name : $"{Profile.Name}  (not calibrated)";
    }
}

/// <summary>Minimal modal text prompt - WinForms has no built-in equivalent.</summary>
internal static class Prompt
{
    public static string? Show(string title, string message, string initial = "")
    {
        using var form = new Form
        {
            Text = title,
            StartPosition = FormStartPosition.CenterParent,
            FormBorderStyle = FormBorderStyle.FixedDialog,
            MinimizeBox = false,
            MaximizeBox = false,
            ClientSize = new Size(380, 130),
            BackColor = Theme.Background,
            ForeColor = Theme.Text,
        };

        var label = Theme.MakeLabel(message);
        label.Location = new Point(16, 16);

        TextBox input = Theme.MakeTextBox(348);
        input.Location = new Point(16, 44);
        input.Text = initial;

        Button ok = Theme.MakeButton("OK", 90);
        ok.Location = new Point(174, 84);
        ok.DialogResult = DialogResult.OK;

        Button cancel = Theme.MakeButton("Cancel", 90);
        cancel.Location = new Point(274, 84);
        cancel.DialogResult = DialogResult.Cancel;

        form.Controls.AddRange([label, input, ok, cancel]);
        form.AcceptButton = ok;
        form.CancelButton = cancel;

        return form.ShowDialog() == DialogResult.OK ? input.Text : null;
    }
}

/// <summary>Run-at-sign-in registration via the per-user Run key.</summary>
/// <remarks>
/// Deliberately the HKCU Run key rather than a scheduled task or a service: it needs no
/// elevation, it is visible to the user in Task Manager's Startup tab, and they can remove
/// it without Tapit's help.
/// </remarks>
internal static class StartupRegistration
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "Tapit";

    public static bool IsEnabled
    {
        get
        {
            using Microsoft.Win32.RegistryKey? key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(RunKey);
            return key?.GetValue(ValueName) is not null;
        }
    }

    public static void SetEnabled(bool enabled)
    {
        try
        {
            using Microsoft.Win32.RegistryKey key =
                Microsoft.Win32.Registry.CurrentUser.CreateSubKey(RunKey, writable: true);

            if (enabled)
            {
                string path = Environment.ProcessPath ?? Application.ExecutablePath;
                key.SetValue(ValueName, $"\"{path}\" --tray");
            }
            else
            {
                key.DeleteValue(ValueName, throwOnMissingValue: false);
            }
        }
        catch (UnauthorizedAccessException)
        {
            MessageBox.Show("Windows refused the startup setting change.", "Tapit");
        }
    }
}
