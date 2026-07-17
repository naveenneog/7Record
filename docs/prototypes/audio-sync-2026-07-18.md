# Audio Synchronization Probe: 2026-07-18

The Windows audio prototype captures the default microphone and default render endpoint loopback through WASAPI/NAudio. Each packet is stamped on the shared QPC project clock, written to an independent float WAV, checked for callback discontinuities, and measured with a fitted device-clock drift estimate.

## Devices and format

| Source | Device | Format |
| --- | --- | --- |
| Microphone | Remote Audio | 44.1 kHz, stereo, 32-bit float |
| System audio | Remote Audio | 44.1 kHz, stereo, 32-bit float |

## Ten-second persistence run

| Metric | Microphone | System audio |
| --- | ---: | ---: |
| WAV duration | 9.570 s | 10.010 s |
| Packets | 144 | 160 |
| Drift | -37.9 ms | -0.9 ms |
| Discontinuities | 1 | 0 |
| Output | recoverable WAV | recoverable WAV |

Both files were atomically published and appended to the checksummed recording journal as sequence 2 and 3.

## One-minute reliability runs

The Remote Audio devices are variable:

- One run ended near the threshold: microphone -40.6 ms, loopback +3.9 ms.
- A later run lost roughly 1.1 seconds of microphone samples while loopback remained near -33.6 ms.

This is a real source dropout, not merely an offset. 7Record must preserve the original samples, record discontinuity events, and repair preview/export timing with silence insertion or bounded asynchronous resampling.

## Decision

- Capture microphone and system audio as independent sources.
- Use the project QPC clock for packet placement.
- Use an online least-squares clock fit to reduce callback scheduling jitter.
- Treat gaps separately from drift; do not hide them in a rate estimate.
- Never trim or stretch immutable source files during recording.
- WAV is the first recoverable intermediate; compressed audio can follow after recovery and sync are stable.
