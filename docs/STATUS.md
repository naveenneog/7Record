# 7Record — Status

> The first file any session reads. Write it as a handover note to someone who knows nothing.

**Active packet:** P-2 — Recorder commands are serialized and cannot race
**State:** PLAN
<!-- PLAN → CONTRACT → RED → GREEN → REFACTOR → COUNCIL → GATE → DONE -->
**Branch:** not yet created

## Acceptance criteria

- [ ] Given camera shutdown or source selection is still awaiting, when a second Record
      click or global hotkey arrives, then exactly one state transition occurs.
- [ ] Given two start commands race, when both reach the state machine, then no exception
      escapes to the UI thread.

## Commands that prove it

```
dotnet test SevenRecord.slnx --configuration Debug --nologo --verbosity quiet
node .ironclad/gate.mjs --stage packet
pwsh -NoProfile -File tools/test-recorder-ui.ps1
```

## Council verdicts (P-1, closed)

| Role | Verdict | Notes |
|---|---|---|
| Architect | PASS | Diagnostics placed in the existing `SevenRecord.Infrastructure` rather than a new assembly, reusing the established `%LOCALAPPDATA%\7Record` convention. No boundary added or violated; gate confirms all 10 rules still hold. |
| Coder | PASS-WITH-NOTES → fixed | Review found 6 real defects. **Issue 1 (High):** retention deleted the newest logs after a same-day restart, and could delete the file being written — fixed by seeding the sequence from disk and pruning by `LastWriteTimeUtc` while excluding the active file. **Issue 2 (High):** `Format`/`GetBytes` sat outside the `try`, so `Write` could throw, and `App` logged *before* setting `e.Handled` — so a log failure handed back the very crash the packet prevents. Fixed both. **Issue 6:** argument validation threw out of the barrier; now contained. |
| QA | PASS-WITH-NOTES → fixed | Review proved the concurrency test ran on exactly one thread and that no test covered a Task faulting *after* an await — the actual `async void` hazard. Also proved the restart-budget test could not fail for the bug it named (2× tolerance vs a 1× failure mode), and that 256/512-byte budgets were silently clamped to 1024. All corrected; suite grew 24 → 33 and runtime dropped 21s → 542ms after removing a thread-pool starvation hazard. |
| UX | PASS-WITH-NOTES | Contained faults now surface in `ReadinessInfoBar` instead of failing silently. Open note carried to P-2/P-3: "handled" should also transition the recorder to a stop-and-flush state so a survived fault cannot masquerade as a healthy recording. |
| Security | PASS | Diagnostics contain exception text and file paths only — no credentials, no user content, no media. Written under the user's own `%LOCALAPPDATA%`. Log is bounded and pruned, so it cannot be used to exhaust the disk. Gate's secret/credential/env scans all clean. |

## Open unknowns

U-1 RESOLVED, U-2 RESOLVED, U-3 RESEARCHED (see `docs/UNKNOWNS.md`). None blocking P-2.

## Last completed

**P-8 — the FFmpeg filter graph is pinned by characterization tests.** 13 tests over
`FfmpegRenderPlanExporter.CreateCommand`, zero production changes, zero runtime risk.
Proven to have teeth by mutation testing (see ROADMAP P-8). Suite: 155 tests.

**P-1 — crash barrier and diagnostics.** Commit `6c12313`.
Proven by: 142 tests pass (was 113); solution builds 0 warnings / 0 errors;
`tools/test-recorder-ui.ps1` passes; gate `--stage packet` = 22 passed / 0 failed; and the
real app was launched and confirmed writing
`%LOCALAPPDATA%\7Record\Diagnostics\7record-<date>-0000.log`.

## Why P-8 was taken before P-2 (recorded re-plan)

The roadmap lists P-2 next. P-8 was taken first deliberately: it needs no production change
and carries no runtime risk, while P-2 is surgery on the recorder lifecycle whose acceptance
criteria ("a second Record click during camera shutdown") cannot be fully validated in this
Remote Desktop session, where `GraphicsCapturePicker` returns null and the redirected camera
intermittently delivers no frames. Doing the zero-risk packet first buys real protection for
the export path without spending the risk budget on work that cannot be properly proven here.
P-2 remains the next packet and should be done on a local console session.

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
