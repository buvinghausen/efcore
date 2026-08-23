# Task B2: Key name resolution seam — Report

## What was implemented

In `src/EFCore.Relational/Metadata/Internal/RelationalKeyExtensions.cs`, `GetName(key, storeObject, logger)` now
resolves key constraint names through a local `ResolveName` function instead of a raw
`(string?)key[RelationalAnnotationNames.Name] ?? defaultName` expression. Both occurrences (the
mapping-fragment branch and the derived-type branch) were replaced. Control flow around them
(the `fragment != null` check, the `GetDerivedTypesInclusive` loop, the final `return null`) was
left untouched, as instructed.

Precedence implemented in `ResolveName`, exactly as specified:

1. Per-store-object override (`RelationalKeyOverrides.Find(key, storeObject)`), when
   `IsNameOverridden` is true — including when the override's `Name` is explicitly `null`, in
   which case it falls to the **default** name (`?? defaultName`), not to the global annotation.
2. Otherwise, the global `Relational:Name` annotation.
3. Otherwise, `defaultName`.

`storeObject` is passed explicitly as an `in` parameter to `ResolveName` rather than captured by
the closure, per the brief's note that a local function can't take `in` while closing over the
same variable.

No new public/internal signatures were added — `GetName`'s signature is unchanged, and
`ResolveName` is a private local function, not a new API surface.

## Test-project corrections applied (per dispatch)

- `RelationalTestHelpers.Instance` → `FakeRelationalTestHelpers.Instance` (the abstract type lives
  in a different test project).
- `[ConditionalFact]` → `[Fact]` (matches Task B1's tests in the same file; no `ConditionalFact`
  usage anywhere in this test project).

## One additional correction, discovered during RED

The brief's `Customer` fixture (from Task B1) has only `Id` and `Name`. The third test
(`Per_table_key_names_flow_into_the_relational_model`) calls `modelBuilder.FinalizeModel()`,
which runs `RelationalModelValidator`. With `Name` moved to `CustomerDetails` via
`SplitToTable`, the main table `Customers` was left with only the primary key column, which
`RelationalModelValidator.ValidateMappingFragment` rejects:

> `Entity type 'Customer' has a split mapping, but it doesn't map any non-primary key property to
> the main store object.`

This is a genuine gap in the brief's test setup, not something the dispatch's two named
corrections covered — confirmed by seeing the exact same
"main table needs its own mapped property" pattern used deliberately in
`test/EFCore.Relational.Tests/Metadata/RelationalModelTest.cs`
(`Can_use_relational_model_with_entity_splitting_and_table_splitting_on_both_fragments`, which
explicitly maps a property to the main table alongside a `SplitToTable` split).

Fix: added one property to the shared `Customer` fixture that stays on the main table by default:

```csharp
private class Customer
{
    public int Id { get; set; }
    public string Name { get; set; } = null!;

    // Split-table tests move Name to a secondary table; this stays on the main table so the
    // main store object keeps at least one non-key property, as required by validation.
    public string Address { get; set; } = null!;
}
```

This is additive only — Task B1's three existing tests reference only `Id` and `Name` and are
unaffected (verified: full suite green, no regressions). All three of the brief's tests keep
their bodies verbatim; only the shared fixture gained one field.

## Files changed

- `src/EFCore.Relational/Metadata/Internal/RelationalKeyExtensions.cs` — `ResolveName` local
  function added, both name-resolution expressions in `GetName` updated to call it.
- `test/EFCore.Relational.Tests/Metadata/RelationalKeyOverridesTest.cs` — three new tests appended
  (verbatim from the brief, with the two dispatch-directed substitutions), plus the one-field
  `Customer` fixture addition described above.
- `src/EFCore.Relational/EFCore.Relational.baseline.json` — **not** touched, per instructions.

## TDD evidence

### RED

Command:

```
./build.sh --test --projects /home/buvy/code/buvinghausen/efcore/.claude/worktrees/feature+per-store-object-constraint-names/test/EFCore.Relational.Tests/EFCore.Relational.Tests.csproj
```

Run against the three new tests with the **old** (pre-fix) `RelationalKeyExtensions.GetName`
implementation still compiled (this run was started before the source edit landed). Result:
`Build FAILED`, 1 error — 3 test failures reported:

```
Microsoft.EntityFrameworkCore.Metadata.RelationalKeyOverridesTest.Explicit_null_key_name_override_falls_back_to_the_default_name
Assert.Equal() Failure: Strings differ
Expected: "PK_CustomerDetails"
Actual:   "pk_global_rewrite"

Microsoft.EntityFrameworkCore.Metadata.RelationalKeyOverridesTest.Key_name_override_beats_the_global_rewritten_name_per_table
Assert.Equal() Failure: Strings differ
Expected: "pk_customers"
Actual:   "pk_global_rewrite"

Microsoft.EntityFrameworkCore.Metadata.RelationalKeyOverridesTest.Per_table_key_names_flow_into_the_relational_model
System.InvalidOperationException : Entity type 'Customer' has a split mapping, but it doesn't map
any non-primary key property to the main store object. Keep at least one non-primary key property
mapped to a column on 'Customers'.
```

The first two failures are exactly the expected RED: without override-awareness, `GetName`
returns the global rewritten name (`pk_global_rewrite`) for both tables instead of the per-table
override or (for the null-override case) the default `PK_CustomerDetails`. The third failure is
the fixture gap described above, not a resolution-logic failure — it was fixed by adding the
`Address` property before re-running, and re-verified below.

### GREEN

After applying the `ResolveName` implementation and the `Address` fixture fix, same command:

```
./build.sh --test --projects /home/buvy/code/buvinghausen/efcore/.claude/worktrees/feature+per-store-object-constraint-names/test/EFCore.Relational.Tests/EFCore.Relational.Tests.csproj
```

Result:

```
Tests succeeded: .../Microsoft.EntityFrameworkCore.Relational.Tests.dll [net11.0|arm64]
Build succeeded.
    0 Warning(s)
    0 Error(s)
```

Test-results summary: `Tests run: 1488 — Skipped: 1` (the pre-existing `#28703` skip,
unrelated to this task), 0 failed. That is 1487 passed = the baseline 1484 + the 3 new tests.
No new warnings.

## Self-review

- **Completeness:** both `(string?)key[RelationalAnnotationNames.Name] ?? defaultName`
  occurrences replaced with `ResolveName(...)`. All three tests from the brief are present and
  assert exactly what the brief specifies (verbatim bodies, only the two dispatch-directed
  substitutions applied).
- **Quality:** precedence chain is override → global → default; an override with `IsNameOverridden
  == true` and `Name == null` returns `defaultName`, not the global annotation — confirmed both
  by reading the code and by the passing
  `Explicit_null_key_name_override_falls_back_to_the_default_name` test.
- **Discipline:** no new public/internal signatures added; `ResolveName` is a private local
  function inside `GetName`. No `GetOverrides`/`RemoveOverrides` extensions added. Baseline JSON
  untouched. Control flow outside the two name-resolution expressions is byte-for-byte unchanged.
- **Testing:** all three tests exercise real behavior — direct `key.GetName(storeObject)`
  precedence (tests 1–2) and the differ-facing `RelationalModel` construction (test 3), which is
  exactly Spike 2 finding 2 turned into a regression test. Output is pristine: 0 warnings,
  0 errors, no new skips.

## Concerns

None. The one deviation from the brief's literal text (adding `Address` to the `Customer`
fixture) was necessary for the brief's own third test to be buildable/finalizable, is minimal and
additive, doesn't touch Task B1's existing tests, and matches an established pattern already used
elsewhere in the same test project for the identical split-table validation requirement.
