using System.Runtime.InteropServices;
using Tapit.Audio.Wasapi;

namespace Tapit.Audio.Tests;

/// <summary>
/// Struct layout is the part of hand-written COM interop that fails silently.
/// </summary>
/// <remarks>
/// A wrong <c>Pack</c> or a missing union field does not produce a compile error; it
/// produces an <c>E_INVALIDARG</c> on one machine and a corrupted sample rate on another.
/// These assertions pin the binary contract against the Windows headers so a refactor
/// cannot quietly break it.
/// </remarks>
public class InteropLayoutTests
{
    [Fact]
    public void WaveFormatEx_Is18Bytes() =>
        Assert.Equal(18, Marshal.SizeOf<WaveFormatEx>());

    [Fact]
    public void WaveFormatExtensible_Is40Bytes() =>
        Assert.Equal(40, Marshal.SizeOf<WaveFormatExtensible>());

    [Fact]
    public void WaveFormatEx_FieldOffsetsMatchTheHeader()
    {
        Assert.Equal(0, Marshal.OffsetOf<WaveFormatEx>(nameof(WaveFormatEx.FormatTag)).ToInt32());
        Assert.Equal(2, Marshal.OffsetOf<WaveFormatEx>(nameof(WaveFormatEx.Channels)).ToInt32());
        Assert.Equal(4, Marshal.OffsetOf<WaveFormatEx>(nameof(WaveFormatEx.SamplesPerSecond)).ToInt32());
        Assert.Equal(8, Marshal.OffsetOf<WaveFormatEx>(nameof(WaveFormatEx.AverageBytesPerSecond)).ToInt32());
        Assert.Equal(12, Marshal.OffsetOf<WaveFormatEx>(nameof(WaveFormatEx.BlockAlign)).ToInt32());
        Assert.Equal(14, Marshal.OffsetOf<WaveFormatEx>(nameof(WaveFormatEx.BitsPerSample)).ToInt32());
        Assert.Equal(16, Marshal.OffsetOf<WaveFormatEx>(nameof(WaveFormatEx.ExtraSize)).ToInt32());
    }

    [Fact]
    public void WaveFormatExtensible_ExtensionFollowsTheHeaderWithoutPadding()
    {
        Assert.Equal(18, Marshal.OffsetOf<WaveFormatExtensible>(nameof(WaveFormatExtensible.ValidBitsPerSample)).ToInt32());
        Assert.Equal(20, Marshal.OffsetOf<WaveFormatExtensible>(nameof(WaveFormatExtensible.ChannelMask)).ToInt32());
        Assert.Equal(24, Marshal.OffsetOf<WaveFormatExtensible>(nameof(WaveFormatExtensible.SubFormat)).ToInt32());
    }

    [Fact]
    public void AudioClientProperties_MatchesTheSizeSetClientPropertiesExpects()
    {
        // cbSize must equal the exact struct size or IAudioClient2::SetClientProperties
        // rejects the call - which would silently cost us raw mode.
        Assert.Equal(16, Marshal.SizeOf<AudioClientProperties>());
    }

    [Fact]
    public void PropVariant_Is24BytesOnX64()
    {
        Assert.Equal(8, IntPtr.Size);
        Assert.Equal(24, Marshal.SizeOf<PropVariant>());
        Assert.Equal(0, Marshal.OffsetOf<PropVariant>(nameof(PropVariant.VarType)).ToInt32());
        Assert.Equal(8, Marshal.OffsetOf<PropVariant>(nameof(PropVariant.Value1)).ToInt32());
        Assert.Equal(16, Marshal.OffsetOf<PropVariant>(nameof(PropVariant.Value2)).ToInt32());
    }

    [Fact]
    public void PropertyKey_Is20Bytes()
    {
        Assert.Equal(20, Marshal.SizeOf<PropertyKey>());
        Assert.Equal(16, Marshal.OffsetOf<PropertyKey>(nameof(PropertyKey.PropertyId)).ToInt32());
    }

    [Fact]
    public void KnownSubFormatGuids_MatchKsMedia()
    {
        Assert.Equal(new Guid("00000001-0000-0010-8000-00AA00389B71"), WasapiGuids.KsDataFormatSubtypePcm);
        Assert.Equal(new Guid("00000003-0000-0010-8000-00AA00389B71"), WasapiGuids.KsDataFormatSubtypeIeeeFloat);
    }

    [Fact]
    public void RawStreamOption_IsBitOne() =>
        Assert.Equal(1, (int)AudioClientStreamOptions.Raw);

    [Fact]
    public void StreamCategory_OtherIsZero()
    {
        // Deliberately not Communications (3): that category invites OS-side speech
        // processing and ducking, which is what raw mode exists to avoid.
        Assert.Equal(0, (int)AudioStreamCategory.Other);
        Assert.Equal(3, (int)AudioStreamCategory.Communications);
    }

    [Fact]
    public void BlobAccessors_ReadTheUnionCorrectly()
    {
        var variant = new PropVariant
        {
            VarType = WasapiConstants.VtBlob,
            Value1 = new IntPtr(1234),
            Value2 = new IntPtr(0x5000),
        };

        Assert.Equal(1234, variant.BlobSize);
        Assert.Equal(new IntPtr(0x5000), variant.BlobData);
    }
}

public class WasapiExceptionTests
{
    [Theory]
    [InlineData(WasapiConstants.AudclntEDeviceInvalidated)]
    [InlineData(WasapiConstants.AudclntEResourcesInvalidated)]
    [InlineData(WasapiConstants.AudclntEServiceNotRunning)]
    [InlineData(WasapiConstants.AudclntEDeviceInUse)]
    public void TransientFailures_AreMarkedRecoverable(int hresult) =>
        Assert.True(new WasapiException(hresult, "test").IsRecoverable);

    [Theory]
    [InlineData(WasapiConstants.AudclntEUnsupportedFormat)]
    [InlineData(WasapiConstants.EInvalidArg)]
    [InlineData(WasapiConstants.AudclntEWrongEndpointType)]
    public void PermanentFailures_AreNotRecoverable(int hresult) =>
        Assert.False(new WasapiException(hresult, "test").IsRecoverable);

    [Fact]
    public void Message_ExplainsTheFailureInPlainLanguage()
    {
        var ex = new WasapiException(WasapiConstants.AudclntEDeviceInvalidated, "IAudioClient::Initialize");

        Assert.Contains("IAudioClient::Initialize", ex.Message, StringComparison.Ordinal);
        Assert.Contains("removed", ex.Message, StringComparison.Ordinal);
        Assert.Contains("0x88890004", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AccessDenied_PointsAtMicrophonePrivacySettings()
    {
        var ex = new WasapiException(unchecked((int)0x80070005), "IMMDevice::Activate");

        Assert.Contains("privacy", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ThrowIfFailed_IgnoresSuccessCodes()
    {
        WasapiException.ThrowIfFailed(WasapiConstants.SOk, "ok");
        WasapiException.ThrowIfFailed(WasapiConstants.SFalse, "s_false");
        WasapiException.ThrowIfFailed(WasapiConstants.AudclntSBufferEmpty, "buffer empty");
    }

    [Fact]
    public void ThrowIfFailed_ThrowsOnFailureCodes() =>
        Assert.Throws<WasapiException>(() =>
            WasapiException.ThrowIfFailed(WasapiConstants.AudclntEUnsupportedFormat, "boom"));
}
