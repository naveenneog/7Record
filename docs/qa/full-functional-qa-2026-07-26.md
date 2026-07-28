# 7Record full functional QA

Date: 2026-07-26  
Revision: `e4dc17f` (`master`)  
Environment: Windows, Remote Desktop, 150% system DPI, High Contrast off, Narrator off  
Verdict: **NOT RELEASE READY**

## Executive summary

- 62 QA cases executed or audited.
- 40 passed.
- 9 failed.
- 9 were blocked by the current desktop/environment.
- 3 planned features are not implemented.
- 1 project-level caption case was not run because that project had no captions.
- Findings: **1 P0, 9 P1, 7 P2**.
- Debug build and all 73 automated tests passed.
- Real landscape, portrait, and square MP4 exports passed with H.264 video, AAC audio, camera overlay, and caption burn-in.
- Release is blocked by crash-loss exposure, severe microphone loss, broken release packaging, missing unpackaged loading analysis, and multiple editor/recovery integrity defects.

## QA health score

| Dimension | Score | Key finding |
| --- | ---: | --- |
| Accessibility | 2/4 | Good labels and keyboard basics, but focus transfer, live announcements, runtime contrast, large text, and camera-position feedback are incomplete |
| Performance | 3/4 | GPU capture/preview and isolated media work are sound; microphone loss and missing real crash segmentation remain operational risks |
| Appearance and theming | 3/4 | Coherent native dark UI and semantic resources; runtime Contrast Theme switching is not reapplied |
| Windows platform conformance | 4/4 | Native WinUI controls, Windows picker, Media Foundation, WASAPI, and MSIX manifest patterns |
| Adaptivity | 3/4 | 1024x720 and basic 150% DPI gates pass; Projects metadata still risks clipping at 150-200% text |
| **Total** | **15/20** | **Good interface quality, but release-blocking functional defects** |

## Environment and evidence

- `dotnet build SevenRecord.slnx --configuration Debug`: 0 warnings, 0 errors.
- `dotnet test SevenRecord.slnx --configuration Debug --no-build`: 73 passed, 0 failed, 0 skipped.
- System DPI: 144 / 150%.
- Camera: Surface Camera Front (redirected), 1280x720.
- FFmpeg real export artifacts:
  - `C:\Users\navg\.copilot\session-state\6609681a-2022-44f6-8af0-4f15e0ea32a1\files\qa-exports-20260726\landscape.mp4`
  - `C:\Users\navg\.copilot\session-state\6609681a-2022-44f6-8af0-4f15e0ea32a1\files\qa-exports-20260726\portrait.mp4`
  - `C:\Users\navg\.copilot\session-state\6609681a-2022-44f6-8af0-4f15e0ea32a1\files\qa-exports-20260726\square.mp4`
  - Visual frame: `...\qa-exports-20260726\landscape-frame-2s.png`
- Probe artifacts are under the same session `files` directory with `qa-*20260726` names.

## Result summary

| Status | Count |
| --- | ---: |
| Pass | 40 |
| Fail | 9 |
| Blocked | 9 |
| Not implemented | 3 |
| Not run | 1 |
| **Total** | **62** |

## Functional QA matrix

### Build and automated tests

| Functionality | Result | Evidence |
| --- | --- | --- |
| Debug solution build | Pass | 0 warnings, 0 errors |
| Capture clocks, readiness, pause mapping, frame health | Pass | 14/14 tests |
| Recorder lifecycle, journal, recovery, project writer | Pass | 17/17 tests |
| Frame pacing, encoder policy, dimensions | Pass | 12/12 tests |
| Audio packet timeline | Pass | 2/2 tests |
| Post-processing, audio repair, cursor zoom, loading detection | Pass | 9/9 tests |
| Timeline loading and caption editing | Pass | 2/2 tests |
| Render plans and FFmpeg command construction | Pass | 7/7 tests |
| Timeline ranges and presenter layout | Pass | 5/5 tests |
| Project library summaries | Pass | 2/2 tests |
| Caption formatting | Pass | 2/2 tests |

### Application, UI, accessibility, and adaptivity

| Functionality | Result | Evidence |
| --- | --- | --- |
| Direct unpackaged launch and responsiveness | Pass | UIA gate opened a responsive 7Record window |
| Readiness checks and accessible status | Pass | Reached Ready with dynamic status name |
| Recorder and Projects navigation | Pass | UIA navigation and recent-project list |
| Record/navigation targets | Pass | 66/60/59 physical pixels at 150% DPI |
| 1024x720 stacked layout | Pass | Setup/health rail stacked below preview |
| Basic 150% DPI flow | Pass | Launch, navigation, playback, targets, stacking |
| Project opening and inline playback controls | Pass | Play and external-open controls enabled |
| Runtime Windows Contrast Theme | Blocked | High Contrast was off; source audit found runtime updates missing |
| Narrator announcements | Blocked | Narrator was off; source audit found announcement/focus gaps |
| Full 150-200% project-detail text validation | Fail | Fixed 380-DIP detail column and incomplete wrapping; `MainPage.xaml:644-658` |
| Camera position accessibility feedback | Fail | No automation value/status after move; `MainPage.xaml.cs:2048-2145` |

### Capture and camera

| Functionality | Result | Evidence |
| --- | --- | --- |
| Camera capture and encoding | Pass | 48 frames, 0 dropped, MP4 published |
| Camera live preview | Pass | 24 preview frames at 640x360 |
| Camera frame centering/full-frame copy | Pass | Export frame visibly centers the user instead of showing a corner crop |
| Camera overlay metadata and rounded export | Pass | Real exports use persisted PresenterLayout |
| Choose application/display picker | Blocked / P1 | Picker returned `null` immediately through physical, UIA, and keyboard activation; no feedback |
| Integrated selected-window screen recording | Blocked | Could not proceed without picker |
| Integrated live screen/camera preview | Blocked | Could not start new capture; standalone camera preview passed |
| Integrated camera placement persistence | Blocked | Current capture was blocked; prior persisted layout and export consumption verified |
| Pre-record camera zoom/crop/pan | Not implemented | Planned backlog |
| Camera background blur/brightness | Not implemented | Planned backlog |

### Audio and synchronization

| Functionality | Result | Evidence |
| --- | --- | --- |
| System audio loopback capture | Pass | 128 packets, 2,829,456 bytes |
| System audio synchronization | Pass | -1.07 ms drift, 0 discontinuities, 0 missing |
| Microphone capture | **Fail / P1** | 2,225.8 ms missing in 8 seconds, 4 discontinuities |
| Long-project microphone synchronization | **Fail / P1** | 71.976 s microphone for 75.637 s project: -3,660.7 ms |
| Audio timing metadata and repair planning | Pass | 2 tracks, 24 persisted repair events |
| Mixed audio export | Pass | AAC stereo stream in landscape, portrait, and square exports |

### Cursor, lifecycle, pause, and hotkeys

| Functionality | Result | Evidence |
| --- | --- | --- |
| Cursor movement metadata | Pass | 18 moves in probe; 1,210 events in real project |
| Cursor click metadata and zoom suggestions | Blocked | Remote desktop intermittently withheld cursor state; real project contains 10 zooms |
| Automatic cursor zoom plan | Pass | 10 deterministic zoom events persisted |
| Pause/resume mapping | Pass (unit) / Blocked (live) | Unit coverage passes; live capture blocked by picker |
| Global start/stop and pause/resume hotkeys | Blocked | Live activation requires working capture selection |

### Recovery, project integrity, and editor

| Functionality | Result | Evidence |
| --- | --- | --- |
| Journal checksum and media SHA-256/length integrity | Pass | All four sources matched journal hash and byte length |
| Corrupt-tail, missing, tampered, orphan/partial inspection | Pass (unit) | Recording recovery suite |
| Five-second crash-loss guarantee | **Fail / P0** | `RecordingSegmentPolicy` is not used by production recorders |
| Multi-segment playback/analysis/captions/export | **Fail / P1** | Downstream paths select only the first source clip |
| Integrity validation before editor open | **Fail / P1** | Timeline loader bypasses `RecordingRecoveryService` |
| Atomic project switching on load failure | **Fail / P1** | Prior timeline/export state can remain after a failed open |
| Automation enable/disable restoration | Fail / P2 | Saved render-plan decisions are not loaded |

### Post-processing and captions

| Functionality | Result | Evidence |
| --- | --- | --- |
| Idempotent post-processing rerun | Pass | Byte-for-byte stable plans; stage isolation test |
| Cursor automation generation | Pass | 10 zoom suggestions in real project |
| Audio repair generation | Pass | 24 repair suggestions in real project |
| Standalone loading/freeze detection | Pass | 8 deterministic intervals including EOF |
| Automatic loading plan after direct-exe recording | **Fail / P1** | `loading-speed-plan.json` absent; expected worker path missing |
| Loading confidence for intentional static content | **Fail / P1** | Uses `freezedetect` alone without cursor/audio context |
| Offline Whisper transcription | Pass | Expected synthetic phrase; JSON/SRT/VTT written |
| Caption edit and undo/redo | Pass | Editor tests |
| Caption overlap/order/timeline bounds | Fail / P2 | Whole-document validation is missing |
| Caption generation in inspected real project | Not run | Project had no `captions.json`; standalone probe passed |

### Playback and export

| Functionality | Result | Evidence |
| --- | --- | --- |
| Inline project playback controls | Pass | UIA gate |
| Landscape export | Pass | H.264 1920x1080, AAC, exactly 5.0 s |
| Portrait export | Pass | H.264 1080x1920, AAC, exactly 5.0 s |
| Square export | Pass | H.264 1080x1080, AAC, exactly 5.0 s |
| Rounded camera overlay export | Pass | Real FFmpeg render |
| Caption burn-in | Pass | Caption visibly present in frame extracted at 2 s |
| Preserve prior valid export on failure | **Fail / P1** | FFmpeg writes directly to the final fixed filename with `-y` |

### Packaging and distribution

| Functionality | Result | Evidence |
| --- | --- | --- |
| Package manifest capabilities | Pass | Full trust, programmatic capture, microphone, webcam declared |
| Self-contained x64 publish | **Fail / P1** | `NETSDK1150`; app references non-self-contained media worker |
| Unsigned x64 MSIX generation | **Fail / P1** | Same `NETSDK1150`; no package generated |
| x86/ARM64 package smoke tests | Blocked | x64 package fails first |
| `npx 7record` installer | Not implemented | No npm package exists |

## Prioritized defects

### QA-20260726-02 — P0 — Crash-safe segment rollover is not implemented

- **Location:** `RecordingSegmentPolicy.cs:3-19`; `SurfaceScreenSegmentRecorder.cs:82-190`; `RecoverableAudioRecordingSession.cs:216-250`; `RecoverableCameraRecordingSession.cs:356-375`
- **Impact:** A process or power crash can leave the entire recording unjournaled. Recovery reports partial files but cannot finalize/adopt them.
- **Required fix:** Roll sources into journaled segments within the configured crash-loss window and add forced-process-kill recovery tests.

### QA-20260726-01 — P1 — Microphone loses seconds of narration

- **Evidence:** 2,225.8 ms missing in an 8-second probe; 3,660.7 ms short in a 75.637-second project. System audio in the same runs was stable.
- **Impact:** Narration is truncated/desynchronized even though recording reports success.
- **Required fix:** Investigate Remote Audio buffering/packet delivery, define an acceptance threshold, and block or strongly warn when loss exceeds tolerance.

### QA-20260726-03 — P1 — Multi-segment projects omit later content

- **Location:** `FfmpegRenderPlanExporter.cs:45-84`; `ProjectPostProcessingPipeline.cs:144-173`; `MainPage.xaml.cs:677-690,839-864`
- **Impact:** Future segmented, imported, or recovered recordings lose later media or become desynchronized.
- **Required fix:** Build ordered per-source concat timelines that honor clip start offsets.

### QA-20260726-04 — P1 — Editor bypasses source integrity validation

- **Location:** `ProjectTimelineLoader.cs:22-36`; validation exists in `RecordingRecoveryService.cs:34-65`
- **Impact:** Missing/tampered media opens as normal and fails later.
- **Required fix:** Gate editor opening on recovery inspection and disable playback/export for unhealthy projects.

### QA-20260726-05 — P1 — Failed project open can retain stale state

- **Location:** `MainPage.xaml.cs:582-633`
- **Impact:** A failed load can leave the prior project exportable or mix state across projects.
- **Required fix:** Load into temporary state and commit UI/editor state only after all steps succeed.

### QA-20260726-06 — P1 — Loading detector can speed up intentional still content

- **Location:** `FfmpegLoadingDetector.cs:35-40`
- **Impact:** Slides, code-reading pauses, and deliberate still frames may be incorrectly accelerated.
- **Required fix:** Combine low visual motion with cursor inactivity and low audio/activity confidence.

### QA-20260726-07 — P1 — Export failure can destroy the last valid export

- **Location:** `FfmpegRenderPlanExporter.cs:71,282-389`; `MainPage.xaml.cs:800-816`
- **Impact:** A failed export can overwrite a good file and leave a partial MP4 that playback prefers.
- **Required fix:** Render to a unique temporary path, validate it, then atomically replace the final output.

### QA-20260726-10 — P1 — Picker returns silently with no source

- **Evidence:** Physical click, UIA InvokePattern, and keyboard activation produced no picker window; `PickSingleItemAsync` returned immediately.
- **Impact:** Recording cannot start in the current desktop and the user receives no reason.
- **Required fix:** Surface cancellation/unavailability, add picker telemetry, provide a safe alternate/package fallback, and retest on local console plus installed MSIX.

### QA-20260726-16 — P1 — Release publish and MSIX packaging fail

- **Evidence:** `NETSDK1150`: self-contained app references non-self-contained `SevenRecord.Media.Worker`.
- **Impact:** No distributable build; blocks installation and `npx` delivery.
- **Required fix:** Decouple/copy the worker artifact or align self-contained runtime settings; add packaging CI.

### QA-20260726-17 — P1 — Direct executable cannot find loading-analysis worker

- **Evidence:** App expects `BaseDirectory\MediaWorker`; debug build copies worker only to `BaseDirectory\AppX\MediaWorker`.
- **Impact:** Loading speed-up plans silently do not generate in direct-exe runs.
- **Required fix:** Resolve both packaged/unpackaged worker paths and make skipped analysis visible.

### P2 issues

| ID | Area | Issue |
| --- | --- | --- |
| QA-20260726-08 | Editor | Automation enable/disable choices are not restored |
| QA-20260726-09 | Captions | Overlaps, ordering, and timeline bounds are not validated |
| QA-20260726-11 | Accessibility | Runtime Contrast Theme changes are not reapplied |
| QA-20260726-12 | Accessibility | Project focus transfer and completion/error announcements are incomplete |
| QA-20260726-13 | Accessibility | Camera movement exposes no accessible position value |
| QA-20260726-14 | Adaptivity | Projects metadata can clip at 150-200% text |
| QA-20260726-15 | QA coverage | UIA gate omits focus, live-region, contrast, scaling, movement, and failure checks |

## Positive findings

- Native WinUI structure and platform conventions are strong.
- Build and all automated tests are clean.
- Camera capture and live preview are stable on the redirected Surface camera.
- System audio is tightly synchronized.
- Journal hashes and file lengths are correct for all four sources.
- Offline Whisper works and produces valid JSON/SRT/VTT.
- Real creator exports work across landscape, portrait, and square.
- Camera overlay framing and caption burn-in are visibly correct.
- UI navigation, basic accessibility names, target sizes, 1024x720 stacking, 150% DPI basics, and project playback controls pass.

## Release decision

Do not publish an installer or describe the recorder as crash-safe until:

1. P0 journaled segment rollover is implemented and kill-tested.
2. Microphone loss meets a defined acceptance threshold.
3. Release/MSIX packaging succeeds.
4. Direct-exe loading analysis locates its worker.
5. Project integrity and export atomicity P1 defects are fixed.

The Windows picker must also be retested on a local console and installed MSIX. If it still returns silently, treat it as a hard recording blocker.

## Overdrive remediation — 2026-07-27

- **QA-20260726-02:** production five-second rollover is now wired for screen, microphone, system audio, and camera. A forced-kill audio run preserved ten journaled segments and left only active partials.
- **QA-20260726-03:** playback, captions, loading analysis, and export now consume ordered multi-segment sources.
- **QA-20260726-07:** export now renders to a partial file and atomically replaces the prior valid MP4 only after success.
- **QA-20260726-16:** self-contained x64 publish and unsigned x64 MSIX now succeed with the media worker included.
- **QA-20260726-17:** packaged and unpackaged worker locations resolve through one runtime locator.
- Real multi-segment H.264/AAC export and cross-boundary loading detection passed.
- Redirected cameras were unavailable during rollover acceptance testing; camera rollover compiled, passed code review, and must be rerun when the remote camera resumes frame delivery.
- Pre-record camera studio implementation passed 102 automated tests and UIA with zero warnings/errors. Final redirected-camera preview validation remains environment-blocked because both enumerated remote cameras stopped delivering frames.
- **QA-20260726-06:** loading suggestions now require cursor coverage plus silence from every available audio track, preserve per-track dropout timing, and invalidate stale plans on failure/cancellation. Release suite: 100 tests, zero warnings/errors.
- Person-aware background effects use Windows Studio Effects Standard/Portrait blur only on supported NPU cameras. Privacy behavior is fail-closed, raw driver flags are restored conditionally, and window close awaits restoration. Current redirected hardware reported no supported blur and intermittently delivered no preview frames.
