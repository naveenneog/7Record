# 7Record Resume Context

Last updated: 2026-07-27 23:30 IST

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
- The unpackaged `SevenRecord.App.exe` now bootstraps the Windows App SDK before WinUI activation and launches directly; the product name remains `7Record`.
- The recorder now centers one `Record` action: camera overlay defaults on and starts automatically with screen, microphone, and system audio through the accelerated Direct3D/Media Foundation path.
- Installed MSIX builds auto-select the primary display after programmatic-capture access; unpackaged development builds open `Capture with 7Record` and continue immediately after the user chooses an application or display.
- `Choose application or display` remains available for explicit source selection.
- `docs/spikes/architecture-recording-orchestration-spike.md` records the accepted migration away from `MainPage`-owned resources: neutral revisioned lifecycle state, Windows controller/active session, single-flight stop, one journal owner, optional-source failure isolation, and post-processing after raw finalization.
- `SevenRecord.Recording` now contains the tested neutral recorder state machine.
- `SevenRecord.Recording.Windows` now owns the Direct3D screen segment encoder/publisher; the next migration step is a reusable Windows controller/active-session aggregate and adapting `MainPage` to its snapshots.
- Active recording now uses one `RecordingProjectWriter` across screen/audio/camera, providing a single journal owner, serialized sequence allocation, and a cancellation-safe publication commit boundary.
- `WindowsRecordingSession` now owns the live project clock, pause mapping, capture resources, optional-source policy, single-flight stop, and raw source finalization; `MainPage` holds only this session and renders results.
- Missing microphone/system playback endpoints are warnings rather than hard blockers.
- Static or odd-sized application windows are copied through GPU render targets and paced to Media Foundation at 60 fps; the odd-window harness produced H.264 538×634 with clean screen/audio publication.
- Source acceptance now opens at one shared QPC start boundary and closes at one shared stop boundary; the validation journal reported 5.87 seconds while screen/microphone/system media measured 5.78/5.80/5.82 seconds.
- The recorder UI is now preview-first with native Recorder/Projects navigation, one branded Record action, state pill/timer/progress feedback, compact source rows, progressive health, adaptive 1024×720 stacking, dynamic UIA names, and contrast-theme resources.
- Redirected camera frames now cross Direct3D device domains through `VideoFrame.CopyToAsync`; the 1280×720 camera probe delivered 40 frames with zero drops and the full camera-on capture harness published screen/audio/camera.
- UI/UX and senior-QA findings, fixes, evidence, and residual manual checks are logged in `docs/qa/recorder-ui-ux-qa-2026-07-18.md`; `tools/test-recorder-ui.ps1` is the repeatable UIA/adaptivity gate.
- Projects now expose explicit `Open recording` actions and an inline `MediaPlayerElement`; playback prefers the newest exported MP4 and falls back to the immutable screen source with clear camera/audio composition messaging.
- `ProjectPostProcessingPipeline` now reloads persisted project artifacts and reruns cursor zoom, loading speed-up, and audio repair stages independently with deterministic IDs and atomic unchanged-aware writes.
- Recording Stop now returns control after raw publication and launches smart-edit analysis in the background, so a new recording is not blocked by FFmpeg loading detection.
- Recording now renders a throttled live screen preview plus a full-stream camera bubble using reusable GPU targets and disposable `SoftwareBitmap` frames; static windows continue previewing from the held-frame pacer.
- The live camera bubble supports pointer/keyboard placement, persists normalized coordinates into `presenter-layout.json`, and the FFmpeg exporter consumes those coordinates and dimensions.
- Camera copying uses each frame's actual Direct3D surface bounds, avoiding redirected-camera corner crops.
- Full release QA is logged in `docs/qa/full-functional-qa-2026-07-26.md`: 62 cases, 40 pass, 9 fail, 9 blocked, 3 not implemented, 1 not run; verdict **NOT RELEASE READY**.
- Highest-priority defects are missing production segment rollover (P0), severe Remote Audio microphone loss, multi-segment downstream omissions, editor recovery validation gaps, destructive export failure behavior, release/MSIX `NETSDK1150`, and missing unpackaged media-worker resolution.
- Release/runtime delivery is repaired: self-contained publish and unsigned x64 MSIX now succeed with the media worker included; direct builds resolve the same worker, and failed FFmpeg exports preserve the prior valid file.
- Project opening is integrity-gated and transactional, editor choices persist across reopen, captions reject overlap/out-of-range edits, and successful Stop opens the completed recording directly in Projects.
- WASAPI callbacks now only timestamp and enqueue owned packets; background consumers perform WAV writes and telemetry. Remote Audio loss improved substantially but remains device-side risk and is now surfaced live and in finalization warnings.
- Production capture now rolls screen, microphone, system audio, and camera into five-second journaled segments. Playback, captions, loading analysis, and export consume all segments through isolated concat-manifest consolidation.
- The Recorder now has a persistent pre-record camera studio: idle preview, zoom/crop framing, overlay size/placement, brightness enhancement, reset, accessibility status, local settings persistence, and matching export metadata.
- Loading speed-ups now require aligned visual freeze, cursor inactivity, and silence evidence from every available audio track; missing evidence, dropouts, failures, or cancellation cannot leave stale enabled edits.
- `packages/7record-cli` implements the secure `npx 7record` flow. Publishing is blocked only on provisioning and pinning the production MSIX signing certificate thumbprint.
- Camera Studio now controls native Windows Studio Effects person-aware Standard/Portrait blur when available. It verifies applied flags, preserves concurrent Windows changes, restores complete prior flags, fails closed for privacy, and falls back to Off on unsupported cameras.
- Projects now persist microphone/system-audio mute and gain controls; FFmpeg export preserves relative two-track balance and applies single-track gain after loudness normalization.
- Projects now persist synchronized non-destructive clip slices supporting trim, split, delete, reorder, undo, and redo; automation/captions are remapped and audio repairs are applied before shared clip edits.
- The project player now refreshes a low-resolution composite after clip, audio, framing, automation, preset, or caption changes using revisioned/cancellable media-worker renders.
- Verified commands:
  - `dotnet build SevenRecord.slnx --configuration Debug`
  - `dotnet test SevenRecord.slnx --configuration Debug --no-build`
- Next camera feature: add a pre-record studio preview with camera zoom/crop/pan controls; follow with optional background blur and low-light/brightness enhancement.
- Next distribution feature: produce signed versioned MSIX artifacts, then implement a thin `npx 7record` Windows installer/launcher that downloads, verifies, installs, and opens 7Record.

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
