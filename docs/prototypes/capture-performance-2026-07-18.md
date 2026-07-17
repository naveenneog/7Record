# Capture Performance Gate: 2026-07-18

Command:

```powershell
tools\measure-capture-performance.ps1 -DurationSeconds 15 -AttachExisting
```

The harness selected the running Android emulator through the real Windows picker, resized it to 1080 x 1920, generated continuous interaction, captured through the Direct3D frame pool, encoded through the isolated worker, finalized the Matroska segment, and verified it with `ffprobe`.

## Result

| Metric | Measured |
| --- | ---: |
| Capture interval | 15.320 s |
| Captured resolution | 840 x 1905 (1.60 MP) |
| WGC source frames | 210 |
| Source frame rate | 13.71 fps |
| Dropped capture frames | 0 |
| Encoded frame rate | 30 fps |
| Container duration | 15.500 s |
| Duration error | +180.4 ms |
| Capture/app/worker/FFmpeg CPU | 142.28% of one core |
| Whole-machine normalized CPU | 8.89% across 16 logical CPUs |
| Combined working set | 1,548.2 MB |
| Segment size | 711,057 bytes |
| Encoder | H.264 `libx264` fallback |

## Decision

The CPU-readback prototype passes correctness, crash-safe publication, duration error, and zero-drop behavior at the source's 13.71 fps update rate. It **fails the production 1080p60 gate**:

- It did not sustain a 60 fps source.
- It consumed about 1.42 CPU cores at 13.71 source fps.
- Combined working set exceeded 1.5 GB.
- Extrapolating the readback/copy load toward 60 fps is not credible within the 20% CPU target.

The path remains useful as a diagnostic and compatibility fallback. Production capture must keep the surface on the GPU and use Media Foundation or a native encoder bridge.

## Harness notes

- The system picker filtered newly launched WPF and same-publisher WinUI source windows.
- Automated Edge selection stayed pending in the picker.
- The Android emulator was picker-visible and accepted reliably.
- The harness closes stale picker windows, waits for readiness and picker content, animates the source, restores its bounds, and verifies the published segment.
