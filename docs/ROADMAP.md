# 7Record — Roadmap

> A **packet** is one behaviour, testable in isolation, shippable in one commit.
> If you can't state it as "given X, when Y, then Z", split it until you can.
> Exactly one packet is ACTIVE at a time (see `docs/STATUS.md`).

## The thesis

Every serious defect found in the 2026-07-31 IronClad + Overdrive audit has the same root cause:

> **Invariants that exist only in the author's head.**

"Don't press Record twice." "Don't switch projects mid-export." "Every `async void` handler is
safe because its callee happens to catch." "The camera filter must be cropped before exposure."
None of these are written down anywhere a program can check. They are enforced by memory and
vigilance — and a 3,833-line file is where vigilance goes to die.

This roadmap has one job: **convert each unwritten invariant into something a program enforces** —
a type that makes the illegal state unrepresentable, a test that fails, or a gate that exits
non-zero. Not documentation. Enforcement.

**Dominant criterion (Overdrive):** *correctness under the conditions that actually occur.*
A 40-minute capture, on flaky redirected hardware, interrupted by the wrong click, must never
silently lose the user's work.

**The median answer we are explicitly rejecting:** "split `MainPage.xaml.cs` into partial classes
and add a few tests." Partial classes relocate lines without changing a single invariant. The
Architect council seat confirmed it: *"Splitting this into partial classes would preserve the
defects."* Ownership seams come first; mechanical file splitting is at best a later cosmetic step.

---

## Audit baseline (2026-07-31, revision `0850a69`)

| Signal | Value |
|---|---|
| Build | 0 warnings, 0 errors |
| Tests | 113 passing across 11 test projects |
| Test files : source files | 52 : 91 (57%) |
| `MainPage.xaml.cs` | 3,833 lines · 93 methods · 202 fields · 7 responsibilities |
| Tests covering `SevenRecord.App` | **0** (no test project exists) |
| Tests covering `FfmpegRenderPlanExporter` (961 lines) | **0** |
| `async void` handlers | 33, of which **22 have no `catch` in their body** |
| Global unhandled-exception handler | **DEBUG-only, debugger-only** (WinUI generated stub) |
| Logging / diagnostics infrastructure | **none** — no `ILogger`, no trace, no crash log |
| Open QA findings | 3 x P2 open, 3 blocked "needs local hardware" |

The last two rows are causally linked, and it is the most important finding of the audit:
**the three QA issues are permanently blocked *because* the app has no diagnostics.** They cannot
be closed by trying harder on a remote desktop; they can only be closed by making the app able to
report what happened. Diagnostics is therefore not a "nice to have" — it is the unblocker.

---

## Now — M1: Survive the conditions that actually occur

The app can currently die silently, mid-recording, in a Release build, and leave no evidence.
Nothing else matters more than this.

- [ ] **P-1  A crash cannot be silent, and a handler fault cannot kill the app**   <- ACTIVE
      Given any `async void` UI handler throws, when the exception escapes the handler,
      then the app stays alive, the user sees an actionable message, and a durable
      diagnostic record is written to disk with the exception and recent context.
      Given a Release build faults where no debugger is attached, when the process would
      otherwise terminate, then the fault is recorded before termination.
      Depends on: —          Unknowns: U-1, U-2

- [ ] **P-2  Recorder commands are serialized and cannot race**
      Given camera shutdown or source selection is still awaiting, when a second Record
      click or global hotkey arrives, then exactly one state transition occurs and no
      exception escapes to the UI thread.
      Depends on: P-1          Unknowns: —
      Source: Architect finding #1 (`MainPage.xaml.cs:2408-2459`, `RecorderStateMachine.cs:119-123`)

- [ ] **P-3  Shutdown owns every background job**
      Given an export, transcription, edited-preview render or post-processing run is in
      flight, when the window closes, then every job is cancelled and awaited before
      closure is approved, and no worker process is orphaned.
      Depends on: P-1          Unknowns: U-3
      Source: Architect finding #3 (`MainPage.xaml.cs:1983-1986`, `MediaWorkerExportClient.cs:49-55`)

## Next — M2: Never lose the user's work

Four confirmed paths silently discard or misfile user data. None of them report failure.

- [ ] **P-4  An export is bound to the project that started it**
      Given an export begins for project A, when project B is opened mid-export, then every
      input, plan and output path still resolves under A, or the export is cancelled outright.
      Source: Architect finding #2 (`MainPage.xaml.cs:1961-1986`, `2321-2330`)

- [ ] **P-5  Editor state is project-scoped and flushed on switch**
      Given project A has a pending debounced audio-mix or automation change, when project B
      is opened, then A's change is persisted against A and no delayed save can target B.
      Source: Architect finding #6 (`MainPage.xaml.cs:1278-1302`, `1565-1588`)

- [ ] **P-6  Caption edits are transactional**
      Given caption persistence is pending, when Apply/Undo/Redo is invoked again, then the
      commands execute in order, temporary files never collide, and on failure the in-memory
      session matches what is on disk.
      Source: Architect finding #5 (`MainPage.xaml.cs:2132-2184`, `2262-2284`)

- [ ] **P-7  Camera settings persistence is serialized**
      Given a debounced camera-layout save is active, when shutdown flushes settings, then
      exactly one atomic save runs, the newest layout wins, and failure is visible.
      Source: Architect finding #7 (`CameraStudioSettingsStore.cs:76-94`)

## Next — M3: Pin the invisible contracts

The export filter graph encodes ordering rules that were learned by rendering real video and
looking at it. They are order-dependent, subtle, and currently protected by nothing.

- [ ] **P-8  The FFmpeg filter graph is pinned by characterization tests**
      Given a render plan exercising camera crop/exposure/mask/overlay, audio repair, clip
      edits, gain and mix, when `FfmpegRenderPlanExporter.CreateCommand` builds the argument
      list, then the relative order of every filter stage is asserted, so that reordering
      `eq` before `crop` — or gain before `amix` in the 2-track case — fails the build
      instead of silently producing wrong video.
      Note: `CreateCommand` is already a pure static function. This packet needs **zero**
      production changes and carries **zero** runtime risk. It is the cheapest large win here.

- [ ] **P-9  Recorder, camera-studio and editor state machines are unit-testable**
      Given a `SevenRecord.App.Presentation` assembly with no WinUI reference (already
      enforced by charter boundary #10), when the coordinators from the Architect's extraction
      plan are moved there, then each is covered by tests that run with no UI thread.
      Depends on: P-2, P-3, P-4, P-5

## Later (ideas, **not** commitments — never treat these as requirements)

- Preview generation counters so an in-flight bitmap cannot resurrect reset UI
  (Architect finding #8, `MainPage.xaml.cs:3119-3197`).
- Externally cancellable camera startup — cancel the startup CTS *before* waiting on the
  transition gate (Architect finding #9, `CameraPreviewSession.cs:87-137`).
- Close the three open P2 accessibility/adaptivity findings: runtime Contrast Theme reapply
  (QA-20260726-11), Projects metadata clipping at 150-200% text (QA-20260726-14), and the
  thin UIA gate (QA-20260726-15).
- `TreatWarningsAsErrors` so "0 warnings" is enforced by the compiler rather than by habit.

## Out of scope (decided, with reasons)

- **Splitting `MainPage.xaml.cs` into `partial class` files as a standalone packet** — it moves
  lines between files while preserving every race in M1/M2. Only meaningful *after* the ownership
  seams exist. (ADR-0002)
- **Production MSIX signing / `npx 7record` publish** — blocked on a code-signing certificate,
  which is a procurement task, not an engineering one. Stays blocked and flagged, not "solved".
- **Adding an FFmpeg blur filter for background blur** — Windows Studio Effects bakes blur into
  driver frames, so an export-side filter would double-blur. Preview/export parity is structural.

---

## Milestone review
Run at every milestone boundary, and write the answers here:

```
[] What shipped vs what we planned?
[] What did we learn that changes the plan?
[] What is now wrong in this roadmap?
[] What new unknowns appeared?              -> docs/UNKNOWNS.md
[] What debt did we take on?                -> a packet now, or explicitly accepted?
[] Is the charter still right?              -> ADR if it changes
```

### M0 review — 2026-07-31

**Shipped:** the discipline harness itself — `.ironclad/charter.json` with 10 machine-enforced
architecture boundaries, 9 documented file-size exceptions (each a downward-only ratchet), the
gate wired into a pre-commit hook and CI, and this roadmap.

**Learned:** the repo's architecture is genuinely clean where it counts, and nobody had written it
down. `SevenRecord.Domain` imports exactly one namespace (`System.Text.Json.Serialization`) and
the Export/Editor/Analysis/Recording layers contain zero WinUI or platform imports. That purity
was an unstated preference; it is now a rule the gate fails on. Preferences erode, rules don't.

**Now wrong in the plan:** nothing yet — this is the first roadmap.

**Debt accepted:** the nine file-size exceptions. Each carries a `why` and a ceiling set at its
current size, so any growth fails the gate. They may only ratchet downward.
