# 7Record Resume Context

Last updated: 2026-07-18 09:07 IST

## Resume Here (deadline checkpoint)

- Scheduled loop `#2` was stopped at/after the 09:06 IST deadline.
- Current branch: `master` (clean working tree).
- Latest completed slices focused on capture-clock/audio sync telemetry hardening:
  - per-source mic/system health tracking in UI,
  - warning thresholds for drift/discontinuities/missing duration,
  - warning detail metrics (ms, ppm, missing duration),
  - immediate warning re-evaluation on resume,
  - readiness refresh after stop to clear stale live telemetry.
- Recommended next feature: editor/export hardening with targeted tests for the new audio warning behavior in `MainPage` orchestration.

## Mission

Build a Windows-first desktop screen recorder and smart editor for software tutorials, product demos, courses, developer content, and screen-led vlogging. It should record screen/window/region, cursor intent, webcam, microphone, and system audio as editable sources, then generate a nearly publishable first edit.

The defining workflow is:

1. Choose screen/window/region, camera, microphone, system audio, and presenter layout.
2. Record with pause/resume, keyboard shortcuts, health indicators, and crash-safe recovery.
3. Analyze the recording locally.
4. Generate reversible cursor zooms, cursor emphasis, loading/silence compression, captions, audio cleanup, and camera layouts.
5. Review in a focused timeline editor.
6. Export creator-ready MP4/WebM/GIF and social aspect-ratio variants.

## User Intent

Original request:

> "Lets build with deep research a Screen recorder software which can RECORD screen with mousepointer zoom and face by side or in a way that can be used for Vlogging look at modern vlogging features and build this software which provides almost ready to go post processing done with speed ups on loadings. Specially designed for software or screen record videos creation"

The user then warned that the device will restart and requested all context be saved for resumption.

## Autonomous Decisions

- Treat the product as a serious desktop tool, not a browser-only demo.
- Prioritize Windows for the first production build because the current environment is Windows and the user was unavailable to answer the platform question.
- Preserve an architecture path to macOS after the Windows recording core is proven.
- Use local-first project storage and media processing by default.
- Treat automation as non-destructive timeline events, never baked irreversibly into source capture.
- Follow WCAG 2.2 AA and keyboard-first desktop interaction.
- Use the visual direction in `DESIGN.md`: near-black editing suite, restrained smoky-magenta recording cue, cool-cyan automation cue.

## Research State

Research is complete and saved in:

- `docs/research/market-landscape.md`
- `docs/research/windows-architecture.md`

Two restarted research agents were cancelled without findings. Broad parallel web searches also timed out, so the successful approach used bounded retrieval of official vendor and platform documentation. The research reports preserve primary sources, uncertainty, prototype requirements, and acceptance thresholds.

The accepted stack is C#/.NET 10 + WinUI 3, Windows.Graphics.Capture/Direct3D 11, WASAPI, Media Foundation, a supervised FFmpeg worker, and MSIX. See `DECISIONS.md` D-005 through D-008.

## Current Implementation State

- `SevenRecord.slnx` contains the WinUI application, domain and platform modules, and MSTest projects described in the architecture report.
- `src/SevenRecord.App` builds against .NET 10 and Windows App SDK 2.3.1.
- The initial focused recorder workspace renders successfully as a packaged WinUI app.
- Domain timeline ranges and the 2-10 second recording segment policy have regression tests.
- Capture readiness now checks screen-capture support, camera, microphone, system audio, storage, and FFmpeg availability without blocking the UI thread.
- The secure Windows capture picker selects a display or application window; recording remains disabled until a target and all required checks are ready.
- Packaged WinUI runtime validation exposed and fixed two native-crash paths: direct FFmpeg execution in the UI process and WinRT device discovery on the STA thread.
- The QPC project clock and source drift estimator normalize screen/audio/camera timestamps, ignore initial device offsets, and detect accumulated parts-per-million drift over one-hour tests.
- Segmented recovery now atomically publishes project-local media, durably appends checksummed journal entries, tolerates only a corrupt crash tail, and reports missing, tampered, orphaned, and partial files without deleting evidence.
- `SevenRecord.Media.Worker` performs real synthetic encoder initialization. On the current machine NVENC, Quick Sync, and AMF are listed but fail runtime initialization; `libx264` validates successfully and is selected as an explicit fallback.
- The selected `GraphicsCaptureItem` now starts a free-threaded Direct3D 11 frame pool. An automated picker run captured a real Explorer frame, normalized its QPC time, stopped cleanly, and reported zero dropped frames.
- The first recoverable screen segment is complete: the app copied BGRA frames, paced a static source at 30 fps, encoded H.264 in the isolated worker, and atomically published a SHA-256-journaled Matroska segment.
- The automated performance gate rejected CPU readback for production: 1.42 CPU cores and 1.55 GB working set at 1.60 MP / 13.71 source fps, despite zero drops and 180 ms duration error.
- Direct3D-surface Media Foundation encoding now publishes recoverable MP4 segments with no pixel readback. It reduced working set to 559 MB with zero drops; CPU stayed near 1.45 cores because this machine has no working hardware encoder runtime.
- Microphone and WASAPI loopback now share the project QPC clock, publish independent float WAVs, report fitted drift, and record callback discontinuities. Remote Audio loopback is stable; microphone delivery can drop samples and is explicitly surfaced.
- Projects now persist `audio-timing.json`; the analysis layer generates reversible silence insertion and bounded playback-rate events from gaps and sustained drift.
- Webcam capture is implemented as an independent QPC-timestamped MediaFrameReader/MP4 source with normalized presenter-layout metadata. Current redirected cameras provide no frames, so the UI keeps camera disabled without blocking screen/audio.
- The recorder shell now lists recent projects and derives Ready, Recoverable, Needs Attention, or Corrupt state from journal replay and segment verification.
- Selecting a recent project now opens the first editor timeline with independent screen/camera/microphone/system-audio clips and reversible repair/layout automation.
- The timeline now builds a shared preview/export render plan with three aspect-ratio presets and per-automation enable/disable controls.
- The isolated media worker now exports persisted render plans to H.264/AAC fast-start MP4; a real 1920 x 1080 / 30 fps export was verified.
- Export now applies mid-track silence repair non-destructively; a 200 ms event was measured as 199.865 ms in the final MP4.
- Offline Whisper captions now generate versioned timeline segments plus SRT/VTT; the tiny model is cached under Local AppData and no audio is uploaded.
- Enabled timeline captions are now burned into MP4 exports through a temporary worker-generated SRT while persistent SRT/VTT remain available.
- Caption text and timing are editable in the timeline with undo/redo; each state regenerates JSON, SRT, and VTT without touching audio.
- Cursor movement/click metadata and reversible zoom suggestions are implemented. The current remote desktop exposes no cursor, so the optional path awaits interactive-desktop validation.
- Enabled cursor zoom suggestions now render through smooth time-based zoompan expressions before camera/caption composition.
- Post-recording low-motion detection now creates reversible 4x loading/waiting suggestions and handles freezes that continue through EOF.
- Enabled loading suggestions now retime composed video and mixed audio together; a 5.0-second fixture rendered to the expected 3.5 seconds.
- Recording now pauses screen/audio/camera/cursor sampling and resumes on a gap-free active timeline.
- System-wide global shortcuts now register `Ctrl+Shift+R` for start/stop and `Ctrl+Shift+P` for pause/resume with conflict feedback when another app owns the shortcut.
- Recording health now shows explicit shortcut status so users can see whether global hotkeys are active or unavailable at startup.
- Recording health includes a `Retry shortcuts` action to re-attempt global shortcut registration after conflicts clear.
- The retry action now disables itself while shortcuts are active and re-enables only when registration is unavailable.
- Successful hotkey registration now restores the readiness banner so prior shortcut-conflict warnings do not linger.
- The record/pause controls now expose hotkey tooltip hints so shortcut discovery does not depend on external docs.
- Live audio telemetry now tracks mic and system capture health independently and raises an explicit in-recording warning when drift/discontinuity risk crosses threshold.
- Pause/resume now refreshes audio warning state immediately on resume, so risk banners stay accurate even before the next audio callback.
- Audio risk warnings now include live drift and discontinuity numbers per source for faster diagnosis while recording.
- Audio health now includes per-source cumulative missing duration and raises warning state when missing audio exceeds threshold.
- Audio health/warnings now include per-source drift-rate ppm, improving visibility into sustained clock skew.
- Recording stop now triggers readiness rechecks, clearing stale live-capture telemetry from status panels.
- Verified commands:
  - `dotnet build SevenRecord.slnx --configuration Debug`
  - `dotnet test SevenRecord.slnx --configuration Debug --no-build`
- Next feature: continue export and editor hardening (UI polish, packaging gates, and end-to-end capture stress validation).

## Environment Observed

- Working directory: `C:\Users\navg\DailyApps\7Record`
- Directory was empty at project start.
- Node.js: `v26.1.0`
- npm: `11.13.0`
- .NET SDK: `10.0.302`
- Rust was not installed.
- FFmpeg `8.1.2-full_build-www.gyan.dev` is installed and working.
- Visual Studio Community 2026 and Visual Studio Enterprise 2022 are installed.
- Windows App Runtime 1.6 through 2.3 is installed.
- The repository is initialized; foundation commit is `219dbde`.

## Product and Design Files

- `PRODUCT.md`: strategic product definition, users, positioning, principles, and accessibility.
- `DESIGN.md`: seed design system following the DESIGN.md specification.
- `CONTEXT.md`: this durable resume document.
- `CHANGELOG.md`: chronological work record.
- `DECISIONS.md`: architecture and product decisions, including unresolved choices.

## Planned Feature Set

### MVP

- Screen/window/region capture at up to 60 fps.
- Microphone, system audio, and webcam capture as independently adjustable sources.
- Cursor position/click metadata and automatic cursor smoothing/emphasis.
- Automatic zoom/pan suggestions around clicks, fields, and active UI regions.
- Presenter camera modes: side-by-side, circular/rounded overlay, full presenter, screen-only, and scene switching.
- Pause/resume and global keyboard shortcuts.
- Crash-safe segmented recording with project recovery.
- Local project library and non-destructive timeline.
- Silence detection and reversible removal/compression.
- Loading/waiting detection using low visual change plus low interaction/audio activity, represented as speed-up events.
- Captions with editable transcript and subtitle export.
- Basic mic cleanup, loudness normalization, and screen/camera framing.
- MP4 export with common landscape, portrait, and square presets.

### Post-MVP

- Text-based editing from transcript.
- AI-assisted chapter/title/description generation.
- Background removal and camera relighting.
- Eye-contact correction where hardware/performance permits.
- Multi-scene templates and brand kits.
- B-roll, callouts, keystroke overlays, and automatic sensitive-data blur.
- Cloud review links and team collaboration as an optional layer.
- macOS capture implementation.

## Accepted Architecture Principles

- Separate capture, media, analysis, project model, editor UI, preview, and export into stable modules.
- Store screen, camera, microphone, system audio, cursor events, keystrokes (opt-in), and edit decisions separately.
- Record in short recoverable segments with a journal/manifest updated atomically.
- Prefer hardware encoding with a software fallback.
- Use proxy media and cached waveforms/thumbnails for responsive editing.
- Keep the project format versioned and human-inspectable where practical.
- Never require cloud upload for core capture, editing, or export.

## Required Resume Sequence

1. Read `CONTEXT.md`, `PRODUCT.md`, `DESIGN.md`, `DECISIONS.md`, and `CHANGELOG.md`.
2. Read both reports under `docs/research/`.
3. Scaffold the .NET 10 solution and WinUI 3 application shell using the module boundaries in the architecture report.
4. Prototype capture clock/synchronization, segmented recovery, and the GPU-to-encoder path before advanced editor work.
5. Commit every feature independently.
6. Maintain `CHANGELOG.md` and `DECISIONS.md`, including attempted approaches and failures.

## Work Checklist

- [x] Complete and save market research.
- [x] Complete and save technical architecture research.
- [x] Select stack and update `DECISIONS.md`.
- [x] Initialize repository and commit foundation docs.
- [x] Scaffold desktop app and automated tests.
- [x] Build capture source selection and readiness UI.
- [ ] Implement screen capture.
- [ ] Implement mic/system audio capture.
- [ ] Implement webcam capture and presenter layouts.
- [ ] Implement cursor metadata and zoom automation.
- [ ] Implement crash-safe project persistence.
- [ ] Implement editor preview and timeline.
- [ ] Implement silence/loading analysis and speed-up events.
- [ ] Implement captions and transcript editing.
- [ ] Implement export presets.
- [ ] Run regression, performance, recovery, accessibility, and packaging validation.

## Version-Control Convention

The user prefers every feature as a separate, well-scoped commit/branch, with clean and documented code. Commits created by Copilot must include:

```text
Co-authored-by: Copilot <223556219+Copilot@users.noreply.github.com>
Copilot-Session: 484070fd-4b16-4ade-a1b8-d9c82b114641
```
