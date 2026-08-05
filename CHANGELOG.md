# Changelog

All notable work, attempted approaches, failures, and decisions are recorded here so the project can resume without repeating failed paths.

## 2026-08-05

### Added

- Adopted the Ironclad engineering harness: `.ironclad/charter.json`, a vendored gate, a pre-commit hook and a CI workflow, so the project's rules are checked by a program rather than remembered.
- Declared 10 architecture boundaries in the charter, all verified passing: `SevenRecord.Domain` may import nothing but the BCL, and the Export/Editor/Analysis/Recording/Media/Transcription layers may not import WinUI or platform namespaces.
- Recorded 9 file-size exceptions, each with a stated reason and a ceiling pinned at its current size, making every one a downward-only ratchet.
- Added `docs/ROADMAP.md`, `docs/STATUS.md`, `docs/UNKNOWNS.md` and ADR-0002, produced from a full IronClad + Overdrive audit of the build.
- Added `SevenRecord.Infrastructure.Diagnostics`: a bounded, rotating, never-throwing file diagnostic log, and a `FaultBarrier` that contains, records and reports faults from guarded operations.

### Fixed

- **Release builds could die silently and lose the user's recording.** The only `UnhandledException` subscription in the app came from the generated `App.g.i.cs`, which is wrapped in `#if DEBUG` and only acts when a debugger is attached, so a shipped build had no handler at all. Any exception escaping one of the UI layer's 33 `async void` handlers terminated the process with no message and no log. `App` now installs handlers for `Application.UnhandledException` (marked handled, so the app survives and the user can still stop and keep their recording), `AppDomain.CurrentDomain.UnhandledException` (last-chance record before termination) and `TaskScheduler.UnobservedTaskException` (fire-and-forget failures, which .NET otherwise discards).
- Gave the app diagnostics at all. There was previously no `ILogger`, no trace and no crash log anywhere in `src/`, which is the direct reason three QA findings were stuck as "blocked, needs local hardware" — they could not be diagnosed because the app reported nothing.
- Kept the diagnostic log bounded across process restarts: a new run adopts a partially-filled file from a previous run and seeds its byte counter from the real file length, so the size budget is a property of the file rather than of a single process lifetime.

### Audit findings recorded (not yet fixed)

An Architect review of `MainPage.xaml.cs` found nine defects, five user-visible. They are recorded with file:line provenance in `docs/ROADMAP.md` as packets P-2 through P-7: recorder start re-entrancy, cross-project export contamination, missing shutdown ownership for background jobs, unguarded page initialization, caption edit corruption, unscoped debounced editor saves, and camera settings save races.

## 2026-07-27

### Fixed

- Made `SevenRecord.Media.Worker` a self-contained runtime payload rather than an incompatible executable project dependency.
- Added one `MediaWorkerLocator` for direct and packaged runtime layouts.
- Included the complete worker in Debug output, self-contained x64 publish output, and the generated x64 MSIX.
- Corrected the MSIX programmatic graphics-capture capability namespace.
- Disabled release trimming until source-generated JSON metadata is available, avoiding trim-unsafe editor/runtime serialization.
- Made FFmpeg export atomic: failed renders leave the prior valid MP4 untouched and clean up partial files.
- Added explicit feedback when Windows returns no capture source instead of silently returning to Ready.
- Restored unpackaged loading analysis by resolving the worker from the actual runtime output.
- Gated timeline loading on journaled media integrity so missing or tampered sources cannot enter playback/export.
- Made project opening transactional: stale preview, caption, timeline, and export state is cleared before load and committed only after all project data succeeds.
- Added deterministic audio-repair and presenter-layout automation identifiers.
- Persisted render preset and disabled automation choices in atomic `editor-state.json`.
- Validated complete caption documents for ordering, overlap, and recording bounds before accepting edits.
- Sorted SRT/VTT output by caption start time.
- Disabled project opening for Needs Attention and Corrupt library states.
- Moved microphone and system-audio packet processing off NAudio callback threads onto independent bounded consumers.
- Added explicit audio queue-overflow telemetry and live/final quality warnings.
- Preserved partial audio publication when a source writer fails instead of abandoning all previously captured audio.
- Wired the five-second recording policy into screen, microphone, system-audio, and camera recorders.
- Published completed rollover segments through the shared journal while capture continues.
- Prevented empty tail segments when Stop races an encoder rollover.
- Added camera-source fallback across all available color source groups.
- Made playback, captions, loading analysis, and export consume every ordered source segment.
- Consolidated long segment lists inside the isolated media worker via FFmpeg concat manifests, avoiding Windows command-line limits.
- Restored loading detection across segment boundaries by analyzing one consolidated screen source.
- Replaced visual-only loading speed-ups with a fail-closed confidence planner requiring cursor coverage and silence on every available audio track.
- Ignored stationary cursor heartbeat samples while treating real movement and clicks as activity.
- Mapped silence evidence through each track's recorded audio dropouts.
- Invalidated stale loading plans when confidence analysis fails or is canceled.
- Terminated concatenation and silence worker process trees on cancellation.

### Validation

- Media tests: 14 passed.
- Export tests: 8 passed, including failed-export preservation.
- Self-contained x64 publish contains `MediaWorker\SevenRecord.Media.Worker.exe`.
- Unsigned x64 MSIX generated successfully and contains 472 media-worker payload entries.
- Editor tests: 7 passed; transcription tests: 3 passed.
- UIA playback/navigation/adaptive gate passed at the active 150% DPI scale.
- Audio and recording tests pass with the queued capture path; the 8-second Remote Audio probe improved from 2,225.8 ms missing to 335.5 ms with zero queue overflows.
- Forced-kill audio recovery preserved ten journaled five-second segments and left only the two active partial files.
- Real multi-segment export produced a 10.0-second H.264/AAC file.
- Cross-boundary loading fixture detected a two-second freeze spanning two five-second source segments.
- Full build completed with zero warnings/errors; automated and UIA suites passed.
- Camera studio release suite: 102 tests passed, zero warnings/errors, UIA passed.
- Loading confidence release suite: 100 tests passed, zero warnings/errors; synthetic silent audio produced one silence interval while audible audio produced none.
- npm installer tests: 5 passed; package dry-run contains only the CLI, installer, README, and manifest.
- The npm installer intentionally fails closed until the production signing certificate thumbprint is provisioned and pinned.
- Windows Studio effect tests validate native driver flags and ABI layout; full build/tests and UIA pass with zero warnings/errors.
- Current redirected camera hardware does not advertise Studio blur and intermittently withheld preview frames; unsupported hardware remains safely Off with explicit status.
- Audio mixer release suite passed with zero warnings/errors; a real single-track fixture measured an exact +6.0 dB exported gain delta.
- Clip-editor release suite passed with zero warnings/errors; a real edit rendered three seconds from a later green segment followed by two seconds from an earlier blue segment with synchronized audio.
- Edited-preview release suite passed with zero warnings/errors and UIA; preview scratch files and canceled worker processes are cleaned per render job.
- Redirected camera sources were enumerated but delivered no frames during final camera-studio hardware validation; device acceptance must be rerun outside the current Remote Desktop state.

### Added

- Successful recording finalization now navigates directly to Projects and opens the completed recording for review.
- Added a persistent pre-record camera studio with idle preview lifecycle, zoom, horizontal/vertical framing, overlay sizing, brightness enhancement, reset, and settings persistence.
- Added a reusable camera crop geometry model and effects-ready presenter metadata that remains reversible.
- Applied the same camera framing and exposure controls to live recording preview and final FFmpeg export.
- Added accessible camera-position/framing status and native slider controls.
- Added a zero-dependency `npx 7record` Windows installer package with architecture selection, GitHub release resolution, SHA-256 verification, pinned publisher verification, package-identity validation, MSIX installation, and launch.
- Added a signed multi-architecture release packaging script that emits release assets and checksum files.
- Added person-aware Standard and Portrait background blur through Windows Studio Effects on supported NPU cameras.
- Added camera-effect capability discovery, shared-readonly compatibility fallback, exact driver SET readback, and full raw-state restoration.
- Added fail-closed privacy behavior: requested blur cannot silently record unblurred frames, and recording cannot start after failed camera-state restoration.
- Added serialized camera-effect transitions and an awaited window-close shutdown path so effect restoration completes before process exit.
- Added persistent microphone and system-audio mute/gain controls to the project editor.
- Added audio-mix settings to editor state and render plans with legacy-state defaults.
- Applied relative per-track gain before two-track mixing and post-normalization gain for single-track exports.
- Added non-destructive synchronized clip trim, split, delete, reorder, undo, and redo controls.
- Added atomic `clip-edits.json` persistence with immutable source ranges shared by every media track.
- Remapped visual automation and captions into edited output time while applying audio repair before clip edits.
- Added FFmpeg trim/reorder filter graphs that apply the same edit sequence to screen, camera, microphone, and system audio.
- Added edit-aware low-resolution preview rendering through the same media-worker graph used by final export.
- Added latest-revision project-open and preview cancellation so stale loads/renders cannot replace current editor state.

## 2026-07-26

### QA

- Executed a 62-case full functional QA pass covering build/tests, native UI, accessibility, camera, audio, cursor metadata, post-processing, recovery, playback, real FFmpeg exports, captions, packaging, and distribution.
- Logged the complete matrix and 17 prioritized findings in `docs/qa/full-functional-qa-2026-07-26.md`.
- Verified 40 passes, 9 failures, 9 environment-blocked cases, 3 unimplemented features, and 1 project caption case not run.
- Confirmed real landscape, portrait, and square exports with H.264 video, AAC audio, rounded camera overlay, and visible caption burn-in.
- Set release verdict to **NOT RELEASE READY** because crash-safe segment rollover is not wired, microphone capture loses seconds, packaging fails, and multiple recovery/export integrity defects remain.

## 2026-07-21

### Added

- Added a throttled live screen preview driven by the existing 60 fps held-frame pacer, including static application windows that emit only one capture frame.
- Added a live camera bubble with full-stream framing, rounded presentation, keyboard placement, pointer dragging, and persisted normalized layout metadata.
- Added reusable GPU preview surfaces and disposable `SoftwareBitmap` leases so live preview avoids sustained large-object-heap allocation churn.
- Updated camera capture to copy each frame's actual Direct3D surface bounds instead of configured dimensions, preventing redirected/rotated feeds from showing a corner crop.
- Updated MP4 export to consume presenter-layout position, size, mode, and rounded-bubble metadata instead of hardcoding the camera at bottom-right.
- Extended the camera probe and full capture harness to verify live screen preview, live camera preview, drag persistence, and all four recorded sources.

### Fixed

- Synchronized camera disposal with active frame processing and pending preview conversion.
- Kept camera overlay placement relative to the visible letterboxed screen frame so preview and export coordinates match.
- Moved overlay pointer capture to the preview canvas so dragging cannot be stolen by the recorder `ScrollViewer`.

## 2026-07-20

### Added

- Extracted cursor zoom, loading speed-up, and audio repair generation into a rerunnable project post-processing pipeline.
- Made generated automation identifiers deterministic and plan writes atomic/unchanged-aware so repeated processing produces byte-for-byte stable artifacts.
- Moved smart-edit processing off the recording Stop path so recorder controls and the next capture are not blocked by FFmpeg analysis.
- Added stage-isolation and idempotence regression tests, including malformed-input recovery and worker-output normalization.

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
- Disabled `Retry shortcuts` automatically when global hotkeys are active and re-enabled it when registration is unavailable.
- Cleared stale global-shortcut warning banners automatically after successful shortcut registration/retry.
- Added tooltip hints on recording controls to advertise `Ctrl+Shift+R` and `Ctrl+Shift+P` shortcuts in-app.
- Hardened live audio sync telemetry by tracking microphone and system drift/discontinuities independently and surfacing in-recording warnings only when thresholds are exceeded.
- Resume now immediately re-evaluates current audio sync risk so warning state stays accurate without waiting for the next callback.
- Audio sync warnings now include live drift/discontinuity metrics for each affected source to improve diagnosis during recording.
- Audio sync telemetry now reports cumulative missing-duration per source and treats significant missing audio as warning-risk alongside drift/discontinuities.
- Audio telemetry and warning details now include drift-rate parts-per-million (ppm) per source for clearer long-recording clock-skew diagnosis.
- Stopping a recording now reruns readiness checks so post-capture UI status resets from live telemetry back to current device/encoder health.
- Fixed direct unpackaged startup by initializing the Windows App SDK dynamic dependency before WinUI activation; packaged launches remain a no-op.
- Simplified the recorder around one `Record` action: the camera overlay is on by default, the first enumerated/default camera starts automatically, and no camera pre-configuration is required.
- Added installed-MSIX primary-display auto-selection with programmatic capture permission while retaining `Choose application or display` for window/display selection.
- Kept the unpackaged development build safe: Record opens the Windows source picker and continues immediately after selection.
- Completed the recording-orchestration architecture spike: accepted a neutral lifecycle state machine, Windows controller/active-session boundary, single-flight teardown, one journal owner, optional-source failure isolation, and separate post-processing pipeline.
- Added a thread-safe, revisioned recorder lifecycle state machine (`Idle`, `Starting`, `Recording`, `Paused`, `Stopping`, `Faulted`) with concurrent-stop and invalid-transition tests.
- Added `SevenRecord.Recording.Windows` and moved the Direct3D screen encoder/publisher out of the WinUI project without changing recording behavior.
- Added one session-owned `RecordingProjectWriter` for serialized segment sequence allocation and journal publication across screen, microphone, system audio, and camera.
- Made segment move→journal append a non-cancellable commit boundary so caller cancellation cannot orphan a successfully moved segment.
- Added `WindowsRecordingSession` as the active resource aggregate; `MainPage` now holds one session instead of concrete screen/audio/camera/cursor recorders.
- Added single-flight stop/finalization, stop-during-start cancellation, explicit stop reasons, and processor-fault teardown.
- Made microphone/system audio optional at runtime so missing endpoints no longer prevent screen recording.
- Added GPU frame pacing that holds the latest change-driven Windows capture surface at 60 fps and pads odd application-window dimensions to codec-safe even dimensions without CPU readback.
- Updated the capture performance harness to launch the verified executable directly and use reliable coordinate-click fallbacks for WinUI controls.
- Added a shared gated start/stop boundary and frozen active duration; a 5.87-second journal aligned with screen/microphone/system media at 5.78/5.80/5.82 seconds.
- Preserved terminal screen-processing failures in finalization results and kept the Direct3D capture device alive until the GPU pacer and encoder drain.
- Rebuilt the WinUI recorder as a preview-first native workspace with real `NavigationView` destinations, one magenta Record action, state pill/timer/progress feedback, compact source rows, progressive health, and adaptive 1024×720 stacking.
- Added dynamic Record/Stop/Cancel and Pause/Resume automation names, dynamic status live-region naming, high-contrast resources, Win32 title-bar contrast colors, 40–44 DIP targets, and human-readable project names.
- Fixed redirected-camera frame processing by replacing cross-device Win2D drawing (`D2DERR_WRONG_RESOURCE_DOMAIN`) with `VideoFrame.CopyToAsync`; camera probe and camera-on full recording now pass.
- Added `tools/test-recorder-ui.ps1` and updated the capture performance harness for the redesigned progressive-disclosure UI.
- Logged baseline, post-redesign, corrective actions, QA evidence, and residual release risks in `docs/qa/recorder-ui-ux-qa-2026-07-18.md`.
- Added explicit `Open recording` actions, inline native playback with transport controls, exported-MP4 preference, raw-screen fallback messaging, and Open in player / Open project folder actions.

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
- Direct unpackaged primary-monitor creation through `IGraphicsCaptureItemInterop` and unguarded display-ID access both caused native WinUI fail-fast crashes (`0xc000027b`) on this runtime; those paths were rejected in favor of package-gated programmatic capture and the safe picker fallback.

### Pending

- App scaffold, capture implementation, smart editor, tests, packaging, and release validation.
