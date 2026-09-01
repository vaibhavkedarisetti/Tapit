namespace Tapit.Audio;

/// <summary>
/// Capture tuning. Defaults are the ones Tapit ships with; everything here is exposed so the
/// values can be measured rather than assumed.
/// </summary>
public sealed class WasapiCaptureOptions
{
    /// <summary>Endpoint ID to capture from. <see langword="null"/> uses the system default.</summary>
    public string? DeviceId { get; set; }

    /// <summary>
    /// Ask the OS for a raw stream, bypassing the APO effect chain (AGC, noise suppression,
    /// echo cancellation, beamforming).
    /// </summary>
    /// <remarks>
    /// This matters more than any other setting in the file. Speech-oriented processing
    /// rescales amplitude with an unknown time-varying gain and spectrally gates exactly the
    /// short broadband transients Tapit classifies. Where the driver refuses raw mode the
    /// stream still works, but <see cref="Tapit.Core.Audio.CaptureStatistics.RawModeActive"/>
    /// reports false and the user is told their accuracy will suffer.
    /// </remarks>
    public bool RequestRawMode { get; set; } = true;

    /// <summary>
    /// If raw mode is refused, fall back to a processed stream. Turning this off makes Tapit
    /// refuse to run on a device it cannot get clean audio from.
    /// </summary>
    public bool AllowProcessedFallback { get; set; } = true;

    /// <summary>
    /// History kept in the ring buffer. Four seconds is far more than the ~90 ms analysis
    /// window needs; the surplus exists so a briefly descheduled DSP thread cannot lose an
    /// event, and so diagnostics can show recent context.
    /// </summary>
    public double RingSeconds { get; set; } = 4.0;

    /// <summary>
    /// Requested engine buffer in milliseconds. Zero lets the audio engine choose its
    /// default period, which on Windows 10/11 shared mode is about 10 ms.
    /// </summary>
    public double RequestedBufferMs { get; set; }

    /// <summary>Register the capture thread with MMCSS under the "Pro Audio" task.</summary>
    public bool UseMmcss { get; set; } = true;

    /// <summary>Reopen the stream automatically when the device is lost.</summary>
    public bool AutoReconnect { get; set; } = true;

    public int InitialReconnectDelayMs { get; set; } = 500;

    public int MaxReconnectDelayMs { get; set; } = 8000;

    /// <summary>
    /// Largest reported capture gap that will be bridged with silence to keep the frame clock
    /// aligned to real time. Longer gaps restart the clock instead, because pushing seconds of
    /// synthetic silence through the detector is worse than admitting the discontinuity.
    /// </summary>
    public double MaxBridgedGapMs { get; set; } = 250.0;

    public WasapiCaptureOptions Clone() => (WasapiCaptureOptions)MemberwiseClone();
}
