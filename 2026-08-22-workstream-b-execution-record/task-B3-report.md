# Task B3 Report: FK override metadata family with composite identity

## Status: DONE

## What I implemented

Followed the brief's steps in order (TDD). Produced the composite-identity
FK override family, mirroring Task B1's key-override family:

- `src/EFCore.Relational/Metadata/StoreObjectPair.cs` — `readonly record struct
  StoreObjectPair(StoreObjectIdentifier DependentStoreObject, StoreObjectIdentifier
  PrincipalStoreObject)` with a `ToString()` override. Record-struct equality/hashing
  is order-sensitive by construction (property order matters to the generated
  `Equals`/`GetHashCode`), verified by a dedicated test.
- `src/EFCore.Relational/Metadata/IReadOnlyStoreObjectPairDictionary.cs` and
  `StoreObjectPairDictionary.cs` — `StoreObjectIdentifier` → `StoreObjectPair` applied
  to `StoreObjectDictionary`/`IReadOnlyStoreObjectDictionary`, with `GetValues()`
  ordered by `DependentStoreObject.Name` then `PrincipalStoreObject.Name` (both
  `StringComparer.Ordinal`) for deterministic snapshot generation.
- `RelationalAnnotationNames.ForeignKeyOverrides = "Relational:ForeignKeyOverrides"`,
  added next to `KeyOverrides` and to the `AllNames` array.
- The FK override quartet + builder pair, applying the brief's substitution table to
  the key family:
  - `IReadOnlyRelationalForeignKeyOverrides` / `IMutableRelationalForeignKeyOverrides` /
    `IConventionRelationalForeignKeyOverrides` / `IRelationalForeignKeyOverrides`
  - `Builders/IConventionRelationalForeignKeyOverridesBuilder`
  - `Internal/InternalRelationalForeignKeyOverridesBuilder` (same `HasName`/`CanSetName`
    split as B1 — precedence enforced on the builder via `CanSetName`, not on the
    metadata `SetName`, which overwrites unconditionally and takes
    `ConfigurationSource.Max` of the source)
  - `Internal/RelationalForeignKeyOverrides` with `Find`, `Get`, `GetOrCreate`, `Remove`,
    `Attach`, `MergeInto`, `Name`/`SetName`/`IsNameOverridden`/`GetNameConfigurationSource`,
    `DebugView`, `ToString`, keyed by `StoreObjectPairDictionary<RelationalForeignKeyOverrides>`
    stored under `RelationalAnnotationNames.ForeignKeyOverrides`.

All XML docs were rewritten for foreign keys (not copy-pasted "key"/"column" wording);
verified with a grep for leftover "key constraint"/"column name" phrasing — every hit
is legitimately about the *foreign key* constraint, not a leftover from the key family.

## API consistency fixture (controller item 1)

In `test/EFCore.Relational.Tests/RelationalApiConsistencyTest.cs`:
- Added `IReadOnlyRelationalForeignKeyOverrides` entry to `_metadataTypes`, following
  the `IReadOnlyRelationalKeyOverrides` entry, with `null!` in the ConventionBuilder slot
  (matches B1's key/property-overrides precedent, since these overrides don't have a
  `ConventionBuilderExtensions` type).
- Added `typeof(IRelationalForeignKeyOverrides)` to `RelationalMetadataTypes`.
- Did **not** register `StoreObjectPair`/`StoreObjectPairDictionary`/
  `IReadOnlyStoreObjectPairDictionary` anywhere in the fixture: I checked and the
  precedent type `StoreObjectDictionary`/`IReadOnlyStoreObjectDictionary` (used by the
  key and property override families) isn't registered in this fixture either (only one
  hit for `StoreObjectIdentifier` itself, unrelated). So the pair types need no
  registration, by the same logic.

## Extension methods / baseline (controller items 2–3)

No `GetOverrides`/`RemoveOverrides` extension methods added anywhere — confirmed by
grep, zero hits. `src/EFCore.Relational/EFCore.Relational.baseline.json` untouched —
confirmed via `git diff --stat` on that path (empty).

## Deviations from the brief's test snippet (beyond the two dispatch-authorized corrections)

The dispatch pre-authorized two corrections (`RelationalTestHelpers.Instance` →
`FakeRelationalTestHelpers.Instance`, `[ConditionalFact]` → `[Fact]`), both applied.
I found **two more problems** while running the tests, both necessary to make the
brief's actual intent work under EF Core's real semantics — documented here in full
since they go beyond what the dispatch flagged:

**1. The known "unmapped main fragment" trap.** As warned, `User` with only
`PasswordHash`/`DisplayName` as non-key properties, both split off, fails
`FinalizeModel()` validation (`EntitySplittingUnmappedMainFragment`). Added an `Email`
property that stays on the main `Users` table (mirrors B1's `Customer.Address` fix in
`RelationalKeyOverridesTest`).

**2. A second, deeper problem the dispatch's known trap didn't cover, discovered via
TDD's RED step.** `Foreign_key_overrides_are_keyed_by_the_dependent_principal_pair`, as
literally written in the brief, never calls `FinalizeModel()` — but the self-referential
linking FK that `entityType.GetForeignKeys()` is supposed to find is only created by
`EntitySplittingConvention.ProcessModelFinalizing`, i.e. it does not exist until the
model is finalized. I traced this by reading
`src/EFCore.Relational/Metadata/Conventions/EntitySplittingConvention.cs`. Worse: I
verified (`src/EFCore/Metadata/Internal/Model.cs` line 1132, `src/EFCore/Infrastructure/
ModelRuntimeInitializer.cs` line 56) that finalizing conventions run and the model
becomes irreversibly read-only (`_conventionDispatcher = null`) in the *same*
`Model.FinalizeModel()` call — there is no public path to observe the post-finalizing-
convention FK while it is still mutable. `RelationalForeignKeyOverrides.GetOrCreate`
must write an annotation onto the FK itself (`foreignKey[...] = ...`), which throws
once the FK's `IsReadOnly` (delegated to `DeclaringEntityType.Model.IsReadOnly`) is
true — confirmed empirically first (RED run below) and then by code trace.

  Fix: in that one test, I construct the identical self-referential linking
  relationship the convention itself builds — `entityType.Builder.HasRelationship(
  entityType, pk.Properties, pk)!.IsUnique(true)!.Metadata` — literally the same call
  as `EntitySplittingConvention.ProcessModelFinalizing` (line 99-101 of that file), but
  invoked directly, before finalization, so the resulting FK is still mutable. This
  preserves the test's intent (one shared FK, keyed by two different
  `(dependent, principal)` pairs) while working within EF Core's actual mutability
  rules. The first test (`Two_fragments_of_one_entity_share_a_single_model_foreign_key`)
  still demonstrates the real, convention-driven spike finding via `FinalizeModel()` +
  read-only inspection, since it never needs to mutate the FK.

I did not escalate this because it's a test-authoring problem with a well-understood,
narrowly-scoped fix informed directly by reading the exact convention code that creates
the FK — not an ambiguous design choice about the production code, which followed the
brief unchanged.

## TDD evidence

**RED** — command:
```
./build.sh --test --projects /home/buvy/code/buvinghausen/efcore/.claude/worktrees/feature+per-store-object-constraint-names/test/EFCore.Relational.Tests/EFCore.Relational.Tests.csproj
```
Relevant failing output (compile errors, before any production code existed):
```
.../RelationalForeignKeyOverridesTest.cs(46,31): error CS0246: The type or namespace name 'StoreObjectPair' could not be found (are you missing a using directive or an assembly reference?)
.../RelationalForeignKeyOverridesTest.cs(49,9): error CS0103: The name 'RelationalForeignKeyOverrides' does not exist in the current context
Build FAILED.
    0 Warning(s)
    12 Error(s)
```
This is exactly what the brief predicted: `StoreObjectPair` and
`RelationalForeignKeyOverrides` did not exist yet.

A second RED iteration (after adding a `using Microsoft.EntityFrameworkCore.Metadata.
Internal;` fix and before fixing the two test-fixture issues above) compiled but failed
at runtime:
```
Microsoft.EntityFrameworkCore.Metadata.RelationalForeignKeyOverridesTest.Two_fragments_of_one_entity_share_a_single_model_foreign_key: FAILED
  System.InvalidOperationException : Entity type 'User' has a split mapping, but it
  doesn't map any non-primary key property to the main store object. Keep at least one
  non-primary key property mapped to a column on 'Users'.
Microsoft.EntityFrameworkCore.Metadata.RelationalForeignKeyOverridesTest.Foreign_key_overrides_are_keyed_by_the_dependent_principal_pair: FAILED
  System.InvalidOperationException : Sequence contains no matching element
Counters total="1491" executed="1490" passed="1488" failed="2" error="0" ... notExecuted="1"
```
This confirmed both problems described above (the known unmapped-main-fragment trap,
and the pre-finalization non-existence of the linking FK) before I wrote any fix.

**GREEN** — command:
```
./build.sh --test --projects /home/buvy/code/buvinghausen/efcore/.claude/worktrees/feature+per-store-object-constraint-names/test/EFCore.Relational.Tests/EFCore.Relational.Tests.csproj
```
Raw summary line (verbatim from the console output):
```
Build succeeded.
    0 Warning(s)
    0 Error(s)

Time Elapsed 00:00:51.43
```
Raw `.trx` counters (verbatim):
```
Counters total="1491" executed="1490" passed="1490" failed="0" error="0" timeout="0" aborted="0" inconclusive="0" passedButRunAborted="0" notRunnable="0" notExecuted="1" disconnected="0" warning="0" completed="0" inProgress="0" pending="0"
```
1491 = baseline 1488 + 3 new tests. 1490 passed = baseline 1487 + 3 new tests. 1
`notExecuted` matches the pre-existing skip from the baseline. 0 failed, 0 warnings.

## Files changed

- `src/EFCore.Relational/Metadata/StoreObjectPair.cs` (new)
- `src/EFCore.Relational/Metadata/IReadOnlyStoreObjectPairDictionary.cs` (new)
- `src/EFCore.Relational/Metadata/StoreObjectPairDictionary.cs` (new)
- `src/EFCore.Relational/Metadata/IReadOnlyRelationalForeignKeyOverrides.cs` (new)
- `src/EFCore.Relational/Metadata/IMutableRelationalForeignKeyOverrides.cs` (new)
- `src/EFCore.Relational/Metadata/IConventionRelationalForeignKeyOverrides.cs` (new)
- `src/EFCore.Relational/Metadata/IRelationalForeignKeyOverrides.cs` (new)
- `src/EFCore.Relational/Metadata/Builders/IConventionRelationalForeignKeyOverridesBuilder.cs` (new)
- `src/EFCore.Relational/Metadata/Internal/InternalRelationalForeignKeyOverridesBuilder.cs` (new)
- `src/EFCore.Relational/Metadata/Internal/RelationalForeignKeyOverrides.cs` (new)
- `src/EFCore.Relational/Metadata/RelationalAnnotationNames.cs` (modified: added
  `ForeignKeyOverrides` constant + `AllNames` entry)
- `test/EFCore.Relational.Tests/Metadata/RelationalForeignKeyOverridesTest.cs` (new)
- `test/EFCore.Relational.Tests/RelationalApiConsistencyTest.cs` (modified: registered
  the new FK quartet)

## Self-review findings

- **Completeness:** every member in the brief's Produces list is present with the
  brief's exact signatures (`StoreObjectPair`, `StoreObjectPairDictionary<T>` with
  `Find`/`GetValues`/`Add`/`Remove`, `RelationalAnnotationNames.ForeignKeyOverrides`,
  `RelationalForeignKeyOverrides.Find`/`GetOrCreate`, plus `Get`/`Remove` and the
  `Name`/`SetName`/`IsNameOverridden`/`GetNameConfigurationSource` instance members).
  All brief-listed files touched.
- **Quality:** XML docs rewritten for foreign keys throughout (verified by grep — no
  stray "column name" wording; "key constraint" hits are all legitimately about the
  *foreign* key constraint). `StoreObjectPair`'s equality/hashing is the compiler-
  generated `record struct` behavior, order-sensitive as required — verified by a
  dedicated test with an explicit code comment about the risk of hash-collision
  flakiness (kept, per the brief's own allowance, since it's currently passing).
- **Discipline:** grepped for `GetOverrides`/`RemoveOverrides` — zero hits; no
  extension methods added. `EFCore.Relational.baseline.json` untouched (`git diff
  --stat` empty for that path).
- **Testing:** `Foreign_key_overrides_are_keyed_by_the_dependent_principal_pair`
  verifies that two different `StoreObjectPair`s sharing the same dependent
  (`UserLockout`/`Users` vs. `UserProfile`/`Users`) resolve independently on the same
  FK object — this is the core composite-identity behavior under test. Output is
  pristine: 0 Warning(s), 0 Error(s) in the final GREEN run.

## Concerns

None outstanding. The one significant judgment call — reconstructing the linking FK
directly via `HasRelationship` instead of relying on `FinalizeModel()` in the second
test — is documented above with the full reasoning and the exact convention code it
mirrors, so a reviewer can verify it independently.
