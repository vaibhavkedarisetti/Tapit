using Tapit.Core.Audio;

namespace Tapit.MicCheck;

internal readonly record struct MonitorSnapshot(
    double RmsDbfs,
    double AcRmsDbfs,
    double DcOffset,
    double PeakDbfs,
    double PeakHoldDbfs,
    double QuietestBlockDbfs,
    double CrestFactorDb,
    long ClippedSamples,
    long BlocksProcessed,
    long FramesConsumed,
    long DroppedFrames,
    long Resyncs,
    double NewestFrameAgeMs);

/// <summary>
/// Consumes the capture ring and measures the signal.
/// </summary>
/// <remarks>
/// This is deliberately the same shape the Phase 2 detector will have: block-at-a-time
/// consumption through a <see cref="RingBufferReader"/>, no work whatsoever on the capture
/// thread, and every dropped frame accounted for. If the meter can keep up here, the
/// detector's budget is real.
/// </remarks>
internal sealed class SignalMonitor
{
    private readonly IAudioCaptureSource _source;
    private readonly RingBufferReader _reader;
    private readonly float[] _block;
    private readonly float[] _recentBlockRms;
    private readonly WavWriter? _recorder;
    private readonly float[]? _recordScratch;

    private int _recentIndex;
    private int _recentCount;

    private double _rms;
    private double _acRms;
    private double _dc;
    private double _peak;
    private double _peakHold;
    private long _clipped;
    private long _blocks;
    private long _frames;

    public SignalMonitor(IAudioCaptureSource source, int blockFrames, int recentBlocks, WavWriter? recorder = null)
    {
        _source = source;
        _reader = new RingBufferReader(source);
        _block = new float[blockFrames];
        _recentBlockRms = new float[Math.Max(1, recentBlocks)];
        _recorder = recorder;

        if (recorder is not null)
        {
            _recordScratch = new float[blockFrames];
        }
    }

    public int BlockFrames => _block.Length;

    /// <summary>Drains everything currently available. Returns the number of blocks consumed.</summary>
    public int Pump()
    {
        int consumed = 0;

        while (_reader.TryReadNextBlock(_block, out _))
        {
            SignalLevels levels = SignalAnalysis.Measure(_block);

            _rms = levels.Rms;
            _acRms = levels.AcRms;

            // Slow average: DC drifts, and a per-block value would be too twitchy to read.
            _dc = _blocks == 0 ? levels.DcOffset : (_dc * 0.95) + (levels.DcOffset * 0.05);

            _peak = levels.Peak;
            _clipped += levels.ClippedSamples;
            _blocks++;
            _frames += _block.Length;

            if (levels.Peak > _peakHold)
            {
                _peakHold = levels.Peak;
            }
            else
            {
                // ~2 dB per block decay, so the hold tracks recent history rather than the
                // loudest thing that ever happened.
                _peakHold *= 0.79f;
            }

            _recentBlockRms[_recentIndex] = levels.AcRms;
            _recentIndex = (_recentIndex + 1) % _recentBlockRms.Length;
            if (_recentCount < _recentBlockRms.Length)
            {
                _recentCount++;
            }

            if (_recorder is not null && _recordScratch is not null)
            {
                _block.AsSpan().CopyTo(_recordScratch);
                _recorder.WriteFrames(_recordScratch, _block.Length);
            }

            consumed++;
        }

        return consumed;
    }

    public MonitorSnapshot Snapshot()
    {
        float quietest = float.MaxValue;
        for (int i = 0; i < _recentCount; i++)
        {
            if (_recentBlockRms[i] < quietest)
            {
                quietest = _recentBlockRms[i];
            }
        }

        double newestAgeMs = double.NaN;
        AudioRingBuffer? buffer = _source.Buffer;
        SampleClock? clock = _source.Clock;
        if (buffer is not null && clock is not null && buffer.WriteIndex > 0)
        {
            newestAgeMs = clock.AgeMilliseconds(buffer.WriteIndex - 1);
        }

        // Crest is measured against the AC component: with a DC-biased raw stream the
        // uncorrected figure would read near 0 dB and say nothing about transients.
        double crestDb = _acRms > 0 ? SignalAnalysis.ToDbfs(_peak) - SignalAnalysis.ToDbfs(_acRms) : 0;

        return new MonitorSnapshot(
            SignalAnalysis.ToDbfs(_rms),
            SignalAnalysis.ToDbfs(_acRms),
            _dc,
            SignalAnalysis.ToDbfs(_peak),
            SignalAnalysis.ToDbfs(_peakHold),
            _recentCount == 0 ? SignalAnalysis.MinimumDbfs : SignalAnalysis.ToDbfs(quietest),
            crestDb,
            _clipped,
            _blocks,
            _frames,
            _reader.DroppedFrames,
            _reader.ResyncCount,
            newestAgeMs);
    }
}
