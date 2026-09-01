# Phase 1 measurements

Findings from running `Tapit.MicCheck` against real hardware. Recorded here because the
project's stated philosophy is that behaviour is measured, not assumed - and because two of
these results change what Phase 2 has to do.

## Test machine

| | |
|---|---|
| OS | Windows 11 Home Single Language, 10.0.26200, x64 |
| Endpoint | Microphone Array (Realtek(R) Audio) - built-in, form factor `Microphone` |
| Advertised mix format | 48000 Hz, 2 ch, Int16 (from `PKEY_AudioEngine_DeviceFormat`) |
| Runtime | .NET 8.0.424 |

Everything below is one laptop, one room. It is evidence that the code works and that the
failure modes are real; it is **not** a claim about other hardware.

---

## 1. Raw mode is not a nicety - without it this microphone reports silence

The headline result. The same device, minutes apart, measured over 5-second runs:

| | `--no-raw` (processed) | raw (default) |
|---|---|---|
| `rawModeActive` | false | true |
| Negotiated format | 48000 Hz, 2 ch, **Float32** | 48000 Hz, 2 ch, **Float32** |
| AC RMS | **−120.0 dBFS** (the reporting floor) | −30.2 dBFS |
| Peak | **−120.0 dBFS** | −20.1 dBFS |
| DC offset | −1.7 × 10⁻¹⁸ | +0.0069 |
| Crest factor | 0.0 dB | 10.2 dB |
| Clipped samples | 0 | 0 |

With Windows audio processing engaged, the endpoint delivered **exact digital silence** for
ambient room sound. Not attenuated - zero. Reproduced across separate runs; one run showed a
single 9.3 dB crest excursion, which identifies the behaviour as a noise gate opening
briefly rather than a dead stream.

The raw stream, on the same microphone, carried a normal room-noise signal at about
−30 dBFS with a 10 dB crest factor.

**Consequence:** on this hardware, a Tapit that did not request
`AUDCLNT_STREAMOPTIONS_RAW` would detect nothing at all. A desk tap is precisely the kind
of short broadband non-speech event a speech noise gate is built to remove. This is why
`RequestRawMode` defaults to true, why `WasapiCaptureOptions.AllowProcessedFallback` exists
as an explicit opt-in, and why the capture state string and the Diagnostics screen say in
plain words when processing could not be bypassed.

## 2. Raw mode changes the sample format

The property store advertises the endpoint as **Int16**. After
`IAudioClient2::SetClientProperties(RAW)`, `GetMixFormat` returned **Float32**.

This is why `WasapiCaptureSource` calls `SetClientProperties` *before* `GetMixFormat`, and
why a profile records the format it was calibrated at. Reading the format first and
enabling raw mode second would have produced a stream decoded with the wrong sample width -
which is not a subtle degradation, it is noise.

## 3. The raw stream carries a DC offset

Consistently measured between **0.0069 and 0.0079** (about −42 dBFS), stable across runs and
of either sign.

The APO chain normally high-passes the signal; bypassing it removes that filter too. Left
uncorrected, DC inflates RMS, drags crest factor toward 1.0, and biases every amplitude and
envelope feature.

**Consequence for Phase 2:** the detector must remove DC before computing anything.
`SignalLevels` already separates `Mean` / `DcOffset` from `AcRms` so the distinction is
visible at the measurement layer, and `Tapit.MicCheck` reports it under HEALTH. The onset
detector will high-pass ahead of envelope extraction.

## 4. Capture timing

Steady state, 5-second runs, event-driven shared mode, MMCSS "Pro Audio":

| Measurement | Value |
|---|---|
| Device period | 10.0 ms |
| Engine buffer | 22.0 ms |
| `GetStreamLatency` | 0.0 ms (this driver reports zero) |
| **Age of newest frame in ring** | **0.5 - 3.6 ms** |
| Slowest capture-thread service pass | 1.6 - 2.4 ms |
| Typical service pass | 0.04 ms |
| Ring overruns | 0 |
| Dropped frames | 0 |
| Capture discontinuities | 0 |
| Silent packets | 0 |

The "age of newest frame" figure is the one that matters: it is measured from the
performance-counter timestamp WASAPI attaches to each packet, so it includes driver and
engine buffering rather than only our own scheduling.

Against the §3 latency budget in [ARCHITECTURE.md](../ARCHITECTURE.md), the acoustic-onset →
sample-in-ring segment is budgeted at ≤ 20 ms and measures **under 4 ms**. The dominant term
in the end-to-end budget will be waiting for the analysis-window tail, exactly as predicted.

## 5. A real concurrency defect the stress test found

The ring buffer originally published a single write index. The unit test caught an
intermittent torn read; a dedicated stress harness pinned it down precisely:

```text
TORN READ
  position     = 29440
  expected     = 29440
  actual       = 33536
  delta        = 4096          <- exactly one capacity
  position - (writeIndex - capacity) = 0
```

The producer copies a packet's samples into their slots **before** publishing the new write
index. During that window the published index still advertises the oldest `capacity` frames
as readable, but the slots at the tail - which alias the frames being written - have already
been overwritten. A consumer sitting at the tail read data exactly one lap in the future,
and every validity check passed.

The fix is a second published index. The producer announces `ReserveIndex` (where the
in-flight write will end) *before* touching any samples and `WriteIndex` after, so the
readable window is `[ReserveIndex − Capacity, WriteIndex)` and stays honest mid-write.

After the fix: 20 trials × 4,000,000 frames, consumer deliberately parked at the tail,
≈28 million frames verified, **zero torn reads**. Before the fix it tore inside the first
trial.

Worth stating plainly: a lost analysis window is a missed tap, but a *silently torn* one is
a window containing audio from two different moments, which the classifier would happily
label and act on. This is the class of bug the "when in doubt, reject" philosophy exists to
protect against, and it is why refused reads are counted rather than papered over.

## 6. Device enumeration

Seven capture endpoints enumerated, one active. Inactive endpoints (`notpresent`,
`unplugged`, `disabled`) are correctly identified and rejected for calibration before the
user can waste time on them. Endpoints that expose no format blob report `Marginal` rather
than failing.

## Reproducing

```bash
dotnet run --project tools/Tapit.MicCheck -- devices
```

```bash
dotnet run --project tools/Tapit.MicCheck -- listen --seconds 10 --json
```

```bash
dotnet run --project tools/Tapit.MicCheck -- listen --seconds 10 --no-raw --json
```

The third command is the comparison that produced §1. On a machine whose driver applies no
capture-side processing, the two runs will look the same - that result is equally useful and
should be recorded.
