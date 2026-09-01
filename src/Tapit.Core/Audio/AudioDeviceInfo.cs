namespace Tapit.Core.Audio;

public enum AudioDeviceState
{
    Unknown,
    Active,
    Disabled,
    NotPresent,
    Unplugged,
}

/// <summary>
/// Whether a device is fit for acoustic zone classification, decided before the user spends
/// five minutes calibrating on something that cannot work.
/// </summary>
public enum DeviceSuitability
{
    /// <summary>Wide bandwidth, plausible for zone separation.</summary>
    Good,

    /// <summary>Usable, but something about it will cost accuracy.</summary>
    Marginal,

    /// <summary>Will not produce separable zones; calibration should be refused.</summary>
    Unsuitable,
}

public sealed record DeviceAssessment(DeviceSuitability Suitability, string Reason)
{
    public bool AllowsCalibration => Suitability != DeviceSuitability.Unsuitable;
}

public sealed record AudioDeviceInfo(
    string Id,
    string FriendlyName,
    AudioDeviceState State,
    bool IsDefault,
    bool IsDefaultCommunications,
    AudioFormat? MixFormat,
    string? InterfaceName = null,
    string? FormFactor = null)
{
    public override string ToString() =>
        MixFormat is null ? FriendlyName : $"{FriendlyName} ({MixFormat})";
}

public sealed class AudioDeviceChangeEventArgs(string? deviceId, string reason) : EventArgs
{
    public string? DeviceId { get; } = deviceId;

    public string Reason { get; } = reason;
}

public interface IAudioDeviceEnumerator : IDisposable
{
    IReadOnlyList<AudioDeviceInfo> GetCaptureDevices();

    AudioDeviceInfo? GetDefaultCaptureDevice();

    AudioDeviceInfo? GetDevice(string deviceId);

    /// <summary>Raised on device arrival, removal, state change, or default-device change.</summary>
    event EventHandler<AudioDeviceChangeEventArgs>? DevicesChanged;
}

public static class DeviceSuitabilityCheck
{
    /// <summary>
    /// Below this rate there is no usable content above 8 kHz, which is precisely the band
    /// where impact location shows up most strongly.
    /// </summary>
    public const int MinimumUsableSampleRate = 16000;

    /// <summary>Below this rate zone separation is materially degraded but not hopeless.</summary>
    public const int RecommendedSampleRate = 32000;

    public static DeviceAssessment Evaluate(AudioDeviceInfo device)
    {
        ArgumentNullException.ThrowIfNull(device);

        if (device.State != AudioDeviceState.Active)
        {
            return new DeviceAssessment(DeviceSuitability.Unsuitable, $"Device is {device.State.ToString().ToLowerInvariant()}.");
        }

        AudioFormat? format = device.MixFormat;
        if (format is null)
        {
            return new DeviceAssessment(DeviceSuitability.Marginal, "Device format could not be read.");
        }

        if (format.SampleRate < MinimumUsableSampleRate)
        {
            return new DeviceAssessment(
                DeviceSuitability.Unsuitable,
                $"{format.SampleRate} Hz is too narrow-band for tap classification. " +
                "This is typical of a Bluetooth hands-free connection.");
        }

        // 16 kHz mono is the signature of a headset running the HFP/HSP profile. It is
        // technically usable but the codec destroys exactly the transient detail we need.
        if (format.SampleRate < RecommendedSampleRate)
        {
            return new DeviceAssessment(
                DeviceSuitability.Marginal,
                $"{format.SampleRate} Hz limits usable bandwidth to {format.NyquistHz / 1000.0:0.#} kHz. " +
                "A built-in laptop microphone at 44.1 kHz or higher will separate zones better.");
        }

        return new DeviceAssessment(
            DeviceSuitability.Good,
            $"{format.SampleRate} Hz, {format.Channels} channel{(format.Channels == 1 ? string.Empty : "s")}.");
    }
}
