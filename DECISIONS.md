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
