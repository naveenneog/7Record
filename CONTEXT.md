# 7Record Resume Context

Last updated: 2026-07-17 19:35 IST

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

Two background research agents were launched before restart:

- `market-researcher`: researching Screen Studio, Descript, Loom, Tella, Camtasia, OBS, Riverside, CapCut, modern creator features, complaints, and MVP prioritization.
- `architecture-researcher`: comparing native C++/WinUI, C#/.NET, Avalonia/WPF, Tauri/Rust, Electron, and Qt; researching Windows.Graphics.Capture, Desktop Duplication, WASAPI loopback, Media Foundation, FFmpeg, encoding, recovery, and testing.

The direct web searches timed out and produced no usable results. On resume, first check whether the background agent results survived. If not, restart equivalent research tasks.

## Environment Observed

- Working directory: `C:\Users\navg\DailyApps\7Record`
- Directory was empty at project start.
- Node.js: `v26.1.0`
- npm: `11.13.0`
- .NET SDK: `10.0.302`
- Rust was not installed.
- FFmpeg availability was not confirmed because the tool probe stopped at the missing Rust command.
- This directory was not a Git repository at session start.

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

## Architecture Principles Pending Research Confirmation

- Separate capture, media, analysis, project model, editor UI, preview, and export into stable modules.
- Store screen, camera, microphone, system audio, cursor events, keystrokes (opt-in), and edit decisions separately.
- Record in short recoverable segments with a journal/manifest updated atomically.
- Prefer hardware encoding with a software fallback.
- Use proxy media and cached waveforms/thumbnails for responsive editing.
- Keep the project format versioned and human-inspectable where practical.
- Never require cloud upload for core capture, editing, or export.

## Required Resume Sequence

1. Read `CONTEXT.md`, `PRODUCT.md`, `DESIGN.md`, `DECISIONS.md`, and `CHANGELOG.md`.
2. Query the session todos if still available; otherwise reconstruct them from the checklist below.
3. Retrieve or restart the two research agents and save their findings under `docs/research/`.
4. Confirm the production stack from evidence. Current likely candidates are:
   - C#/.NET + Windows-native capture/media core and a native desktop shell.
   - Electron only if research proves it is the fastest reliable way to deliver complete capture now without compromising recovery/performance.
   - Tauri/Rust is not the immediate default because Rust is absent and Windows-first native media integration remains required.
5. Initialize Git and commit documentation as the first scoped commit with the required Copilot trailers.
6. Scaffold the app and commit each feature independently.
7. Implement and validate capture before building advanced editor automation.
8. Maintain `CHANGELOG.md` and `DECISIONS.md`, including attempted approaches and failures.

## Work Checklist

- [ ] Complete and save market research.
- [ ] Complete and save technical architecture research.
- [ ] Select stack and update `DECISIONS.md`.
- [ ] Initialize repository and commit foundation docs.
- [ ] Scaffold desktop app and automated tests.
- [ ] Build capture source selection and readiness UI.
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
