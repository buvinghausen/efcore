# Task B10 Report: Baseline and provider sweep

## Status: DONE_WITH_CONCERNS

The only open item is SQL Server functional coverage, which cannot be run in this sandbox at all
(no `Test__SqlServer__DefaultConnection`, no instance) — noted explicitly below and in the PR body,
per the brief.

## Commit

`d741ad1346` — "Baseline the per-store-object constraint name API" (Task B10), on top of `e0377c3846`.

Files touched (all staged explicitly by name; nothing under `.superpowers/` was ever staged):
- `src/EFCore.Relational/EFCore.Relational.baseline.json` (+409/-0)
- `src/EFCore.Design/EFCore.Design.baseline.json` (+6/-0)
- `src/EFCore.Relational/Metadata/IConventionRelationalKeyOverrides.cs` (+2/-2, doc fix)
- `src/EFCore.Relational/Metadata/IConventionRelationalForeignKeyOverrides.cs` (+2/-2, doc fix)

## Correction 1 applied — two independent baseline checks

The brief conflated `RelationalApiConsistencyTest` (naming/docs/metadata-quartet checks, in
`EFCore.Relational.Tests`) with the `baseline.json` snapshot check (in a wholly separate project,
`test/EFCore.ApiBaseline.Tests/ApiBaselineTest.cs:24`). Handled both, separately:

**`RelationalApiConsistencyTest`** — built and ran directly:
```
total: 20
failed: 0
succeeded: 20
skipped: 0
```
Stayed green throughout the workstream, confirmed here to still be green. No changes needed.

**`test/EFCore.ApiBaseline.Tests`** — read `ApiBaselineTest.AssertBaselineMatch` (`:20-71`) before
touching anything, per the "Before You Begin" instruction. Behavior confirmed by reading the code:
when not running in CI (`CI`/`BUILD_BUILDID`/`PIPELINE_WORKSPACE`/`GITHUB_RUN_ID` all unset here —
confirmed via `env | grep`), a stale-but-present baseline is **rewritten from the live assembly and
the test still reports Passed**, rather than failing. So the correct action was: build the two
affected assemblies, run the filtered baseline tests once (which silently regenerates the stale
files), then verify by re-running that the diff is now stable (no further changes on a second run)
and that the JSON is well-formed.

Filtered run (`EFCoreRelationalApiBaselineTest`): `total: 1, failed: 0, succeeded: 1` — first pass
regenerated `EFCore.Relational.baseline.json` (409 insertions, 0 deletions — purely additive, matching
a workstream that never removed public API). Second pass (full `EFCore.ApiBaseline.Tests` suite,
all 14 provider baseline classes): `total: 14, failed: 0, succeeded: 14`, and `git diff --stat`
showed **zero further changes** — confirming the regenerated baseline is stable, not still drifting.

`python3 -c "import json; json.load(...)"` confirmed the regenerated file is valid JSON.

## What actually landed in the Relational baseline (verified by reading the diff, not assumed)

- `StoreObjectPair` (record-like readonly class), `StoreObjectPairDictionary<T>`,
  `IReadOnlyStoreObjectPairDictionary<T>`
- The eight override interfaces: `IReadOnlyRelationalKeyOverrides`, `IMutableRelationalKeyOverrides`,
  `IConventionRelationalKeyOverrides`, `IRelationalKeyOverrides`, and their four FK equivalents,
  plus the two builder interfaces (`IConventionRelationalKeyOverridesBuilder`,
  `IConventionRelationalForeignKeyOverridesBuilder`)
- The two runtime types: `RuntimeRelationalKeyOverrides`, `RuntimeRelationalForeignKeyOverrides`
- The two conventions: `KeyOverridesConvention`, `ForeignKeyOverridesConvention`
- `RelationalAnnotationNames.KeyOverrides` / `.ForeignKeyOverrides` constants
- New members on the four extension classes: `RelationalKeyExtensions`, `RelationalForeignKeyExtensions`,
  `RelationalKeyBuilderExtensions`, `RelationalForeignKeyBuilderExtensions` — including
  `HasConstraintName`/`HasName` overloads taking a `StoreObjectIdentifier` (and pair), `GetOverrides`,
  `RemoveOverrides`, `SetName`/`SetConstraintName`, `CanSetName`/`CanSetConstraintName`,
  `GetNameConfigurationSource`/`GetConstraintNameConfigurationSource`
- `RelationalModelValidator.ValidateSharedForeignKeyNameOverrides` / new
  `RelationalRuntimeModelConvention.ProcessKeyOverridesAnnotations` /
  `.ProcessForeignKeyOverridesAnnotations`, and `RelationalStrings.DuplicateConstraintNameOverride`.

**Correction 2 confirmed by the tooling itself, not just asserted:** `git diff` on the baseline shows
**zero** new `Type` entries for `IConventionKeyBuilder` or `IConventionForeignKeyBuilder` — the new
`HasConstraintName`/`HasName`/`CanSetName`/`CanSetConstraintName` members appear only as `static`
extension methods inside `RelationalKeyBuilderExtensions` / `RelationalForeignKeyBuilderExtensions`,
confirming those two Core-assembly interfaces were never touched. `git diff` also shows 0 deletion
lines overall — the whole change is additive, as expected for a pure API-extension workstream.

### The Design baseline also needed regenerating — not anticipated by the original dispatch

`src/EFCore.Design/EFCore.Design.baseline.json` picked up 6 new lines:
```
+ virtual void GenerateForeignKeyOverrides(IForeignKey foreignKey, IndentedStringBuilder stringBuilder);
+ virtual void GenerateKeyOverrides(string keyBuilderName, IKey key, IndentedStringBuilder stringBuilder);
```
Root cause, confirmed by `git log --oneline 2965e616bf..HEAD -- test/EFCore.Design.Tests/ src/EFCore.Design/`:
Task B8 (commit `322d7e2d97`, "Emit per-store-object constraint names into snapshots") added these
two public virtual methods to `CSharpSnapshotGenerator` in `EFCore.Design`, which is its own
baseline-checked assembly, separate from `EFCore.Relational`. The full `EFCore.ApiBaseline.Tests`
run (14/14 passed) regenerated it the same way, and a second run confirmed stability (no further
diff). Nothing else in the 14-baseline sweep changed — verified via `git status --short` after the
full-suite run: only these two files were modified.

## Doc fix

`IConventionRelationalKeyOverrides.cs:20,22` and `IConventionRelationalForeignKeyOverrides.cs:20,22`
both read "Gets the builder that can be used to configure this **function**" / "If the **function**
has been removed from the model" — copy/paste leftovers from
`IConventionRelationalPropertyOverrides.cs:20,22`, which has the identical wording (verified by
`grep`; left untouched, as instructed, so an upstream PR fixing it stays single-subject). Fixed the
two files this branch owns to say "this key"/"the key" and "this foreign key"/"the foreign key"
respectively. Both projects that reference these files (`EFCore.Relational.Tests`,
`EFCore.Design.Tests`) built with 0 warnings / 0 errors afterward, confirming StyleCop-as-errors is
satisfied.

## Provider sweep — raw counters, all from direct-dll runs (never `build.sh`, per the evidence rules)

### EFCore.Relational.Tests — REQUIRED, PASSED
```
total: 1501
failed: 0
succeeded: 1500
skipped: 1
```
Matches the stated current baseline (total=1501 executed=1500 passed=1500 failed=0 notExecuted=1)
exactly — no regression, and this is the project the workstream's own tests live in.
Targeted confirmation that the workstream's tests are actually present and passing (not silently
absent from a stale run):
- `--filter-class '*RelationalKeyOverridesTest'` → `total: 8, failed: 0, succeeded: 8`
- `--filter-class '*RelationalForeignKeyOverridesTest'` → `total: 10, failed: 0, succeeded: 10`

### test/EFCore.ApiBaseline.Tests — ADDED per Correction 1, PASSED
```
total: 14
failed: 0
succeeded: 14
```
All 14 provider baseline classes (Relational, Design, EFCore core, Abstractions, Cosmos, InMemory,
Proxies, Sqlite Core/NTS, SqlServer/Abstractions/HierarchyId/NTS, Microsoft.Data.Sqlite.Core) pass
after the two regenerations above.

### EFCore.Sqlite.FunctionalTests — REQUIRED, PASSED (net of confirmed-environmental failures)
Full project run, direct-dll, `--report-trx` for auditability:
```
total: 38420
failed: 177
succeeded: 37957
skipped: 286
duration: 6m 46s
```
This run auto-backgrounded once (exceeded the tool's 120s foreground default) and I ended a turn
without collecting it — a controller course-correction caught that and I polled the process to
completion in the foreground on resume, which is the version reported here. No result was fabricated
in between; the "waiting" message sent before the correction stated only that the run was in
progress, made no claim about its outcome, and predicted nothing about pass/fail counts.

**All 177 failures independently and exhaustively verified as the same environmental cause** — I did
not spot-check and extrapolate. Parsed the TRX programmatically: every one of the 177 `outcome="Failed"`
entries has `mod_spatialite.so: cannot open shared object file: No such file or directory` in its
error message (`SqliteException`, `SqliteConnection.LoadExtensionCore`/`.Open`), either directly
(non-spatial-named tests like `CompiledModelSqliteTest.BigModel`/`SimpleModel`/`No_NativeAOT`/
`BigModel_with_JSON_columns`, which incidentally use a spatial-enabled connection string) or via
`SpatialQuerySqliteFixture.InitializeAsync` throwing during fixture setup for the ~170
`SpatialQuerySqliteTest.*` cases. This arm64 sandbox has no native `mod_spatialite` binary — the
brief flagged this as a known possibility and asked me to confirm rather than assume; confirmed via
a 177/177 exhaustive scan, 0 failures with any other cause.

**The four new B7/B8 tests are confirmed present and passing in this exact full run** — none of the
four `*survive*_the_compiled_model` test names appear anywhere in the 177-line failure list.
Separately (before the full run), filtered directly:
```
--filter-class '*CompiledModelSqliteTest' --filter-method '*survive*'
total: 4, failed: 0, succeeded: 4
```
Using `*survive*` deliberately, not `*survives*` — the four methods are
`Per_store_object_constraint_names_survive_the_compiled_model`,
`Per_store_object_foreign_key_constraint_names_survive_the_compiled_model`,
`Explicit_null_key_constraint_name_override_survives_the_compiled_model`, and
`Explicit_null_foreign_key_constraint_name_override_survives_the_compiled_model` — two "survive",
two "survives". A `*survives*` filter would have silently matched only 2 of 4, exactly the failure
mode the evidence rules warned about from earlier in this run.

Net assessment: **0 non-environmental failures** in `EFCore.Sqlite.FunctionalTests`.

### EFCore.Design.Tests — REQUIRED, 5 pre-existing failures confirmed, all else PASSED
Full project run, direct-dll:
```
total: 1275
failed: 5
succeeded: 1269
skipped: 1
```
The 5 failures are all in `OperationExecutorTest`
(`AddMigration_errors_for_bad_names(migrationName: "A\B\C")`,
`AddMigration_errors_for_bad_names(migrationName: "to fix error: add column is_deleted")`,
`AddAndApplyMigration_errors_for_bad_names(migrationName: "to fix error: add column")`,
`AddAndApplyMigration_errors_for_bad_names(migrationName: "A\B\C")`,
`AddMigration_errors_for_bad_output_dirs(outputDir: "Something:Else")`) — each asserts that a
Windows-illegal path/name (backslashes, a bare colon) is rejected; Linux's path-validity rules don't
reject those characters, so the assertion is false on this platform.

Confirmed pre-existing rather than assumed: `git log --oneline 2965e616bf..HEAD --
test/EFCore.Design.Tests/Design/OperationExecutorTest.cs` returns **no commits** — this workstream
never touched that file. The only Design-Tests file this workstream touched is
`CSharpMigrationsGeneratorTest.ModelSnapshot.cs` (Task B8), confirmed separately:
`--filter-class '*CSharpMigrationsGeneratorTest*'` → `total: 171, failed: 0, succeeded: 171`.

Net assessment: 5/5 failures pre-existing and platform-specific, 0 attributable to this branch.

### EFCore.SqlServer.FunctionalTests — NOT RUN, cannot be run here
`Test__SqlServer__DefaultConnection` is unset (`env | grep -i Test__SqlServer` → empty) and there is
no SQL Server instance in this sandbox. Per the brief, did not attempt it. **This coverage is
unverified and must be run before the PR is opened.**

## Concerns

1. **SQL Server functional coverage is entirely unverified.** The change is provider-agnostic
   (lives in `EFCore.Relational`), and the Sqlite sweep found nothing wrong, but SQL Server has its
   own constraint-naming quirks (e.g., 128-char identifier limits, different quoting) that neither
   Sqlite nor the unit tests exercise. Someone with SQL Server access needs to run
   `EFCore.SqlServer.FunctionalTests` before merge.
2. The `EFCore.Sqlite.FunctionalTests` run took ~7 minutes and initially exceeded this tool's 120s
   foreground default, auto-backgrounding. I now have the complete, non-stale result via a foreground
   poll to process exit, confirmed by a fresh build immediately beforehand and by the four new tests
   being nameable and present in the TRX.
3. Two pre-existing, unrelated issues are visible but out of scope for this task: the 5 Design.Tests
   Windows-path failures, and the dead `HasConstraintName(string)` test wrapper on
   `TestReferenceCollectionBuilder`/its NonGeneric twin noted during Task B6 (see `progress.md`). Both
   are worth a line in the PR body as drive-by findings; neither is fixed here.

## Drafted PR — title and body, ready to paste

**Title:**
```
Support per-store-object key and foreign key constraint names
```

**Body:**
```markdown
Addresses #27972 and #27971.

Key overrides are keyed by a single store object; foreign key overrides are
keyed by the (dependent, principal) store-object pair, because one model
foreign key serves every entity-splitting fragment's linking constraint.

Unblocks efcore/EFCore.NamingConventions#396, which currently has to remove
rewritten names for fragment-bearing entities rather than rewrite them per
table.

## Metadata checklist (spec §4)

Covered:
- Global-name fallback precedence and explicit-null semantics (an override set
  to null suppresses a global rewritten name down to the default) — both the
  in-memory `RelationalRuntimeModelConvention.Create` path and the NativeAOT
  codegen `Create` helpers are covered separately, since they are independent
  conversion paths.
- Shared-constraint conflict handling: conflicting overrides on a constraint
  that deduplicates across table-sharing entity types are a validation error
  rather than last-writer-wins.
- `Attach`/`MergeInto` on key and FK re-creation (entity-type re-parenting,
  detach/reattach during model building).
- Convention-versus-explicit configuration sources.
- Runtime-model (compiled-model) generation and migrations-snapshot generation.
- Debug output (`ToDebugString`).

See `RelationalKeyOverridesTest` and `RelationalForeignKeyOverridesTest` for
the metadata-layer coverage, and `CompiledModelSqliteTest`'s four
`*survive*_the_compiled_model` tests plus `CSharpMigrationsGeneratorTest`'s
snapshot tests for the round-trip coverage.

Deliberately left open:
- TPT root-FK behaviour.
- View and function store-object types: the resolvers early-return for
  non-table store objects today, and the overrides inherit that.

## Proof it solves the driving problem

`Rewriting_constraint_names_per_store_object_produces_no_collisions`
(`MigrationsModelDifferTest`) reproduces the EFCore.NamingConventions#396
collision shape end-to-end: a model-finalizing convention appended after
`EntitySplittingConvention` attaches per-store-object names to the linking FK
the instant it's created, and the finalized `RelationalModel` comes out with
zero duplicate constraint names across the split tables.

## Drive-by findings (not fixed on this branch)

- `IConventionRelationalPropertyOverrides.cs:20` has the same "Gets the builder
  that can be used to configure this function" / "If the function has been
  removed" copy/paste error that this PR fixes on the two interfaces it added
  (`IConventionRelationalKeyOverrides`, `IConventionRelationalForeignKeyOverrides`).
  Left alone here so this PR's diff stays single-subject; worth a follow-up.
- `TestReferenceCollectionBuilder`'s Generic/NonGeneric implementations
  (`ModelBuilderTest.Generic.cs:1496,1630`) implement neither
  `IInfrastructure<ReferenceCollectionBuilder<,>>` nor the non-generic form,
  making the pre-existing single-arg `HasConstraintName(string)` test wrapper
  (`RelationalTestModelBuilderExtensions.cs:1586-1603`) dead code. Worked around
  via `GetInfrastructure()` bridging for this PR's new tests; not fixed upstream.

## Test coverage

- `EFCore.Relational.Tests`: 1500/1501 passed (1 pre-existing skip), including
  18 new tests in `RelationalKeyOverridesTest`/`RelationalForeignKeyOverridesTest`.
- `EFCore.Sqlite.FunctionalTests`: full project sweep, 0 non-environmental
  failures (177 failures are `mod_spatialite` load failures in a sandbox
  without that native library — unrelated to this change, verified
  exhaustively). The 4 new compiled-model round-trip tests pass.
- `EFCore.Design.Tests`: 1269/1275 passed; the 5 failures are pre-existing,
  platform-specific (`OperationExecutorTest` Windows-path assertions failing
  on Linux), and untouched by this branch. The 171 tests in
  `CSharpMigrationsGeneratorTest` (including this PR's snapshot-generation
  tests) all pass.
- `EFCore.SqlServer.FunctionalTests`: **not run — no SQL Server instance
  available in the environment that prepared this PR.** Needs to run before
  merge.

🤖 Generated with [Claude Code](https://claude.com/claude-code)
```

## Self-review checklist

- **Does the baseline now contain every public member this workstream added, and nothing it did
  not?** Yes for the parts verifiable from the diff: purely additive (0 deletions), contains all the
  named types/interfaces/conventions/runtime types, and the extension-method members land on the
  four `*Extensions` classes rather than on `IConventionKeyBuilder`/`IConventionForeignKeyBuilder`
  (Correction 2, confirmed structurally). I did not manually cross-reference every single member
  signature in the 409-line diff against every commit's source changes line-by-line; I relied on the
  tool that generates the baseline from the compiled assembly, which is the same mechanism CI uses
  to gate this file, plus targeted checks (the Type-count of IConventionKeyBuilder/IConventionForeignKeyBuilder
  is 0, no deletions, JSON is valid, two independent regenerate-then-verify-stable passes).
- **Did every sweep suite actually run, with counts you can name?** Yes for all four required plus
  the added ApiBaseline one — all five have raw counter lines above. SQL Server explicitly did not
  run, stated plainly rather than glossed over.
- **Is the PR description accurate about what was built and honest about what was not verified?**
  Yes — SQL Server gap is called out twice (its own section, `## Test coverage` bullet), and the two
  drive-by findings are disclosed rather than silently fixed or silently dropped.
