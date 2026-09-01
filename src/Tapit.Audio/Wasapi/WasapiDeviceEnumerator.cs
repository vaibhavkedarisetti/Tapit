using System.Runtime.InteropServices;
using Tapit.Core.Audio;

namespace Tapit.Audio.Wasapi;

/// <summary>
/// Enumerates capture endpoints and reports device arrival, removal and default-device
/// changes.
/// </summary>
/// <remarks>
/// Device formats are read from the endpoint property store rather than by activating an
/// <c>IAudioClient</c> per device: listing microphones should never risk waking a device or
/// failing because something else holds it exclusively.
/// </remarks>
public sealed class WasapiDeviceEnumerator : IAudioDeviceEnumerator
{
    private readonly ComApartmentWorker _worker = new("Tapit MMDevice");

    private IMMDeviceEnumerator? _enumerator;
    private NotificationClient? _notifications;
    private bool _disposed;

    public event EventHandler<AudioDeviceChangeEventArgs>? DevicesChanged;

    public IReadOnlyList<AudioDeviceInfo> GetCaptureDevices() => _worker.Invoke(() =>
    {
        IMMDeviceEnumerator enumerator = GetEnumerator();

        string? defaultId = TryGetDefaultId(enumerator, ERole.Console);
        string? defaultCommsId = TryGetDefaultId(enumerator, ERole.Communications);

        int hr = enumerator.EnumAudioEndpoints(
            EDataFlow.Capture, WasapiConstants.DeviceStateAll, out IMMDeviceCollection collection);
        WasapiException.ThrowIfFailed(hr, "IMMDeviceEnumerator::EnumAudioEndpoints");

        try
        {
            WasapiException.ThrowIfFailed(collection.GetCount(out int count), "IMMDeviceCollection::GetCount");

            var devices = new List<AudioDeviceInfo>(count);
            for (int i = 0; i < count; i++)
            {
                if (collection.Item(i, out IMMDevice? device) < 0 || device is null)
                {
                    continue;
                }

                try
                {
                    AudioDeviceInfo? info = EndpointProperties.Describe(device, defaultId, defaultCommsId);
                    if (info is not null)
                    {
                        devices.Add(info);
                    }
                }
                finally
                {
                    EndpointProperties.Release(device);
                }
            }

            // Active devices first, then the user's default, then alphabetical: the order a
            // person actually wants to choose from.
            return (IReadOnlyList<AudioDeviceInfo>)devices
                .OrderByDescending(d => d.State == AudioDeviceState.Active)
                .ThenByDescending(d => d.IsDefault)
                .ThenBy(d => d.FriendlyName, StringComparer.CurrentCultureIgnoreCase)
                .ToList();
        }
        finally
        {
            EndpointProperties.Release(collection);
        }
    });

    public AudioDeviceInfo? GetDefaultCaptureDevice() => _worker.Invoke(() =>
    {
        IMMDeviceEnumerator enumerator = GetEnumerator();

        int hr = enumerator.GetDefaultAudioEndpoint(EDataFlow.Capture, ERole.Console, out IMMDevice? device);
        if (hr < 0 || device is null)
        {
            return null;
        }

        try
        {
            return EndpointProperties.Describe(
                device,
                EndpointProperties.TryGetId(device),
                TryGetDefaultId(enumerator, ERole.Communications));
        }
        finally
        {
            EndpointProperties.Release(device);
        }
    });

    public AudioDeviceInfo? GetDevice(string deviceId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(deviceId);

        return _worker.Invoke(() =>
        {
            IMMDeviceEnumerator enumerator = GetEnumerator();

            if (enumerator.GetDevice(deviceId, out IMMDevice? device) < 0 || device is null)
            {
                return null;
            }

            try
            {
                return EndpointProperties.Describe(
                    device,
                    TryGetDefaultId(enumerator, ERole.Console),
                    TryGetDefaultId(enumerator, ERole.Communications));
            }
            finally
            {
                EndpointProperties.Release(device);
            }
        });
    }

    private IMMDeviceEnumerator GetEnumerator()
    {
        if (_enumerator is not null)
        {
            return _enumerator;
        }

        _enumerator = (IMMDeviceEnumerator)new MMDeviceEnumeratorComObject();
        _notifications = new NotificationClient(this);

        if (_enumerator.RegisterEndpointNotificationCallback(_notifications) < 0)
        {
            // Losing change notifications degrades the experience but does not stop Tapit
            // working, so this is not treated as fatal.
            _notifications = null;
        }

        return _enumerator;
    }

    private static string? TryGetDefaultId(IMMDeviceEnumerator enumerator, ERole role)
    {
        if (enumerator.GetDefaultAudioEndpoint(EDataFlow.Capture, role, out IMMDevice? device) < 0 || device is null)
        {
            return null;
        }

        try
        {
            return EndpointProperties.TryGetId(device);
        }
        finally
        {
            EndpointProperties.Release(device);
        }
    }

    private void RaiseDevicesChanged(string? deviceId, string reason)
    {
        // Never block the OS notification thread: hand the event off and return immediately.
        EventHandler<AudioDeviceChangeEventArgs>? handler = DevicesChanged;
        if (handler is null)
        {
            return;
        }

        ThreadPool.UnsafeQueueUserWorkItem(
            static state => state.Handler(state.Sender, new AudioDeviceChangeEventArgs(state.DeviceId, state.Reason)),
            (Handler: handler, Sender: (object)this, DeviceId: deviceId, Reason: reason),
            preferLocal: false);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        try
        {
            _worker.Invoke(() =>
            {
                if (_enumerator is not null && _notifications is not null)
                {
                    _enumerator.UnregisterEndpointNotificationCallback(_notifications);
                }

                EndpointProperties.Release(_enumerator);
                _enumerator = null;
                _notifications = null;
            });
        }
        catch (Exception ex) when (ex is InvalidOperationException or COMException or ObjectDisposedException)
        {
            // Shutdown racing with COM teardown is not worth surfacing.
        }

        _worker.Dispose();
    }

    /// <summary>
    /// Receives endpoint notifications from the audio service. Every method must return
    /// promptly; the OS calls these on its own thread and holds locks while doing so.
    /// </summary>
    private sealed class NotificationClient(WasapiDeviceEnumerator owner) : IMMNotificationClient
    {
        public int OnDeviceStateChanged(string deviceId, uint newState)
        {
            owner.RaiseDevicesChanged(deviceId, $"Device state changed to {EndpointProperties.MapState(newState)}.");
            return WasapiConstants.SOk;
        }

        public int OnDeviceAdded(string deviceId)
        {
            owner.RaiseDevicesChanged(deviceId, "Device added.");
            return WasapiConstants.SOk;
        }

        public int OnDeviceRemoved(string deviceId)
        {
            owner.RaiseDevicesChanged(deviceId, "Device removed.");
            return WasapiConstants.SOk;
        }

        public int OnDefaultDeviceChanged(EDataFlow dataFlow, ERole role, string? defaultDeviceId)
        {
            if (dataFlow == EDataFlow.Capture && role == ERole.Console)
            {
                owner.RaiseDevicesChanged(defaultDeviceId, "Default capture device changed.");
            }

            return WasapiConstants.SOk;
        }

        public int OnPropertyValueChanged(string deviceId, PropertyKey key)
        {
            // An endpoint format change under a calibrated profile is exactly the silent
            // accuracy killer the architecture calls out, so it is worth reporting.
            if (key.FormatId == PropertyKeys.AudioEngineDeviceFormat.FormatId &&
                key.PropertyId == PropertyKeys.AudioEngineDeviceFormat.PropertyId)
            {
                owner.RaiseDevicesChanged(deviceId, "Device audio format changed.");
            }

            return WasapiConstants.SOk;
        }
    }
}
