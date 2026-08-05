# 7Record — Status

> The first file any session reads. Write it as a handover note to someone who knows nothing.

**Active packet:** P-3 — Shutdown owns every background job
**State:** PLAN
<!-- PLAN → CONTRACT → RED → GREEN → REFACTOR → COUNCIL → GATE → DONE -->
**Branch:** not yet created

## Acceptance criteria

- [ ] Given an export, transcription, edited-preview render or post-processing run is in
      flight, when the window closes, then every job is cancelled and awaited before
      closure is approved.
- [ ] Given a job does not drain, when the shutdown wait expires, then the window still
      closes and the stuck job is recorded. A hung export must not become an unclosable window.

## Commands that prove it

```
dotnet test SevenRecord.slnx --configuration Debug --nologo --verbosity quiet
node .ironclad/gate.mjs --stage packet
pwsh -NoProfile -File tools/test-recorder-ui.ps1
```

## Council verdicts (P-2, closed)

| Role | Verdict | Notes |
|---|---|---|
| Architect | PASS | The fix is an atomic claim in the state machine, not a UI-level latch, so the invariant lives where the state does. Created `SevenRecord.App.Presentation` under the boundary the charter already declared; gate confirms all 10 rules hold. `RecorderTextFormatter` deliberately takes `(string, string)` pairs rather than `WindowsRecordingIssue` so the assembly does not pull Win2D and the Windows App SDK into its test closure. |
| Coder | PASS-WITH-NOTES → fixed | Review found four regressions introduced by moving the claim earlier. **High:** `TryBeginStart` raises `StateChanged` synchronously, so `ApplyRecorderVisualState(Starting)` had already relabelled the button to "Cancel" and enabled it — and the next line disabled it again, killing the only escape from a hung start. **Medium:** `AbandonClaimedStart` forced `IsEnabled = true`, re-enabling Record after readiness had just refused. **Medium:** it also transitioned state *before* signalling the completion with no `finally`, so a throw in `ResetLivePreview` would leave the completion unresolved and, because `MainWindow` latches `_closeInProgress`, make the window permanently unclosable. **Medium:** app close began waiting on a claimed start that could be blocked on the capture picker. All four fixed. |
| QA | PASS-WITH-NOTES → partly fixed | Review correctly established that `tools/test-recorder-ui.ps1` is **not** evidence for this packet — it never presses Record and cannot observe `Starting`. Added the missing `RecorderTextFormatter` tests (the only extracted member whose signature and body changed, and it had none), `TryAbortStart` coverage from `Idle` and `Faulted`, and a bounded rendezvous in the concurrency test, which previously used an unbounded `Barrier` that could hang a small CI agent. **Accepted gap:** the MainPage claim-before-await ordering itself still has no automated test, because `MainPage` has no seam. That is exactly what P-9 exists to fix, and it is recorded there rather than pretended away. |
| UX | PASS-WITH-NOTES | Abandoning a start returns straight to `Idle` rather than through `Stopping`, so the user is never told a recording that never began is being torn down. The Cancel affordance is preserved during startup (the High finding above). Note carried forward: the recorder now shows `Starting` while the capture picker is open, which is honest but is a visible behaviour change. |
| Security | PASS | No new I/O, no new external surface. `BestEffortCleanup.DeletePartialRenders` matches on a pattern derived from the export's own file name, and a test pins that a sibling export's partial file is not deleted. |

## Open unknowns

U-1 RESOLVED, U-2 RESOLVED, U-3 RESEARCHED (see `docs/UNKNOWNS.md`). U-3 directly shapes P-3.

## Last completed

**P-2 — recorder commands are serialized and cannot race.** 201 tests (was 155 at P-8),
13 test projects. Build 0 warnings / 0 errors; UIA gate passes; Ironclad gate 22 passed /
0 failed with 10 boundaries upheld; app launches and writes diagnostics.

**P-8 — the FFmpeg filter graph is pinned by characterization tests.** Proven to have teeth
by mutation testing (see ROADMAP P-8).

**P-1 — crash barrier and diagnostics.** Commit `6c12313`.

## Why P-8 was taken before P-2 (recorded re-plan)

P-8 needed no production change and carried no runtime risk, while P-2 is surgery on the
recorder lifecycle whose acceptance criteria ("a second Record click during camera
shutdown") cannot be fully validated in this Remote Desktop session, where
`GraphicsCapturePicker` returns null and the redirected camera intermittently delivers no
frames. Doing the zero-risk packet first bought protection for the export path without
spending the risk budget on work that could not be properly proven here. P-2 was then done
with its testable core (the state machine) covered by 15 unit tests; the UI ordering it
changed still wants a manual pass on a local console.

## Notes for the next session

- **Read `docs/ROADMAP.md` "The thesis" first.** The whole plan follows from one idea: every
  defect found is an invariant that exists only in someone's head. The work is converting those
  into things a program enforces.
- The audit that produced this plan is real and cited. The Architect council seat's nine findings
  carry exact file:line references; they are recorded inline in the roadmap next to each packet.
- **`FaultBarrier` is wired into 6 handlers, not all 33.** `OnLoaded`, `OnUnloaded`,
  `OnRefreshReadinessClicked`, `OnRefreshProjectsClicked`, `OnCloseCameraStudioClicked` and
  `StopCaptureFromDispatcher`. The global handler in `App.xaml.cs` covers the rest as a
  backstop. Wire more as you touch them — do not do a mass edit for its own sake.
- **Do not "fix" the God object by making partial classes.** It is explicitly out of scope in the
  roadmap with a reason. Ownership seams first (P-9), file splitting maybe never.
- The `MainPage.xaml.cs` file-size exception in the charter is a **ratchet set at 3900**; the file
  is currently 3865. It may only ever move down. Raising it is an ADR, not an edit.
- Windows Defender intermittently locks `SevenRecord.Media.Worker` DLLs mid-build. The build
  fails with `CS2012 ... being used by another process`. Wait ~5s and rebuild; it is not your
  change. A running `SevenRecord.App.exe` also locks assemblies — `Stop-Process -Id <PID>` first.
- This is a Remote Desktop session: `GraphicsCapturePicker` returns null with no window, and the
  redirected camera intermittently delivers no frames and advertises no Windows Studio blur. Those
  are environment limits, not regressions.
