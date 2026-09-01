using System.Runtime.InteropServices;

namespace Tapit.Audio.Wasapi;

internal enum EDataFlow
{
    Render = 0,
    Capture = 1,
    All = 2,
}

internal enum ERole
{
    Console = 0,
    Multimedia = 1,
    Communications = 2,
}

internal enum AudioClientShareMode
{
    Shared = 0,
    Exclusive = 1,
}

/// <summary>
/// Stream category. Tapit deliberately does <b>not</b> use
/// <c>AudioCategory_Communications</c>: that category invites the OS to apply
/// communications-grade processing and ducking to the stream, which is exactly what we are
/// trying to avoid.
/// </summary>
internal enum AudioStreamCategory
{
    Other = 0,
    ForegroundOnlyMedia = 1,
    Communications = 3,
    Alerts = 4,
    SoundEffects = 5,
    GameEffects = 6,
    GameMedia = 7,
    GameChat = 8,
    Speech = 9,
    Movie = 10,
    Media = 11,
}

[Flags]
internal enum AudioClientStreamOptions
{
    None = 0,

    /// <summary>
    /// Bypasses the APO effect chain - AGC, noise suppression, echo cancellation,
    /// beamforming. This is the single most important flag in the whole capture path.
    /// </summary>
    Raw = 1,

    MatchFormat = 2,
    Ambisonics = 4,
}

internal static class WasapiConstants
{
    public const uint DeviceStateActive = 0x00000001;
    public const uint DeviceStateDisabled = 0x00000002;
    public const uint DeviceStateNotPresent = 0x00000004;
    public const uint DeviceStateUnplugged = 0x00000008;
    public const uint DeviceStateAll = 0x0000000F;

    public const uint ClsCtxAll = 0x17;

    public const uint StreamFlagsCrossProcess = 0x00010000;
    public const uint StreamFlagsLoopback = 0x00020000;
    public const uint StreamFlagsEventCallback = 0x00040000;
    public const uint StreamFlagsNoPersist = 0x00080000;
    public const uint StreamFlagsRateAdjust = 0x00100000;
    public const uint StreamFlagsAutoConvertPcm = 0x80000000;
    public const uint StreamFlagsSrcDefaultQuality = 0x08000000;

    public const uint BufferFlagsDataDiscontinuity = 0x1;
    public const uint BufferFlagsSilent = 0x2;
    public const uint BufferFlagsTimestampError = 0x4;

    public const int SOk = 0;
    public const int SFalse = 1;
    public const int EInvalidArg = unchecked((int)0x80070057);
    public const int ENoInterface = unchecked((int)0x80004002);
    public const int ENotImplemented = unchecked((int)0x80004001);

    public const int AudclntSBufferEmpty = unchecked((int)0x08890001);
    public const int AudclntENotInitialized = unchecked((int)0x88890001);
    public const int AudclntEWrongEndpointType = unchecked((int)0x88890003);
    public const int AudclntEDeviceInvalidated = unchecked((int)0x88890004);
    public const int AudclntENotStopped = unchecked((int)0x88890005);
    public const int AudclntEBufferTooLarge = unchecked((int)0x88890006);
    public const int AudclntEOutOfOrder = unchecked((int)0x88890007);
    public const int AudclntEUnsupportedFormat = unchecked((int)0x88890008);
    public const int AudclntEDeviceInUse = unchecked((int)0x8889000A);
    public const int AudclntEBufferOperationPending = unchecked((int)0x8889000B);
    public const int AudclntEServiceNotRunning = unchecked((int)0x88890010);
    public const int AudclntEEventHandleNotSet = unchecked((int)0x88890014);
    public const int AudclntEIncorrectBufferSize = unchecked((int)0x88890015);
    public const int AudclntEBufferSizeNotAligned = unchecked((int)0x88890019);
    public const int AudclntEBufferSizeError = unchecked((int)0x88890018);
    public const int AudclntEInvalidDevicePeriod = unchecked((int)0x88890020);
    public const int AudclntEResourcesInvalidated = unchecked((int)0x88890026);

    public const ushort WaveFormatPcm = 1;
    public const ushort WaveFormatIeeeFloat = 3;
    public const ushort WaveFormatExtensible = 0xFFFE;

    public const ushort VtEmpty = 0;
    public const ushort VtLpwstr = 31;
    public const ushort VtBlob = 65;
    public const ushort VtUi4 = 19;

    /// <summary>One REFERENCE_TIME tick is 100 ns.</summary>
    public const double ReferenceTimesPerMillisecond = 10_000.0;
}

internal static class WasapiGuids
{
    public static readonly Guid MMDeviceEnumerator = new("BCDE0395-E52F-467C-8E3D-C4579291692E");
    public static readonly Guid IAudioClient = new("1CB9AD4C-DBFA-4C32-B178-C2F568A703B2");
    public static readonly Guid IAudioClient2 = new("726778CD-F60A-4EDA-82DE-E47610CD78AA");
    public static readonly Guid IAudioCaptureClient = new("C8ADBD64-E71E-48A0-A4DE-185C395CD317");

    public static readonly Guid KsDataFormatSubtypePcm = new("00000001-0000-0010-8000-00AA00389B71");
    public static readonly Guid KsDataFormatSubtypeIeeeFloat = new("00000003-0000-0010-8000-00AA00389B71");
}

[StructLayout(LayoutKind.Sequential, Pack = 4)]
internal struct PropertyKey(Guid formatId, int propertyId)
{
    public Guid FormatId = formatId;
    public int PropertyId = propertyId;
}

internal static class PropertyKeys
{
    private static readonly Guid DeviceFmtId = new("a45c254e-df1c-4efd-8020-67d146a850e0");
    private static readonly Guid DeviceInterfaceFmtId = new("026e516e-b814-414b-83cd-856d6fef4822");
    private static readonly Guid AudioEngineFmtId = new("f19f064d-082c-4e27-bc73-6882a1bb8e4c");
    private static readonly Guid AudioEndpointFmtId = new("1da5d803-d492-4edd-8c23-e0c0ffee7f0e");

    /// <summary>PKEY_Device_FriendlyName - "Microphone Array (Realtek Audio)".</summary>
    public static readonly PropertyKey DeviceFriendlyName = new(DeviceFmtId, 14);

    /// <summary>PKEY_Device_DeviceDesc - "Microphone Array".</summary>
    public static readonly PropertyKey DeviceDescription = new(DeviceFmtId, 2);

    /// <summary>PKEY_DeviceInterface_FriendlyName - the adapter, e.g. "Realtek(R) Audio".</summary>
    public static readonly PropertyKey DeviceInterfaceFriendlyName = new(DeviceInterfaceFmtId, 2);

    /// <summary>PKEY_AudioEngine_DeviceFormat - WAVEFORMATEX blob, read without activating.</summary>
    public static readonly PropertyKey AudioEngineDeviceFormat = new(AudioEngineFmtId, 0);

    /// <summary>PKEY_AudioEndpoint_FormFactor - microphone, headset, line level, ...</summary>
    public static readonly PropertyKey AudioEndpointFormFactor = new(AudioEndpointFmtId, 0);
}

/// <summary>PROPVARIANT. On x64 this is 8 bytes of header followed by a 16-byte union.</summary>
[StructLayout(LayoutKind.Sequential)]
internal struct PropVariant
{
    public ushort VarType;
    public ushort Reserved1;
    public ushort Reserved2;
    public ushort Reserved3;
    public IntPtr Value1;
    public IntPtr Value2;

    /// <summary>For VT_BLOB the union holds { ULONG cbSize; BYTE* pBlobData; }.</summary>
    public readonly int BlobSize => (int)(Value1.ToInt64() & 0xFFFFFFFF);

    public readonly IntPtr BlobData => Value2;
}

[StructLayout(LayoutKind.Sequential, Pack = 1)]
internal struct WaveFormatEx
{
    public ushort FormatTag;
    public ushort Channels;
    public uint SamplesPerSecond;
    public uint AverageBytesPerSecond;
    public ushort BlockAlign;
    public ushort BitsPerSample;
    public ushort ExtraSize;
}

[StructLayout(LayoutKind.Sequential, Pack = 1)]
internal struct WaveFormatExtensible
{
    public WaveFormatEx Format;
    public ushort ValidBitsPerSample;
    public uint ChannelMask;
    public Guid SubFormat;
}

/// <summary>
/// AUDIOCLIENT_PROPERTIES, passed to <c>IAudioClient2::SetClientProperties</c> before
/// <c>Initialize</c>. <c>cbSize</c> must be the exact struct size or the call fails.
/// </summary>
[StructLayout(LayoutKind.Sequential)]
internal struct AudioClientProperties
{
    public uint Size;
    public int IsOffload;
    public AudioStreamCategory Category;
    public AudioClientStreamOptions Options;
}

internal static class NativeMethods
{
    /// <summary>
    /// Registers the calling thread with the Multimedia Class Scheduler so the audio thread
    /// is not starved by ordinary UI or background work.
    /// </summary>
    [DllImport("avrt.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "AvSetMmThreadCharacteristicsW")]
    internal static extern IntPtr AvSetMmThreadCharacteristics(string taskName, ref uint taskIndex);

    [DllImport("avrt.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool AvRevertMmThreadCharacteristics(IntPtr handle);

    [DllImport("ole32.dll")]
    internal static extern void CoTaskMemFree(IntPtr ptr);

    [DllImport("ole32.dll")]
    internal static extern int PropVariantClear(ref PropVariant variant);
}

/// <summary>An HRESULT failure from the Windows audio stack, translated to something readable.</summary>
public sealed class WasapiException(int hresult, string operation)
    : Exception($"{operation} failed: {Describe(hresult)} (0x{hresult:X8})")
{
    public int HResult0 { get; } = hresult;

    public string Operation { get; } = operation;

    /// <summary>True for failures that a reconnect can plausibly resolve.</summary>
    public bool IsRecoverable => HResult0 is
        WasapiConstants.AudclntEDeviceInvalidated or
        WasapiConstants.AudclntEResourcesInvalidated or
        WasapiConstants.AudclntEServiceNotRunning or
        WasapiConstants.AudclntEDeviceInUse;

    internal static string Describe(int hresult) => hresult switch
    {
        WasapiConstants.AudclntEDeviceInvalidated =>
            "the audio device was removed, disabled, or its format changed",
        WasapiConstants.AudclntEResourcesInvalidated =>
            "the audio device resources were invalidated",
        WasapiConstants.AudclntEServiceNotRunning =>
            "the Windows Audio service is not running",
        WasapiConstants.AudclntEDeviceInUse =>
            "the device is held exclusively by another application",
        WasapiConstants.AudclntEUnsupportedFormat =>
            "the endpoint does not support the requested format",
        WasapiConstants.AudclntEWrongEndpointType =>
            "the endpoint is not a capture device",
        WasapiConstants.AudclntEBufferSizeNotAligned =>
            "the requested buffer size is not aligned for this device",
        WasapiConstants.AudclntEInvalidDevicePeriod =>
            "the requested device period is invalid",
        WasapiConstants.EInvalidArg => "an argument was rejected by the audio engine",
        unchecked((int)0x80070005) => "access to the microphone was denied - check Windows microphone privacy settings",
        _ => "an audio engine error occurred",
    };

    internal static void ThrowIfFailed(int hresult, string operation)
    {
        if (hresult < 0)
        {
            throw new WasapiException(hresult, operation);
        }
    }
}
