# Task B9 Report: Differ, shared constraints, and the NamingConventions end-to-end

## The single most important sentence

**The finalizing-convention approach to the linking FK worked exactly as the task predicted.** A
model-finalizing convention appended to `ConventionSet.ModelFinalizingConventions` *after*
`EntitySplittingConvention` observes the linking foreign key the instant that convention creates
it (`EntitySplittingConvention.ProcessModelFinalizing`), while the model is still mutable, and can
attach per-store-object key/FK-name overrides to it. `Rewriting_constraint_names_per_store_object_produces_no_collisions`
proves this end-to-end: `FinalizeModel().GetRelationalModel()` comes out with zero duplicate
constraint names across `users`/`user_lockout`/`user_profile`.

## What was implemented

### 1. Differ regression guard (Step 1/2) — passed on first run, as predicted

`test/EFCore.Relational.Tests/Migrations/Internal/MigrationsModelDifferTest.cs`:
`Per_table_constraint_names_reach_migration_operations` — a `Customer`/`CustomerDetails`
split-table model with per-store-object `HasKey(...).HasName(...)` calls, asserting the resulting
`CreateTableOperation.PrimaryKey.Name` differs per table. Passed immediately, confirming Spike 2
finding 2 (the differ consumes the resolvers for free via `RelationalModel`). Kept as the
regression guard the brief asked for.

Used a new private `SplitCustomer { Id, Email, Name }` class rather than reusing the file's
existing shared `Customer` fixture (no `Name` property, and used by ~15 other tests with exact
operation-list assertions — adding a property risked collateral changes to unrelated tests).

### 2. Shared-constraint conflict validation (Steps 3–4)

`src/EFCore.Relational/Infrastructure/RelationalModelValidator.cs`:
- New `protected virtual void ValidateSharedForeignKeyNameOverrides(IReadOnlyList<IEntityType> mappedTypes, in StoreObjectIdentifier table, IDiagnosticsLogger<DbLoggerCategory.Model.Validation> logger)`, called from `ValidateTable` immediately after `ValidateSharedForeignKeysCompatibility` (line 1030).
- Private nested `ConstraintStructure` class: name-independent identity (dependent columns +
  principal table + principal-key columns), compared by sequence equality, hashed with count +
  elements (not delimiter-joined strings), exactly per the brief's injectivity argument.
- `RelationalStrings.DuplicateConstraintNameOverride(table, constraintName, otherConstraintName)`
  added to both `RelationalStrings.resx` and `RelationalStrings.Designer.cs` by hand, alphabetically
  positioned between `DuplicateColumnNameUnicodenessMismatch` and `DuplicateForeignKeyColumnMismatch`.

**One necessary deviation from the brief's literal method body, found via TDD, not by inspection:**
the brief's structural-bucketing code as given throws whenever two structurally identical FKs
resolve to *different* names, full stop. Running the full `EFCore.Relational.Tests` suite with
that version live broke three pre-existing, still-correct tests:
`Passes_for_incompatible_foreignKeys_within_hierarchy`,
`Passes_for_incompatible_foreignKeys_within_hierarchy_when_one_name_configured_explicitly`, and
(via a related double-logging issue, see below) `Detects_unmapped_foreign_keys_in_{TPT,TPC,entity_splitting}`.

Root cause: `SharedTableConvention.UniquifyForeignKeyNames` already auto-renames one of two
structurally-identical-but-*behaviorally incompatible* (different `OnDelete`, etc.) FKs to a
uniquified name (`FK_Animal_Person_Name` / `FK_Animal_Person_Name1`) specifically so they can
coexist as two separate physical constraints — this is correct, load-bearing, existing behavior,
not a naming-override conflict. My structural bucket alone couldn't distinguish "the same
constraint split across two overrides" (a real bug) from "two different constraints that happen to
share columns" (a legitimate outcome of incompatibility). I added a compatibility gate using the
same `IReadOnlyForeignKey.AreCompatible(..., shouldThrow: false)` helper `ValidateCompatible` and
`SharedTableConvention` already use: only throw when the two FKs are both structurally identical
*and* behaviorally compatible. This is additive precision, not a weakening — the new
`Conflicting_overrides_on_a_deduplicated_constraint_are_rejected` test still throws correctly with
this gate in place, and the false positives are gone.

Second finding from the same full-suite run: I originally passed the `logger` parameter through to
`GetConstraintName(table, principalTable, logger)`, which caused
`RelationalStrings.ForeignKeyPropertiesMappedToUnrelatedTables` warnings to be logged **twice**
(once by `ValidateSharedForeignKeysCompatibility`, again by my new method), breaking
`VerifyWarning`'s `.Single()` assertion in three TPT/TPC/entity-splitting tests. Fixed by calling
the 2-argument `GetConstraintName(table, principalTable)` overload (implicit `null` logger) in the
new method — `ValidateSharedForeignKeysCompatibility` already owns that diagnostic.

**Third finding, model-building, not validator:** the brief's literal test model (`Order`/
`OrderDetails` sharing a table, both declaring `CustomerId` FKs to `Customer` with no explicit
column configuration) does **not** actually deduplicate into one physical constraint by default.
`SharedTableConvention`'s column-disambiguation renames the second entity type's `CustomerId`
property onto a prefixed column (`OrderDetails_CustomerId`) to avoid a *column* collision, which
also changes its default constraint name — so the two FKs never become "the same database
constraint" and the conflict check (correctly) never fires. I added explicit
`.Property(x => x.CustomerId).HasColumnName("CustomerId")` on **both** sides to force the columns
(and therefore the constraint identity) to genuinely collide, which is what "shared constraint"
requires in the first place. The test's sanity-check `Assert.Equal` on the two default constraint
names is what caught this empirically — it passed even before the fix because default-name
resolution happens on the *pre-finalization* mutable model, before `SharedTableConvention`'s
disambiguation runs; only post-`FinalizeModel()` (where the real validator runs) do the columns
(and hence the names) actually diverge. Used new `SharedCustomer`/`SharedOrder`/`SharedOrderDetails`
classes rather than the brief's literal `Customer`/`Order`/`OrderDetails` names to avoid colliding
with existing large fixtures elsewhere in the test project.

### 3. The NamingConventions end-to-end proof (Step 5, rewritten per the task's explicit instruction)

`Rewriting_constraint_names_per_store_object_produces_no_collisions` in
`RelationalForeignKeyOverridesTest.cs`, rewritten exactly as instructed: instead of reaching for
`entityType.GetForeignKeys().Single(fk => fk.PrincipalEntityType == entityType)` on a
not-yet-finalized model (which — as predicted — does not have the linking FK, since
`EntitySplittingConvention.ProcessModelFinalizing` only creates it during finalization, and a
hand-built stand-in is deleted first by `ModelCleanupConvention.RemoveNavigationlessForeignKeys`
because Core finalizing conventions dispatch before Relational ones), the test appends a private
`RewriteConstraintNamesPerStoreObjectConvention : IModelFinalizingConvention` to
`ConventionSet.ModelFinalizingConventions` via
`FakeRelationalTestHelpers.Instance.CreateConventionBuilder(configureConventions: b => b.ConventionSet.ModelFinalizingConventions.Add(...))`.
Because `TestHelpers.CreateConventionBuilder` calls the provider's `CreateConventionSet()` (which
already registers `EntitySplittingConvention`) before invoking `configureConventions`, the appended
convention runs strictly after it — the same ordering guarantee
`RuntimeConventionSetBuilder.CreateConventionSet()` gives real `IConventionSetPlugin`s like
EFCore.NamingConventions in production. Inside `ProcessModelFinalizing`, the convention finds the
now-existing linking FK, casts it to `IMutableForeignKey`, and calls `SetName`/`SetConstraintName`
per store object — exactly what a real naming-convention plugin does. The final assertion checks
`constraintNames.Count == constraintNames.Distinct().Count()` (no duplicates at all), not merely
that some name was set, per the task's tightened bar.

## TDD evidence

### RED (before the `AreCompatible` gate and the `logger`-double-pass fix)

Full-suite run with the brief's literal validator body live:

```
Test run summary: Failed! - Microsoft.EntityFrameworkCore.Relational.Tests.dll (net11.0|arm64)
  total: 1501
  failed: 5
  succeeded: 1495
  skipped: 1
```
Failures: `Detects_unmapped_foreign_keys_in_entity_splitting`, `Detects_unmapped_foreign_keys_in_TPC`,
`Detects_unmapped_foreign_keys_in_TPT`, `Passes_for_incompatible_foreignKeys_within_hierarchy`,
`Passes_for_incompatible_foreignKeys_within_hierarchy_when_one_name_configured_explicitly`.

Also RED before the `HasColumnName` fix on the new conflict test in isolation:
```
failed Microsoft.EntityFrameworkCore.Metadata.RelationalForeignKeyOverridesTest.Conflicting_overrides_on_a_deduplicated_constraint_are_rejected (84ms)
  Assert.Throws() Failure: No exception was thrown
```

### GREEN (final state)

New-test isolation run:
```
$ .dotnet/dotnet artifacts/bin/EFCore.Relational.Tests/Debug/net11.0/Microsoft.EntityFrameworkCore.Relational.Tests.dll --filter-class '*RelationalForeignKeyOverridesTest'
Test run summary: Passed!
  total: 10
  failed: 0
  succeeded: 10
  skipped: 0
```
Confirmed by name (via `--report-xunit-trx`) that both new tests executed:
`Conflicting_overrides_on_a_deduplicated_constraint_are_rejected` and
`Rewriting_constraint_names_per_store_object_produces_no_collisions` — plus all 8 pre-existing
tests in the class, unchanged.

```
$ .dotnet/dotnet artifacts/bin/EFCore.Relational.Tests/Debug/net11.0/Microsoft.EntityFrameworkCore.Relational.Tests.dll --filter-method '*Per_table_constraint_names_reach_migration_operations*'
Test run summary: Passed!
  total: 1
  failed: 0
  succeeded: 1
```

Full-suite warm-node run after the fixes:
```
Test run summary: Passed! - Microsoft.EntityFrameworkCore.Relational.Tests.dll (net11.0|arm64)
  total: 1501
  failed: 0
  succeeded: 1500
  skipped: 1
```

### Final foreground `build.sh --test` on EFCore.Relational.Tests (required evidence)

```
$ ./build.sh --test --projects .../test/EFCore.Relational.Tests/EFCore.Relational.Tests.csproj
...
Tests succeeded: .../Microsoft.EntityFrameworkCore.Relational.Tests.dll [net11.0|arm64]
Build succeeded.
```

Raw counter line, pulled from the produced `.trx`
(`artifacts/TestResults/Debug/EFCore.Relational.Tests_net11.0_arm64.trx`):

```
<Counters total="1501" executed="1500" passed="1500" failed="0" error="0" timeout="0" aborted="0" inconclusive="0" passedButRunAborted="0" notRunnable="0" notExecuted="1" disconnected="0" warning="0" completed="0" inProgress="0" pending="0" />
```

Baseline given: `total=1498 executed=1497 passed=1497 failed=0 notExecuted=1`. New:
`total=1501 executed=1500 passed=1500 failed=0 notExecuted=1` — exactly +3, matching the three
tests added (differ, conflict, end-to-end). The one `notExecuted` is the pre-existing skipped
`RelationalModelTest.Can_use_relational_model_with_sprocs_and_views` (`#28703`), unrelated to this
task.

### EFCore.Design.Tests (Step 6)

```
$ .dotnet/dotnet artifacts/bin/EFCore.Design.Tests/Debug/net11.0/Microsoft.EntityFrameworkCore.Design.Tests.dll
Test run summary: Failed!
  total: 1275
  failed: 5
  succeeded: 1269
  skipped: 1
```
The 5 failures are all in `OperationExecutorTest` (`AddMigration_errors_for_bad_names`,
`AddAndApplyMigration_errors_for_bad_names`, `AddMigration_errors_for_bad_output_dirs`) and assert
that Windows-illegal path characters (`A\B\C`, `Something:Else`, a colon in a migration name)
produce an error — on this Linux environment those characters are legal in file paths, so the
expected error never fires. **Verified pre-existing and unrelated to this change**: `git stash`ed
this task's diff, rebuilt, and reran the same filtered set — identical 5 failures, identical
messages, identical line numbers. Restored the stash immediately after (`git stash pop`);
`git diff --stat` confirmed all five intended files came back unchanged before committing.

## Files changed

- `src/EFCore.Relational/Infrastructure/RelationalModelValidator.cs` — `ValidateSharedForeignKeyNameOverrides`, `ConstraintStructure`, call site.
- `src/EFCore.Relational/Properties/RelationalStrings.resx` / `.Designer.cs` — `DuplicateConstraintNameOverride`.
- `test/EFCore.Relational.Tests/Migrations/Internal/MigrationsModelDifferTest.cs` — `Per_table_constraint_names_reach_migration_operations`, `SplitCustomer`.
- `test/EFCore.Relational.Tests/Metadata/RelationalForeignKeyOverridesTest.cs` — `Conflicting_overrides_on_a_deduplicated_constraint_are_rejected` (+ `SharedCustomer`/`SharedOrder`/`SharedOrderDetails`), `Rewriting_constraint_names_per_store_object_produces_no_collisions` (+ `RewriteConstraintNamesPerStoreObjectConvention`).

Commit: `e0377c3846` "Reject conflicting overrides on deduplicated constraints; prove the naming case".

## Self-review

- **Completeness:** all three pieces present — differ regression guard, conflict validation with
  its string added by hand to both `.resx` and `.Designer.cs`, and the end-to-end proof.
- **Quality:** `ConstraintStructure` compares column-name sequences directly (no delimiter-joined
  strings) and hashes counts alongside elements, matching the brief's injectivity requirement. The
  end-to-end test asserts `constraintNames.Count == constraintNames.Distinct().Count()` — genuinely
  "no duplicates," not merely "some name got set" — plus positive checks that the two specific
  rewritten names (`pk_user_lockout`, `fk_user_profile_users_id`) are present.
- **Discipline:** only the Produces list's two items were added (one string, one validator method).
  `EFCore.Relational.baseline.json` untouched. Nothing staged under `.superpowers/`; commit used an
  explicit file list, never `git add -A`/`.`.
- **Testing:** every new test observed executing by name via `--report-xunit-trx` / filtered runs,
  with expected-vs-actual counts stated for each filtered run above, plus one full foreground
  `build.sh --test` with its raw `<Counters>` line pasted.

## Concerns / judgment calls for review

1. **The `AreCompatible` gate is a deviation from the brief's literal code**, discovered only by
   running the full suite (not just the two new/targeted tests) — this is exactly the kind of gap
   isolated `--filter-method` runs would have hidden. I'm confident in the fix (it reuses the same
   helper the sibling `ValidateCompatible` and `SharedTableConvention` already trust for this exact
   distinction), but flagging it since it changes the throw condition from "structurally identical"
   to "structurally identical and behaviorally compatible."
2. **The shared-constraint conflict test requires explicit `HasColumnName` calls** that the brief's
   snippet didn't include, to make the two FKs genuinely collapse into one physical constraint
   before the override conflict is meaningful. Without it, `SharedTableConvention` silently
   disambiguates the columns and the test would pass for the wrong reason (as the brief itself
   warned about for a different pitfall — the `IncompatibleTableNoRelationship` one). Flagging in
   case this reveals something worth documenting elsewhere about table-splitting's default
   behavior.
3. Did not attempt to also run `EFCore.Design.Tests` through `build.sh` (only direct-dll), since the
   direct-dll run plus the stash-based pre-existing-failure check already gave clear, reproducible
   evidence that nothing in this diff affects that project; `build.sh`'s own `--nodeReuse false`
   cycle is ~3.5 minutes and the brief's evidence-rules section flagged it as corroboration only,
   not primary evidence.
