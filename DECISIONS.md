# Decision Log

## D-001: Windows-first production target

**Status:** Accepted provisionally  
**Date:** 2026-07-17

The first production build targets Windows desktop. The user was unavailable to answer the platform interview before a restart, and Windows is the active development environment. Architecture should preserve a macOS path without forcing MVP capture code into a lowest-common-denominator abstraction.

## D-002: Product register

**Status:** Accepted

7Record is a product/tool interface. Design serves the recording and editing workflow rather than acting as a marketing surface.

## D-003: Local-first, non-destructive projects

**Status:** Accepted

Core recording, analysis, editing, recovery, and export work locally. Screen, camera, audio, cursor events, analysis results, and edit decisions remain separate so automated edits can be disabled or adjusted.

## D-004: Smart edits are visible timeline events

**Status:** Accepted

Automatic cursor zooms, silence changes, loading speed-ups, presenter scenes, captions, and cleanup decisions must be inspectable and reversible rather than hidden behind a destructive magic-edit action.

## D-005: Technical stack

**Status:** Accepted  
**Date:** 2026-07-17

The selected stack is:

- C#/.NET 10 application and domain services.
- WinUI 3 on the stable Windows App SDK for the Windows shell.
- Windows.Graphics.Capture with Direct3D 11 for primary screen/window capture.
- WASAPI for system-audio loopback and microphone capture.
- Media Foundation for camera ingestion and live source encoding.
- FFmpeg as a supervised worker for probing, proxies, analysis, composition, and export.
- MSIX as the primary signed distribution format.

A narrow C++/WinRT bridge may be introduced only where profiling or API access proves it necessary. The first implementation must not create a large native core speculatively.

This decision follows the evidence and risks documented in `docs/research/windows-architecture.md`. WPF remains the fallback shell if the WinUI timeline/Direct3D prototype fails its interaction target. Avalonia, Electron, Tauri, and Qt do not remove the need for platform-specific capture and add cost to the Windows-first MVP.

## D-006: Capture API strategy

**Status:** Accepted  
**Date:** 2026-07-17

Use Windows.Graphics.Capture as the default monitor/window capture path. Region capture crops a monitor surface on the GPU. Desktop Duplication is a fallback/prototype for lower-level pointer and monitor-update needs, not the primary architecture.

Cursor interaction metadata is recorded separately on the project QPC clock. Where supported, the baked cursor may be disabled and reconstructed non-destructively.

## D-007: Recording recovery model

**Status:** Accepted  
**Date:** 2026-07-17

Record independent, short, finalized source segments plus an append-only checksummed journal. Project state is published atomically. A crash may lose only the active segment, never the complete recording.

The intermediate container remains prototype-gated: compare short MP4, fragmented MP4, and Matroska before accepting a container decision.

## D-008: FFmpeg process boundary

**Status:** Accepted  
**Date:** 2026-07-17

FFmpeg runs as a pinned, supervised worker outside the UI/capture process. Capture must survive analysis-worker failure. The application owns capability probing, timeouts, cancellation, logs, and software fallback.

## D-009: Capture clock

**Status:** Accepted  
**Date:** 2026-07-17

Use QueryPerformanceCounter as the project clock. Windows.Graphics.Capture `SystemRelativeTime`, audio device positions, camera timestamps, cursor events, and health events are normalized to a zero-based project time.

Each independently clocked source tracks elapsed source time against elapsed project time. Initial device offsets are ignored; accumulated drift is measured in duration and parts per million. Correction is applied only in preview/export so immutable source timing remains available for diagnosis and reprocessing.

## D-010: Recording journal

**Status:** Accepted  
**Date:** 2026-07-17

Each finalized source segment is atomically moved from a project-local temporary path into `segments/`, then appended to a checksummed newline-delimited JSON journal with a durable flush.

Recovery accepts an incomplete final journal line as a crash tail but rejects corruption in the middle. Referenced segments are verified by length and SHA-256. Missing, corrupt, orphaned, and temporary files are reported without automatic deletion.

## D-011: H.264 encoder fallback order

**Status:** Accepted  
**Date:** 2026-07-17

Encoder enumeration runs in `SevenRecord.Media.Worker`, never in the packaged WinUI process. Automatic H.264 selection uses NVIDIA NVENC, Intel Quick Sync, AMD AMF, then software `libx264`.

A user preference moves its encoder to the front but does not disable fallback. The worker returns a structured report containing the discovered capabilities, selected encoder, and whether fallback occurred.

## D-012: Screen frame delivery

**Status:** Accepted  
**Date:** 2026-07-17

Use a free-threaded `Direct3D11CaptureFramePool` with three buffers. Frame callbacks normalize `SystemRelativeTime` through the project QPC clock and enqueue at most three disposable frame leases for a single asynchronous processor.

When processing falls behind, new frames are dropped and counted rather than blocking capture. Size changes drop the transition frame and recreate the pool only after queued leases drain. Frame surfaces remain valid only for the lifetime of their lease.

## D-013: First screen segment path

**Status:** Accepted as prototype  
**Date:** 2026-07-17

The first recoverable path copies BGRA pixels from each leased Direct3D surface, keeps the latest image in a 30 fps pacer, streams frames to the packaged media worker, and encodes a Matroska/H.264 temporary file. On stop, the file is published through D-010.

This CPU-readback path is accepted only as a correctness prototype. It must be benchmarked against the 1080p60 CPU/drop targets and replaced with GPU sharing or a native hardware encoder path if it cannot meet them.

## D-014: CPU readback is not the production encoder path

**Status:** Accepted  
**Date:** 2026-07-18

The benchmark in `docs/prototypes/capture-performance-2026-07-18.md` measured 1.42 CPU cores and 1.55 GB combined working set at 1.60 MP and 13.71 source fps. Although it produced zero drops and a 180 ms duration error, it did not approach 60 source fps.

Keep BGRA readback only as a diagnostic/compatibility fallback. The production path must share Direct3D surfaces with Media Foundation or a narrow native encoder bridge and must be re-benchmarked at 1080p60.

## D-015: Direct3D surface encoding

**Status:** Accepted as production architecture; release gate open  
**Date:** 2026-07-18

Create `MediaStreamSample` objects directly from leased Direct3D 11 surfaces and feed them through `MediaStreamSource` to a hardware-enabled `MediaTranscoder`. Keep the capture-frame lease alive until the sample's `Processed` event.

The prototype produced a recoverable 874 x 1980 H.264 MP4 with zero drops and reduced combined working set from 1.55 GB to 559 MB. CPU remained near 1.45 cores because the reference machine fell back to software encoding. Release still requires a true 1080p60 hardware-encoder benchmark.

## D-016: Audio clock and recovery

**Status:** Accepted  
**Date:** 2026-07-18

Capture microphone and WASAPI loopback independently through the Windows audio module. Stamp packets on the project QPC clock, track device sample position, fit drift over time, and record callback discontinuities separately.

Write recoverable 32-bit float WAV intermediates and journal them after the screen segment. Preview/export may insert silence or apply bounded asynchronous resampling, but source samples remain immutable.

## D-017: Audio repair events

**Status:** Accepted  
**Date:** 2026-07-18

Persist versioned `audio-timing.json` metadata beside source WAVs. Convert gaps into `InsertSilence` events immediately. Convert sustained drift into `AdjustPlaybackRate` only after 30 seconds and 50 ppm, clamped to 0.995-1.005.

Repair events are reversible timeline decisions. They never modify recorded audio samples.

## D-018: Optional webcam and presenter layout

**Status:** Accepted; hardware validation pending  
**Date:** 2026-07-18

Capture camera frames independently with `MediaFrameReader`, normalize them on the project QPC clock, convert incompatible camera textures through a GPU-only BGRA render target, and encode a separate Media Foundation MP4.

Camera configuration must prove a processed frame within five seconds. Failure never blocks screen/audio recording. Presenter placement is stored in normalized `presenter-layout.json` metadata and is never baked into source media.

## D-019: Project recovery states

**Status:** Accepted  
**Date:** 2026-07-18

The project library derives state from journal replay and segment inspection:

- **Ready:** every journaled source exists and matches its length/SHA-256.
- **Recoverable:** only a crash tail, orphan, or partial file is present.
- **Needs Attention:** a journaled source is missing or damaged.
- **Corrupt:** the journal is invalid before its final line.

The library reports evidence; it never deletes or silently repairs source files.

## D-020: Timeline source model

**Status:** Accepted  
**Date:** 2026-07-18

Build the editor timeline from journaled source clips: screen, camera, microphone, and system audio remain independent tracks. Load `audio-repair-plan.json` and `presenter-layout.json` as enabled, reversible automation events.

Preview and export must consume this same timeline document so automated edits can be disabled without touching source media.

## D-021: Shared render plan

**Status:** Accepted  
**Date:** 2026-07-18

Preview and export use one deterministic `RenderPlan`: canvas preset, immutable source clips, and only enabled automation events. Disabling an automatic edit removes it from the plan without changing the timeline source data.

Initial framing presets are landscape 1920 x 1080, portrait 1080 x 1920, and square 1080 x 1080. Plans can be persisted as `render-plan.json`.

## D-022: First MP4 exporter

**Status:** Accepted  
**Date:** 2026-07-18

Execute `render-plan.json` in the isolated media worker. The first exporter supports screen scale/pad, optional camera overlay, microphone/system mix, loudness normalization, bounded whole-track playback rate, H.264/AAC, and fast-start MP4.

If an enabled automation event is not implemented, export fails explicitly. It must never silently omit an edit.
