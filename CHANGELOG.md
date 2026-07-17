# Changelog

All notable work, attempted approaches, failures, and decisions are recorded here so the project can resume without repeating failed paths.

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

### Pending

- App scaffold, capture implementation, smart editor, tests, packaging, and release validation.
