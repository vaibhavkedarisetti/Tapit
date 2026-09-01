using System.Runtime.InteropServices;
using Tapit.Core.Audio;

namespace Tapit.Audio.Wasapi;

/// <summary>
/// Reads endpoint metadata out of an <c>IMMDevice</c> property store.
/// </summary>
/// <remarks>
/// Shared by the enumerator and the capture source so both describe a device identically -
/// the capture source has to name its own device without spinning up a second COM apartment.
/// </remarks>
internal static class EndpointProperties
{
    private const uint StgmRead = 0;

    public static string? TryGetId(IMMDevice device) =>
        device.GetId(out string? id) >= 0 ? id : null;

    public static AudioDeviceState GetState(IMMDevice device) =>
        device.GetState(out uint rawState) >= 0 ? MapState(rawState) : AudioDeviceState.Unknown;

    public static AudioDeviceInfo? Describe(IMMDevice device, string? defaultId, string? defaultCommsId)
    {
        string? id = TryGetId(device);
        if (id is null)
        {
            return null;
        }

        AudioDeviceState state = GetState(device);

        string friendlyName = id;
        string? interfaceName = null;
        string? formFactor = null;
        AudioFormat? mixFormat = null;

        if (device.OpenPropertyStore(StgmRead, out IPropertyStore? store) >= 0 && store is not null)
        {
            try
            {
                friendlyName = ReadString(store, PropertyKeys.DeviceFriendlyName)
                               ?? ReadString(store, PropertyKeys.DeviceDescription)
                               ?? id;

                interfaceName = ReadString(store, PropertyKeys.DeviceInterfaceFriendlyName);
                formFactor = MapFormFactor(ReadUInt32(store, PropertyKeys.AudioEndpointFormFactor));
                mixFormat = ReadFormat(store, PropertyKeys.AudioEngineDeviceFormat);
            }
            finally
            {
                Release(store);
            }
        }

        return new AudioDeviceInfo(
            id,
            friendlyName,
            state,
            IsDefault: string.Equals(id, defaultId, StringComparison.OrdinalIgnoreCase),
            IsDefaultCommunications: string.Equals(id, defaultCommsId, StringComparison.OrdinalIgnoreCase),
            mixFormat,
            interfaceName,
            formFactor);
    }

    public static string GetFriendlyName(IMMDevice device)
    {
        string fallback = TryGetId(device) ?? "Unknown device";

        if (device.OpenPropertyStore(StgmRead, out IPropertyStore? store) < 0 || store is null)
        {
            return fallback;
        }

        try
        {
            return ReadString(store, PropertyKeys.DeviceFriendlyName)
                   ?? ReadString(store, PropertyKeys.DeviceDescription)
                   ?? fallback;
        }
        finally
        {
            Release(store);
        }
    }

    public static string? ReadString(IPropertyStore store, PropertyKey key)
    {
        PropertyKey local = key;
        if (store.GetValue(ref local, out PropVariant value) < 0)
        {
            return null;
        }

        try
        {
            return value.VarType == WasapiConstants.VtLpwstr
                ? Marshal.PtrToStringUni(value.Value1)
                : null;
        }
        finally
        {
            NativeMethods.PropVariantClear(ref value);
        }
    }

    public static uint? ReadUInt32(IPropertyStore store, PropertyKey key)
    {
        PropertyKey local = key;
        if (store.GetValue(ref local, out PropVariant value) < 0)
        {
            return null;
        }

        try
        {
            return value.VarType == WasapiConstants.VtUi4
                ? (uint)(value.Value1.ToInt64() & 0xFFFFFFFF)
                : null;
        }
        finally
        {
            NativeMethods.PropVariantClear(ref value);
        }
    }

    public static AudioFormat? ReadFormat(IPropertyStore store, PropertyKey key)
    {
        PropertyKey local = key;
        if (store.GetValue(ref local, out PropVariant value) < 0)
        {
            return null;
        }

        try
        {
            return value.VarType == WasapiConstants.VtBlob
                ? WaveFormatMarshaler.TryReadBlob(value.BlobData, value.BlobSize)
                : null;
        }
        finally
        {
            NativeMethods.PropVariantClear(ref value);
        }
    }

    public static AudioDeviceState MapState(uint state) => state switch
    {
        WasapiConstants.DeviceStateActive => AudioDeviceState.Active,
        WasapiConstants.DeviceStateDisabled => AudioDeviceState.Disabled,
        WasapiConstants.DeviceStateNotPresent => AudioDeviceState.NotPresent,
        WasapiConstants.DeviceStateUnplugged => AudioDeviceState.Unplugged,
        _ => AudioDeviceState.Unknown,
    };

    public static string? MapFormFactor(uint? formFactor) => formFactor switch
    {
        null => null,
        0 => "Remote network device",
        1 => "Speakers",
        2 => "Line level",
        3 => "Headphones",
        4 => "Microphone",
        5 => "Headset",
        6 => "Handset",
        7 => "Unknown digital passthrough",
        8 => "S/PDIF",
        9 => "Digital audio display device",
        _ => "Unknown",
    };

    public static void Release(object? comObject)
    {
        if (comObject is not null && Marshal.IsComObject(comObject))
        {
            Marshal.ReleaseComObject(comObject);
        }
    }
}
