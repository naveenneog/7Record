# Webcam Capture Probe: 2026-07-18

## Implemented path

1. Enumerate `MediaFrameSourceGroup` color sources.
2. Initialize `MediaCapture` in shared read-only video mode.
3. Select the highest-resolution supported color format.
4. Read realtime frames through `MediaFrameReader`.
5. Normalize `SystemRelativeTime` on the project QPC clock.
6. Copy the camera surface into a compatible BGRA Direct3D texture on the GPU.
7. Encode through the Media Foundation surface encoder.
8. Publish an independent camera MP4 as journal sequence 4.
9. Persist normalized presenter-layout metadata in `presenter-layout.json`.

## Current environment

The machine exposes two redirected color camera groups:

- `0 (redirected)`
- `1 (redirected)`

Both initialize, but neither delivers a processable frame within five seconds. The probe now returns a structured failure instead of creating an invalid MP4:

```json
{
  "succeeded": false,
  "errorType": "InvalidOperationException",
  "message": "Camera '0 (redirected)' did not deliver a processable frame within five seconds."
}
```

## Product behavior

Camera is optional. The user must choose **Configure camera**, which proves a real frame before camera recording is enabled. A missing or redirected-but-inactive camera never blocks screen and audio recording.

The default presenter mode is a reversible rounded overlay. Side-by-side, full presenter, and screen-only remain metadata choices rather than baked pixels.
