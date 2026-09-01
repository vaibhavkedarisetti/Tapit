# Tapit

Turn the desk around your laptop into four acoustic input zones. Tap the desk; Tapit works
out **which** zone you hit and runs the action bound to it.

```text
                       LAPTOP
              ┌─────────────────────┐
              │                     │
              │       SCREEN        │
              │                     │
              └─────────────────────┘

         ●                               ●
     LEFT FRONT                     RIGHT FRONT

         ●                               ●
     LEFT REAR                       RIGHT REAR
```

Everything is deterministic signal processing and classical statistics, running locally.

> **No AI. No LLM. No neural networks. No embeddings. No cloud. No telemetry.
> No computer vision. No remote processing. No raw audio persistence by default.**

Inspired by [Holo](https://github.com/JustinGamer191/Holo) (macOS). Tapit reuses the
concept - a desk is a resonator, and impact location changes the transfer function to the
microphone - with a clean Windows implementation and its own parameter choices.

---

## Status

Under construction, one phase at a time.

| Phase | Deliverable | Status |
|---|---|---|
| 0 | Reference study, [`ARCHITECTURE.md`](ARCHITECTURE.md) | **Done** |
| 1 | WASAPI capture, ring buffer, device lifecycle, `Tapit.MicCheck` | **Done** |
| 2 | Adaptive noise floor, onset detector, impact validation | **Validated on a real desk** |
| 3 | FFT and feature extraction | Built |
| 4 | Four-zone guided calibration | Built |
| 5 | Classifier and rejection stack | Built |
| 6 | Held-out evaluation | Built |
| 7 | Action engine | Built |
| 8 | Desktop interface | Built (WinForms, not WinUI 3 - see below) |
| 9 | Tray and startup behaviour | Built |
| 10 | Performance work and packaging | Partial |

**Built is not the same as validated.** Every phase compiles, is unit-tested (310 tests),
and runs. Phase 1 was measured on real hardware. Everything from Phase 2 onward has only
been exercised against synthetic signals and ambient room noise - **it has never seen an
actual desk tap.**

That means the detector thresholds, the feature set and the classifier choice are all
unmeasured hypotheses. The accuracy of this system on your desk is currently unknown, and
nothing in this repository claims otherwise. Finding out takes about ten minutes: calibrate,
then run an evaluation.

## Requirements

* Windows 11, or Windows 10 1809+
* x64
* .NET 8 SDK
* A microphone - the built-in laptop one is normally the right choice, because it is
  mechanically coupled to the same surface the taps travel through

## Build and test

```bash
dotnet build Tapit.sln
```

```bash
dotnet test Tapit.sln
```

Some tests open the real microphone. To skip those:

```bash
dotnet test Tapit.sln --filter Category!=Hardware
```

## Try Phase 1

List capture endpoints, with the negotiated format and whether each is fit for tap
classification:

```bash
dotnet run --project tools/Tapit.MicCheck -- devices
```

Open the microphone and watch the live signal and capture health:

```bash
dotnet run --project tools/Tapit.MicCheck -- listen --seconds 30
```

Compare against a stream with Windows audio processing left in place - on many machines this
is a dramatic difference, and it is the single most important thing to check on new hardware:

```bash
dotnet run --project tools/Tapit.MicCheck -- listen --seconds 10 --no-raw --json
```

Listen for desk taps. Shows the noise floor, the live level, and every candidate event
with the reason it was accepted or rejected:

```bash
dotnet run --project tools/Tapit.MicCheck -- detect
```

Collect experimental data - one WAV per detected window plus `events.csv` of measurements
and features:

```bash
dotnet run --project tools/Tapit.MicCheck -- detect --save taps --save-rejected
```

Record a session, then replay it through the identical detector as many times as you like
with different thresholds. This is the tuning loop:

```bash
dotnet run --project tools/Tapit.MicCheck -- record session.wav --seconds 60
```

```bash
dotnet run --project tools/Tapit.MicCheck -- detect --file session.wav --features
```

`listen` and `replay` keep audio in memory and discard it. `record` is the only command that
writes audio to disk, and it says so on screen the whole time it is running.

## What Phase 1 measured

On the development machine (built-in Realtek array, 48 kHz):

* **Windows audio processing gated the microphone to exact digital silence** for ambient
  sound, while raw mode on the same device carried a normal signal. Requesting
  `AUDCLNT_STREAMOPTIONS_RAW` is not an optimisation here; without it there is nothing to
  detect.
* Raw mode **changed the reported sample format** from Int16 to Float32, so raw-mode
  negotiation has to happen before the format is read.
* The raw stream carries a **DC offset** (~−42 dBFS), because bypassing the effects chain
  also bypasses its high-pass filter. The Phase 2 detector must remove it.
* Age of the newest frame in the ring: **0.5-3.6 ms**, against a 20 ms budget.
* A **torn-read race in the ring buffer** that a single published write index could not
  prevent. Fixed with a reserve index; 28 million frames verified since.

Numbers, method and how to reproduce: [`docs/PHASE1-MEASUREMENTS.md`](docs/PHASE1-MEASUREMENTS.md).

The detector's own working log - including three defects that only showed up when it was
run, and what is still unproven - is in [`docs/DETECTOR-NOTES.md`](docs/DETECTOR-NOTES.md).

## Run it

```bash
dotnet run --project src/Tapit.App
```

Tapit opens a window and puts an icon in the notification area. Closing the window keeps it
listening; Quit is on the tray menu. `--tray` starts it hidden.

The order that matters:

1. **Settings** - pick the microphone. Check it says *raw - Windows audio effects bypassed*.
   If it says PROCESSED, turn off audio enhancements for that device first; on the
   development machine, processing gated the microphone to complete silence.
2. **Calibration** - 10 taps per zone, 40 total. It only counts taps it accepts, and it tells
   you why it refused the others. At the end it reports leave-one-out agreement, which is a
   consistency check, *not* accuracy.
3. **Actions** - bind each zone. Every action has a Test button that runs it directly.
4. **Evaluation** - 60 taps, 15 per zone, held out from training. This is the only number
   that means anything.
5. **Diagnostics** - when something does not work, this answers why: signal level, noise
   floor, the last event's waveform and spectrum, and which gate rejected it.

## Layout

```text
src/Tapit.Core/     portable DSP core - no Windows, no UI, no packages
                      Audio/ Detection/ DSP/ Features/
src/Tapit.Audio/    WASAPI capture and device lifecycle
src/Tapit.Actions/  20 action implementations + bounded dispatcher
src/Tapit.App/      desktop application and tray
tests/              unit and hardware integration tests
tools/Tapit.MicCheck/    capture verification and live tap detection
tools/Tapit.AudioReplay/ offline WAV → detector → classifier harness
docs/
```

`Tapit.Core` has no reference to any other project. Dependencies point inward only, so the
DSP engine can be tested without a microphone, a UI, or Windows.

## Why WinForms and not WinUI 3

The specification asked for WinUI 3. It was tried and dropped. The Windows App SDK restores,
but its self-contained unpackaged build on .NET 8 fails resolving runtime packs for legacy
`win10-*` RIDs, and the non-self-contained path needs a separate Windows App Runtime install
before the app will launch at all. Meanwhile WinUI 3 has no tray support whatsoever - a tray
utility needs `Shell_NotifyIcon` interop either way - and `Tapit.Actions` already depended on
WinForms for clipboard and screen capture.

So: a deliberate deviation, for something that builds and runs anywhere .NET 8 is installed.
Full reasoning in [`ARCHITECTURE.md`](ARCHITECTURE.md) §4.6.

## Privacy

Local-first by construction. Audio becomes features, features become a classification, and
the audio is discarded. Profiles store feature vectors and model parameters - never
recordings. There is no telemetry, no analytics, no crash reporting and no update ping.
Pausing the microphone genuinely stops the stream, so the Windows in-use indicator goes out.

Raw WAV recording exists only for developing the DSP. It is off by default, opt-in, and
visibly marked while active.

## Honest expectations

A profile is specific to one laptop, one desk, one laptop position, one room. Move any of
them and it must be recalibrated. Soft, damped, wobbly or very large surfaces may not
separate the zones at all. Typing, mouse clicks, chassis touches and dropped objects
genuinely resemble taps, which is why rejection is a first-class feature and the defaults
are conservative: **it is better to miss a tap than to fire the wrong action.**

Accuracy targets (≥ 80 % over a balanced 60-tap held-out session, < 200 ms median latency)
are engineering targets, not guarantees. The only number that means anything is the one
measured on your own desk.

## License

MIT. See [LICENSE](LICENSE).
