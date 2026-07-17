# Screen Segment Prototype: 2026-07-17

The packaged WinUI app completed an automated end-to-end recording pass:

1. Opened the secure Windows capture picker.
2. Selected an existing File Explorer window.
3. Validated encoders in `SevenRecord.Media.Worker`.
4. Selected software `libx264` after hardware initialization failures.
5. Captured a `GraphicsCaptureItem` through a free-threaded Direct3D 11 frame pool.
6. Copied the latest BGRA frame into a 30 fps pacer.
7. Streamed raw frames to the isolated worker.
8. Encoded a Matroska/H.264 temporary file.
9. Atomically published the segment and durably appended its SHA-256 journal entry.

## Measured result

| Property | Result |
| --- | --- |
| Encoded resolution | 1216 x 1430 |
| Codec | H.264 |
| Frame rate | 30 fps |
| Container duration | 9.766 seconds |
| Journal duration | 10.011 seconds |
| File size | 590,550 bytes |
| WGC source frames | 1 (the selected window was static) |
| Dropped capture frames | 0 |

The constant-frame-rate pacer repeated the latest frame while the source window was static. Container duration differed from wall-clock journal duration by about 245 ms, within the prototype's 500 ms acceptance bound.

## Limitation

This prototype performs a CPU readback from the Direct3D surface before sending BGRA frames to the media worker. It proves correctness, isolation, fallback, and recovery, but it is not the final performance path. The next optimization must benchmark and replace the readback with GPU sharing or a native Media Foundation encoder path before claiming 1080p60 performance.
