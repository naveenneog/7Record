# Local Captions Probe: 2026-07-18

7Record now uses Whisper.net with a cached GGML tiny model for offline transcription.

The probe synthesized:

> This is a Seven Record local caption test for clear software tutorials.

Whisper returned one timestamped segment:

> This is a seven-record local caption test for clear software tutorials.

Outputs:

- `captions.json` with versioned timestamped segments;
- `captions.srt`;
- `captions.vtt`.

The model is downloaded once to `%LOCALAPPDATA%\7Record\Models\ggml-tiny.bin`. Recorded audio is normalized locally to 16 kHz mono PCM through FFmpeg, transcribed locally, and the temporary normalized file is deleted.

The editor can generate captions from the microphone source and display them on the caption track. No recording is uploaded.

Caption segments now flow into `render-plan.json`. The isolated worker generates a temporary SRT and burns enabled captions through FFmpeg's subtitles filter. A real timed caption rendered successfully into a 1920 x 1080 H.264 MP4; the temporary SRT was removed after export.
