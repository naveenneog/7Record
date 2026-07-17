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

## Direct3D surface follow-up

The follow-up replaced pixel readback with `MediaStreamSample.CreateFromDirect3D11Surface`, `MediaStreamSource`, and hardware-enabled `MediaTranscoder`.

| Metric | CPU readback + FFmpeg | Direct3D surface + Media Foundation |
| --- | ---: | ---: |
| Capture interval | 15.320 s | 15.259 s |
| Captured resolution | 840 x 1905 | 874 x 1980 |
| Source frame rate | 13.71 fps | 11.80 fps |
| Dropped frames | 0 | 0 |
| Encoded frame rate | 30 fps | 60 fps |
| Duration error | +180.4 ms | +357.2 ms |
| CPU core-equivalent | 142.28% | 145.09% |
| Whole-machine CPU | 8.89% | 9.07% |
| Combined working set | 1,548.2 MB | 559.2 MB |
| Segment size | 711,057 B | 2,383,068 B |

The surface path cut combined working set by about 64% and removed the pixel-copy architecture, while retaining zero drops and a recoverable MP4. CPU stayed near 1.45 cores because this reference machine has no working NVENC, Quick Sync, or AMF runtime and Media Foundation therefore encodes in software.

The surface path is accepted as the production architecture, but the 1080p60 release gate remains open until it is tested with a true 60 fps source on hardware with an available encoder.

## Harness notes

- The system picker filtered newly launched WPF and same-publisher WinUI source windows.
- Automated Edge selection stayed pending in the picker.
- The Android emulator was picker-visible and accepted reliably.
- The harness closes stale picker windows, waits for readiness and picker content, animates the source, restores its bounds, and verifies the published segment.
- `-AttachExisting -UseExistingSelection -PreserveSourceSize` measures an already-selected source without reopening the system picker.
