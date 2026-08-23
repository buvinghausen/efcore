# Final whole-branch review fix report

Branch: `worktree-feature+per-store-object-constraint-names`, starting point `d741ad1346` (14 commits).

## Fix 1 (Important) — per-store-object FK names dropped from snapshot for ownership FKs

**Root cause confirmed**: `CSharpSnapshotGenerator.GenerateForeignKey` called `GenerateForeignKeyOverrides(foreignKey, stringBuilder)` only inside `if (!foreignKey.IsOwnership)`. The raw `RelationalAnnotationNames.ForeignKeyOverrides` annotation is also filtered out of `GenerateForeignKeyAnnotations` via `AnnotationCodeGenerator.IgnoredRelationalAnnotations`, so there was no path at all for an ownership FK's per-store-object name to reach the snapshot.

**Changes**:
1. `src/EFCore.Design/Migrations/Design/CSharpSnapshotGenerator.cs` — moved the `GenerateForeignKeyOverrides(foreignKey, stringBuilder)` call out of the `if (!foreignKey.IsOwnership)` block so it runs unconditionally, right after that block and before `GenerateForeignKeyAnnotations`.
2. `src/EFCore.Relational/Extensions/RelationalForeignKeyBuilderExtensions.cs` — added two new store-object `HasConstraintName` overloads:
   - `OwnershipBuilder HasConstraintName(this OwnershipBuilder, string? name, in StoreObjectIdentifier storeObject, in StoreObjectIdentifier principalStoreObject)`
   - `OwnershipBuilder<TEntity, TDependentEntity> HasConstraintName<TEntity, TDependentEntity>(this OwnershipBuilder<TEntity, TDependentEntity>, string? name, in StoreObjectIdentifier storeObject, in StoreObjectIdentifier principalStoreObject)`

   Both mirror the existing global `OwnershipBuilder` overloads (lines 209/231) and the store-object overloads already present for `ReferenceReferenceBuilder`/`ReferenceCollectionBuilder`, full XML docs included, and delegate to `IMutableForeignKey.SetConstraintName(name, storeObject, principalStoreObject)`.
3. `src/EFCore.Relational/EFCore.Relational.baseline.json` — regenerated via `EFCore.ApiBaseline.Tests` (see below). Diff shows **exactly** the two new members added, nothing else changed:
   ```
   + "static Microsoft.EntityFrameworkCore.Metadata.Builders.OwnershipBuilder HasConstraintName(this Microsoft.EntityFrameworkCore.Metadata.Builders.OwnershipBuilder ownershipBuilder, string? name, in Microsoft.EntityFrameworkCore.Metadata.StoreObjectIdentifier storeObject, in Microsoft.EntityFrameworkCore.Metadata.StoreObjectIdentifier principalStoreObject);"
   + "static Microsoft.EntityFrameworkCore.Metadata.Builders.OwnershipBuilder<TEntity, TDependentEntity> HasConstraintName<TEntity, TDependentEntity>(this Microsoft.EntityFrameworkCore.Metadata.Builders.OwnershipBuilder<TEntity, TDependentEntity> ownershipBuilder, string? name, in Microsoft.EntityFrameworkCore.Metadata.StoreObjectIdentifier storeObject, in Microsoft.EntityFrameworkCore.Metadata.StoreObjectIdentifier principalStoreObject);"
   ```

**Covering test**: `Snapshot_round_trips_per_store_object_foreign_key_constraint_name_for_owned_type_in_own_table` added to `test/EFCore.Design.Tests/Migrations/Design/CSharpMigrationsGeneratorTest.ModelSnapshot.cs`. New private types `OverridesCustomerWithAddress` / `OverridesAddress` (self-contained, not shared with other tests to avoid perturbing unrelated fixtures). Uses `OwnsOne(..., a => { a.ToTable("CustomerAddress"); a.WithOwner().HasConstraintName("fk_customeraddress_customers", storeObject, principalStoreObject); })`, then asserts the generated snapshot contains the `.HasConstraintName(...)` call, and — via the shared `Test(...)` harness — that the round-tripped model compiles and produces an empty model diff against the original (the harness's compile-and-empty-diff assertion, which is what proves the emission is real, not just that a substring matched).

**Proof the bug was real** (reverted only `CSharpSnapshotGenerator.cs`, kept everything else, ran the new test):
```
failed ...Snapshot_round_trips_per_store_object_foreign_key_constraint_name_for_owned_type_in_own_table (2s 033ms)
  Assert.Equal() Failure: Strings differ
             ↓ (pos 0)
  Expected: "fk_customeraddress_customers"
  Actual:   "FK_CustomerAddress_Customers_OverridesCustomerWith"···
```
This is exactly the silent-drop-to-default-name symptom described in the review. After re-applying the fix, the same test passes.

## Fix 2 (Important) — shared-constraint validation fired on models with no overrides

**Root cause confirmed**: `RelationalModelValidator.ValidateSharedForeignKeyNameOverrides` threw whenever two structurally identical, `AreCompatible` FKs on one shared table resolved to different names — regardless of whether either name came from a per-store-object override. Two entity types sharing a table with differing *global* `HasConstraintName` (no per-store-object override anywhere) now threw, which is a behavior change unrelated to the new feature's stated scope.

**Change**: `src/EFCore.Relational/Infrastructure/RelationalModelValidator.cs` — before throwing, gate on:
```csharp
var storeObjectPair = new StoreObjectPair(dependentTable, principalTableValue);
if (RelationalForeignKeyOverrides.Find(foreignKey, storeObjectPair) is { IsNameOverridden: true }
    || RelationalForeignKeyOverrides.Find(existing.ForeignKey, storeObjectPair) is { IsNameOverridden: true })
{
    throw new InvalidOperationException(...);
}
```
Only throws when at least one of the two conflicting names actually originated from a per-store-object override.

**Covering tests** in `test/EFCore.Relational.Tests/Metadata/RelationalForeignKeyOverridesTest.cs`:
- New: `Differing_global_constraint_names_on_a_deduplicated_constraint_are_not_rejected` — two entity types sharing a table (`SharedOrder`/`SharedOrderDetails` → `SharedCustomer`), each configured with its own *global* `HasConstraintName("fk_a")` / `HasConstraintName("fk_b")`, no per-store-object overrides. Asserts `FinalizeModel()` succeeds and both names resolve as configured.
- Existing (unchanged): `Conflicting_overrides_on_a_deduplicated_constraint_are_rejected` — same shared-table shape but using `SetConstraintName(name, storeObject, principalStoreObject)` (a per-store-object override) on both FKs with different names — still throws.

**Proof the bug was real** (reverted only `RelationalModelValidator.cs`, ran the new test):
```
failed ...Differing_global_constraint_names_on_a_deduplicated_constraint_are_not_rejected (505ms)
  Xunit.MicrosoftTestingPlatform.XunitException: System.InvalidOperationException : Entity types sharing
  table 'Orders' configure conflicting constraint names 'fk_a' and 'fk_b' for the same database constraint.
```
After re-applying the fix, both the new test and the pre-existing conflict test pass — proving the narrowing didn't just disable the check.

## Fix 3 (Minor) — non-deterministic ordering in generated output

`src/EFCore.Relational/Metadata/StoreObjectPairDictionary.cs`: `GetValues()` now orders by the `StoreObjectIdentifier` values themselves (which implement `IComparable<StoreObjectIdentifier>`, comparing `StoreObjectType` then `Name` then `Schema`) instead of `.Name` strings alone:
```csharp
=> _dictionary
    .OrderBy(pair => pair.Key.DependentStoreObject)
    .ThenBy(pair => pair.Key.PrincipalStoreObject)
    .Select(pair => pair.Value);
```
This removes the schema/store-object-type blind spot that previously fell back to undocumented `Dictionary` enumeration order.

## Fix 4 (Minor) — FK explicit-null not covered through snapshot round trip

Added `Snapshot_round_trips_explicit_null_foreign_key_constraint_name_override` to `CSharpMigrationsGeneratorTest.ModelSnapshot.cs`, symmetric to the existing key-side `Snapshot_round_trips_explicit_null_key_constraint_name_override`. Configures a global `HasConstraintName("fk_global")` then an explicit-null per-store-object override, and asserts the resolved name after round trip equals `GetDefaultName(...)`, not the global name — proving the null override (not "no override") survived serialization.

## Fix 5 (Minor) — justify null-forgiving operator

Added an identical explanatory comment above the `.HasName(...)!.Metadata` call in both:
- `src/EFCore.Relational/Metadata/Internal/RelationalKeyOverrides.cs`
- `src/EFCore.Relational/Metadata/Internal/RelationalForeignKeyOverrides.cs`

> The null-forgiving operator is safe here: MergeInto is only ever reached through Attach, which calls HasName on the override it just created via GetOrCreate, so CanSetName has nothing to refuse and HasName cannot return null.

## Test commands and raw results

### EFCore.Design.Tests — targeted (new tests only)
```
.dotnet/dotnet build test/EFCore.Design.Tests/EFCore.Design.Tests.csproj -v q --nologo
.dotnet/dotnet exec artifacts/bin/EFCore.Design.Tests/Debug/net11.0/Microsoft.EntityFrameworkCore.Design.Tests.dll \
  --filter-class '*CSharpMigrationsGeneratorTest' \
  --filter-method '*Snapshot_round_trips_per_store_object_foreign_key_constraint_name_for_owned_type_in_own_table*'
# total: 1, failed: 0, succeeded: 1

.dotnet/dotnet exec artifacts/bin/EFCore.Design.Tests/Debug/net11.0/Microsoft.EntityFrameworkCore.Design.Tests.dll \
  --filter-class '*CSharpMigrationsGeneratorTest' \
  --filter-method '*Snapshot_round_trips_explicit_null_foreign_key_constraint_name_override*'
# total: 1, failed: 0, succeeded: 1
```

### EFCore.Design.Tests — class filter
```
.dotnet/dotnet exec artifacts/bin/EFCore.Design.Tests/Debug/net11.0/Microsoft.EntityFrameworkCore.Design.Tests.dll --filter-class '*CSharpMigrationsGeneratorTest*'
# total: 173, failed: 0, succeeded: 173, skipped: 0
```
(Class total moved from an implicit 171 to 173 — the +2 new tests are present and passing; not a stale-count situation.)

### EFCore.Design.Tests — full assembly
```
.dotnet/dotnet exec artifacts/bin/EFCore.Design.Tests/Debug/net11.0/Microsoft.EntityFrameworkCore.Design.Tests.dll \
  --report-xunit-trx --report-xunit-trx-filename design-final.trx
# total: 1277, failed: 5, succeeded: 1271, skipped: 1
```
Baseline was 1275 total with 5 pre-existing failures. New total 1277 = 1275 + 2 (our new tests), failures unchanged at 5 (all in `OperationExecutorTest`, unrelated Windows-path assertions on Linux — file/method names match the known pre-existing list: `AddMigration_errors_for_bad_names` ×2, `AddAndApplyMigration_errors_for_bad_names` ×2, `AddMigration_errors_for_bad_output_dirs` ×1). Failure count did not grow.

### EFCore.Relational.Tests — targeted (RelationalForeignKeyOverridesTest)
```
.dotnet/dotnet build test/EFCore.Relational.Tests/EFCore.Relational.Tests.csproj -v q --nologo
.dotnet/dotnet exec artifacts/bin/EFCore.Relational.Tests/Debug/net11.0/Microsoft.EntityFrameworkCore.Relational.Tests.dll \
  --filter-class '*RelationalForeignKeyOverridesTest'
# total: 11, failed: 0, succeeded: 11, skipped: 0
```
(Was 10 [Fact] methods before this pass; +1 new test = 11, all passing including the pre-existing `Conflicting_overrides_on_a_deduplicated_constraint_are_rejected`.)

### EFCore.Relational.Tests — full assembly
```
.dotnet/dotnet exec artifacts/bin/EFCore.Relational.Tests/Debug/net11.0/Microsoft.EntityFrameworkCore.Relational.Tests.dll \
  --report-xunit-trx --report-xunit-trx-filename relational-final.trx
# total: 1502, failed: 0, succeeded: 1501, skipped: 1
```
Baseline was total=1501 executed=1500 passed=1500 failed=0 notExecuted=1. New total 1502 = 1501 + 1 (our new Fix-2 test), succeeded 1501 = 1500 + 1. The one skip (`Can_use_relational_model_with_sprocs_and_views`, `#28703`) is the same pre-existing skip.

### EFCore.ApiBaseline.Tests — full assembly (regenerated baseline)
```
.dotnet/dotnet build test/EFCore.ApiBaseline.Tests/EFCore.ApiBaseline.Tests.csproj -v q --nologo
.dotnet/dotnet exec artifacts/bin/EFCore.ApiBaseline.Tests/Debug/net11.0/Microsoft.EntityFrameworkCore.ApiBaseline.Tests.dll
# total: 14, failed: 0, succeeded: 14, skipped: 0
```
Matches the expected 14/14. `git diff --stat` after this run touched only `src/EFCore.Relational/EFCore.Relational.baseline.json` (+6 lines: 2 new member entries plus their JSON braces) — no other baseline file changed, confirming no other public API surface moved.

### Sqlite / SQL Server
Not run, per instructions — Sqlite is ~38k tests with known environmental `mod_spatialite` failures on this arm64 sandbox, and SQL Server cannot run here at all.

## Bug reproduction summary

| Fix | Reproduced before fix? | Evidence |
|---|---|---|
| Fix 1 (ownership FK snapshot drop) | **Yes** | New test failed with wrong (default) constraint name when only `CSharpSnapshotGenerator.cs` was reverted; passed after reapplying. |
| Fix 2 (validator over-firing) | **Yes** | New test threw `InvalidOperationException` (`'fk_a'`/`'fk_b'` conflict) when only `RelationalModelValidator.cs` was reverted; passed after reapplying, and the pre-existing conflict test still throws. |

## Files touched

- `src/EFCore.Design/Migrations/Design/CSharpSnapshotGenerator.cs`
- `src/EFCore.Relational/Extensions/RelationalForeignKeyBuilderExtensions.cs`
- `src/EFCore.Relational/Infrastructure/RelationalModelValidator.cs`
- `src/EFCore.Relational/Metadata/Internal/RelationalForeignKeyOverrides.cs`
- `src/EFCore.Relational/Metadata/Internal/RelationalKeyOverrides.cs`
- `src/EFCore.Relational/Metadata/StoreObjectPairDictionary.cs`
- `src/EFCore.Relational/EFCore.Relational.baseline.json` (regenerated, not hand-edited)
- `test/EFCore.Design.Tests/Migrations/Design/CSharpMigrationsGeneratorTest.ModelSnapshot.cs`
- `test/EFCore.Relational.Tests/Metadata/RelationalForeignKeyOverridesTest.cs`
