# Encoder Probe: 2026-07-17

Environment:

- Windows development machine
- FFmpeg 8.1.2 full build
- Probe input: 15 synthetic 1280x720 frames at 60 fps
- Worker: `SevenRecord.Media.Worker`

## Result

FFmpeg listed all expected H.264 encoders, but capability listing alone was not sufficient:

| Encoder | Listed | Initialization | First failure |
| --- | --- | --- | --- |
| NVIDIA NVENC (`h264_nvenc`) | Yes | Failed | `Cannot load nvcuda.dll` |
| Intel Quick Sync (`h264_qsv`) | Yes | Failed | `Error creating a MFX session: -9` |
| AMD AMF (`h264_amf`) | Yes | Failed | `DLL amfrt64.dll failed to open` |
| Software x264 (`libx264`) | Yes | Passed | None |

The selected encoder was `libx264`. This is an automatic fallback, not a user preference.

## Product decision

7Record must never treat `ffmpeg -encoders` output as proof that hardware encoding is usable. The isolated worker validates a short synthetic encode in preference order and records every failed attempt before selecting the first working encoder.

Hardware acceleration remains enabled automatically when the required GPU, driver, and runtime are present. Software H.264 keeps recording available when they are not.
