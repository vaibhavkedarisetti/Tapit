# Detector notes - first implementation

Working log for the tap detector. Records what was measured, what broke, and what is still
unproven. Updated as experiments are run.

**Status: detection validated on a real desk.** Taps on a real table are detected and
accepted, and ambient room noise still produces zero false accepts. Getting there required
fixing a gate that was structurally incapable of passing a real tap (defect 4 below).

**Zone separation is still unproven.** Detecting *that* a tap happened and identifying
*which zone* it came from are different problems. The second is answered only by a held-out
evaluation.

---

## What was built

The smallest pipeline that can be tested end to end:

```text
WASAPI  →  ring buffer  →  DC blocker  →  frame energy  →  adaptive noise floor
        →  onset (rise + floor margin + absolute gate)  →  90 ms window
        →  validation gates  →  event (accepted / rejected with a reason)
```

Deliberately *not* built yet, because nothing has shown they are needed:
mel/MFCC features, multiple classifiers, an action engine, a UI. A mel filter bank and a
spare biquad were written and then deleted for exactly this reason.

Feature set is the starting one only: RMS, peak, crest, attack, decay, duration, ZCR,
early-energy fraction, spectral centroid, spectral bandwidth, six relative band energies.

## Defects found by measurement

Three real bugs, all found by running the thing rather than by reading it.

### 1. Refractory period was permanently active

`_lastEventOnset` was initialised to `long.MinValue`, so `_position - _lastEventOnset`
overflowed and wrapped negative. The refractory test was therefore always true and **not a
single event was ever emitted**. Every detector test failed at once, which is what made it
obvious. Fixed with a nullable rather than a sentinel.

### 2. The noise floor could not follow a room that got louder

The first design froze floor tracking whenever a frame was above threshold, reasoning that
a tap must not raise its own detection threshold. But once the room got persistently louder
the floor stayed low, every frame read as above threshold, and the floor could never
recover - the detector jammed.

Replaced with plain asymmetric tracking on every frame: falls toward quiet with a 50 ms
constant, rises with a 1500 ms one. A 50 ms tap moves it by a fraction of a percent; a fan
switching on is absorbed within a few seconds. Simpler, and it self-corrects.

A second-order version of the same bug survived that fix: frames *inside* a pending
candidate's window are never scanned, so in a continuously noisy room the tracker saw
almost no audio and rose only 3.7 dB in three seconds. Rejected windows now fold their own
level into the floor as if those frames had been scanned. Accepted taps still never raise it.

### 3. Refractory on candidates vs. on accepted events

Both obvious choices are wrong:

* Refract on **every candidate** → a burst of room noise opens a 180 ms window that
  swallows a real tap arriving just after it.
* Refract only on **accepted** events → one physical strike whose first window is rejected
  can be picked up again from its own ringing a few frames later and accepted. This was
  caught by a test with three impulses 10 ms apart producing three events.

Now there are two timers: every candidate consumes its own 90 ms analysis window (the scan
position advances past it, so two candidates can never share audio), and an accepted tap
additionally holds off for the full refractory period.

### 4. The sustained-sound gate could never pass a real tap

The one that mattered. Every tap on a real desk was rejected as `SustainedSound`, having
already passed the loudness, SNR, crest-factor and attack gates - so the detector was
correctly identifying a genuine impact and then throwing it away.

Effective duration was measured as *time the envelope stays above 10 % of peak*, inside a
90 ms window, rejecting above 55 ms. A struck table **rings**: low-frequency resonance
decays over 100 ms or more, so the envelope never falls to 10 % inside the window, the
measurement pins at the window length, and the gate fires on everything. No tap of any kind
could have passed it on a resonant surface.

The threshold had been calibrated against a synthetic fixture using an 8 ms decay constant -
a far deader impulse than any real desk. **The fixture was wrong, so the threshold was
wrong, and a full passing test suite could never have caught it.** Synthetic tests verify the
code does what it was written to do; they cannot tell you it was written against the wrong
physics.

Fixed by measuring duration at 25 % of peak (a decaying impact crosses that quickly, a
sustained tone still never does), raising the limit to 65 ms, and tightening the
early-energy gate to 0.60 to keep sustained rejection strong. Rejection messages now carry
the measured value and the limit it failed against, which is what makes this class of
mistake visible in seconds instead of hours.

Saved profiles carried the broken thresholds in their own JSON and would have silently
overridden the corrected defaults, so the profile schema was bumped and old detector
settings are reset on load.

## Measurements

### Ambient room noise, live microphone, built-in Realtek array

14-second runs, no deliberate sounds made.

| | candidates | accepted | rejection reasons | noise floor |
|---|---|---|---|---|
| Level gate only | 62 | **0** | SlowAttack×51, SustainedSound×11 | −44.3 dBFS |
| + frame-to-frame rise gate (9 dB) | 33 | **0** | SlowAttack×23, SustainedSound×9, FlatDynamics×1 | −40.3 dBFS |

Zero false accepts in both configurations - the validation gates did their job. But 4.4
candidates/second was too many: each opens a 90 ms window, and a candidate window active
~40 % of the time is a real risk of masking a genuine tap. Requiring a jump from the
previous frame (an impact rises almost vertically; room noise drifts) roughly halved it at
no cost.

Still ~2.4 candidates/second on this desk in this room. Whether that matters can only be
answered with real taps.

### Synthetic fixture - plumbing only

Four exponentially-decaying broadband bursts plus a sustained 420 Hz tone, 5 s:
4 accepted, tone rejected. The noise floor visibly absorbs the tone - SNR of successive
candidates falls 51.8 → 27.8 → 22.0 → 18.7 → 16.4 dB as the floor rises to meet it, which
is the adaptive tracker working as intended.

Replay runs at ~75× realtime, so parameter sweeps over recorded sessions are cheap.

**This proves the plumbing, not the physics.** Synthetic impulses are not desk taps.

### Real-desk tuning session - 42 captured events

First tuning pass against actual taps rather than a fixture. Distributions, accepted vs
rejected:

| | accepted (15) | rejected (27) |
|---|---|---|
| attack ms | 0.67 - 4.35 (med 2.8) | 1.4 - 78.4 (med 11.9) |
| duration ms | 28 - 64.5 (med 54) | 10 - 89.5 (med 78) |
| **SNR dB** | **33.9 - 48.9** | 7.1 - 49.1 (med 22.9) |
| peak dBFS | −7.2 - **+6.1** | −27.1 - +6.7 |
| crest dB | 9.6 - 18.4 | 7.2 - 18.1 |

Three findings.

**SNR is the cleanest separator in the whole gate set.** Every genuine tap landed at 34 dB
or better; ring tails and room noise sat below 25. The envelope-*shape* gates - attack,
duration, crest - overlap heavily between real taps and their own decay, and were doing far
more harm than good at their original settings. Raising `MinSnrDb` from 10 to 25 does the
work that three shape gates were failing to do, which allowed attack (10 → 20 ms) and
duration (65 → 78 ms) to be widened to sit *above* the real population instead of through
the middle of it.

**One strike produced nine events.** A hard tap at 19.70 s (peak +6.7 dBFS) was rejected for
clipping, and because refractory was tied to *acceptance* nothing suppressed its ringing:
seven further detections of its own decay at −15 to −27 dBFS, then a tail. Refractory now
follows any acoustically **loud** candidate, accepted or not - loudness, not verdict, is
what says a physical event occurred. Quiet room noise still cannot open a refractory window.

**The input is overdriven.** Accepted taps had a median peak of −0.1 dBFS and a p75 of
**+4.7 dBFS** - the raw float stream is running past full scale, and even accepted windows
carried up to 38 clipped samples. Distortion flattens the waveform, which corrupts attack
and duration for *every* event, not only the ones flagged. This is a user-side gain problem
the software cannot fix, so `Tapit.MicCheck detect` now reports it explicitly.

Simulated over the same 42 events, the retune gives:

```text
before:  42 candidates, 15 accepted  (36%)
after:   38 candidates, 18 accepted  (47%)    4 ring events suppressed
         + 3 more once input level is corrected → 55%
```

Remaining rejections are dominated by `LowSignalToNoise` (11) - the quiet ring tails, which
is exactly what should be rejected.

### Left-versus-right failed at chance level

First classification result on a real desk: tapping the left side four times gave one left
and three right. That is chance, and the cause is structural rather than a tuning problem.

**Left-versus-right is the symmetry axis of a centred microphone.** Front and rear differ in
path length to the mic and so differ in the mono spectrum. Two taps equidistant either side
of it do not - they are close to the same signal once mixed to mono. Front/rear is the easy
axis; left/right is the hard one, and the feature set was mono-only, so the information that
distinguishes them was being discarded at the mixdown.

Whether it is recoverable at all depends on the hardware, so it was measured rather than
assumed (architecture §7 requires exactly this). On the development machine, ambient audio,
built-in Realtek array in raw mode:

```text
mean |level diff|        1.372 dB
mean |lag|              10.1 us
mean corr @ zero lag     0.673
blocks looking degenerate  0 / 18
```

Correlation of 0.67 rather than ~1.0, with a real level difference and a real delay: **the
two channels are independent physical elements.** Raw mode is exposing the actual array
rather than a beamformed mono copy - which is a direct payoff from the §4.1 raw-mode
decision, and would not have been true through the processed APO path.

Three spatial features were added - inter-channel level difference (dB), arrival delay (µs),
and peak correlation - computed over a 10 ms region centred on the direct arrival. The ring
that follows is diffuse and carries the surface's own resonance, which is the same whichever
side was struck, so including it would only dilute the cue.

Feature count went 16 → 19, which invalidates any calibration collected under the old set:
those vectors describe different quantities at each position, so they are wrong rather than
merely stale. Profiles now detect the mismatch, discard the samples, and say why.

**Still unverified:** that these features actually separate left from right *on a real desk*.
The array elements are only centimetres apart, so the available delay is small, and whether
it survives the surface's structure-borne propagation is an empirical question.

## Open questions

1. ~~**Is a tap detected at all?**~~ Yes. Confirmed on a real desk.
2. **Are the four zones actually separable?** Partly answered: front/rear is the tractable
   axis, left/right measured at chance with mono-only features. Spatial features have been
   added and the hardware is known to support them, but their effect on a real desk is
   unmeasured. Calibration's leave-one-out agreement is the first hint; a held-out
   evaluation is the answer. If the zones do not separate in the feature numbers, no
   classifier will rescue them - and that would be a fact about the desk, not about the code.
3. **Does the starting feature set suffice?** Only worth asking once (2) is positive. If
   agreement is middling, compare the per-zone feature columns in `events.csv` before adding
   anything: more features on 40 samples overfits before it helps.
4. **Are the thresholds right, or merely no longer wrong?** They were set to unblock a real
   tap, not optimised. A sweep over a recorded session is cheap and would settle them.

## Parameters and their status

Everything is exposed on the command line so it can be swept rather than argued about.

| Parameter | Default | Basis |
|---|---|---|
| Window | 90 ms | Holo's value, adopted as a starting hypothesis. Untested here. |
| Pre-roll | 12 ms | Must cover detector lag so the leading edge survives. Untested. |
| Onset threshold | +12 dB over floor | Guess. |
| Min rise | +9 dB frame-to-frame | Chosen from the ambient-noise measurement above. |
| Min onset level | −55 dBFS | Guess; stops a very quiet room making everything relative. |
| Refractory | 180 ms | Guess. |
| Max attack | 20 ms | **Measured** - real taps reach ~21 ms when the envelope peak lands in the ring. |
| Max duration | 78 ms | **Measured** - real accepted taps ran to 64.5 ms, genuine ones to 81 ms. |
| Min SNR | 25 dB | **Measured** - the sharpest separator; every real tap ≥ 34 dB. |
| Duration fraction | 25 % of peak | **Measured** - at 10 % a ringing desk pins at the window length. |
| Min crest | 6 dB | Guess; overlaps too much between taps and ring to be load-bearing. |
| DC blocker | 20 Hz | Required by the Phase 1 finding that raw capture carries DC. |

Four now have real measurement behind them; the rest are still guesses. The onset-stage
parameters in particular have never been swept against a labelled corpus.
