namespace Tapit.Core.Detection;

/// <summary>
/// Tunable detector parameters.
/// </summary>
/// <remarks>
/// Every value here is a starting hypothesis, not a constant. They are exposed so they can
/// be swept over recorded WAV corpora with the replay path and chosen from measurement.
/// The defaults are deliberately conservative: a missed tap costs one repeat, a false one
/// fires the wrong action.
/// </remarks>
public sealed class DetectorOptions
{
    /// <summary>Total analysis window around an onset.</summary>
    public double WindowMs { get; set; } = 90.0;

    /// <summary>
    /// How much of the window sits before the detected onset. An energy detector fires a
    /// frame or two after the physical strike; without pre-roll the sharpest part of the
    /// transient - the leading edge - would be cut off.
    /// </summary>
    public double PreRollMs { get; set; } = 12.0;

    /// <summary>Envelope frame size. Also the onset time resolution.</summary>
    public double FrameMs { get; set; } = 1.0;

    /// <summary>How far a frame must rise above the noise floor to be an onset.</summary>
    public double OnsetThresholdDb { get; set; } = 12.0;

    /// <summary>
    /// Absolute gate. Without it, a very quiet room drives the noise floor so low that any
    /// faint sound clears the relative threshold.
    /// </summary>
    public double MinOnsetDbfs { get; set; } = -55.0;

    /// <summary>
    /// Required jump in level from the previous frame.
    /// </summary>
    /// <remarks>
    /// Measured on the development machine: with a level gate alone, ambient room noise
    /// produced 62 candidates in 14 seconds (all correctly rejected later, but each one
    /// opened a refractory window that would have swallowed a real tap). An impact rises
    /// almost vertically; room noise drifts. Requiring a frame-to-frame jump costs one
    /// comparison and removes nearly all of them.
    /// </remarks>
    public double MinRiseDb { get; set; } = 9.0;

    /// <summary>One physical tap must not become several events.</summary>
    public double RefractoryMs { get; set; } = 180.0;

    /// <summary>Quiet period at startup used to seed the noise floor before detecting.</summary>
    public double RoomLearnSeconds { get; set; } = 0.75;

    /// <summary>Noise-floor tracking: falls quickly toward quiet, rises slowly.</summary>
    public double NoiseFallMs { get; set; } = 50.0;

    public double NoiseRiseMs { get; set; } = 1500.0;

    // ---- validation gates -------------------------------------------------------------

    /// <summary>Window peak must reach this, or the event is too weak to classify.</summary>
    public double MinPeakDbfs { get; set; } = -48.0;

    /// <summary>
    /// Window peak must exceed the noise floor by this much.
    /// </summary>
    /// <remarks>
    /// Measured over 42 real events: every genuine tap came in at 34 dB SNR or better, while
    /// the ringing tails and room noise sat below 25 dB. This turns out to be the cleanest
    /// single separator in the whole gate set - far sharper than the envelope-shape gates,
    /// which overlap heavily between real taps and their own decay.
    /// </remarks>
    public double MinSnrDb { get; set; } = 25.0;

    public float ClipThreshold { get; set; } = 0.98f;

    public double MaxClippedFraction { get; set; } = 0.01;

    /// <summary>
    /// Impacts rise fast; anything slower is not a strike.
    /// </summary>
    /// <remarks>
    /// Raised from 10 ms after real measurement. On a ringing surface the envelope's global
    /// peak can land in the resonance rather than on the initial strike, which stretches the
    /// measured 10-to-90 % rise well past the true attack. Genuine hard taps measured up to
    /// ~21 ms this way. The SNR gate now does the heavy lifting instead.
    /// </remarks>
    public double MaxAttackMs { get; set; } = 20.0;

    /// <summary>
    /// Fraction of the peak the envelope must fall below to be considered finished.
    /// </summary>
    /// <remarks>
    /// Measured on a real desk: at 10 % this gate rejected every genuine tap. A struck
    /// table <i>rings</i> - low-frequency resonance decays over 100 ms or more - so the
    /// envelope never drops that low inside a 90 ms window, duration pins at the window
    /// length, and the gate fires on everything. (The synthetic fixture that validated the
    /// original value used an 8 ms decay constant, far deader than any real surface.)
    /// At 25 % a decaying impact crosses quickly while a sustained sound still never does.
    /// </remarks>
    public double DurationFractionOfPeak { get; set; } = 0.25;

    /// <summary>
    /// Energy must be temporally compact. Speech, fans and music stay loud for the whole
    /// window; a tap decays, however long its tail rings on.
    /// </summary>
    /// <remarks>
    /// 65 ms cut straight through the middle of the real distribution - accepted taps topped
    /// out at 64.5 ms and plainly genuine ones ran to 81 ms. Widened to sit above the real
    /// population rather than inside it.
    /// </remarks>
    public double MaxEffectiveDurationMs { get; set; } = 78.0;

    /// <summary>
    /// Fraction of window energy that must land in the first half. This is the stronger
    /// sustained-sound discriminator of the two: an impact measures ~0.9+, a steady tone
    /// sits at ~0.5 by construction.
    /// </summary>
    public double MinEarlyEnergyFraction { get; set; } = 0.60;

    /// <summary>Peak-to-RMS ratio floor. Sustained sound is flat; an impact is peaky.</summary>
    public double MinCrestFactorDb { get; set; } = 6.0;

    /// <summary>DC blocker corner. Raw WASAPI capture has no high-pass in front of it.</summary>
    public double DcBlockerHz { get; set; } = 20.0;

    public DetectorOptions Clone() => (DetectorOptions)MemberwiseClone();
}
