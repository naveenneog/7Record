---
title: "Recording orchestration and lifecycle architecture"
category: "Architecture & Design"
status: "🟢 Complete"
priority: "Critical"
timebox: "90 minutes"
created: 2026-07-18
updated: 2026-07-18
owner: "@naveenneog / GitHub Copilot"
tags: ["technical-spike", "architecture", "recording", "research"]
---

# Recording orchestration and lifecycle architecture

## Summary

**Spike Objective:** Select an architecture that makes one-click recording reliable while preserving synchronized sources, crash-safe publication, optional camera behavior, and a responsive WinUI shell.

**Why This Matters:** `MainPage.xaml.cs` is 1,305 lines and directly owns capture resources, pause state, source publication, analysis, and UI policy. Moving this code without defining lifecycle and ownership rules would relocate existing races instead of fixing them.

**Timebox:** 90 minutes

**Decision Deadline:** Before adding more recording modes or distribution work.

## Research Question(s)

**Primary Question:** What is the smallest architecture that separates UI from recording lifecycle while improving synchronization and failure safety?

**Secondary Questions:**

- Which state and result contracts belong in platform-neutral code?
- Which Windows resources belong in one active-session aggregate?
- When should post-recording analysis run?
- How should concurrent stop, close, failure, and unload requests converge?
- Who owns the project journal and publication sequence?

## Investigation Plan

### Research Tasks

- [x] Trace start, pause, resume, stop, and finalization in `MainPage`.
- [x] Inspect screen, audio, camera, cursor, clock, and journal ownership.
- [x] Identify lifecycle and cancellation races.
- [x] Challenge the proposed state-machine/orchestrator split.
- [x] Document an incremental migration that keeps every commit buildable.

### Success Criteria

**This spike is complete when:**

- [x] UI, lifecycle, resource, and analysis boundaries are explicit.
- [x] Start/stop synchronization requirements are documented.
- [x] Failure severity rules distinguish mandatory and optional sources.
- [x] Journal ownership has one recommended authority.
- [x] A staged implementation sequence is accepted.

## Technical Context

**Related Components:** `SevenRecord.App`, `SevenRecord.Recording`, `SevenRecord.Capture.Abstractions`, all Windows capture modules, `SevenRecord.Media.Worker`, and project analysis.

**Dependencies:** One-click recording, pause/resume, global hotkeys, recovery, post-processing, MSIX distribution, and future `npx` installation.

**Constraints:**

- QPC remains the project clock.
- Screen is mandatory; audio, camera, and cursor are attempted by default but may degrade with explicit warnings.
- Source media remains separate and non-destructive.
- FFmpeg/analysis remains outside the UI process.
- Capture uses Direct3D surfaces and requests Media Foundation hardware acceleration.
- Existing projects and recordings must remain compatible.

## Research Findings

### Investigation Results

1. `MainPage` owns six concrete recording resources and performs project publication and analysis. UI event handlers therefore act as the application service and resource owner.
2. Source initialization and shutdown are sequential. There is no explicit shared acceptance boundary for the first/last sample.
3. Capture close, capture failure, user stop, global hotkey stop, and page unload can request teardown independently.
4. Optional camera stop/publish failure currently shares the same sequential finalization block as screen/audio and can prevent mandatory source publication.
5. Screen, audio, and camera recorders each create their own `RecordingJournal` and hard-code sequence values. A session must own one journal and sequence allocator.
6. Screen frame-processor exceptions can fault the consumer task without reliably raising `CaptureFailed`.
7. Loading detection, cursor zoom planning, and audio repair generation are project post-processing. They should not extend the lifetime of live capture resources or block the next recording.

### Prototype/Testing Notes

- The one-click UI was implemented without changing media architecture: camera defaults on, Record initiates source selection when necessary, and Direct3D/Media Foundation acceleration remains enabled.
- Unsafe unpackaged programmatic-monitor paths were rejected after repeatable native fail-fast crashes. Installed MSIX builds use declared programmatic capture access; unpackaged builds use the Windows picker.
- A high-signal architecture challenge confirmed that a semaphore alone is insufficient: stop must cancel/join startup, and all stop causes must converge on one teardown task.
- The post-review harness produced synchronized journal durations of 5.87 seconds with screen/microphone/system media at 5.78/5.80/5.82 seconds.

### External Resources

- [Windows Graphics Capture](https://learn.microsoft.com/windows/uwp/audio-video-camera/screen-capture)
- [Windows App SDK deployment](https://learn.microsoft.com/windows/apps/windows-app-sdk/deploy-unpackaged-apps)
- `DECISIONS.md` D-003 through D-032
- `docs/research/windows-architecture.md`

## Decision

### Recommendation

Adopt two layers:

1. **`SevenRecord.Recording`** owns a small synchronous lifecycle state machine and neutral state/result records.
2. **`SevenRecord.Recording.Windows`** owns a reusable controller and one private active-session aggregate containing the project clock, pause controller, Windows source resources, and finalization policy.

`MainPage` becomes an adapter that resolves the requested source, sends commands, and renders immutable snapshots/results.

### Rationale

This preserves platform-neutral lifecycle rules without pretending Windows health and capture resources are cross-platform. A private one-project session prevents resource reuse bugs, while a reusable controller provides the stable command surface required by UI, hotkeys, capture callbacks, and tests.

### Implementation Notes

- State: `Idle`, `Starting`, `Recording`, `Paused`, `Stopping`, `Faulted`.
- State transitions are synchronous and revisioned; async serialization stays in the Windows controller.
- Use single-flight start/stop tasks. Stop first signals startup/session cancellation, then all callers join the same internal teardown.
- Capture one shared start boundary and one shared stop boundary. Close sample acceptance before draining sources.
- Screen failure produces failed finalization. Unavailable or failed audio, camera, and cursor sources produce structured warnings and must not block screen publication.
- Introduce one session-owned project writer/journal and sequence allocator.
- `StopAsync` ends after raw sources are published and resources are disposed.
- Queue idempotent post-processing separately; it must not block another recording.
- Include session ID and monotonically increasing revision in snapshots/events so stale callbacks can be ignored.

### Follow-up Actions

- [x] Add and test the neutral lifecycle state machine.
- [x] Move `SurfaceScreenSegmentRecorder` out of `SevenRecord.App`.
- [x] Harden frame-processor fault propagation and idempotent source disposal.
- [x] Add the shared project writer/journal.
- [x] Implement the Windows active-session aggregate and end-to-end harness.
- [x] Adapt `MainPage` to lifecycle/session commands and results.
- [x] Add GPU frame pacing for static and odd-sized application windows.
- [ ] Extract post-processing into an idempotent project pipeline.
- [ ] Build signed MSIX artifacts before implementing `npx 7record`.

## Status History

| Date       | Status         | Notes |
| ---------- | -------------- | ----- |
| 2026-07-18 | 🟡 In Progress | Traced lifecycle and challenged the proposed split. |
| 2026-07-18 | 🟢 Complete    | Accepted neutral state machine + Windows controller/session architecture. |

---

_Last updated: 2026-07-18 by GitHub Copilot_
