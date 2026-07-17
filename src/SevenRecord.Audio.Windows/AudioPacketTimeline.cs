using SevenRecord.Capture.Abstractions;

namespace SevenRecord.Audio.Windows;

public readonly record struct AudioPacketTiming(
    long SamplePosition,
    ClockDriftEstimate Drift,
    bool HasDiscontinuity);

public sealed class AudioPacketTimeline
{
    private readonly ClockDriftEstimator _drift = new();
    private readonly object _gate = new();
    private TimeSpan? _lastProjectTime;
    private long _samplePosition;

    public AudioPacketTiming AddPacket(
        TimeSpan projectTime,
        long frames,
        int sampleRate)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(projectTime, TimeSpan.Zero);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(frames);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(sampleRate);

        lock (_gate)
        {
            _samplePosition += frames;
            TimeSpan packetDuration = TimeSpan.FromSeconds(frames / (double)sampleRate);
            bool hasDiscontinuity = _lastProjectTime is TimeSpan previous &&
                projectTime - previous >
                packetDuration + packetDuration + TimeSpan.FromMilliseconds(20);
            _lastProjectTime = projectTime;
            ClockDriftEstimate drift = _drift.AddSample(
                projectTime,
                _samplePosition,
                sampleRate);

            return new AudioPacketTiming(
                _samplePosition,
                drift,
                hasDiscontinuity);
        }
    }
}
