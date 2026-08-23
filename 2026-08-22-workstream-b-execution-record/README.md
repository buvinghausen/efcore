# Workstream B — execution record

Preserved from the subagent-driven execution of
[`2026-08-21-temporal-entity-splitting-implementation-plan.md`](../2026-08-21-temporal-entity-splitting-implementation-plan.md),
Phase B (Tasks B1–B10), run 2026-08-22 to 2026-08-23.

The code is on `feature/per-store-object-constraint-names` (28 commits). This directory holds
the *reasoning* behind it — the part commit messages are too short to carry, and the part worth
having when someone asks "why is it built this way".

Kept because it is not reconstructible. Deliberately dropped: the per-task review diffs (they are
just `git diff A..B` over commits that are all on origin) and the brief-extraction scripts.

## Contents

| File | What it holds |
|---|---|
| `00-sdd-ledger.md` | The controller's ledger. Every ruling made during execution with its evidence and what it costs if wrong, the pre-flight conflict scan, and per-task outcomes. **Start here.** |
| `task-B1-report.md` … `task-B10-report.md` | Per-task implementer reports: TDD evidence with RED/GREEN transcripts, files changed, deviations from the plan with justification, self-review findings. |
| `final-fix-report.md` | The fix wave from the whole-branch review — ownership-FK snapshot drop, and narrowing the shared-constraint validation. |
| `codex-fixes-report.md` | Fixes for six of seven findings raised by an automated reviewer on the fork PR, plus the regression that wave introduced and the correction for it. |
| `final-polish-report.md` | The last two fixes: making the validator test order-independent, and closing two untested cases around ownership foreign keys. |

## The decisions most likely to be asked about

- **Why foreign key overrides carry a composite `(dependent, principal)` identity** while key
  overrides key on a single store object — one model foreign key serves every entity-splitting
  fragment's linking constraint, so single-store-object identity cannot name them apart.
  Spec §4; proven in `task-B3-report.md`.

- **Why a naming convention can name the linking foreign key at all.** It does not exist before
  `FinalizeModel()` — `EntitySplittingConvention.ProcessModelFinalizing` creates it — and a
  hand-built stand-in is deleted first by `ModelCleanupConvention.RemoveNavigationlessForeignKeys`.
  The path that works is a finalizing convention registered *after* `EntitySplittingConvention`,
  which is exactly how EFCore.NamingConventions plugs in. Analysis in `task-B4-report.md`,
  proven by the acceptance test in `task-B9-report.md`.

- **Why the reattach conventions use `IKeyAnnotationChangedConvention` rather than
  `IKeyAddedConvention`.** `Attach` sometimes reuses an existing key instead of creating one, and
  no added-event fires on that path while the stale override is still merged across.
  `task-B5-report.md`.

- **Why `RemoveNameOverride`'s configuration-source comparison looks backwards.** It is
  byte-identical to upstream's shipped `RelationalPropertyOverrides.RemoveColumnNameOverride`.
  An automated reviewer flagged it; it was deliberately left alone, because diverging in one copy
  of a faithful mirror is worse than matching an upstream oddity. If it is a bug it is upstream's.
  `codex-fixes-report.md`.

## Known gaps at the time of writing

- **SQL Server functional tests have never run** — no instance was available in the execution
  environment. This is the one real coverage hole. Sqlite, Relational, Design and the API
  baseline suites were all swept.
- Sqlite shows 177 `mod_spatialite` failures on linux-arm64; environmental, confirmed unrelated.
- `EFCore.Design.Tests` carries 5 pre-existing failures (Windows-path assertions on Linux) in an
  untouched file.
