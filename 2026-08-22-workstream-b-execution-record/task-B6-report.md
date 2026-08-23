# Task B6 Report: Public and convention configuration API

## What was implemented

Public fluent API and Core-facing convention seam for per-store-object key/FK constraint names, plus the metadata extension methods they route through.

### Key side

`src/EFCore.Relational/Extensions/RelationalKeyExtensions.cs`:
- `void SetName(this IMutableKey, string?, in StoreObjectIdentifier)`
- `string? SetName(this IConventionKey, string?, in StoreObjectIdentifier, bool fromDataAnnotation = false)`
- `ConfigurationSource? GetNameConfigurationSource(this IConventionKey, in StoreObjectIdentifier)`
- `IEnumerable<IReadOnlyRelationalKeyOverrides> GetOverrides(this IReadOnlyKey)` — plus `IMutableKey`, `IConventionKey`, and `IKey` mirrors (added beyond the brief's literal list; required by `RelationalApiConsistencyTest`, see below)
- `IMutableRelationalKeyOverrides? RemoveOverrides(this IMutableKey, in StoreObjectIdentifier)` — plus an `IConventionKey` mirror (same reason)

`src/EFCore.Relational/Extensions/RelationalKeyBuilderExtensions.cs`:
- `KeyBuilder HasName(this KeyBuilder, string?, in StoreObjectIdentifier)` and the generic `KeyBuilder<TEntity>` overload
- `IConventionKeyBuilder? HasName(this IConventionKeyBuilder, string?, in StoreObjectIdentifier, bool fromDataAnnotation = false)` and `bool CanSetName(this IConventionKeyBuilder, string?, in StoreObjectIdentifier, bool fromDataAnnotation = false)` — placed directly below the existing global `HasName`/`CanSetName` pair, mirroring their shape exactly. `CanSetName` compares against `GetNameConfigurationSource(storeObject)` (cannot delegate to `CanSetAnnotation` — the value lives inside a `StoreObjectDictionary`, not a single annotation).

### FK side (mirrors the key side, with a pair of store objects)

`src/EFCore.Relational/Extensions/RelationalForeignKeyExtensions.cs`:
- `void SetConstraintName(this IMutableForeignKey, string?, in StoreObjectIdentifier storeObject, in StoreObjectIdentifier principalStoreObject)`
- `string? SetConstraintName(this IConventionForeignKey, string?, in StoreObjectIdentifier, in StoreObjectIdentifier, bool fromDataAnnotation = false)`
- `ConfigurationSource? GetConstraintNameConfigurationSource(this IConventionForeignKey, in StoreObjectIdentifier, in StoreObjectIdentifier)`
- `IEnumerable<IReadOnlyRelationalForeignKeyOverrides> GetOverrides(this IReadOnlyForeignKey)` — plus `IMutableForeignKey`, `IConventionForeignKey`, `IForeignKey` mirrors
- `IMutableRelationalForeignKeyOverrides? RemoveOverrides(this IMutableForeignKey, in StoreObjectIdentifier, in StoreObjectIdentifier)` — plus an `IConventionForeignKey` mirror

Each of these constructs `new StoreObjectPair(storeObject, principalStoreObject)` internally, so callers never touch the pair type.

`src/EFCore.Relational/Extensions/RelationalForeignKeyBuilderExtensions.cs`:
- `ReferenceCollectionBuilder HasConstraintName(this ReferenceCollectionBuilder, string?, in StoreObjectIdentifier, in StoreObjectIdentifier)` + generic `ReferenceCollectionBuilder<TEntity, TRelatedEntity>` overload
- `ReferenceReferenceBuilder HasConstraintName(this ReferenceReferenceBuilder, string?, in StoreObjectIdentifier, in StoreObjectIdentifier)` + generic `ReferenceReferenceBuilder<TEntity, TRelatedEntity>` overload — per the brief, `OwnershipBuilder` was **not** given a store-object overload (the brief names only the `ReferenceCollectionBuilder`/`ReferenceReferenceBuilder` pairs as the ones to mirror).
- `IConventionForeignKeyBuilder? HasConstraintName(this IConventionForeignKeyBuilder, string?, in StoreObjectIdentifier, in StoreObjectIdentifier, bool fromDataAnnotation = false)` and `bool CanSetConstraintName(this IConventionForeignKeyBuilder, string?, in StoreObjectIdentifier, in StoreObjectIdentifier, bool fromDataAnnotation = false)`

### Assembly boundary

All convention-seam methods (`HasName`/`CanSetName` for keys, `HasConstraintName`/`CanSetConstraintName` for FKs) are extension methods over `IConventionKeyBuilder`/`IConventionForeignKeyBuilder` in `src/EFCore.Relational/Extensions/`. `src/EFCore/Metadata/Builders/IConventionKeyBuilder.cs` and `IConventionForeignKeyBuilder.cs` were **not touched**.

### Deviation from the brief's literal Produces list: extra Mutable/Convention `GetOverrides`/`RemoveOverrides` overloads

The brief's Produces list only asked for `GetOverrides(IReadOnlyKey)` / `RemoveOverrides(IMutableKey, storeObject)` (and the FK equivalents). Implementing exactly that made `RelationalApiConsistencyTest.Mutable_metadata_types_have_matching_methods` and `Convention_metadata_types_have_matching_methods` fail:

```
No IMutable equivalent of RelationalKeyExtensions.GetOverrides(IReadOnlyKey)
No IMutable equivalent of RelationalForeignKeyExtensions.GetOverrides(IReadOnlyForeignKey)
No IConvention equivalent of RelationalKeyExtensions.RemoveOverrides(IMutableKey, StoreObjectIdentifier&)
No IConvention equivalent of RelationalForeignKeyExtensions.RemoveOverrides(IMutableForeignKey, StoreObjectIdentifier&, StoreObjectIdentifier&)
```

This mirrors the existing, already-passing pattern in `RelationalPropertyExtensions.cs` (`GetOverrides`/`FindOverrides`/`RemoveOverrides` each have `IReadOnlyProperty`/`IMutableProperty`/`IConventionProperty`/`IProperty` variants). I added the missing `IMutableKey`/`IConventionKey`/`IKey` `GetOverrides` overloads and the `IConventionKey` `RemoveOverrides` overload (and the FK equivalents) to satisfy consistency — this is additive surface, not a change to any signature named in the brief.

### B5 convention rewrite (as instructed)

`KeyOverridesConvention.cs` and `ForeignKeyOverridesConvention.cs` now call `key.GetOverrides()` / `((IMutableKey)key).RemoveOverrides(...)` (and FK equivalents) instead of the internal `RelationalKeyOverrides.Get`/`.Remove` statics directly. Diff:

```diff
-        foreach (var overrides in RelationalKeyOverrides.Get(key) ?? [])
+        foreach (var overrides in key.GetOverrides())
...
-            var removedOverrides = RelationalKeyOverrides.Remove((IMutableKey)key, overrides.StoreObject);
+            var removedOverrides = ((IMutableKey)key).RemoveOverrides(overrides.StoreObject);
             if (removedOverrides != null)
             {
-                RelationalKeyOverrides.Attach(key, removedOverrides);
+                RelationalKeyOverrides.Attach(key, (IConventionRelationalKeyOverrides)removedOverrides);
             }
```
(FK convention: identical shape, using `overrides.StoreObjects.DependentStoreObject`/`PrincipalStoreObject` to call the two-store-object `RemoveOverrides` extension since `RelationalForeignKeyOverrides.Remove` takes a `StoreObjectPair` but the public extension takes the two identifiers separately.)

### Test infrastructure change (required, not in the brief's file list)

`HasKey(...)` in the shared `ModelBuilderTest` harness returns the wrapper type `ModelBuilderTest.TestKeyBuilder<TEntity>`, not the real `KeyBuilder<TEntity>` — so the brief's literal test body (`b.HasKey(c => c.Id).HasName("pk_customers", StoreObjectIdentifier.Table("Customers"))`) does not compile without a matching wrapper extension. `test/EFCore.Relational.Specification.Tests/ModelBuilding/RelationalTestModelBuilderExtensions.cs` already has this exact pattern for the single-arg `HasName(string)` (switches on `IInfrastructure<KeyBuilder<TEntity>>` vs `IInfrastructure<KeyBuilder>` and delegates to the real extension). I added the store-object overload alongside it, following the identical shape.

### Test attribute change

The brief's test used `[ConditionalFact]`. That attribute's parameterless-style constructor (`ConditionalFactAttribute(params string[])`) is marked `[Obsolete]` in the `Microsoft.DotNet.XUnitV3Extensions` package pinned by this repo, and `TreatWarningsAsErrors` is `true` repo-wide, so it fails to build. Every sibling test in `RelationalModelBuilderTest.cs` uses plain `[Fact]`; I used `[Fact]` too, matching the file's own convention (this is exactly the class B5's brief flagged as the place to follow local conventions rather than the brief's literal snippet).

## TDD evidence

**RED** — before any implementation, building the Sqlite functional tests project with only the test added:

```
$ .dotnet/dotnet build test/EFCore.Sqlite.FunctionalTests/EFCore.Sqlite.FunctionalTests.csproj -v q --nologo
.../RelationalModelBuilderTest.cs(333,22): error CS1501: No overload for method 'HasName' takes 2 arguments [.../EFCore.Relational.Specification.Tests.csproj]
Build FAILED.
```

**GREEN** — after implementing the extensions, builders, and the `TestKeyBuilder<TEntity>` wrapper overload:

```
$ .dotnet/dotnet build test/EFCore.Sqlite.FunctionalTests/EFCore.Sqlite.FunctionalTests.csproj -v q --nologo
Build succeeded.
    0 Warning(s)
    0 Error(s)

$ .dotnet/dotnet artifacts/bin/EFCore.Sqlite.FunctionalTests/Debug/net11.0/Microsoft.EntityFrameworkCore.Sqlite.FunctionalTests.dll --filter-method '*Can_configure_per_table_key_and_foreign_key_constraint_names*'
Test run summary: Passed! - .../Microsoft.EntityFrameworkCore.Sqlite.FunctionalTests.dll (net11.0|arm64)
  total: 1
  failed: 0
  succeeded: 1
  skipped: 0
```

Discovery confirmed the test is not silently skipped — its full qualified name and outcome:

```
$ .dotnet/dotnet artifacts/bin/EFCore.Sqlite.FunctionalTests/Debug/net11.0/Microsoft.EntityFrameworkCore.Sqlite.FunctionalTests.dll --filter-method '*Can_configure_per_table_key_and_foreign_key_constraint_names*' --list-tests
  Microsoft.EntityFrameworkCore.ModelBuilding.SqliteModelBuilderGenericTest+SqliteGenericNonRelationship.Can_configure_per_table_key_and_foreign_key_constraint_names
Test discovery summary: found 1 test(s)
```

(Only one Sqlite subclass exists — `SqliteModelBuilderGenericTest`; there is no `SqliteModelBuilderNonGenericTest`, so 1 execution is the expected/full count for this provider.)

## Full suite verification (foreground, `build.sh --test`, run alone — no concurrent test runs)

### EFCore.Relational.Tests

```
$ ./build.sh --test --projects .../test/EFCore.Relational.Tests/EFCore.Relational.Tests.csproj
...
Tests succeeded: .../Microsoft.EntityFrameworkCore.Relational.Tests.dll [net11.0|arm64]
Build succeeded.
```

TRX `<ResultSummary>` counters:

```
outcome="Completed"
total="1496" executed="1495" passed="1495" failed="0" error="0" ... notExecuted="1"
```

Matches the stated baseline (`total=1496 executed=1495 passed=1495 failed=0 notExecuted=1`) exactly — no regressions. `RelationalApiConsistencyTest` (20 tests, includes `Fluent_api_methods_should_not_return_void` and `Generic_fluent_api_methods_should_return_generic_types`, which check `RelationalKeyBuilderExtensions`/`RelationalForeignKeyBuilderExtensions`) all pass:

```
$ .dotnet/dotnet artifacts/bin/EFCore.Relational.Tests/Debug/net11.0/Microsoft.EntityFrameworkCore.Relational.Tests.dll --filter-class '*RelationalApiConsistencyTest'
Test run summary: Passed! - .../Microsoft.EntityFrameworkCore.Relational.Tests.dll (net11.0|arm64)
  total: 20
  failed: 0
  succeeded: 20
  skipped: 0
```

### EFCore.Sqlite.FunctionalTests

```
$ ./build.sh --test --projects .../test/EFCore.Sqlite.FunctionalTests/EFCore.Sqlite.FunctionalTests.csproj
...
Tests succeeded: .../Microsoft.EntityFrameworkCore.Sqlite.FunctionalTests.dll [net11.0|arm64]
Build succeeded.
```

TRX `<ResultSummary>` counters:

```
outcome="Completed"
total="38226" executed="37940" passed="37940" failed="0" error="0" ... notExecuted="286"
```

Our test's own TRX entry: `testName="Microsoft.EntityFrameworkCore.ModelBuilding.SqliteModelBuilderGenericTest+SqliteGenericNonRelationship.Can_configure_per_table_key_and_foreign_key_constraint_names" ... outcome="Passed"`.

**Note on a transient failure during this task**: an earlier attempt to run this suite via `build.sh` collided with a leftover `build.sh --test` invocation from a prior (interrupted) turn, both writing to the same file-backed SQLite test databases concurrently. That produced 18 spurious failures, all in `MigrationsInfrastructureSqliteTest`, all `SQLite Error 10: 'disk I/O error'` — unrelated to any code in this task (no ModelBuilding, no key/FK naming). After confirming no test processes were still running, I re-ran the suite alone and it passed clean, as shown above.

## Files changed

- `src/EFCore.Relational/Extensions/RelationalKeyExtensions.cs`
- `src/EFCore.Relational/Extensions/RelationalKeyBuilderExtensions.cs`
- `src/EFCore.Relational/Extensions/RelationalForeignKeyExtensions.cs`
- `src/EFCore.Relational/Extensions/RelationalForeignKeyBuilderExtensions.cs`
- `src/EFCore.Relational/Metadata/Conventions/KeyOverridesConvention.cs`
- `src/EFCore.Relational/Metadata/Conventions/ForeignKeyOverridesConvention.cs`
- `test/EFCore.Relational.Specification.Tests/ModelBuilding/RelationalModelBuilderTest.cs`
- `test/EFCore.Relational.Specification.Tests/ModelBuilding/RelationalTestModelBuilderExtensions.cs`

## Self-review

- **Completeness**: every signature in the brief's Produces list is present, verbatim, on both the key and FK sides, including the generic `KeyBuilder<TEntity>`/`ReferenceCollectionBuilder<TEntity,TRelatedEntity>`/`ReferenceReferenceBuilder<TEntity,TRelatedEntity>` overloads. Verified by grepping every `public static` signature against the brief's list.
- **Quality**: XML docs present on every new public member (`<summary>`, `<param>` per parameter, `<returns>`, and the `<remarks>` see-also block copied from the neighbouring overloads) — StyleCop-as-errors would have failed the build otherwise, and the build is clean. The `IConventionKey`/`IConventionForeignKey`-taking `SetName`/`SetConstraintName` extensions mirror the brief's literal code (unconditional overwrite via `RelationalKeyOverrides`/`RelationalForeignKeyOverrides.SetName`, matching the existing global `SetName(this IConventionKey, ...)`'s own unconditional-overwrite-via-`SetOrRemoveAnnotation` behavior). Precedence is enforced one layer up, in the convention-builder seam: `CanSetName`/`CanSetConstraintName` compare `configurationSource.Overrides(...GetNameConfigurationSource(storeObject))`, so `HasName`/`HasConstraintName` on `IConventionKeyBuilder`/`IConventionForeignKeyBuilder` refuse to let a convention clobber an explicit value — the same split the internal `InternalRelationalKeyOverridesBuilder.CanSetName` uses.
- **Discipline**: no baseline change (`git diff --stat -- src/EFCore.Relational/EFCore.Relational.baseline.json` is empty). Nothing under `.superpowers/` staged (files added explicitly by path, no `git add -A`/`.`). B5's two convention bodies rewritten to the new extensions, confirmed by diff above — the only other change in those two files. The only additions beyond the brief's literal Produces list are the `IMutable`/`IConvention`/`I`-level `GetOverrides` and the `IConvention`-level `RemoveOverrides` mirrors, not scope creep into unrelated functionality. **Correction (see Finding 3 in the fix report below):** only `GetOverrides(IMutableKey)`, `GetOverrides(IConventionKey)`, `RemoveOverrides(IConventionKey)` (and their FK equivalents) were actually required to keep `RelationalApiConsistencyTest` green. The `IKey`/`IForeignKey`-level `GetOverrides` pair is not forced by that test (`RuntimeExtensions` is registered `null!` for both `IReadOnlyKey`/`IReadOnlyForeignKey`, so the check never scans for them) — it was added purely for symmetry with the same not-forced pair already present in `RelationalPropertyExtensions`, and is kept per the reviewer's instruction.
- **Testing**: new test confirmed to execute (not silently skipped) via `--list-tests` and via its TRX entry with `outcome="Passed"` in the full-suite run, under the one Sqlite subclass that exists (`SqliteModelBuilderGenericTest+SqliteGenericNonRelationship`). Both full suites (`EFCore.Relational.Tests`, `EFCore.Sqlite.FunctionalTests`) pass clean via `build.sh --test`, matching or exceeding the stated baseline.

## Concerns

- The brief's literal test snippet didn't account for the `TestKeyBuilder<TEntity>` wrapper indirection or the `[ConditionalFact]` obsolete-constructor build break; I diagnosed and fixed both, matching existing local conventions rather than guessing. Flagging in case a later task's brief carries similar literal-snippet assumptions.
- The additional `GetOverrides`/`RemoveOverrides` Mutable/Convention mirrors are new public surface beyond what the brief named. I judged this in-scope because most of them were required for `RelationalApiConsistencyTest` to pass, and the rest mirror an existing, already-shipped pattern (`RelationalPropertyExtensions`) — see the Finding 3 correction below for exactly which subset was forced.

## Fix report: review findings addressed (commit `20f4e42f60`)

Review came back "Needs fixes" with three Important findings. All three addressed.

### Finding 1 — drop the dead `fromDataAnnotation` parameter

`RelationalKeyExtensions.RemoveOverrides(this IConventionKey, in StoreObjectIdentifier)` and
`RelationalForeignKeyExtensions.RemoveOverrides(this IConventionForeignKey, in StoreObjectIdentifier, in StoreObjectIdentifier)`
carried a `bool fromDataAnnotation = false` parameter that was accepted, documented, and never read —
removal was unconditional. Dropped the parameter (and its `<param>` doc) from both, matching
`RelationalPropertyExtensions.RemoveOverrides(this IConventionProperty, in StoreObjectIdentifier)` exactly, which has
no such parameter. No guard was implemented in its place, per the reviewer's explicit instruction — less
surface is the better outcome here.

### Finding 2 — expand the test to cover the FK side and the refusal path

**What changed:**
- Extended `Can_configure_per_table_key_and_foreign_key_constraint_names` to also configure an ordinary
  navigated `Order → Customer` relationship (`Order.Customer`/`Customer.Orders`, `Order.CustomerId` FK — not
  the entity-splitting linking FK, which doesn't exist pre-`FinalizeModel()` and gets deleted by
  `ModelCleanupConvention.RemoveNavigationlessForeignKeys` if hand-built), call
  `.HasConstraintName("fk_orders_customers", orderTable, customersTable)` through the public
  `ReferenceCollectionBuilder`, and assert `foreignKey.GetConstraintName(orderTable, customersTable)` on the
  finalized model.
- Added `Convention_cannot_override_explicit_per_table_key_and_foreign_key_constraint_names`, covering the
  refusal path on **both** sides: after an explicit `HasName`/`HasConstraintName` call, grab the
  `IConventionKeyBuilder`/`IConventionForeignKeyBuilder` off the metadata (`((IConventionKey)key).Builder`)
  and assert `CanSetName`/`CanSetConstraintName` return `false` and `HasName`/`HasConstraintName` return
  `null` for a convention-sourced write, with the explicit value left standing.

**A real bug surfaced along the way, in test infrastructure, not in the B6 production code:**
`ModelBuilderTest.TestReferenceCollectionBuilder<TEntity, TRelatedEntity>`'s two concrete
implementations (`GenericTestReferenceCollectionBuilder` in
`test/EFCore.Specification.Tests/ModelBuilding/ModelBuilderTest.Generic.cs` and
`NonGenericTestReferenceCollectionBuilder` in `...ModelBuilderTest.NonGeneric.cs`) implement **neither**
`IInfrastructure<ReferenceCollectionBuilder<TEntity, TRelatedEntity>>` nor `IInfrastructure<ReferenceCollectionBuilder>`.
The existing single-string `HasConstraintName(string)` wrapper extension in
`RelationalTestModelBuilderExtensions.cs` (present before this task, lines ~1566-1584) switches on exactly
those two interface types and therefore matches neither case — it is unreachable dead code today. I proved
this empirically: my first attempt added a matching 3-arg wrapper overload following the same pattern, and
the resulting test failed even *before* `FinalizeModel()` — `GetConstraintName` returned the
auto-generated default name (`"FK_Order_Customers_CustomerId"`), not the one just set — while an isolated,
non-wrapper scratch test (`foreignKey.SetConstraintName(...)` / `foreignKey.GetConstraintName(...)` called
directly on real metadata, no wrapper involved) round-tripped correctly on the first try. That isolates the
break to the wrapper's `IInfrastructure` switch, not to any B6 production code —
`RelationalForeignKeyOverridesTest.Foreign_key_name_override_reaches_the_relational_model`
(pre-existing, B3/B4) already proves the underlying resolution mechanism works for an ordinary navigated FK.

Fixing that test-infrastructure gap generally (adding the missing `IInfrastructure` implementations to two
shared base classes used by every provider's model-builder tests) is out of scope for B6 and carries its own
blast-radius risk. Instead, both new/extended tests configure the FK side by bridging to the *real*
`ModelBuilder` via `testModelBuilder.GetInfrastructure()` — the exact pattern this same file's
`HasDefaultSchema`/`UseCollation` extensions already use for relational-only configuration the
`TestModelBuilder` wrapper doesn't cover (`RelationalTestModelBuilderExtensions.cs:8-14`). This calls the
actual public `ReferenceCollectionBuilder.HasConstraintName(...)` builder method directly — satisfying
"through the public builder" — without touching shared test infrastructure. I removed the 3-arg wrapper
extension I had added (it would have been additional dead code matching the existing bug) rather than leave
it in the tree unreachable. No production code or shared test-infrastructure file was touched to work around
this; the workaround is contained entirely in `RelationalModelBuilderTest.cs`.

### Finding 3 — report correction

`GetOverrides(IMutableKey)`, `GetOverrides(IConventionKey)`, `RemoveOverrides(IConventionKey)` (and the FK
equivalents) were required to keep `RelationalApiConsistencyTest` green — confirmed by the original failures
reproduced under `Mutable_metadata_types_have_matching_methods` and `Convention_metadata_types_have_matching_methods`.
The `IKey`/`IForeignKey`-level `GetOverrides` pair is **not** forced by that test (`RuntimeExtensions` is
registered as `null!` for both `IReadOnlyKey` and `IReadOnlyForeignKey` in
`RelationalApiConsistencyTest.cs:238-254`, so that check never scans for them) — kept per the reviewer's
instruction, for symmetry with the same not-forced pair already present in `RelationalPropertyExtensions`.

## Covering tests, commands, and raw output

**Fast loop — API consistency (Finding 1):**

    $ .dotnet/dotnet build test/EFCore.Relational.Tests/EFCore.Relational.Tests.csproj -v q --nologo
    Build succeeded.
        0 Warning(s)
        0 Error(s)

    $ .dotnet/dotnet artifacts/bin/EFCore.Relational.Tests/Debug/net11.0/Microsoft.EntityFrameworkCore.Relational.Tests.dll --filter-class '*RelationalApiConsistencyTest'
    Test run summary: Passed! - .../Microsoft.EntityFrameworkCore.Relational.Tests.dll (net11.0|arm64)
      total: 20
      failed: 0
      succeeded: 20
      skipped: 0

**Fast loop — new/extended tests (Finding 2), by name:**

    $ .dotnet/dotnet build test/EFCore.Sqlite.FunctionalTests/EFCore.Sqlite.FunctionalTests.csproj -v q --nologo
    Build succeeded.
        0 Warning(s)
        0 Error(s)

    $ .dotnet/dotnet artifacts/bin/EFCore.Sqlite.FunctionalTests/Debug/net11.0/Microsoft.EntityFrameworkCore.Sqlite.FunctionalTests.dll --filter-class '*SqliteGenericNonRelationship*'
    Test run summary: Passed! - .../Microsoft.EntityFrameworkCore.Sqlite.FunctionalTests.dll (net11.0|arm64)
      total: 200
      failed: 0
      succeeded: 200
      skipped: 0

    $ .dotnet/dotnet artifacts/bin/EFCore.Sqlite.FunctionalTests/Debug/net11.0/Microsoft.EntityFrameworkCore.Sqlite.FunctionalTests.dll --filter-method '*constraint_names*' --list-tests
      Microsoft.EntityFrameworkCore.ModelBuilding.SqliteModelBuilderGenericTest+SqliteGenericNonRelationship.Can_configure_per_table_key_and_foreign_key_constraint_names
      Microsoft.EntityFrameworkCore.ModelBuilding.SqliteModelBuilderGenericTest+SqliteGenericNonRelationship.Convention_cannot_override_explicit_per_table_key_and_foreign_key_constraint_names
    Test discovery summary: found 2 test(s)

Both named tests are confirmed executed and passing (not silently skipped).

**Full suites, foreground, run alone (no stray process from the earlier stalled turn — verified with
`ps aux | grep -iE "testhost|vstest|dotnet test"` returning nothing before each run):**

    $ ./build.sh --test --projects .../test/EFCore.Relational.Tests/EFCore.Relational.Tests.csproj
    ...
    Tests succeeded: .../Microsoft.EntityFrameworkCore.Relational.Tests.dll [net11.0|arm64]
    Build succeeded.

TRX `<ResultSummary>`:

    outcome="Completed"
    total="1496" executed="1495" passed="1495" failed="0" error="0" ... notExecuted="1"

Matches the stated baseline exactly.

    $ ./build.sh --test --projects .../test/EFCore.Sqlite.FunctionalTests/EFCore.Sqlite.FunctionalTests.csproj
    ...
    Tests succeeded: .../Microsoft.EntityFrameworkCore.Sqlite.FunctionalTests.dll [net11.0|arm64]
    Build succeeded.

TRX `<ResultSummary>`:

    outcome="Completed"
    total="38227" executed="37941" passed="37941" failed="0" error="0" ... notExecuted="286"

Both new tests' individual TRX entries show `outcome="Passed"`:
`Can_configure_per_table_key_and_foreign_key_constraint_names` → `Passed`;
`Convention_cannot_override_explicit_per_table_key_and_foreign_key_constraint_names` → `Passed`.

## Files changed (this fix pass)

- `src/EFCore.Relational/Extensions/RelationalKeyExtensions.cs` — dropped `fromDataAnnotation` param from `RemoveOverrides(IConventionKey, ...)`
- `src/EFCore.Relational/Extensions/RelationalForeignKeyExtensions.cs` — dropped `fromDataAnnotation` param from `RemoveOverrides(IConventionForeignKey, ...)`
- `test/EFCore.Relational.Specification.Tests/ModelBuilding/RelationalModelBuilderTest.cs` — extended FK coverage and added the convention-refusal test

`test/EFCore.Relational.Specification.Tests/ModelBuilding/RelationalTestModelBuilderExtensions.cs` was
touched during investigation (an added, then-removed 3-arg `HasConstraintName` wrapper overload) and ends
this pass with **no net diff** from the prior commit — confirmed via `git status`/`git diff --stat`, not
staged.

## Fix-pass concerns

- The `TestReferenceCollectionBuilder` `IInfrastructure` gap described above is a genuine pre-existing bug in
  shared test infrastructure (`test/EFCore.Specification.Tests/ModelBuilding/ModelBuilderTest.Generic.cs` and
  `.NonGeneric.cs`), affecting every provider's model-builder tests, not just this branch. It silently no-ops
  the existing single-string `HasConstraintName(string)` wrapper extension wherever it's used through
  `TestReferenceCollectionBuilder`. Left unfixed here since it's out of B6's scope, but flagging it for
  visibility — worth its own follow-up task.
