# 7Record — Status

> The first file any session reads. Write it as a handover note to someone who knows nothing.

**Active packet:** P-1 — A crash cannot be silent, and a handler fault cannot kill the app
**State:** CONTRACT
<!-- PLAN → CONTRACT → RED → GREEN → REFACTOR → COUNCIL → GATE → DONE -->
**Branch:** `feat/crash-barrier-diagnostics`

## Acceptance criteria

- [ ] Given an `async void` UI handler throws, when the exception escapes it, then the process
      stays alive and a durable diagnostic record is written.
- [ ] Given a fault occurs with no debugger attached (Release), when it reaches the XAML
      framework, then it is recorded before any termination.
- [ ] Given a fire-and-forget `Task` faults and is never observed, when it is finalized, then the
      fault is still recorded (`TaskScheduler.UnobservedTaskException`).
- [ ] Given the diagnostics directory is unwritable, when a fault is recorded, then recording the
      fault must not itself throw. **A crash reporter that crashes is worse than none.**
- [ ] Given many faults occur, when the log grows, then it is bounded and rotates — an unbounded
      log on a recording machine is a disk-space bug.

## Commands that prove it

```
dotnet test SevenRecord.slnx --configuration Debug --nologo --verbosity quiet
node .ironclad/gate.mjs --stage packet
```

## Council verdicts (this packet)

| Role | Verdict | Notes |
|---|---|---|
| Architect | — | |
| Coder | — | |
| QA | — | |
| UX | — | |
| Security | — | |

## Open unknowns

U-1 RESOLVED, U-2 RESOLVED (see `docs/UNKNOWNS.md`). None blocking.

## Last completed

M0 — the discipline harness: charter with 10 machine-enforced architecture boundaries, gate,
pre-commit hook, CI workflow, roadmap, unknowns register, ADR-0001/0002.

## Notes for the next session

- **Read `docs/ROADMAP.md` "The thesis" first.** The whole plan follows from one idea: every
  defect found is an invariant that exists only in someone's head. The work is converting those
  into things a program enforces.
- The audit that produced this plan is real and cited. The Architect council seat's nine findings
  carry exact file:line references; they are recorded inline in the roadmap next to each packet.
- **Do not "fix" the God object by making partial classes.** It is explicitly out of scope in the
  roadmap with a reason. Ownership seams first (P-9), file splitting maybe never.
- The `MainPage.xaml.cs` file-size exception in the charter is a **ratchet set at 3900**. It may
  only ever move down. If you need to raise it, that is an ADR, not an edit.
- Windows Defender intermittently locks `SevenRecord.Media.Worker` DLLs mid-build. The build
  fails with `CS2012 ... being used by another process`. Wait ~5s and rebuild; it is not your
  change. A running `SevenRecord.App.exe` also locks assemblies — `Stop-Process -Id <PID>` first.
- This is a Remote Desktop session: `GraphicsCapturePicker` returns null with no window, and the
  redirected camera intermittently delivers no frames and advertises no Windows Studio blur. Those
  are environment limits, not regressions.
