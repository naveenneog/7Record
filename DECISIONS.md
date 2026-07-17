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

**Status:** Pending research

Do not lock the framework until the architecture research compares capture reliability, GPU encoding, system audio, webcam synchronization, recovery, timeline preview performance, packaging, and testability. Prefer evidence over familiarity.

Current candidates:

1. C#/.NET desktop shell with Windows-native capture/media services.
2. Native C++ media core with a higher-level UI.
3. Electron for rapid delivery if Windows capture/audio and recovery prove production-worthy.
4. Tauri/Rust only if the Rust toolchain and required media integrations justify setup cost.
