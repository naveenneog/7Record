# 7Record — Charter

> The prose half of the contract. The machine-checkable half is `.ironclad/charter.json`,
> enforced by `node .ironclad/gate.mjs`. When the two disagree, one of them is a bug.

## What this is
<!-- One paragraph. What does this do, and for whom? -->

## Goals
<!-- 3–5 outcomes that define success. Observable, not aspirational. -->
1.
2.
3.

## Non-goals
<!-- As important as the goals: what we are deliberately NOT building, and why.
     This is what stops the same idea being re-litigated in every session. -->
-

## Constraints
<!-- Platforms, budgets, offline/keyless requirements, privacy, performance, deadlines. -->
-

## Quality bar
| Dimension | Bar |
|---|---|
| Tests | Test-first. Every behaviour has a test; every bug fix starts with a failing test. |
| Coverage | Floor: not set — set quality.coverageFloor once a baseline exists (never goes down) |
| Architecture | Boundaries in `.ironclad/charter.json` are enforced on every commit |
| Security | No secrets in source; input validated at the boundary; authz checked per object |
| Accessibility | Keyboard-operable, labelled controls, ≥4.5:1 contrast, four states per surface |
| Docs | ADR for every significant decision; STATUS + CHANGELOG updated per packet |

## Definition of done (a packet is done when all are true)
- [ ] Acceptance criteria in `docs/STATUS.md` are met, each with a test
- [ ] Tests pass; nothing skipped, `.only`'d, deleted or weakened
- [ ] Council verdicts recorded (Architect · Coder · QA · UX · Security)
- [ ] `node .ironclad/gate.mjs --stage packet` exits 0
- [ ] Unknowns for this packet closed (RESEARCHED / ASSUMED-with-detector / RESOLVED)
- [ ] ADR written if the decision was significant
- [ ] CHANGELOG + README updated where behaviour or usage changed
- [ ] Committed, with the hash recorded in `docs/STATUS.md`

## Stack
unknown — fill in docs/CHARTER.md

## Commands
```
test       <declare commands.test in .ironclad/charter.json>
gate       node .ironclad/gate.mjs --stage packet
```
