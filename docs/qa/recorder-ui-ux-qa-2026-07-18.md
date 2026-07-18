# 7Record recorder UI/UX QA log

Date: 2026-07-18  
Target: `src/SevenRecord.App/MainPage.xaml`

## Assessment scores

| Phase | UX / Nielsen | Senior native QA | Verdict |
| --- | ---: | ---: | --- |
| Baseline | 17/40 | 19/56 | Generic dashboard composition with major accessibility, adaptivity, state, and theming gaps |
| Post-redesign independent audit | 32/40 | 43/56 | Focused native recorder; remaining verified findings were corrected in the final pass |

## Issue log

| ID | Severity | Area | Baseline issue | Final resolution | Status |
| --- | --- | --- | --- | --- | --- |
| UX-01 | P1 | Primary action | Stock blue Fluent Record button | Dedicated magenta `RecordButtonStyle`; magenta reserved for recording/commit | Fixed |
| UX-02 | P1 | Capture safety | No persistent recording/paused/stopping indicator or elapsed time | Added state pill, label, timer, progress state, and dynamic UIA names | Fixed |
| UX-03 | P1 | Information architecture | Recorder, health, projects, and editor in one long scroll | Native `NavigationView`; separate Recorder and Projects workspaces | Fixed |
| UX-04 | P1 | Hierarchy | Three equal source cards competed with Record | One compact Sources rail beside the preview stage | Fixed |
| UX-05 | P1 | Preview | Largest surface was an inert black rectangle | Dominant source/state stage with contextual action and profile footer | Fixed |
| UX-06 | P1 | Status comprehension | Health was undifferentiated text | Semantic icons, dynamic `InfoBar`, and health expansion during capture/warnings | Fixed |
| QA-01 | P1 | Theme/contrast | Light-theme control text leaked onto custom dark surfaces | Coherent dark theme, contrast-theme resource overrides, Win32 title-bar system colors | Fixed |
| QA-02 | P1 | Adaptivity | Fixed sidebar/padding/columns failed at 1024×720 | Compact native navigation and explicit responsive stacking | Fixed |
| QA-03 | P1 | Accessibility | Stop retained automation name “Start recording” | Record/Stop/Cancel and Pause/Resume names now follow recorder state | Fixed |
| QA-04 | P1 | Async states | Starting/stopping had no progress or cancellation feedback | Starting/Stopping labels, ProgressRing, cancellation, disabled incompatible actions | Fixed |
| QA-05 | P1 | Live announcements | Status changes lacked a named live region | Dynamic recorder-status UIA name plus polite live settings | Fixed |
| QA-06 | P1 | High contrast | Static brushes had no contrast-theme behavior | HighContrast theme dictionary and theme-aware interactive-state brushes | Fixed at launch |
| QA-07 | P2 | Keyboard | Visual and declaration order conflicted | Pause then Record declaration matches visual/Tab order | Fixed |
| QA-08 | P2 | Dead controls | Recorder/Settings/Configure audio were inert | Removed dead controls; only functional destinations/actions remain | Fixed |
| QA-09 | P2 | Device fallback | Copy promised unavailable camera/audio | Readiness disables unavailable camera and uses “when available” copy | Fixed |
| QA-10 | P2 | Text scaling | Fixed dimensions/status clipping | Adaptive stacking and unrestricted source-status wrapping | Fixed |
| QA-11 | P2 | List semantics | Project/timeline lists lacked useful labels | UIA list names, structured rows, human-readable recording timestamps | Fixed |
| QA-12 | P2 | Recovery UX | Raw exceptions had weak recovery guidance | Plain-language summary states and functional refresh/retry actions; some editor details remain technical | Partial |
| QA-13 | P3 | Touch | Many controls were 32 DIP high | Record is 44 DIP; buttons/navigation are 40+ DIP | Fixed |
| QA-14 | P3 | Regression coverage | No UIA/resize/accessibility regression harness | Added `tools/test-recorder-ui.ps1`, capture harness updates, camera probe, and visual evidence | Partial |

## Final corrective pass

- Corrected Screen/Camera/Audio status-icon mapping.
- Auto-expands Recording health during active capture and on warning/error.
- Disables Projects navigation during Starting/Recording/Paused/Stopping so Stop remains visible.
- Fixed camera-test completion race.
- Replaced raw project folder IDs with readable timestamps.
- Replaced redirected-camera cross-device Win2D drawing with `VideoFrame.CopyToAsync`.
- Replaced raw local state colors with contrast-theme-aware brush aliases.
- Replaced unstable WinRT title-bar contrast detection with Win32 system-color detection.
- Added a purposeful Projects detail empty state.

## QA evidence

- `dotnet build SevenRecord.slnx`: zero warnings/errors.
- Full automated suite: 68 tests passed.
- `tools/test-recorder-ui.ps1`: passed.
  - Record target: 44 DIP.
  - Recorder/Projects navigation targets: 40 DIP.
  - Dynamic recorder-status UIA name verified.
  - 1024×720 adaptive stacking verified.
  - Projects navigation and list UIA name verified.
- Camera probe: Surface Camera Front (redirected), 1280×720, 40 frames, 0 dropped.
- Full camera-on capture harness: screen, microphone, system audio, and camera published successfully.
- Visual evidence:
  - `ui-final-postfix-recorder.png`
  - `ui-final-1024x720.png`
  - `ui-final-postfix-projects.png`

## Required release-candidate manual checks

| Configuration | Status |
| --- | --- |
| 1024×720, 100% | Automated pass |
| 1440×900 | Visual pass |
| Keyboard / focus order | Source-reviewed and UIA pass |
| No camera/audio | Existing fallback path verified |
| Camera-on redirected device | Probe and full-capture pass |
| Windows Contrast Theme | Code-reviewed; manual RC pass still required |
| 150–200% Windows text scaling | Structural fixes complete; manual RC pass still required |
| Narrator announcements | UIA names/live regions verified; manual RC pass still required |

## Residual release risks

- Some editor/export exceptions still expose technical details rather than mapped recovery actions.
- Contrast Theme, large text scaling, and Narrator still require a final manual release-candidate pass on an interactive desktop.
