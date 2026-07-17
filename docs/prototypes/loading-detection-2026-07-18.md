# Loading / Waiting Detection Probe: 2026-07-18

7Record runs FFmpeg `freezedetect` in the isolated media worker after screen publication. Sustained low-motion intervals of at least two seconds become reversible 4x speed suggestions in `loading-speed-plan.json`.

A synthetic static 1280 x 720 clip produced:

```json
{
  "start": "00:00:00",
  "duration": "00:00:05",
  "speed": 4,
  "confidence": 0.6
}
```

The detector probes media duration with FFprobe so a freeze that continues through end-of-file is closed correctly even when FFmpeg emits no `freeze_end`.

These are conservative visual-change suggestions. Later scoring will combine pointer, keyboard, and audio inactivity before increasing confidence.
