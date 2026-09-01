using System.Media;
using Tapit.Actions;
using Tapit.Audio;
using Tapit.Audio.Wasapi;
using Tapit.Core;
using Tapit.Core.Audio;
using Tapit.Core.Classification;
using Tapit.Core.Features;
using Tapit.Core.Profiles;

namespace Tapit.App.Services;

/// <summary>
/// Owns the running system: capture, engine, profile and action dispatch.
/// </summary>
/// <remarks>
/// One instance for the whole application. The UI observes it; it never observes the UI.
/// Everything here is deliberately explicit about microphone state, because a background
/// process holding a microphone must never be ambiguous about whether it is listening.
/// </remarks>
public sealed class AppServices : IDisposable
{
    private readonly object _gate = new();

    private WasapiCaptureSource? _capture;
    private TapitEngine? _engine;
    private bool _disposed;

    public AppServices()
    {
        Store = new ProfileStore();
        Registry = new ActionRegistry();
        Feedback = new AppFeedback();
        Dispatcher = new ActionDispatcher(Registry, Feedback);
        Devices = new WasapiDeviceEnumerator();

        Profile = LoadOrCreateProfile();
        InvalidateStaleCalibration();
    }

    /// <summary>
    /// Discards calibration collected under a different feature set.
    /// </summary>
    /// <remarks>
    /// Samples are vectors of a fixed length with fixed meaning per position. When the
    /// feature set changes - as it did when inter-channel spatial cues were added - old
    /// samples describe different quantities and are not merely stale, they are wrong.
    /// Keeping them would mean either a dimension mismatch at inference or, worse, a model
    /// trained on numbers that no longer mean what their names say.
    /// </remarks>
    private void InvalidateStaleCalibration()
    {
        if (Profile.FeatureNames.Count == 0 ||
            Profile.FeatureNames.SequenceEqual(TapFeatureExtractor.Names))
        {
            return;
        }

        CalibrationInvalidated =
            $"Calibration was collected with a different feature set " +
            $"({Profile.FeatureNames.Count} features, now {TapFeatureExtractor.Count}). " +
            "Recalibration is required.";

        Profile.Samples.Clear();
        Profile.Negatives.Clear();
        Profile.FeatureNames.Clear();
        Store.Save(Profile);
    }

    /// <summary>Set when stored calibration had to be discarded, so the UI can say why.</summary>
    public string? CalibrationInvalidated { get; private set; }

    public ProfileStore Store { get; }

    public ActionRegistry Registry { get; }

    public ActionDispatcher Dispatcher { get; }

    public AppFeedback Feedback { get; }

    public WasapiDeviceEnumerator Devices { get; }

    public TapitProfile Profile { get; private set; }

    public TapitEngine? Engine => _engine;

    public ZoneModel? Model { get; private set; }

    public bool IsListening => _engine?.IsRunning == true && _engine.IsPaused == false;

    public bool IsRunning => _engine?.IsRunning == true;

    /// <summary>
    /// When true, accepted taps are reported but no action is fired. Calibration and
    /// evaluation both set this: a tap collected as a sample must not also skip a track.
    /// </summary>
    public bool SuppressActions { get; set; }

    public string? LastError { get; private set; }

    public event EventHandler<TapResult>? TapProcessed;

    public event EventHandler? StateChanged;

    private TapitProfile LoadOrCreateProfile()
    {
        string activeId = Store.ActiveProfileId;

        if (!string.IsNullOrEmpty(activeId) && Store.Load(activeId) is { } existing)
        {
            return existing;
        }

        IReadOnlyList<TapitProfile> all = Store.LoadAll();
        if (all.Count > 0)
        {
            Store.ActiveProfileId = all[0].Id;
            return all[0];
        }

        var profile = new TapitProfile { Name = "My Desk" };
        Store.Save(profile);
        Store.ActiveProfileId = profile.Id;
        return profile;
    }

    public void SwitchProfile(TapitProfile profile)
    {
        ArgumentNullException.ThrowIfNull(profile);

        bool wasRunning = IsRunning;
        Stop();

        Profile = profile;
        Store.ActiveProfileId = profile.Id;
        CalibrationInvalidated = null;
        InvalidateStaleCalibration();

        if (wasRunning)
        {
            Start();
        }

        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    public void SaveProfile() => Store.Save(Profile);

    /// <summary>Rebuilds the trained model from the profile's calibration samples.</summary>
    public void ReloadModel()
    {
        Model = Profile.BuildModel();

        if (_engine is not null)
        {
            _engine.Model = Model;
        }

        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    public void Start()
    {
        lock (_gate)
        {
            if (_engine is not null)
            {
                _engine.IsPaused = false;
                StateChanged?.Invoke(this, EventArgs.Empty);
                return;
            }

            LastError = null;

            try
            {
                _capture = new WasapiCaptureSource(new WasapiCaptureOptions
                {
                    DeviceId = string.IsNullOrWhiteSpace(Profile.Device?.DeviceId) ? null : Profile.Device.DeviceId,
                });

                Model = Profile.BuildModel();

                _engine = new TapitEngine(_capture, Profile.BuildDetectorOptions(), Model);
                _engine.TapProcessed += OnTapProcessed;
                _engine.Start();

                RecordDeviceBinding();
            }
            catch (Exception ex)
            {
                LastError = ex.Message;
                _engine?.Dispose();
                _engine = null;
                _capture?.Dispose();
                _capture = null;
            }
        }

        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// Stops capture and releases the microphone, so the Windows in-use indicator goes out.
    /// </summary>
    public void Stop()
    {
        lock (_gate)
        {
            if (_engine is not null)
            {
                _engine.TapProcessed -= OnTapProcessed;
                _engine.Dispose();
                _engine = null;
            }

            _capture?.Dispose();
            _capture = null;
        }

        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    public void TogglePaused()
    {
        if (_engine is null)
        {
            Start();
            return;
        }

        Stop();
    }

    /// <summary>Captures what the profile was actually calibrated against.</summary>
    private void RecordDeviceBinding()
    {
        if (_capture?.Format is not { } format || _capture.DeviceId is null)
        {
            return;
        }

        Profile.Device ??= DeviceBinding.From(
            _capture.DeviceId, _capture.DeviceName ?? "Unknown", format, _capture.RawModeActive);
    }

    /// <summary>The live setup, for comparison against what the profile was calibrated on.</summary>
    public DeviceBinding? CurrentBinding =>
        _capture?.Format is { } format && _capture.DeviceId is not null
            ? DeviceBinding.From(_capture.DeviceId, _capture.DeviceName ?? "Unknown", format, _capture.RawModeActive)
            : null;

    private void OnTapProcessed(object? sender, TapResult result)
    {
        // Dispatch first so an accepted tap is not delayed by UI marshalling, then notify.
        if (result.Accepted && !SuppressActions && result.Zone is Zone zone)
        {
            if (Profile.Actions.TryGetValue(zone, out ZoneActionBinding? binding))
            {
                Dispatcher.Dispatch(zone, binding.ActionId, binding.Argument, result.Decision.Confidence);
            }
        }

        TapProcessed?.Invoke(this, result);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        Stop();
        Dispatcher.Dispose();
        Devices.Dispose();
    }
}

/// <summary>Routes action feedback to the UI thread.</summary>
public sealed class AppFeedback : IActionFeedback
{
    public event EventHandler<(Zone Zone, string Message)>? Flashed;

    public event EventHandler<string>? Notified;

    public void Flash(Zone zone, string message) => Flashed?.Invoke(this, (zone, message));

    public void Beep()
    {
        try
        {
            SystemSounds.Asterisk.Play();
        }
        catch (Exception)
        {
            // Audio feedback is a nicety; never let it break an action.
        }
    }

    public void Notify(string message) => Notified?.Invoke(this, message);
}
