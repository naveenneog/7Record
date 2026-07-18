# Changelog

All notable work, attempted approaches, failures, and decisions are recorded here so the project can resume without repeating failed paths.

## 2026-07-17

### Added

- Established the product strategy in `PRODUCT.md`.
- Seeded the visual design system in `DESIGN.md`.
- Added durable restart/resume context in `CONTEXT.md`.
- Added the initial architecture decision log in `DECISIONS.md`.
- Scaffolded the .NET 10 / WinUI 3 solution with the accepted module boundaries.
- Added the first recorder workspace shell, domain timeline primitives, segment policy, and automated tests.
- Added live readiness checks for Windows screen capture, camera, microphone, system audio, storage, and FFmpeg encoders.
- Wired readiness state into the recorder workspace with explicit blocking reasons and regression tests.
- Added the secure Windows display/window picker and require a selected capture target before recording can start.
- Added the QPC project clock, cross-frequency normalization, and source drift estimator with one-hour synchronization tests.
- Added atomic segment publication, checksummed journal replay, corrupt-tail recovery, and orphan/tamper inspection.
- Added the isolated media worker, real FFmpeg encoder enumeration, and deterministic hardware-to-software fallback selection.
- Validated the current machine: NVENC, Quick Sync, and AMF are advertised but fail initialization; software `libx264` succeeds and is selected as fallback.
- Added the first free-threaded Windows.Graphics.Capture frame pool, QPC frame timestamps, bounded processing queue, resize handling, frame-drop health, and a Direct3D readiness gate.
- Added raw BGRA surface copying and an isolated worker command that encodes BGRA frame streams into Matroska segments.
- Wired live screen capture through a 30 fps frame pacer into the packaged media worker and crash-safe segment publisher.
- Added an automated capture performance harness and rejected CPU readback as the production path after measuring 1.42 CPU cores and 1.55 GB at 13.71 source fps.
- Added Direct3D-surface Media Foundation encoding; the working set fell to 559 MB with zero drops, while software-encoder CPU remained about 1.45 cores.
- Added synchronized WASAPI microphone and loopback capture, fitted clock drift, discontinuity detection, recoverable WAV publication, and audio timing tests.
- Added versioned audio timing metadata and deterministic silence/rate repair events without modifying source WAVs.
- Added optional QPC-synchronized webcam capture, GPU camera-surface conversion, recoverable camera MP4 publication, presenter-layout metadata, and explicit first-frame validation.
- Added project discovery, journal/segment health inspection, recovery states, recent-project UI, and regression tests.
- Added the first editor timeline loader and UI for independent source tracks, audio repair events, and presenter-layout automation.
- Added shared preview/export render plans, landscape/portrait/square presets, automation toggles, persistence, and regression tests.
- Added isolated render-plan MP4 export with screen framing, optional camera overlay, mixed normalized audio, worker invocation, UI export controls, and real-file validation.
- Added mid-track silence repair export and verified a requested 200 ms gap within 0.2 ms.
- Added offline Whisper caption generation, cached local model download, timeline captions, JSON/SRT/VTT output, UI controls, and formatter tests.
- Added caption-aware render plans and verified subtitle burn-in through the isolated MP4 worker.
- Added caption text/timing editing, atomic persistence, and undo/redo controls with regression tests.
- Added QPC cursor movement/click metadata, persisted cursor events, automatic click-centered zoom suggestions, timeline loading, and tests.
- Added structured cursor zoom render-plan metadata and verified time/center-aware zoompan MP4 export.
- Added isolated low-motion loading detection, EOF interval handling, persisted 4x speed suggestions, timeline loading, and tests.
- Added synchronized video/audio loading speed-up export and verified an exact 5.0-to-3.5-second render.
- Added active-time pause/resume mapping across screen/audio/camera/cursor paths, UI controls, keyboard accelerators, and tests.
- Added system-wide `Ctrl+Shift+R` / `Ctrl+Shift+P` global hotkeys with Win32 registration, conflict reporting, and clean unregister on app unload.
- Added a recorder health-panel shortcut status line so users can immediately see whether global hotkeys are active or unavailable.
- Added a `Retry shortcuts` action in recording health so global-hotkey registration can be retried after startup conflicts without restarting.

### Research

- Started a market research agent covering modern creator screen recorders and smart editors.
- Started a Windows capture architecture research agent.
- Added a sourced competitor matrix and MVP recommendation in `docs/research/market-landscape.md`.
- Added the Windows capture/media architecture recommendation in `docs/research/windows-architecture.md`.
- Accepted the production stack and capture/recovery/process-boundary decisions in `DECISIONS.md`.
- Updated `CONTEXT.md` with the completed research state and exact scaffold/prototype sequence.

### Attempted

- Direct web research calls for competitor and Windows capture information timed out and returned no usable findings.
- Environment probe confirmed Node.js, npm, and .NET; the probe stopped when Rust was not found, so FFmpeg still needs a separate check.
- Both restarted research agents were cancelled after about 22 minutes without returning findings.
- Parallel broad web searches mostly timed out, so research switched to bounded official-page retrieval.
- Running `ffmpeg -encoders` directly from the packaged WinUI process crashed the Windows App Runtime with `0xc000027b` / `0x8000ffff`; readiness now performs safe executable discovery and leaves encoder enumeration to the isolated media worker required by D-008.
- Running WinRT device discovery on the WinUI STA thread produced the same native crash; the Windows readiness probe now executes on an MTA worker and marshals only results back to the UI.
- WPF and same-publisher WinUI benchmark windows were filtered by the system picker, while automated Edge selection remained pending; the working harness uses the picker-visible Android emulator and clears stale picker windows between runs.
- Media Foundation initially left its output stream open through publication; closing it after transcode completion fixed the atomic MP4 move.
- Remote Audio microphone capture showed variable loss (one 60-second run missed about 1.1 seconds), so dropouts are tracked separately from clock drift.
- Both redirected camera devices enumerate but deliver no frames in the current environment; camera configuration now times out safely and remains optional.
- The remote desktop session exposes no cursor through `GetCursorPos`; cursor metadata now reports unavailable and remains optional.

### Pending

- App scaffold, capture implementation, smart editor, tests, packaging, and release validation.
