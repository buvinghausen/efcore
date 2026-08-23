# Task B7 & B8 Report

## Task B7: Runtime model conversion

### What I implemented

- `src/EFCore.Relational/Metadata/RuntimeRelationalKeyOverrides.cs` (new) — immutable runtime clone of
  `RelationalKeyOverrides`, copied from `RuntimeRelationalPropertyOverrides.cs` and adapted for keys:
  constructor `RuntimeRelationalKeyOverrides(RuntimeKey key, in StoreObjectIdentifier storeObject, bool
  isNameOverridden, string? name)`, storing `Name` as an annotation only when `isNameOverridden` is true (so
  `IsNameOverridden` is `FindAnnotation(...) != null`, distinguishing "no override" from "override to null").
- `src/EFCore.Relational/Metadata/RuntimeRelationalForeignKeyOverrides.cs` (new) — same shape for foreign
  keys: constructor `RuntimeRelationalForeignKeyOverrides(RuntimeForeignKey foreignKey, in StoreObjectPair
  storeObjects, bool isNameOverridden, string? name)`.
- `src/EFCore.Relational/Metadata/Conventions/RelationalRuntimeModelConvention.cs` — added the non-runtime
  branches of `ProcessKeyAnnotations` and `ProcessForeignKeyAnnotations` that convert the design-time
  `KeyOverrides`/`ForeignKeyOverrides` annotations (`StoreObjectDictionary<RelationalKeyOverrides>` /
  `StoreObjectPairDictionary<RelationalForeignKeyOverrides>`) into their Runtime equivalents, plus
  `ProcessKeyOverridesAnnotations` / `ProcessForeignKeyOverridesAnnotations` extension seams (mirroring
  `ProcessPropertyOverridesAnnotations`).
- `src/EFCore.Relational/Design/Internal/RelationalCSharpRuntimeAnnotationCodeGenerator.cs` — added the
  compiled-model **code generation** path (separate from the in-memory runtime model above): `Generate(IKey,
  ...)` and `Generate(IForeignKey, ...)` now emit `new StoreObjectDictionary<RuntimeRelationalKeyOverrides>()`
  / `new StoreObjectPairDictionary<RuntimeRelationalForeignKeyOverrides>()` plus one `new
  RuntimeRelationalKeyOverrides(...)`/`new RuntimeRelationalForeignKeyOverrides(...)` call per override,
  mirroring the existing `Generate(IProperty, ...)` / property-overrides `Create(...)` helpers. Added a
  `AppendStoreObjectPairLiteral` helper next to the existing `AppendLiteral(StoreObjectIdentifier, ...)`.
- `test/EFCore.Relational.Specification.Tests/Scaffolding/CompiledModelRelationalTestBase.cs` — added
  `Customer`/`Order` model classes and two `[Fact]` tests (matching the file's actual convention — it uses
  `[Fact]`, not `[ConditionalFact]`, throughout):
  - `Per_store_object_constraint_names_survive_the_compiled_model` (the brief's given key test, with an added
    `Email` property on `Customer` so the split-table validator doesn't reject a main table with only a key
    column — `SplitToTable` requires at least one non-key property to remain on the main table).
  - `Per_store_object_foreign_key_constraint_names_survive_the_compiled_model` (new — not in the brief's Step
    1, but the brief's own instructions ask for the FK side too via `RuntimeRelationalForeignKeyOverrides`,
    and Step 5 explicitly requires the FK code-generation path to work; I added this test because Step 1's
    given test only exercises keys).

### Deviation from the brief's literal `Produces` signature

The brief's Produces list writes `RuntimeRelationalKeyOverrides(IKey key, ...)` /
`RuntimeRelationalForeignKeyOverrides(IForeignKey foreignKey, ...)`. I used `RuntimeKey key` /
`RuntimeForeignKey foreignKey` instead, matching the *actual* pattern in the file the brief says to copy
(`RuntimeRelationalPropertyOverrides` takes `RuntimeProperty property`, not `IProperty`). Runtime metadata
objects hold references to other Runtime* objects, not the design-time interfaces, so this is the correct
type for consistency with the rest of the runtime object graph; the brief's literal type name appears to be
shorthand. This compiles, round-trips correctly, and matches the established file's own convention.

### Testing (TDD evidence)

**RED** — ran with the implementation stashed (`git stash push -u` on the 4 new/changed src files, keeping the
test changes) to prove the test genuinely fails without the runtime-model conversion:

```
$ .dotnet/dotnet artifacts/bin/EFCore.Sqlite.FunctionalTests/Debug/net11.0/Microsoft.EntityFrameworkCore.Sqlite.FunctionalTests.dll --filter-class '*CompiledModelSqliteTest*' --filter-method '*Per_store_object*'
failed Microsoft.EntityFrameworkCore.Scaffolding.CompiledModelSqliteTest.Per_store_object_constraint_names_survive_the_compiled_model (939ms)
  Xunit.MicrosoftTestingPlatform.XunitException: System.InvalidOperationException : Cannot scaffold C# literals of type 'Microsoft.EntityFrameworkCore.Metadata.StoreObjectDictionary`1[Microsoft.EntityFrameworkCore.Metadata.Internal.RelationalKeyOverrides]'. The provider should implement CoreTypeMapping.GenerateCodeLiteral to support using it at design time.
failed Microsoft.EntityFrameworkCore.Scaffolding.CompiledModelSqliteTest.Per_store_object_foreign_key_constraint_names_survive_the_compiled_model (114ms)
  Xunit.MicrosoftTestingPlatform.XunitException: System.InvalidOperationException : Cannot scaffold C# literals of type 'Microsoft.EntityFrameworkCore.Metadata.StoreObjectPairDictionary`1[Microsoft.EntityFrameworkCore.Metadata.Internal.RelationalForeignKeyOverrides]'. ...

Test run summary: Failed! - .../Microsoft.EntityFrameworkCore.Sqlite.FunctionalTests.dll (net11.0|arm64)
  total: 2
  failed: 2
  succeeded: 0
  skipped: 0
  duration: 7s 490ms
```

(An earlier RED run, before I fixed a test-model bug — `Customer` had only the key property, which the
split-table validator rejects — failed with a *different*, wrong-reason error:
`InvalidOperationException: Entity type 'Customer' has a split mapping, but it doesn't map any non-primary
key property to the main store object.` I fixed the test model (`Email` property) before treating the RED as
valid evidence.)

Then `git stash pop` restored the implementation.

**GREEN**:

```
$ .dotnet/dotnet artifacts/bin/EFCore.Sqlite.FunctionalTests/Debug/net11.0/Microsoft.EntityFrameworkCore.Sqlite.FunctionalTests.dll --filter-class '*CompiledModelSqliteTest*' --filter-method '*Per_store_object*'
Test run summary: Passed! - .../Microsoft.EntityFrameworkCore.Sqlite.FunctionalTests.dll (net11.0|arm64)
  total: 2
  failed: 0
  succeeded: 2
  skipped: 0
  duration: 9s 171ms
```

Named tests, both PASSED: `Per_store_object_constraint_names_survive_the_compiled_model`,
`Per_store_object_foreign_key_constraint_names_survive_the_compiled_model`.

Full `CompiledModelSqliteTest` class (all tests, not just mine) — same 4 failures present both **with and
without** my change (verified by stashing my change and re-running: identical 4 names fail either way), all
`SQLite Error 1: 'mod_spatialite.so: cannot open shared object file: No such file or directory'` — a missing
native library on this machine, unrelated to constraint names:

```
Test run summary: Failed! - .../Microsoft.EntityFrameworkCore.Sqlite.FunctionalTests.dll (net11.0|arm64)
  total: 18
  failed: 4
  succeeded: 14
  skipped: 0
  duration: 28s 054ms
```//`total: 16 failed: 4 succeeded: 12` before my 2 new tests were added — same 4 names both times.

Official `build.sh --test` gate for the touched suites — both green:

```
$ ./build.sh --test --projects .../test/EFCore.Relational.Tests/EFCore.Relational.Tests.csproj
Tests succeeded: .../Microsoft.EntityFrameworkCore.Relational.Tests.dll [net11.0|arm64]
Build succeeded.

$ ./build.sh --test --projects .../test/EFCore.Sqlite.FunctionalTests/EFCore.Sqlite.FunctionalTests.csproj
Tests succeeded: .../Microsoft.EntityFrameworkCore.Sqlite.FunctionalTests.dll [net11.0|arm64]
Build succeeded.
  Time Elapsed 00:06:15.94
```

`EFCore.Relational.Tests` full run (direct dll invocation, matches the stated baseline exactly):

```
Test run summary: Passed! - .../Microsoft.EntityFrameworkCore.Relational.Tests.dll (net11.0|arm64)
  total: 1496
  failed: 0
  succeeded: 1495
  skipped: 1
  duration: 10s 791ms
```

`RelationalApiConsistencyTest` (checks the two new public runtime types and no unintended API drift):

```
Test run summary: Passed! - .../Microsoft.EntityFrameworkCore.Relational.Tests.dll (net11.0|arm64)
  total: 20
  failed: 0
  succeeded: 20
  skipped: 0
  duration: 774ms
```

### Files changed

- `src/EFCore.Relational/Metadata/RuntimeRelationalKeyOverrides.cs` (new)
- `src/EFCore.Relational/Metadata/RuntimeRelationalForeignKeyOverrides.cs` (new)
- `src/EFCore.Relational/Metadata/Conventions/RelationalRuntimeModelConvention.cs`
- `src/EFCore.Relational/Design/Internal/RelationalCSharpRuntimeAnnotationCodeGenerator.cs`
- `test/EFCore.Relational.Specification.Tests/Scaffolding/CompiledModelRelationalTestBase.cs`
- `test/EFCore.Sqlite.FunctionalTests/Scaffolding/Baselines/Per_store_object_constraint_names_survive_the_compiled_model/*`
  (NativeAOT compiled-model source baselines, auto-generated with `EF_TEST_REWRITE_BASELINES=1` and reviewed
  by hand — confirmed `RuntimeRelationalKeyOverrides`/`StoreObjectDictionary<RuntimeRelationalKeyOverrides>`
  literals are present and correct)
- `test/EFCore.Sqlite.FunctionalTests/Scaffolding/Baselines/Per_store_object_foreign_key_constraint_names_survive_the_compiled_model/*`
  (same, for the FK test; confirmed `RuntimeRelationalForeignKeyOverrides`/`StoreObjectPairDictionary<...>`/
  `new StoreObjectPair(...)` literals)

Commit: `e9d0b86f4a` "Convert key and FK overrides into the runtime model"

### Self-review

- **Completeness**: both runtime types present with the constructor shape used throughout the file family
  (Runtime* parameter types, not I* interfaces — see deviation note above). Both
  `ProcessKeyOverridesAnnotations`/`ProcessForeignKeyOverridesAnnotations` seams added. Both the in-memory
  runtime-model conversion (`RelationalRuntimeModelConvention`) and the NativeAOT **code generation** path
  (`RelationalCSharpRuntimeAnnotationCodeGenerator`, Step 5) are covered — Step 5 turned out to be required,
  not optional: without it the FK/Key test failed with `Cannot scaffold C# literals of type
  StoreObject(Pair)Dictionary<...>` even after the in-memory conversion was correct, because
  `CompiledModelTestBase.Test` always exercises NativeAOT code generation (`ForNativeAot = true` by default),
  not just the in-memory runtime model.
- **Quality**: XML docs written for keys/FKs specifically (not copy-pasted "column"/"property" wording).
- **Discipline**: no baseline.json change; no speculative public surface — `RuntimeRelationalKeyOverrides`
  and `RuntimeRelationalForeignKeyOverrides` are exactly the two Produces-list types, both consumed by the
  convention and code generator I also had to touch (not optional — required for the test to pass).
- **Testing**: both new tests confirmed to *execute* (not silently undiscovered) — non-zero counts, named in
  output, both PASSED. Verified the four unrelated Sqlite failures are pre-existing by running the same suite
  with my changes stashed (identical 4 names fail either way).

### Concerns

- None for B7. The `RuntimeKey`/`RuntimeForeignKey` vs. brief's literal `IKey`/`IForeignKey` deviation is
  intentional and matches the file's own established pattern (see above) — flagging it per the "ask before
  guessing" instruction's spirit, even though I judged it unambiguous enough not to stop and escalate.

---

## Task B8: Snapshot generation and debug output

### What I implemented

- `src/EFCore.Relational/Design/AnnotationCodeGenerator.cs` — added `RelationalAnnotationNames.KeyOverrides`
  and `RelationalAnnotationNames.ForeignKeyOverrides` to `IgnoredRelationalAnnotations`, alongside the existing
  `RelationalOverrides` entry. Without this the raw annotation dictionary would be emitted as a
  `.HasAnnotation("Relational:KeyOverrides", ...)` call *in addition to* the fluent `.HasName(...)` calls
  (and would fail outright, since there's no literal generator for the dictionary type).
- `src/EFCore.Design/Migrations/Design/CSharpSnapshotGenerator.cs`:
  - `GenerateKeyOverrides(string keyBuilderName, IKey key, IndentedStringBuilder stringBuilder)` — called from
    `GenerateKey` right after the `.HasKey(...)`/`.HasAlternateKey(...)` header, before the existing
    `GenerateAnnotations(...)` call (which terminates the statement). Emits one `.HasName(name,
    <StoreObjectIdentifier literal>)` **chained onto the same statement** per override (no per-override `;` —
    the final `;` is added once, by the existing `GenerateAnnotations` call that follows).
  - `GenerateForeignKeyOverrides(IForeignKey foreignKey, IndentedStringBuilder stringBuilder)` — called from
    `GenerateForeignKey` right before `GenerateForeignKeyAnnotations`, same chaining approach: one
    `.HasConstraintName(name, <dependent literal>, <principal literal>)` per override.
  - `AppendStoreObjectIdentifierLiteral(StoreObjectIdentifier, IndentedStringBuilder)` (private helper) —
    `ICSharpHelper` has no literal generator for `StoreObjectIdentifier` (confirmed: `UnknownLiteral` throws
    `Cannot scaffold C# literals of type StoreObjectIdentifier` when asked), so I emit the constructor call
    explicitly, covering all seven `StoreObjectType` cases (mirroring the existing analogous helper in
    `RelationalCSharpRuntimeAnnotationCodeGenerator.AppendLiteral`). **The type name is always fully
    qualified** (`Microsoft.EntityFrameworkCore.Metadata.StoreObjectIdentifier.Table(...)`), not bare
    `StoreObjectIdentifier.Table(...)` — see "Deviation" below for why.
- `test/EFCore.Design.Tests/Migrations/Design/CSharpMigrationsGeneratorTest.ModelSnapshot.cs` — 4 new tests
  (in the `#region HasKey` / `#region ForeignKey` sections) plus two new private nested model classes
  (`OverridesCustomer`, `OverridesOrder`), all through the round-trip harness (`Test(...)`, which compiles the
  generated snapshot and asserts an empty model-differ diff against the original):
  1. `Snapshot_round_trips_per_store_object_key_constraint_names` — the brief's key scenario.
  2. `Snapshot_round_trips_per_store_object_foreign_key_constraint_names` — the FK equivalent, both store
     objects.
  3. `Snapshot_round_trips_explicit_null_key_constraint_name_override` — global `.HasName("pk_global")` plus
     `.HasName(null, StoreObjectIdentifier.Table("CustomerDetails", "DefaultSchema"))` on the fragment;
     asserts the fragment resolves to `key.GetDefaultName(...)`, not `"pk_global"` — the only assertion that
     proves `IsNameOverridden: true, Name: null` survived the round trip.
  4. `Snapshot_does_not_emit_raw_key_or_foreign_key_override_annotations` — negative test, built by hand
     (not through the `Test(...)` harness, since that helper never exposes the raw generated code string to
     the caller) — asserts `Assert.DoesNotContain(RelationalAnnotationNames.KeyOverrides, code)` and same for
     `ForeignKeyOverrides`.

### Deviations from the brief

1. **`GenerateKeyOverrides` doesn't append `;` per override.** The brief's given code appends `AppendLine(");")`
   inside the `foreach`, which is correct for exactly one override but **breaks compilation for two or more**
   (the second `.HasName(...)` would start a bare statement with no receiver — `AppendChainedBuilderHeader`
   only introduces a real local-variable receiver when there are *type-qualified* fluent calls, which per-store
   overrides are not). The Step 1 test's own model has *two* key overrides (`Customers` and
   `CustomerDetails`). I chained all overrides onto the single ongoing `.HasKey(...)` statement instead, with
   the one terminating `;` supplied by the existing `GenerateAnnotations(...)` call that already follows —
   this is exactly what the dispatch's guidance anticipated ("Match the indentation of the `expectedCode`
   fragment to what the generator actually emits — run once, read the failure output ... then paste the real
   fragment"), which I read as license to fix the generated-code *shape*, not just its whitespace, when the
   literal snippet doesn't compile for the test's own model.
2. **`GenerateForeignKeyOverrides` doesn't take a `foreignKeyBuilderName` prefix parameter.** For keys,
   `keyBuilderName` is `""` in the common (no type-qualified-annotation) case, so prefixing it is a no-op. For
   foreign keys, `foreignKeyBuilderName` is **never** empty — it's always the full receiver expression text
   (e.g. `entity.HasOne(...)`). Prefixing it the same way would duplicate the entire relationship-builder
   expression as a second, syntactically invalid fragment rather than continuing the existing chain. I dropped
   the parameter for the FK method; nothing in the brief's Produces list names an exact FK signature (only "the
   FK equivalent"), so this isn't a Produces-list violation.
3. **`AppendStoreObjectIdentifierLiteral` fully qualifies `StoreObjectIdentifier`.** The default namespace list
   the snapshot generator assembles (`Microsoft.EntityFrameworkCore`,
   `Microsoft.EntityFrameworkCore.Infrastructure`, `Microsoft.EntityFrameworkCore.Storage.ValueConversion`,
   plus whatever `GetNamespaces(model)` finds by scanning **annotation values'** CLR types) has no path by
   which a plain literal-text `StoreObjectIdentifier.Table(...)` I emit would get a `using
   Microsoft.EntityFrameworkCore.Metadata;` added for it — that mechanism only tracks types that appear as
   actual reflected CLR objects, not text I hand-write. `CSharpSnapshotGenerator` has no hook (unlike
   `RelationalCSharpRuntimeAnnotationCodeGenerator`, which has `parameters.Namespaces` for exactly this)
   through which it could ask for an extra `using`. Fully qualifying the type name sidesteps this
   architectural gap entirely and is self-contained within the one file the brief scopes this task to. I
   discovered this the hard way — first attempt used the bare name and the generated snapshot failed to
   compile with `The type or namespace name 'StoreObjectIdentifier' could not be found`. (Not shown as a
   separate RED run below since it was caught and fixed before I recorded the first genuine RED for the
   feature.)
4. Neither of these deviations touches the *literal text* the brief specified for the two file changes it
   marked mandatory (`AnnotationCodeGenerator.cs` ignore-list addition, and the general shape of
   `GenerateKeyOverrides`) — only the parts that were demonstrably broken as literally written.

### Debug strings (Step 5)

Checked `IReadOnlyRelationalKeyOverrides.ToDebugString` / `IReadOnlyRelationalForeignKeyOverrides.ToDebugString`
(default interface methods, already present from earlier tasks) by reading the code:
`RelationalKeyOverrides.ToString()` / `RelationalForeignKeyOverrides.ToString()` already delegate to them, and
they already produce readable output — `"Override: <StoreObject.DisplayName()>[ Name: <name>]"` for keys, and
`"Override: <StoreObjects>[ Name: <name>]"` for FKs where `StoreObjects` is a `StoreObjectPair` whose
`ToString()` (Task B3) gives `"UserLockout -> Users"` as the brief expects. **No code change was needed** —
Step 5 was a verification step, not a required edit, and I did not modify `RelationalKeyOverrides.cs` /
`RelationalForeignKeyOverrides.cs` (listed in the brief's Files section but nothing there was actually broken).

### Testing (TDD evidence)

**RED** (before Step 3 + Step 4 implementation, ran with the round-trip harness):

```
$ .dotnet/dotnet artifacts/bin/EFCore.Design.Tests/Debug/net11.0/Microsoft.EntityFrameworkCore.Design.Tests.dll --filter-class '*CSharpMigrationsGeneratorTest*' --filter-method '*Snapshot_round_trips_per_store_object*'
failed ...Snapshot_round_trips_per_store_object_key_constraint_names (1s 979ms)
  Assert.Equal() Failure: Strings differ
  Expected: "pk_customers"
  Actual:   null
failed ...Snapshot_round_trips_per_store_object_foreign_key_constraint_names (149ms)
  Assert.Equal() Failure: Strings differ
  Expected: "fk_orders_customers"
  Actual:   null

Test run summary: Failed! - .../Microsoft.EntityFrameworkCore.Design.Tests.dll (net11.0|arm64)
  total: 2
  failed: 2
  succeeded: 0
  skipped: 0
  duration: 2s 602ms
```

That RED came from the *model assertion* on the rebuilt model (the harness runs the assert callback before
comparing the expected-code fragment), proving the round trip itself was broken, not just a text mismatch.

**A second RED worth recording** — while diagnosing why the round-trip failed even with the emitter working,
I found (via a throwaway debug test, since removed) that the *harness itself* (`CSharpMigrationsGeneratorTestBase.Test`)
unconditionally calls `modelBuilder.HasDefaultSchema("DefaultSchema")`, so a `StoreObjectIdentifier.Table("Customers")`
override with `Schema: null` never matches the entity's actual `Table("Customers", "DefaultSchema")` identity —
the override was stored correctly (`key.GetOverrides()` showed both overrides present with `IsNameOverridden:
true`), but `GetName`'s resolution guard (`StoreObjectIdentifier.Create(containingType, ...) == storeObject`)
never matched. This was a bug in my *test*, not the implementation — fixed by passing `"DefaultSchema"`
explicitly in every `StoreObjectIdentifier.Table(...)` call in the 4 new tests (B7's compiled-model harness
does *not* impose a default schema, which is why that test's `StoreObjectIdentifier.Table("Customers")` with no
schema worked as-is).

**GREEN**:

```
$ .dotnet/dotnet artifacts/bin/EFCore.Design.Tests/Debug/net11.0/Microsoft.EntityFrameworkCore.Design.Tests.dll --filter-class '*CSharpMigrationsGeneratorTest*' --filter-method '*per_store_object*'
Test run summary: Passed! - .../Microsoft.EntityFrameworkCore.Design.Tests.dll (net11.0|arm64)
  total: 2
  failed: 0
  succeeded: 2
  skipped: 0
  duration: 2s 634ms

$ .dotnet/dotnet artifacts/bin/EFCore.Design.Tests/Debug/net11.0/Microsoft.EntityFrameworkCore.Design.Tests.dll --filter-class '*CSharpMigrationsGeneratorTest*' --filter-method '*verride*'
Test run summary: Passed! - .../Microsoft.EntityFrameworkCore.Design.Tests.dll (net11.0|arm64)
  total: 3
  failed: 0
  succeeded: 3
  skipped: 0
  duration: 2s 802ms
```

Named tests, all PASSED: `Snapshot_round_trips_per_store_object_key_constraint_names`,
`Snapshot_round_trips_per_store_object_foreign_key_constraint_names`,
`Snapshot_round_trips_explicit_null_key_constraint_name_override`,
`Snapshot_does_not_emit_raw_key_or_foreign_key_override_annotations` (the fourth `*verride*` match,
`Can_override_table_name_for_many_to_many_join_table_stored_in_snapshot`, is a pre-existing, unrelated test).

Full `CSharpMigrationsGeneratorTest` class:

```
Test run summary: Passed! - .../Microsoft.EntityFrameworkCore.Design.Tests.dll (net11.0|arm64)
  total: 170
  failed: 0
  succeeded: 170
  skipped: 0
  duration: 19s 650ms
```

Full `EFCore.Design.Tests` project (direct dll invocation):

```
Test run summary: Failed! - .../Microsoft.EntityFrameworkCore.Design.Tests.dll (net11.0|arm64)
  total: 1274
  failed: 5
  succeeded: 1268
  skipped: 1
  duration: 23s 363ms
```

Those 5 failures are all in `OperationExecutorTest` (`AddMigration_errors_for_bad_names`,
`AddAndApplyMigration_errors_for_bad_names`, `AddMigration_errors_for_bad_output_dirs`) — path/filename
validation tests (e.g. `"A\\B\\C"`, `"Something:Else"`) that assume Windows path-character restrictions;
unrelated to constraint names. Confirmed pre-existing by stashing my 3 changed files and re-running: identical
5 failures with none of my changes present.

`DesignApiConsistencyTest` (checks the new protected virtual `GenerateKeyOverrides`/`GenerateForeignKeyOverrides`
don't violate any API-consistency rule; `EFCore.Design.baseline.json` did not need updating):

```
Test run summary: Passed! - .../Microsoft.EntityFrameworkCore.Design.Tests.dll (net11.0|arm64)
  total: 19
  failed: 0
  succeeded: 19
  skipped: 0
  duration: 560ms
```

Official `build.sh --test` gate — both green:

```
$ ./build.sh --test --projects .../test/EFCore.Design.Tests/EFCore.Design.Tests.csproj
Tests succeeded: .../Microsoft.EntityFrameworkCore.Design.Tests.dll [net11.0|arm64]
Build succeeded.

$ ./build.sh --test --projects .../test/EFCore.Relational.Tests/EFCore.Relational.Tests.csproj
Tests succeeded: .../Microsoft.EntityFrameworkCore.Relational.Tests.dll [net11.0|arm64]
Build succeeded.
```

Final sanity check after both commits landed — re-ran B7's compiled-model tests to confirm B8's shared-infra
change (`AnnotationCodeGenerator.cs`) didn't regress them:

```
$ .dotnet/dotnet artifacts/bin/EFCore.Sqlite.FunctionalTests/Debug/net11.0/Microsoft.EntityFrameworkCore.Sqlite.FunctionalTests.dll --filter-class '*CompiledModelSqliteTest*' --filter-method '*Per_store_object*'
Test run summary: Passed! - .../Microsoft.EntityFrameworkCore.Sqlite.FunctionalTests.dll (net11.0|arm64)
  total: 2
  failed: 0
  succeeded: 2
  skipped: 0
  duration: 9s 171ms
```

### Files changed

- `src/EFCore.Design/Migrations/Design/CSharpSnapshotGenerator.cs`
- `src/EFCore.Relational/Design/AnnotationCodeGenerator.cs`
- `test/EFCore.Design.Tests/Migrations/Design/CSharpMigrationsGeneratorTest.ModelSnapshot.cs`
- `src/EFCore.Relational/Metadata/Internal/RelationalKeyOverrides.cs`,
  `RelationalForeignKeyOverrides.cs` — **not modified**; verified correct by inspection (see Debug strings
  above).

Commit: `322d7e2d97` "Emit per-store-object constraint names into snapshots"

### Self-review

- **Completeness**: `GenerateKeyOverrides(string keyBuilderName, IKey key, IndentedStringBuilder
  stringBuilder)` matches the Produces list exactly. The FK equivalent is `GenerateForeignKeyOverrides(IForeignKey
  foreignKey, IndentedStringBuilder stringBuilder)` — no exact signature was mandated, and the dropped
  `foreignKeyBuilderName` parameter was a deliberate correctness fix (see Deviations). Both round trips
  covered, plus the explicit-null case, plus the negative "no raw annotation" check — all 4 through the
  empty-diff round-trip harness (the negative test additionally does its own direct text check, since the
  harness doesn't expose the generated code string).
- **Quality**: XML docs on `GenerateKeyOverrides`/`GenerateForeignKeyOverrides` describe keys/FKs specifically.
  The generator produces code that compiles, rebuilds, and diffs empty against the original model for every
  test — that's the load-bearing verification per the dispatch's own framing, not the `Assert.Contains` text
  checks (which exist alongside it, matching each test's real, observed output rather than the brief's literal
  example).
- **Discipline**: no baseline.json change (confirmed `EFCore.Design.baseline.json` didn't need touching either
  — `DesignApiConsistencyTest` passed as-is). No speculative public surface — both new members are exactly
  what the brief's Produces list names (with the one narrow, justified FK signature simplification above).
- **Testing**: all 4 new tests confirmed to *execute* — non-zero counts, named in output, all PASSED — not
  silently undiscovered.

### Concerns

- The two deviations in `GenerateKeyOverrides`/`GenerateForeignKeyOverrides` (no per-override `;`, no FK
  builder-name prefix) mean the actually-shipped code differs from the brief's literal snippets in ways that
  matter for correctness, not just style. I'm confident in them because I verified the alternative (following
  the brief verbatim) produces code that fails to compile for the brief's own test scenario — but flagging
  this explicitly since "read every brief value as a requirement" was the default instruction.
- I did not add an explicit-null-override test to B7's compiled-model suite (only to B8's snapshot suite,
  where the dispatch explicitly asked for it). B7's runtime conversion is a direct 1:1 field copy
  (`overrides.IsNameOverridden`, `overrides.Name` straight into the constructor) with no text-round-trip risk
  comparable to B8's invented `StoreObjectIdentifier`/`StoreObjectPair` literal form, and I verified the
  null-preserving logic in `RuntimeRelationalKeyOverrides`'s constructor by reading it
  (`SetAnnotation(RelationalAnnotationNames.Name, name)` runs whenever `isNameOverridden` is true, regardless
  of whether `name` is null, so `IsNameOverridden` stays `true` and `Name` stays `null` — they don't collapse).
  If this reasoning is disputed, adding the test is cheap.

---

## Fix report (post-review)

Review verdict: **B7 Approved, B8 Needs fixes.** Three of the four original deviations were checked and cleared on evidence; two Important findings remained. Both are fixed below, plus the requested real test counters with the mod_spatialite reconciliation.

### Finding 1 (B8): `GenerateKeyOverrides` emitted uncompilable code when a type-qualified key annotation was also present

**Root cause.** `keyBuilderName` (`AppendChainedBuilderHeader`'s return value) is `""` only when `GetAnnotationCalls` produces no type-qualified calls. When a provider-level type-qualified key annotation also exists — the review's example, SQL Server's `HasFillFactor`/`IsClustered` (`SqlServerKeyBuilderExtensions`, a static extension outside `RelationalModelBuilderExtensions`'s assembly) — `AppendChainedBuilderHeader` emits `var key = ` and leaves the `.HasKey(...)` chain **unterminated**, pending `GenerateAnnotations`. My original `GenerateKeyOverrides` ran *before* that termination and unconditionally prefixed `.Append(keyBuilderName)`, splicing a bare `key` identifier into the middle of the still-open `var key = b.HasKey("Id")` expression:

```
var key = b.HasKey("Id")
key.HasName("pk1", null)
;
```

— `error CS1002: ; expected`, reproduced exactly with a temporarily-reverted build (see RED evidence below).

**Fix** (`src/EFCore.Design/Migrations/Design/CSharpSnapshotGenerator.cs`): took the reviewer's "simpler and probably better" option — per-store-object key overrides are now **always** emitted as their own statement(s) against a stable `key` receiver, independent of whether a type-qualified annotation also needed one:

- `AppendChainedBuilderHeader` gained a `bool forceLocal = false` parameter (defaults false; only `GenerateKey`'s call site passes `true`, so `GenerateProperty`'s and `GenerateIndex`'s call sites are unaffected).
- `GenerateKey` now computes `hasKeyOverrides = key.GetOverrides().Any(o => o.IsNameOverridden)` up front and passes it as `forceLocal`, guaranteeing `keyBuilderName` is a real, already-initialized local whenever `GenerateKeyOverrides` is going to emit anything.
- `GenerateKeyOverrides` is now called *after* `GenerateAnnotations` (which is what actually appends the terminating `;` and decrements the indent), not before — so the `var key = ...;` statement is always closed before any `key.HasName(...)` reference to it.
- `GenerateKeyOverrides` itself now always terminates each override with `;` (reverting to closer to the brief's original per-override `AppendLine(");")`, which is correct once `keyBuilderName` is guaranteed to be a real receiver rather than sometimes-empty chain-continuation text).

For the case with no type-qualified calls at all (my original two B8 round-trip tests), the shape changed from a single chained statement to `var key = b.HasKey("Id"); ... key.HasName(...); key.HasName(...);` — a purely internal formatting change; the round-trip harness (compile + rebuild + empty-diff) doesn't care about statement shape, only content, so no test assertions on override *resolution* needed to change. I did update the `expectedCode` `Assert.Contains` fragments in the two existing tests to match the new (still correct) shape, per the dispatch's explicit license to do this ("run once, read the failure output ... then paste the real fragment").

**FK path — confirmed safe, not assumed.** Read `GenerateForeignKey` end to end and grepped it for `AppendChainedBuilderHeader`: **zero matches**. `foreignKeyBuilderName` is built entirely by string concatenation (`foreignKeyBuilderNameStringBuilder.ToString()`), never through `AppendChainedBuilderHeader`, so there is no code path by which the FK builder ever becomes a `var foreignKeyBuilder = ` declaration — it is always repeatable expression text (calling `.HasOne(...)` / `.WithOwner(...)` again is idempotent). `GenerateForeignKeyOverrides` chains directly onto that still-open statement before `GenerateForeignKeyAnnotations` closes it, which is safe unconditionally. No change needed on the FK side.

**New covering test:** `Snapshot_round_trips_per_store_object_key_constraint_name_with_type_qualified_key_annotation` (`test/EFCore.Design.Tests/Migrations/Design/CSharpMigrationsGeneratorTest.ModelSnapshot.cs`) — a key with both `.HasName("pk_customers", StoreObjectIdentifier.Table(...))` and `.HasFillFactor(90)` (the SQL-Server type-qualified extension already proven elsewhere in this file, e.g. `Key_fill_factor_is_stored_in_snapshot`, to force `AppendChainedBuilderHeader`'s `var key = ` path — the "nearest reachable equivalent" to the review's `IsClustered` example, reusing infrastructure this SQL-Server-based test file already exercises rather than reaching for something new), run through the full round-trip harness (compile + rebuild + empty-diff).

**RED** (temporarily reverted just `CSharpSnapshotGenerator.cs` via `git stash`, kept the new test):

```
$ .dotnet/dotnet artifacts/bin/EFCore.Design.Tests/Debug/net11.0/Microsoft.EntityFrameworkCore.Design.Tests.dll --filter-method '*Snapshot_round_trips_per_store_object_key_constraint_name_with_type_qualified_key_annotation*'
failed ...Snapshot_round_trips_per_store_object_key_constraint_name_with_type_qualified_key_annotation
  Xunit.MicrosoftTestingPlatform.XunitException: System.InvalidOperationException : Build failed.

  First diagnostic:
  Snapshot.cs(39,41): error CS1002: ; expected

  Location:
  ("Id")
  ...
Test run summary: Failed!
  total: 1
  failed: 1
  succeeded: 0
```

— the exact `error CS1002: ; expected` the review reported, reproduced independently.

**GREEN** (restored the fix):

```
$ .dotnet/dotnet artifacts/bin/EFCore.Design.Tests/Debug/net11.0/Microsoft.EntityFrameworkCore.Design.Tests.dll --filter-class '*CSharpMigrationsGeneratorTest*' --filter-method '*per_store_object*'
Test run summary: Passed!
  total: 3
  failed: 0
  succeeded: 3
  skipped: 0
  duration: 2s 745ms
```

Full `CSharpMigrationsGeneratorTest` class after the fix:

```
Test run summary: Passed! - .../Microsoft.EntityFrameworkCore.Design.Tests.dll (net11.0|arm64)
  total: 171
  failed: 0
  succeeded: 171
  skipped: 0
  duration: 19s 099ms
```

Commit: `957d85edf9` "Fix B8 review finding: key overrides broke compilation with a type-qualified key annotation"

---

### Finding 2 (B7): the explicit-null case had no test on either runtime conversion path

**Why the existing tests didn't count as coverage.** My earlier rationale — "a straightforward 1:1 field copy verified by reading the code" — was true of the *constructors* but not of the *conversion sites*, and B7 has **two independent** conversion sites: `RelationalRuntimeModelConvention`'s in-memory conversion (every regular `DbContext.Model` access) and `RelationalCSharpRuntimeAnnotationCodeGenerator`'s NativeAOT source generation (compiled models). B8's explicit-null test only covers snapshot generation — a disjoint path — and contributes nothing here.

**A real trap I walked into and want to flag explicitly.** My first attempt at the in-memory-path test asserted only `key.GetName(customers) == "pk_global"` and `key.GetName(details) == key.GetDefaultName(details)` on the post-conversion runtime model. That test **passed even with `RelationalRuntimeModelConvention`'s new conversion code entirely reverted** (verified by temporarily restoring the pre-B7 version of the file and re-running) — because `IReadOnlyStoreObjectDictionary<out T>` (and `IReadOnlyStoreObjectPairDictionary<out T>`) is **covariant**, so the unconverted design-time `RelationalKeyOverrides`/`RelationalForeignKeyOverrides` object, left attached to the runtime key/FK by simple pass-through, still satisfies every read the resolution logic (`GetName`, `GetOverrides`) performs — the read-only interface doesn't care which concrete type backs it. A test built only on resolution outcomes was evidence-free for this specific finding: it would have shipped green whether or not the conversion existed. I caught this by deliberately checking (not assuming) that my new test failed when I removed the code it was supposed to be testing — it didn't, on the first attempt.

**Real fix:** added `Assert.IsType<RuntimeRelationalKeyOverrides>(...)` / `Assert.IsType<RuntimeRelationalForeignKeyOverrides>(...)` on the resolved override object — the only assertion that actually distinguishes "converted" from "design-time object carried through untouched." Re-verified: reverting the conversion code now genuinely fails these tests (RED below), and restoring it passes them (GREEN below). The resolution-outcome assertions (`GetName`/`GetDefaultName` equality) are kept alongside the type check, since they're still meaningful confirmation that resolution behaves correctly *given* a correctly-converted object — just not sufficient alone.

**New tests, in-memory runtime-model path** (`test/EFCore.Relational.Tests/Metadata/RelationalKeyOverridesTest.cs`, `RelationalForeignKeyOverridesTest.cs`) — built on the exact pattern already used elsewhere in this codebase for testing `IModelRuntimeInitializer` conversions directly (`test/EFCore.Tests/Metadata/Internal/PropertyBaseTest.cs`, `contextServices.GetRequiredService<IModelRuntimeInitializer>().Initialize(model, designTime: false, ...)`), rather than going through a full `DbContext`/compiled-model harness:

- `Explicit_null_key_name_override_survives_the_in_memory_runtime_model` — `Customer` split across `Customers`/`CustomerDetails`, global `.HasName("pk_global")` plus `.HasName(null, StoreObjectIdentifier.Table("CustomerDetails"))`; asserts `Assert.IsType<RuntimeRelationalKeyOverrides>` on the fragment's override, that the main table still resolves to `"pk_global"`, and that the fragment resolves to `GetDefaultName`, not `"pk_global"`.
- `Explicit_null_foreign_key_constraint_name_override_survives_the_in_memory_runtime_model` — `Blog`/`Post` FK, global `SetConstraintName("fk_global")` plus `SetConstraintName(null, postTable, blogTable)` on the only pair; same `Assert.IsType<RuntimeRelationalForeignKeyOverrides>` plus the resolves-to-default assertion.

**RED** (temporarily restored the pre-B7 `RelationalRuntimeModelConvention.cs` via `git show e9d0b86f4a~1:...`, rebuilt `EFCore.Relational` + `EFCore.Relational.Tests`):

```
$ .dotnet/dotnet artifacts/bin/EFCore.Relational.Tests/Debug/net11.0/Microsoft.EntityFrameworkCore.Relational.Tests.dll --filter-method '*survives_the_in_memory_runtime_model*'
failed Microsoft.EntityFrameworkCore.Metadata.RelationalKeyOverridesTest.Explicit_null_key_name_override_survives_the_in_memory_runtime_model (462ms)
  Assert.IsType() Failure: Value is not the exact type
  Expected: typeof(Microsoft.EntityFrameworkCore.Metadata.RuntimeRelationalKeyOverrides)
  Actual:   typeof(Microsoft.EntityFrameworkCore.Metadata.Internal.RelationalKeyOverrides)
failed Microsoft.EntityFrameworkCore.Metadata.RelationalForeignKeyOverridesTest.Explicit_null_foreign_key_constraint_name_override_survives_the_in_memory_runtime_model (459ms)
  Assert.IsType() Failure: Value is not the exact type
  Expected: typeof(Microsoft.EntityFrameworkCore.Metadata.RuntimeRelationalForeignKeyOverrides)
  Actual:   typeof(Microsoft.EntityFrameworkCore.Metadata.Internal.RelationalForeignKeyOverrides)

Test run summary: Failed!
  total: 2
  failed: 2
  succeeded: 0
```

(Confirming: the resolution-only assertions in the same tests did **not** fail in this reverted state — only the `Assert.IsType` calls did, exactly demonstrating the covariance trap above.)

**GREEN** (restored the committed `RelationalRuntimeModelConvention.cs` exactly, confirmed `git diff` empty against HEAD before rebuilding):

```
$ .dotnet/dotnet artifacts/bin/EFCore.Relational.Tests/Debug/net11.0/Microsoft.EntityFrameworkCore.Relational.Tests.dll --filter-method '*survives_the_in_memory_runtime_model*'
Test run summary: Passed!
  total: 2
  failed: 0
  succeeded: 2
  skipped: 0
  duration: 958ms
```

**Compiled-model (NativeAOT codegen) path** — extended `test/EFCore.Relational.Specification.Tests/Scaffolding/CompiledModelRelationalTestBase.cs` (the file the existing B7 codegen tests already live in, run through the Sqlite provider suite per the original dispatch correction) with:

- `Explicit_null_key_constraint_name_override_survives_the_compiled_model`
- `Explicit_null_foreign_key_constraint_name_override_survives_the_compiled_model` (added for symmetry — the review's fix bullet named only the key case, but the identical risk applies to `RelationalCSharpRuntimeAnnotationCodeGenerator`'s FK `Create` helper, and the marginal cost of covering it was low)

This path is structurally different from the in-memory one — the compiled model is rebuilt from **generated C# source**, not from a design-time object graph carried over by reference, so there's no equivalent covariance trap: if `RelationalCSharpRuntimeAnnotationCodeGenerator`'s conversion were broken, the generated code either wouldn't compile or wouldn't contain the right `new RuntimeRelationalKeyOverrides(...)`/`RuntimeRelationalForeignKeyOverrides(...)` calls at all — exactly the class of failure the original B7 RED run already demonstrated (`Cannot scaffold C# literals of type StoreObjectDictionary<RelationalKeyOverrides>`). Confirmed the generated baseline source directly:

```
$ grep -A3 'new RuntimeRelationalKeyOverrides' test/EFCore.Sqlite.FunctionalTests/Scaffolding/Baselines/Explicit_null_key_constraint_name_override_survives_the_compiled_model/CustomerEntityType.cs
        var keyCustomerDetails = new RuntimeRelationalKeyOverrides(
            key,
            StoreObjectIdentifier.Table("CustomerDetails", null),
            true,
            null);
```

— `isNameOverridden: true, name: null`, exactly as intended.

```
$ .dotnet/dotnet artifacts/bin/EFCore.Sqlite.FunctionalTests/Debug/net11.0/Microsoft.EntityFrameworkCore.Sqlite.FunctionalTests.dll --filter-class '*CompiledModelSqliteTest*' --filter-method '*Explicit_null*'
Test run summary: Passed! (after EF_TEST_REWRITE_BASELINES=1 generated the new baseline source files)
  total: 2
  failed: 0
  succeeded: 2
  skipped: 0
  duration: 9s 603ms
```

Full `CompiledModelSqliteTest` class after both new tests:

```
Test run summary: Failed! - .../Microsoft.EntityFrameworkCore.Sqlite.FunctionalTests.dll (net11.0|arm64)
  total: 20
  failed: 4
  succeeded: 16
  skipped: 0
```

The 4 failures are the same pre-existing `BigModel`, `No_NativeAOT`, `BigModel_with_JSON_columns`, `SimpleModel` (`mod_spatialite.so` missing) identified in the original report — none of the 6 tests I've added across both B7 fix rounds (2 original + 2 explicit-null) are among them.

Commit: `e3111b76ff` "Fix B7 review finding: cover the explicit-null case on both runtime paths"

---

### Real test counters, and the mod_spatialite reconciliation

Per-suite full-project runs (direct dll invocation, all tests, no filter):

```
EFCore.Relational.Tests:
  total: 1498
  failed: 0
  succeeded: 1497
  skipped: 1
  duration: 10s 325ms

EFCore.Design.Tests:
  total: 1275
  failed: 5
  succeeded: 1269
  skipped: 1
  duration: 23s 127ms
```
(The 5 `EFCore.Design.Tests` failures are all pre-existing `OperationExecutorTest` path-validation tests — `AddMigration_errors_for_bad_names("A\\B\\C")`, `AddMigration_errors_for_bad_output_dirs("Something:Else")`, etc. — that assume Windows path-character restrictions; confirmed pre-existing by stashing all 3 of this fix round's changed `EFCore.Design`/test files and re-running: identical 5 failures with none of the changes present.)

```
EFCore.Sqlite.FunctionalTests (direct dll invocation, single clean run — see contamination note below):
  total: 38420
  failed: 177
  succeeded: 37957
  skipped: 286
  duration: 6m 12s 163ms
```

**Contamination caveat, disclosed rather than hidden:** my first full-project run overlapped in time with a second one I started before confirming the first had finished (both processes alive simultaneously, both touching shared SQLite test-store files) and reported `failed: 295` — 118 *more* failures than the clean, single run. I killed the second process, ran once more with nothing else touching SQLite state, and got the `177` above; every one of those 177 failure entries names `mod_spatialite.so` in its exception message (`grep -c "mod_spatialite"` = 177, matching `grep -c "^failed"` = 177 exactly), and none references any file this task's B7 or B8 work touched. I'm treating this second, uncontaminated `177` as authoritative and flagging the discrepancy so it isn't silently glossed over.

**The mod_spatialite discrepancy, reconciled:** `mod_spatialite` in this sandbox's NuGet cache (`~/.nuget/packages/mod_spatialite`) ships native binaries only for `runtimes/win-x86` and `runtimes/win-x64` — there is no `linux-arm64` (or any Linux) variant available. `SqliteTestStore`/`SqliteDatabaseCleaner` unconditionally try to load the spatialite extension on *every* connection open (not just spatial-specific tests), so every test that opens a shared SQLite test-store connection through that path fails here with `SQLite Error 1: 'mod_spatialite.so: cannot open shared object file: No such file or directory'` — this is an environment gap (missing native asset for this platform), not something fixable from test/source code in this repo.

The `build.sh --test` run for the same project reports "Tests succeeded", and its generated TRX (`artifacts/TestResults/Debug/EFCore.Sqlite.FunctionalTests_net11.0_arm64.trx`) shows `total="38227" executed="37941" passed="37941" failed="0" notExecuted="286"` — matching the stated baseline numbers *exactly*, including a total 193 lower than the direct-invocation run. I could not fully root-cause why Arcade's VSTest-based test execution doesn't discover (or reports as not-executed) that gap of ~193 tests rather than running and failing them the way the direct Microsoft.Testing.Platform host invocation does, and I'm not going to claim more certainty than I have: **the two invocations disagree, `build.sh`'s aggregate result is not trustworthy evidence for this project in this environment** (it reproduced the exact pre-existing baseline numbers even after this fix round added 2 more passing tests to `CompiledModelSqliteTest`, which it should have reflected and didn't), and I'm relying on the direct dll invocation — where I independently confirmed by name that every one of my 6 new `CompiledModelSqliteTest` tests executes and passes, and that none of the 177 genuine failures touch anything from this task — as the trustworthy source for this task's verification. `build.sh --test` still ran to completion without error for all three touched projects (`EFCore.Design.Tests`, `EFCore.Relational.Tests`, `EFCore.Sqlite.FunctionalTests`) as the required pre-commit gate, per the testing instructions, but its per-test counts should not be read as authoritative for this project on this machine.

### Files changed in this fix round

- `src/EFCore.Design/Migrations/Design/CSharpSnapshotGenerator.cs` (Finding 1)
- `test/EFCore.Design.Tests/Migrations/Design/CSharpMigrationsGeneratorTest.ModelSnapshot.cs` (Finding 1 — new test, updated `expectedCode` fragments for the two pre-existing tests)
- `test/EFCore.Relational.Tests/Metadata/RelationalKeyOverridesTest.cs` (Finding 2 — in-memory path, keys)
- `test/EFCore.Relational.Tests/Metadata/RelationalForeignKeyOverridesTest.cs` (Finding 2 — in-memory path, FKs)
- `test/EFCore.Relational.Specification.Tests/Scaffolding/CompiledModelRelationalTestBase.cs` (Finding 2 — NativeAOT codegen path, both keys and FKs)
- `test/EFCore.Sqlite.FunctionalTests/Scaffolding/Baselines/Explicit_null_key_constraint_name_override_survives_the_compiled_model/*`, `.../Explicit_null_foreign_key_constraint_name_override_survives_the_compiled_model/*` (new baseline source, `EF_TEST_REWRITE_BASELINES=1`, reviewed by hand)

Commits: `957d85edf9` (Finding 1), `e3111b76ff` (Finding 2).
