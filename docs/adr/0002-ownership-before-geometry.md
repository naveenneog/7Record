# ADR-0002: Fix ownership before geometry — no `partial class` split of MainPage

- **Status:** Accepted
- **Date:** 2026-07-31
- **Packet:** M1 planning
- **Deciders:** 7Record maintainers + Ironclad council (Architect seat)

## Context

`src/SevenRecord.App/MainPage.xaml.cs` is 3,833 lines, 93 methods and 202 fields in a single class,
carrying seven distinct responsibilities: recorder lifecycle, camera studio, live preview, project
library, clip editing, audio mixing, caption editing and export.

The Ironclad gate flags it as the largest file-size violation in the repo (budget 400). The obvious
remedy — and the one almost every "clean this up" task reaches for — is to split it into
`MainPage.Recorder.cs`, `MainPage.Camera.cs`, `MainPage.Editor.cs` and so on. It is one mechanical
refactor, it is low risk, it makes the gate green, and it would take an afternoon.

We ran an Architect review over the file before doing it. The review found nine concrete defects,
five of them user-visible and blocking. Its summary judgement:

> "The core problem is not the 3,833-line file itself. It is **unowned asynchronous operations
> sharing mutable page fields**. Splitting this into partial classes would preserve the defects."

Concrete examples of what a partial split would *not* fix:

- `OnStartRecordingClicked` is `async void` with no command gate; a second click or hotkey during
  an await can drive `RecorderStateMachine` to throw on the UI thread (`MainPage.xaml.cs:2408-2459`).
- An export snapshots its plan before an await, then re-reads `_currentTimeline` after it; opening
  another project mid-export can write project A's video into project B's folder
  (`MainPage.xaml.cs:1961-1986`, `2321-2330`).
- A debounced editor-state save captures neither project path nor state, so it silently applies to
  whichever project is current when it finally fires (`MainPage.xaml.cs:1278-1302`).

Every one of these survives a file split unchanged, because `partial class` members share exactly
the same fields and the same absence of ownership. The gate would go green while the app got no
safer — which is the precise failure the charter's exception mechanism exists to prevent.

## Options considered

1. **Split into `partial class` files now.** Fast, low risk, makes the largest gate violation
   disappear. But it relocates lines without changing a single invariant, and it would let us
   report "refactored the God object" while all nine defects remain. It also makes the *later*
   real work harder to review, because the ownership refactor's diff would then be tangled with a
   large mechanical move.
2. **Extract ownership seams first** (`RecordingCoordinator`, `ProjectWorkspace`,
   `ExportController`, ...) into a non-UI assembly, each owning its own state, tasks and
   cancellation, each unit-testable without a UI thread. Slower and riskier per packet, but each
   packet removes a real defect and is provable by a test.
3. **Do both, ownership first, geometry later.** Option 2, and revisit whether the file split is
   still worth doing once the coordinators have moved out — by which point the file will have
   shrunk on its own and the split may be unnecessary.

## Decision

**Option 3, and we explicitly refuse option 1 as a standalone packet.**

- Ownership seams come first, as roadmap packets P-2 through P-9. Each one removes a named defect
  with a named acceptance criterion.
- To keep the gate honest in the meantime, `MainPage.xaml.cs` gets a **recorded charter exception
  with a ceiling of 3,900 lines** — its current size plus a small margin. This is a *ratchet*: the
  file may shrink but may never grow. Raising that number requires a new ADR, which makes growth a
  visible decision rather than a silent drift.
- A new assembly `SevenRecord.App.Presentation` is pre-declared in the charter's architecture
  boundaries, forbidden from importing `Microsoft.UI`, `Windows.*` or `SevenRecord.App.*`, so the
  extraction cannot quietly drag WinUI along with it and become untestable again.

## Consequences

+ Every M1/M2 packet removes a defect a user could actually hit, instead of moving text.
+ The 3,900-line ratchet means the God object provably cannot get worse while we work.
+ Pre-declaring the `SevenRecord.App.Presentation` boundary means the eventual extraction is
  checked by the gate from its first commit — it cannot regress into a second UI-coupled layer.
- The largest gate finding stays visible (as a documented exception) for several packets. Anyone
  reading the scorecard will see a 3,833-line file and may assume it is unaddressed. This ADR is
  the answer to that; the roadmap names the packets that shrink it.
- More total work than the afternoon a partial split would take.

## How we'd know this was wrong

If P-2 through P-9 stall and the coordinators never get extracted, then we will have paid the cost
of refusing the cheap refactor and received none of the benefit — the file will still be 3,833
lines, just with an ADR explaining why. The check: if two milestones pass with no coordinator
extracted, take option 1 as a consolation prize and record the retreat honestly.
