# Tapit - Windows Acoustic Desk Input

**Phase 0 architecture document.**
Tapit turns the desk surface around a laptop into four acoustic input zones. The user
taps the desk; Tapit decides *which* zone was struck and runs the action bound to it.

Everything is deterministic signal processing and classical statistics.

> **No AI. No LLM. No neural networks. No embeddings. No cloud. No telemetry.
> No computer vision. No remote processing. No raw audio persistence by default.**

---

## 1. Scope and honest framing

Tapit is inspired by [Holo](https://github.com/JustinGamer191/Holo) (macOS). It is **not**
a port. The concept - a desk is a mechanical resonator, and impact location changes the
transfer function between impact point and microphone - is reused. The implementation,
the parameter choices, and several of the engineering decisions are Windows-specific and
deliberately different (§4).

The physics Tapit relies on:

* A desk struck at point *P* radiates a short broadband impulse.
* The path from *P* to the microphone applies a location-dependent filter: direct
  airborne path length, structure-borne propagation through the desk, chassis coupling
  through the laptop feet, and modal excitation of the panel all vary with *P*.
* Four zones that are far apart, on a rigid surface, with a fixed laptop position,
  therefore produce measurably different short-time spectra.

The physics Tapit *cannot* overcome:

* A soft, damped, wobbly, thickly-padded, or very large desk may not separate the zones
  at all. Neither will a setup where the laptop moves between taps.
* Every profile is bound to **one laptop, one surface, one laptop position, one room**.
  Move any of them and the profile is invalid.
* Typing, mouse clicks, laptop-chassis touches, dropped objects, plosive consonants and
  nearby impacts genuinely resemble desk taps. Rejection is a first-class feature, not
  an afterthought.

Accuracy is therefore **measured on the user's real setup** (§10), never inferred from
synthetic tests. Synthetic tests prove the code is correct; only a held-out session on
the real desk says anything about the device.

Until a user has run a held-out evaluation on their own desk, Tapit should be described
as *functional experimental software*, not a proven input device.

---

## 2. Pipeline

```text
                         ┌──────────────────────────────┐
   REALTIME THREAD       │  WASAPI shared-mode capture  │   MMCSS "Pro Audio"
   (never blocks)        │  event-driven, RAW if avail. │   no alloc / no lock
                         └──────────────┬───────────────┘
                                        │  interleaved device frames
                                        ▼
                         ┌──────────────────────────────┐
                         │  Sample conversion           │  int16/24/32, float32,
                         │  → float32 planar + mono mix │  WAVE_FORMAT_EXTENSIBLE
                         └──────────────┬───────────────┘
                                        ▼
                         ┌──────────────────────────────┐
                         │  SPSC ring buffer (~4 s)     │  absolute sample index
                         │  lock-free, pre-allocated    │  = the only shared state
                         └──────────────┬───────────────┘
   ─────────────────────────────────────┼──────────────────────────────────────
                                        ▼
   DSP THREAD            ┌──────────────────────────────┐
   (soft realtime)       │  Adaptive noise estimation   │  Phase 2
                         └──────────────┬───────────────┘
                                        ▼
                         ┌──────────────────────────────┐
                         │  Onset detector + refractory │  Phase 2
                         └──────────────┬───────────────┘
                                        ▼
                         ┌──────────────────────────────┐
                         │  Candidate window            │  ~90 ms, incl. pre-roll
                         │  (pulled back out of ring)   │  Phase 2
                         └──────────────┬───────────────┘
                                        ▼
                         ┌──────────────────────────────┐
                         │  Impact validation gate      │  Phase 2
                         │  clip / weak / sustained     │
                         └──────────────┬───────────────┘
                                        ▼
                         ┌──────────────────────────────┐
                         │  Feature extraction          │  Phase 3
                         │  time · spectral · (spatial) │
                         └──────────────┬───────────────┘
                                        ▼
                         ┌──────────────────────────────┐
                         │  Zone classifier             │  Phase 5
                         │  linear / kNN, trained local │
                         └──────────────┬───────────────┘
                                        ▼
                         ┌──────────────────────────────┐
                         │  Rejection stack             │  Phase 5
                         │  confidence · ambiguity ·    │
                         │  novelty · quality           │
                         └──────────────┬───────────────┘
                                        │  ACCEPTED only
   ─────────────────────────────────────┼──────────────────────────────────────
                                        ▼
   ACTION THREAD         ┌──────────────────────────────┐
   (may block)           │  Action dispatcher           │  Phase 7
                         │  bounded queue, coalescing   │
                         └──────────────────────────────┘
```

Rejected events **never** reach the dispatcher. There is no code path from a rejected
event to an `IAction`.

---

## 3. Threading and realtime contract

| Thread | Priority | Allowed to | Must never |
|---|---|---|---|
| **Capture** | MMCSS *Pro Audio* | Copy + convert samples, advance ring write index, increment counters | Allocate, lock, log, touch UI, run DSP, execute actions, do I/O |
| **DSP** | `AboveNormal` | Detect, extract features, classify, enqueue accepted events | Block on UI, block on disk, execute actions |
| **Action** | `Normal` | Launch processes, PowerShell, clipboard, screenshots, media keys | Feed back into the audio path |
| **UI** | UI | Poll immutable snapshots at ~30 Hz | Be on the critical path of anything above |

Rules enforced by design, not by convention:

* The **only** memory shared between capture and DSP is the ring buffer plus a small set
  of `Interlocked`/`volatile` counters. No locks are taken on the capture thread, ever.
* All capture-side buffers are allocated once, at stream start, and reused.
* Every queue between stages is **bounded**. Overflow drops the *oldest* event and
  increments a visible counter - it never grows without limit and never blocks upstream.
* Diagnostics are read via a lock-free snapshot struct written by the producer and copied
  by the reader; a torn read costs a stale number, not a stall.

### Latency budget (target)

| Segment | Target |
|---|---|
| Acoustic onset → sample in ring | ≤ 20 ms (WASAPI shared-mode period ≈ 10 ms) |
| Onset detected → full 90 ms window available | ≈ 80 ms (window tail must arrive) |
| Feature extraction | < 3 ms |
| Classification + rejection | < 1 ms |
| Dispatch → action begins | < 10 ms |
| **Total, onset → action** | **< 200 ms median** |

The dominant, irreducible term is waiting for the analysis window tail. That is why the
window length is a real engineering trade (§6), not a cosmetic constant.

---

## 4. Windows-specific decisions

These are the places where Tapit deliberately diverges from the macOS reference.

### 4.1 Signal-processing bypass is mandatory, not optional

This is the single biggest Windows-specific risk and the main reason a naive port fails.

Windows capture streams routinely pass through an **APO** (Audio Processing Object) chain
before the application sees them: OEM enhancements (Realtek / Waves / Dolby / Nahimic),
the Windows "Audio Enhancements" toggle, AGC, noise suppression, echo cancellation, and
on newer machines Windows Studio Effects "Voice Focus". These are tuned for speech and
are *actively hostile* to this application:

* **AGC** rescales the very amplitude the classifier depends on, with an unknown,
  time-varying, non-linear gain.
* **Noise suppression** is a spectral gate. A 90 ms broadband transient looks exactly
  like the noise it is built to remove - it will be attenuated, and attenuated
  *differently* depending on preceding content.
* **Beamforming / array downmix** collapses a microphone array into a mono speech
  channel, destroying the inter-channel information of §7.

Tapit therefore requests a raw stream via `IAudioClient2::SetClientProperties` with
`AUDCLNT_STREAMOPTIONS_RAW`, and sets the stream category to `AudioCategory_Other` so the
OS does not apply communications-grade ducking.

`RAW` is a *request*. When it is unavailable Tapit still runs, but Diagnostics states
plainly that processing could not be bypassed, and that calibration must be redone after
any change to the enhancement settings. This is surfaced as a first-class health signal,
not buried.

**This is measured, not theorised.** On the Phase 1 test machine (built-in Realtek array),
the same microphone over 5-second runs minutes apart:

| | processed | raw |
|---|---|---|
| AC RMS | **−120 dBFS - exact digital silence** | −30.2 dBFS |
| Crest factor | 0.0 dB | 10.2 dB |
| Negotiated format | Float32 | Float32 |

With processing engaged the endpoint returned *zeros* for ambient room sound. A desk tap is
exactly the short broadband non-speech event a speech noise gate exists to remove, so on
that hardware a Tapit without raw mode would detect nothing whatsoever. Full numbers and
method in [docs/PHASE1-MEASUREMENTS.md](docs/PHASE1-MEASUREMENTS.md).

Two consequences fall out of the same experiment:

* **Order matters.** That endpoint advertises Int16 in its property store but returns
  Float32 once raw mode is set, so `SetClientProperties` must run *before* `GetMixFormat`.
  Reading the format first would decode the stream at the wrong sample width.
* **Raw audio has no high-pass.** Bypassing the APO chain also bypasses its DC blocking; the
  raw stream carries a stable ~0.007 (−42 dBFS) offset. The detector must remove DC before
  computing any amplitude or envelope feature (§8).

### 4.2 Shared mode, event-driven - not exclusive mode

Exclusive mode gives lower latency but seizes the device: no Teams call, no browser mic,
no voice chat while Tapit runs. A background utility that silently steals the microphone
is unacceptable. Tapit uses **shared mode, event-driven**
(`AUDCLNT_STREAMFLAGS_EVENTCALLBACK`), which on Windows 10/11 yields a ~10 ms period -
comfortably inside the budget.

### 4.3 The device dictates the format

In shared mode the mix format is not negotiable; `GetMixFormat` returns what the engine
uses, typically 32-bit float, 48 kHz, 1-2 channels (16 kHz mono on some webcam and
Bluetooth HFP devices).

* Tapit **accepts the native rate** rather than forcing a resample. Desk transients carry
  discriminative energy well above 8 kHz; downsampling to a "nice" rate throws away the
  most location-sensitive part of the spectrum.
* All DSP parameters are expressed in **milliseconds and Hz**, then converted to samples
  at runtime. Nothing in the codebase assumes 48 kHz.
* Devices reporting < 16 kHz, or a Bluetooth HFP profile, are flagged as unsuitable
  before the user wastes time calibrating on them.
* A profile records the exact format it was calibrated at. Changing sample rate, channel
  count, or the RAW-bypass state **invalidates the profile**, and the user is told why,
  instead of silently getting bad classification.

### 4.4 Device lifecycle is hostile

USB mics vanish, Bluetooth headsets reconnect, and the default device changes when a
headset is plugged in mid-session. Tapit registers an `IMMNotificationClient` and handles
`OnDefaultDeviceChanged`, `OnDeviceStateChanged`, `OnDeviceAdded`, `OnDeviceRemoved`.
The state machine - `Stopped → Starting → Running → Faulted → Reconnecting` - retries with
backoff, and any format or identity change surfaces as a profile-mismatch warning rather
than a silent accuracy collapse.

`AUDCLNT_E_DEVICE_INVALIDATED` from `GetBuffer` is treated as expected, not exceptional.

### 4.5 Direct WASAPI interop, no third-party audio library

`Tapit.Audio` talks to WASAPI through hand-written COM interop. Reasons: `IAudioClient2`
RAW-mode client properties, glitch counters, MMCSS registration and precise QPC
timestamping are all needed and are variably exposed by wrappers; and the realtime path
must be free of allocations we do not control. The cost is ~900 lines of interop, written
once, in `Tapit.Audio/Wasapi/`.

### 4.6 WinForms shell, not WinUI 3

The original plan was WinUI 3 / Windows App SDK. It was tried and abandoned, and the reasons
are worth recording rather than hiding.

The Windows App SDK package restores, but on .NET 8 its self-contained unpackaged
configuration drags in the legacy `win10-*` RID graph and fails resolving runtime packs for
RIDs Tapit does not target; the non-self-contained path builds, but then needs the Windows
App Runtime installed separately before the app will launch at all.

Against that, WinForms:

* has a real `NotifyIcon`. WinUI 3 has no tray support whatsoever - a tray utility written
  in it needs `Shell_NotifyIcon` interop regardless, so the "native" framework would have
  bought nothing for the one UI feature this application most depends on;
* builds and runs with no additional runtime install;
* was already a dependency, because `Tapit.Actions` needs `Clipboard` and screen capture.

The visual language (§ the Views layer) still follows the precision-utility brief: one
accent colour, monospaced numerics, no gradients, and exactly one animation - the tap flash.
This is a deviation from the original specification, made deliberately, for a runnable
deliverable over a nominally more modern one.

### 4.7 Timestamps come from QPC, not from a stopwatch

`IAudioCaptureClient::GetBuffer` returns a QPC position in 100 ns units for the first
frame of each packet. Tapit anchors every sample's absolute index to that, so
"onset → action" latency is measured against the acoustic event, not against the moment a
managed thread happened to wake up. `AUDCLNT_BUFFERFLAGS_DATA_DISCONTINUITY` marks a
break in that mapping and invalidates any window straddling it.

---

## 5. Ring buffer and the absolute sample clock

A single-producer / single-consumer float ring, power-of-two capacity, holding **4 s** of
audio per channel plus a mono mixdown.

The buffer is addressed by a **monotonically increasing 64-bit absolute sample index**,
not by a wrapped offset. That gives three properties the detector needs:

1. **Pre-roll.** An onset detected at index *n* needs samples from *n − preRoll*. Absolute
   indexing makes "read the window around this event" a total function.
2. **Overrun detection is exact.** If the consumer asks for a range whose start has already
   been overwritten (`readIndex < writeIndex − capacity`), the read fails loudly and the
   event is dropped and counted, instead of returning silently corrupt audio.
3. **Deterministic replay.** The offline replay tool feeds the identical ring with the
   identical indices, so a WAV file produces bit-identical detector behaviour to a live
   microphone. This is what makes DSP work repeatable (§11).

### Two published indices

Capture publishes **two** indices, and the reason is a defect the Phase 1 stress test
actually caught rather than a hypothetical.

A producer copies a packet's samples into their slots *before* it publishes the new write
index. During that window a single published index still advertises the oldest `capacity`
frames as readable - but the slots at the tail alias the frames being written and have
already been overwritten. A consumer positioned at the tail reads data exactly one lap in
the future, and every validity check passes. Measured delta on the failure: exactly
`+capacity`.

So the producer publishes `ReserveIndex` - where the in-flight write will end - *before*
touching any samples, and `WriteIndex` after. The readable window is

```text
[ ReserveIndex - Capacity , WriteIndex )
```

which stays honest while a write is in progress. Usable history is the capacity minus one
in-flight packet. Reads re-check the reserve index after copying, so a consumer lapped
mid-copy is refused rather than handed a window stitched from two different moments.

That distinction matters more than it looks: a *dropped* window is a missed tap, but a
*silently torn* one is audio from two different instants that the classifier would label
and act on. Refused reads are therefore counted and surfaced, never papered over.

---

## 6. Analysis window

Default **90 ms**, split as **12 ms pre-roll + 78 ms post-onset**, and configurable
throughout (`DetectorOptions.WindowMs`, `PreRollMs`).

Rationale, and why the value is treated as an experiment rather than a constant:

* The pre-roll captures the true attack. Onset detectors fire a few frames *after* the
  physical strike; without pre-roll the sharpest, most location-sensitive part of the
  transient - the leading edge - is truncated.
* 78 ms of tail is enough to observe the decay envelope and early room reflections, which
  is where zone separation lives, while staying inside the 200 ms budget.
* Longer windows improve frequency resolution but add latency and admit more competing
  sound.

The macOS reference uses a fixed 90 ms window. Tapit starts there, but the value is plumbed
as a tunable, and the replay tool (§11) exists specifically so window length can be swept
over recorded corpora and chosen from measurement. **We do not adopt 90 ms because Holo
did; we adopt it as the starting hypothesis.**

Preprocessing is deliberately minimal - no aggressive filtering, no normalisation that
would erase the amplitude and spectral-tilt cues the classifier needs. Preserve the event;
let the feature stage decide what matters.

---

## 7. Multichannel

Some Windows microphones expose 2-4 channels. Tapit will use them **if and only if** they
carry information, and never assumes they are physically independent microphones.

* In non-RAW mode, a "stereo" laptop mic is very often one beamformed signal duplicated, or
  a fixed matrix mix. Inter-channel features would then be pure noise.
* Tapit measures this instead of assuming: at calibration it computes inter-channel
  correlation and energy-difference variance. If the channels are effectively degenerate
  (correlation ≈ 1.0 with near-constant delay), spatial features are dropped from the
  feature vector and the UI says so.
* Where channels *are* independent, candidate features are inter-channel energy ratio,
  onset time difference (sub-sample, via cross-correlation interpolation), spectral
  difference, and coherence.

**The application is fully functional on an ordinary single-channel microphone.** Spatial
features are a bonus tier, never a requirement.

---

## 8. Features (Phase 3)

Computed on the mono mixdown, plus per-channel where §7 permits. All features are
finite-checked; a non-finite feature invalidates the event rather than poisoning the model.

**DC removal comes first.** Raw capture is not high-pass filtered (§4.1), and a constant
offset inflates RMS, drags crest factor toward 1.0, and biases every envelope measurement.
`SignalLevels` already separates the mean from the AC component so the distinction is
visible at the measurement layer; the detector high-passes ahead of envelope extraction.

**Time domain** - RMS, peak, crest factor, attack time (10→90 % of peak envelope), decay
time (peak → −20 dB), zero-crossing rate, effective impulse duration, and temporal energy
distribution across sub-windows (early / mid / late energy ratios).

**Frequency domain** - one windowed FFT over the event (Hann, zero-padded to the next power
of two): spectral centroid, bandwidth, rolloff (85 % / 95 %), flatness (Wiener entropy),
band energies on a log-spaced band set spanning 100 Hz → Nyquist, and dominant peak
frequencies with their prominences.

**Optional tier** - log-mel band energies and MFCC-style cepstral coefficients via DCT-II.
These are ordinary DSP; there is no learned model anywhere in this stage.

**Selection.** Features earn their place. Each candidate is scored by leave-one-out accuracy
contribution on real calibration data, and features that do not improve separation are
removed. A smaller vector on 40 samples is not a stylistic preference - it is what keeps a
classifier trained on 10 examples per class from overfitting.

All features are standardised (z-scored) using statistics stored **in the profile**, so
scaling at inference exactly matches training.

---

## 9. Classification and rejection (Phase 5)

Trained locally, from the user's own calibration taps. Nothing is pre-trained, downloaded,
or shared.

Implemented and compared on the same held-out data:

1. Nearest-neighbour (baseline, and the source of the novelty distance)
2. k-nearest-neighbour
3. Multinomial logistic regression (L2-regularised)
4. Regularised linear discriminant / ridge classifier

With 10 samples per class, a **regularised linear model** is the expected winner and is the
default; kNN is retained because its nearest-example distance is required by the novelty
gate regardless of which classifier decides the label.

### The rejection stack

An event must clear **every** gate to become an action:

| Gate | Rejects when | Message |
|---|---|---|
| **Quality** | clipped, below SNR floor, sustained-sound shape, discontinuity in window | "Signal too weak" / "Clipped" / "Sustained sound" |
| **Confidence** | top-class probability < threshold | "Not confident" |
| **Ambiguity** | margin between top two classes too small | "Ambiguous between two zones" |
| **Novelty** | distance to nearest calibration example above learned percentile | "Doesn't match calibration" |
| **Negative model** | closer to a collected non-tap example than to any zone | "Looks like typing / speech" |

Thresholds are derived from the user's own calibration distribution (percentiles of the
leave-one-out score distribution), not hard-coded magic numbers.

> **Design philosophy: it is better to miss a tap than to fire the wrong action.**
> A missed tap costs one repeat. A wrong action can close a window, skip a track during a
> call, or run a command. The defaults are tuned conservative and the user can loosen them.

---

## 10. Calibration and evaluation

**Calibration** - 10 accepted taps × 4 zones = 40. Guided, one zone at a time, with undo /
retry-zone / pause / resume / cancel. Only *accepted* events count; sounds arriving while
the collector is not armed are discarded, so a cough between prompts cannot become training
data. Post-calibration, leave-one-out cross-validation reports per-zone agreement and flags
weak zones.

Leave-one-out agreement is a **calibration diagnostic**: it says the samples are
self-consistent. It does **not** measure real-world accuracy, and the UI says so in those
words.

**Evaluation** - a separate, held-out session: 15 taps × 4 zones = 60. This data is **never**
used for training, threshold fitting, or feature selection. It reports overall accuracy,
per-zone accuracy, a 4×4 confusion matrix, rejection counts and reasons, mean confidence,
and median / p95 latency.

Engineering targets - **≥ 80 % overall accuracy, < 200 ms median latency** - are targets, not
guarantees. The number that matters is the one the user measures on their own desk.

---

## 11. Offline replay (`Tapit.AudioReplay`)

A console tool that feeds WAV files through *the identical* detector → features →
classifier → rejection code the live app uses, by substituting the audio source behind
`IAudioCaptureSource`. No microphone, no UI, no timing jitter.

It supports batch directories, per-event feature dumps (CSV / JSON), classification and
rejection reports, parameter sweeps, and latency accounting in simulated time. This is what
turns DSP tuning from anecdote into measurement - every parameter in §6 and §9 is meant to
be chosen with this tool over a recorded corpus, not guessed.

---

## 12. Privacy

Local-first, by construction:

```text
microphone → DSP → features → classification → audio discarded
```

* No network stack is used for any application purpose. There is no telemetry, no crash
  reporting, no update ping, no analytics.
* Profiles store **feature vectors and model parameters**, never audio.
* Raw WAV capture exists solely for developing the DSP. It is **off by default**, requires an
  explicit opt-in, writes only to a user-chosen folder, and displays a persistent recording
  indicator in the window and the tray icon while active.
* Pausing the microphone from the tray genuinely stops the WASAPI stream, so the OS
  microphone-in-use indicator goes out. Microphone state is always visible.

---

## 13. Project layout

```text
Tapit/
├── ARCHITECTURE.md            ← this document
├── README.md
├── Tapit.sln
├── src/
│   ├── Tapit.Core/            net8.0, portable, no Windows deps - unit-testable
│   │   ├── Audio/             formats, ring buffer, conversion, mixing, source interfaces
│   │   ├── Detection/         noise estimation, onset, validation      (Phase 2)
│   │   ├── DSP/               FFT, windows, envelopes, filters         (Phase 3)
│   │   ├── Features/          extractors, feature vector, scaling      (Phase 3)
│   │   ├── Classification/    kNN, logistic regression, rejection      (Phase 5)
│   │   ├── Calibration/       session state machine, quality gates     (Phase 4)
│   │   ├── Evaluation/        held-out runs, confusion matrix, latency (Phase 6)
│   │   └── Profiles/          persistence, schema, versioning
│   ├── Tapit.Audio/           net8.0-windows - WASAPI interop + capture service
│   │   └── Wasapi/
│   ├── Tapit.Actions/         net8.0-windows - IAction implementations (Phase 7)
│   └── Tapit.App/             WinUI 3 / Windows App SDK               (Phase 8-9)
│       ├── Views/  ViewModels/  Services/
├── tests/
│   ├── Tapit.Core.Tests/  Tapit.Audio.Tests/  Tapit.Actions.Tests/
├── tools/
│   ├── Tapit.AudioReplay/     offline WAV → pipeline harness
│   └── Tapit.MicCheck/        Phase 1 verification console
└── docs/
```

`Tapit.Core` has **no** reference to `Tapit.Audio`, `Tapit.Actions`, or `Tapit.App`.
Dependencies point inward only. The DSP engine never learns that a UI exists.

---

## 14. Persistence

`%LOCALAPPDATA%\Tapit\`

```text
profiles\<profile-id>\profile.json        device binding, options, zone→action map
                     \samples.json        calibration feature vectors + labels
                     \model.json          trained parameters, scaler, thresholds
                     \evaluations\*.json  held-out run history
settings.json                              global settings, active profile
logs\                                      local diagnostic logs, rotated, opt-in
```

Human-readable JSON with an explicit `schemaVersion`. A profile whose device binding, audio
format, or RAW-bypass state no longer matches reality is loaded read-only and the user is
told exactly what changed and that recalibration is required.

---

## 15. Phase plan and status

| Phase | Deliverable | Status |
|---|---|---|
| 0 | Reference study + this document | **Done** |
| 1 | WASAPI capture, ring buffer, device lifecycle, `Tapit.MicCheck` | **Done** |
| 2 | Adaptive noise floor, onset detector, impact validation | **Validated on a real desk** |
| 3 | FFT + feature extraction | Built |
| 4 | Four-zone guided calibration | Built |
| 5 | Classifier + rejection stack | Built |
| 6 | Held-out evaluation | Built |
| 7 | Action engine | Built |
| 8 | Desktop interface (WinForms, §4.6) | Built |
| 9 | Tray + startup behaviour | Built |
| 10 | Performance work + packaging | Partial - single-file publish, no installer |

**Built is not validated.** Every phase above compiles, is unit-tested, and runs. None of it
has been measured against an actual desk tap, because that requires a person tapping a desk.
Until that happens the accuracy of this system is unknown, and no number in this document
says otherwise.

Phases 0-1 landed one at a time with hardware measurement at each step. Phases 2-10 were
then built out in one pass at the user's explicit direction, which front-loads the risk: the
detector's thresholds, the feature set, and the classifier choice are all still unmeasured
hypotheses. The replay tool (§11) exists precisely so they can be settled by experiment
rather than argument.

---

## 16. Engineering principles

1. No AI, no LLM, no neural networks, no embeddings.
2. No cloud, no telemetry, no remote processing.
3. No raw audio persistence by default.
4. No action on an uncertain classification - rejection has no path to `IAction`.
5. No blocking, allocating, or locking in the audio callback.
6. DSP is independent of the UI and independent of Windows.
7. Every queue is bounded; every drop is counted and visible.
8. Parameters are measured with the replay tool, not guessed.
9. Real accuracy is measured on the real desk, or it is not claimed.
10. When in doubt, reject.
