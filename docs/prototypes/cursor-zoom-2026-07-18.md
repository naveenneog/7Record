# Cursor Intent and Zoom Prototype: 2026-07-18

## Implemented

- 16 ms Windows cursor sampling.
- QPC project timestamps.
- screen coordinates and foreground-window normalized coordinates.
- left/right click edge detection.
- throttled movement metadata.
- `cursor-events.json` project persistence.
- reversible click-centered zoom suggestions with 200 ms pre-roll, 1.2 second duration, and 1.8x scale.
- editor automation-track loading.

## Current environment

The active remote desktop session returns `false` from `GetCursorPos`, including a direct PowerShell/User32 probe. Cursor capture therefore reports:

> Windows cursor position is unavailable in the current desktop session.

Cursor metadata is optional. This environment limitation does not block screen, audio, captions, or export. The live cursor path requires validation on an interactive Windows desktop with an accessible pointer.

## Export rendering

Cursor zoom suggestions carry structured `centerX`, `centerY`, and `scale` data into the shared render plan. FFmpeg `zoompan` applies a smooth sine-ramped zoom during the event while camera overlays and captions remain in the final output coordinate space.

A synthetic 1.8x zoom centered at 30%, 60% from 1.0-2.2 seconds rendered successfully into a 1920 x 1080 / 30 fps MP4.
