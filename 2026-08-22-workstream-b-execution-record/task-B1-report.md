# Task B1 Report: Key override metadata family

## What was implemented

Mirrored the existing `RelationalPropertyOverrides` family (per-store-object *column* name
overrides on `IProperty`) to a new `RelationalKeyOverrides` family (per-store-object *key
constraint name* overrides on `IKey`), applying the brief's substitution table
(`Property`→`Key`, `ColumnName`→`Name`, etc.) mechanically.

Files created:
- `src/EFCore.Relational/Metadata/IReadOnlyRelationalKeyOverrides.cs`
- `src/EFCore.Relational/Metadata/IMutableRelationalKeyOverrides.cs`
- `src/EFCore.Relational/Metadata/IConventionRelationalKeyOverrides.cs`
- `src/EFCore.Relational/Metadata/IRelationalKeyOverrides.cs`
- `src/EFCore.Relational/Metadata/Internal/RelationalKeyOverrides.cs`
- `src/EFCore.Relational/Metadata/Builders/IConventionRelationalKeyOverridesBuilder.cs`
- `src/EFCore.Relational/Metadata/Internal/InternalRelationalKeyOverridesBuilder.cs`
- `test/EFCore.Relational.Tests/Metadata/RelationalKeyOverridesTest.cs`

Files modified:
- `src/EFCore.Relational/Metadata/RelationalAnnotationNames.cs` — added
  `RelationalAnnotationNames.KeyOverrides = Prefix + "KeyOverrides"` next to
  `RelationalOverrides`, and added `KeyOverrides` to the `AllNames` array.
- `test/EFCore.Relational.Tests/RelationalApiConsistencyTest.cs` — per the controller's second
  decision, added an `IReadOnlyRelationalKeyOverrides` entry to `_metadataTypes` (mirroring the
  `IReadOnlyRelationalPropertyOverrides` entry, with `null!` for the ConventionBuilder slot) and
  added `typeof(IRelationalKeyOverrides)` to `RelationalMetadataTypes`.

Applied the three deliberate differences from the property precedent, per the brief:
1. Annotation read/write uses `RelationalAnnotationNames.KeyOverrides` (not `RelationalOverrides`).
2. Constructor's model access goes through the key:
   `((IConventionModel)key.DeclaringEntityType.Model).Builder`.
3. `IsReadOnly` delegates to the key: `((Annotatable)Key).IsReadOnly`.

Did **not** add `GetOverrides`/`RemoveOverrides` extension methods (per the controller's first
decision — those arrive in a later task that will call `RelationalKeyOverrides.Get`/`.Remove`
directly).

## Deviations from the brief's verbatim text (environment drift, not scope changes)

The brief's Step 1 test body, copied verbatim, did not compile against this worktree's actual
test infrastructure — two pre-existing environment facts, unrelated to my implementation:

1. **`RelationalTestHelpers.Instance` does not exist.** `RelationalTestHelpers` in this repo is
   `abstract` (see `test/EFCore.Relational.Specification.Tests/TestUtilities/RelationalTestHelpers.cs`).
   The concrete instance used throughout `test/EFCore.Relational.Tests/Metadata/*.cs` is
   `FakeRelationalTestHelpers.Instance` (e.g. `DbFunctionTest.cs`,
   `RelationalEntityTypeAttributeConventionTest.cs`). I substituted
   `FakeRelationalTestHelpers.Instance` for `RelationalTestHelpers.Instance` in all three test
   methods.
2. **`[ConditionalFact]` (parameterless) is obsolete in this repo's xunit v3 upgrade.** The
   package `Microsoft.DotNet.XUnitV3Extensions` only exposes a `ConditionalFactAttribute`
   overload taking a `Type` parameter now; the parameterless usage triggers CS0618 as an error
   (warnings-as-errors). A repo-wide search found **zero** other files using bare
   `[ConditionalFact]`; every test in this project and its siblings uses plain `[Fact]`. I
   replaced `[ConditionalFact]` with `[Fact]` on all three test methods to match the
   codebase-wide convention.
3. Since `RelationalKeyOverrides` lives in `Microsoft.EntityFrameworkCore.Metadata.Internal`
   (matching the precedent `RelationalPropertyOverrides`), and the test file's namespace is
   `Microsoft.EntityFrameworkCore.Metadata` with no global `Metadata.Internal` using in this test
   project, I added `using Microsoft.EntityFrameworkCore.Metadata.Internal;` at the top of the
   test file — the same pattern already used by sibling files in the same directory (e.g.
   `RelationalModelTest.cs`, `SequenceTest.cs`).

None of these changes altered the test's assertions, structure, or intent — only the syntax
needed to make the verbatim scenario compile and run against this worktree's actual toolchain.

One additional adaptation inside `RelationalKeyOverrides.cs` itself: the precedent's `Builder`
getter's exception message uses `Property.Name` (a simple string on `IReadOnlyProperty`).
`IReadOnlyKey` has no analogous `.Name` member (keys are identified by their constituent
properties, not a single name). I used `Key.Properties.Format()` — the existing
`PropertyBaseExtensions.Format()` extension, whose own doc comment says it exists "such as is
useful when throwing exceptions about keys, indexes, etc. that use the properties" — which is
exactly this use case and avoids any coupling to `RelationalKeyExtensions.GetName()` (which is
resolution-seam machinery a later task owns).

## TDD evidence

**RED** — before creating any of the new production types, ran:

```
./build.sh --test --projects /home/buvy/code/buvinghausen/efcore/.claude/worktrees/feature+per-store-object-constraint-names/test/EFCore.Relational.Tests/EFCore.Relational.Tests.csproj
```

Relevant failing output (compile errors, as expected — `RelationalKeyOverrides` did not yet
exist):

```
.../RelationalKeyOverridesTest.cs(18,21): error CS0103: The name 'RelationalKeyOverrides' does not exist in the current context
.../RelationalKeyOverridesTest.cs(20,25): error CS0103: The name 'RelationalKeyOverrides' does not exist in the current context
... (10 total CS0103 errors, one per RelationalKeyOverrides.* call site)
Build FAILED.
```

(The first RED run also showed CS0618 on `[ConditionalFact]` and CS0117 on
`RelationalTestHelpers.Instance` — the environment-drift issues described above, fixed alongside
the main implementation before the GREEN run.)

**GREEN** — after creating the interface quartet, `RelationalKeyOverrides`, the builder pair, the
annotation name, and the test-syntax fixes, ran the same command:

```
./build.sh --test --projects /home/buvy/code/buvinghausen/efcore/.claude/worktrees/feature+per-store-object-constraint-names/test/EFCore.Relational.Tests/EFCore.Relational.Tests.csproj
```

Output:

```
Tests succeeded: .../Microsoft.EntityFrameworkCore.Relational.Tests.dll [net11.0|arm64]
Build succeeded.
    0 Warning(s)
    0 Error(s)
```

Confirmed via the `.trx` results file (`artifacts/TestResults/Debug/EFCore.Relational.Tests_net11.0_arm64.trx`):
- `RelationalKeyOverridesTest.Key_overrides_are_stored_per_store_object_with_configuration_sources` — Passed
- `RelationalKeyOverridesTest.Removing_an_override_detaches_it_from_the_model` — Passed
- `RelationalKeyOverridesTest.Explicit_null_name_override_is_distinguishable_from_unset` — Passed
- Suite totals: `total="1485" executed="1484" passed="1484" failed="0" error="0"` (the one
  "not executed" is a pre-existing skip unrelated to this change, present in the baseline).

Output is pristine: 0 warnings, 0 errors, no new skips.

## Files changed

- `src/EFCore.Relational/Metadata/RelationalAnnotationNames.cs` (modified)
- `src/EFCore.Relational/Metadata/IReadOnlyRelationalKeyOverrides.cs` (new)
- `src/EFCore.Relational/Metadata/IMutableRelationalKeyOverrides.cs` (new)
- `src/EFCore.Relational/Metadata/IConventionRelationalKeyOverrides.cs` (new)
- `src/EFCore.Relational/Metadata/IRelationalKeyOverrides.cs` (new)
- `src/EFCore.Relational/Metadata/Internal/RelationalKeyOverrides.cs` (new)
- `src/EFCore.Relational/Metadata/Builders/IConventionRelationalKeyOverridesBuilder.cs` (new)
- `src/EFCore.Relational/Metadata/Internal/InternalRelationalKeyOverridesBuilder.cs` (new)
- `test/EFCore.Relational.Tests/Metadata/RelationalKeyOverridesTest.cs` (new)
- `test/EFCore.Relational.Tests/RelationalApiConsistencyTest.cs` (modified)

Commit: `9e42b97b55` — "Add the per-store-object key constraint name override family"

`src/EFCore.Relational/EFCore.Relational.baseline.json` was deliberately **not** touched, per
instructions (a later task owns the API baseline in one pass).

## Self-review findings

- **Completeness:** every member in the brief's **Produces** list is present:
  `RelationalAnnotationNames.KeyOverrides`, `RelationalKeyOverrides.Find`, `.Get`, `.GetOrCreate`,
  `.Remove` (static), and instance members `StoreObject`, `Name`, `SetName`, `IsNameOverridden`,
  `GetNameConfigurationSource`. `Key`, `Builder`, `IsInModel`, `SetRemovedFromModel`, `Attach`,
  `MergeInto`, `GetConfigurationSource`, `UpdateConfigurationSource`, `ToString`, `DebugView`,
  and the explicit interface implementations were carried over as part of mirroring the
  precedent's full structure (matching how `RelationalPropertyOverrides` itself is structured,
  since the brief says "this task is that file's structure").
- **Quality:** XML doc `<summary>` text was rewritten from "column name" to "key constraint name"
  everywhere in the four public interfaces and their members (`IReadOnlyRelationalKeyOverrides`,
  `IMutableRelationalKeyOverrides`, `IConventionRelationalKeyOverrides`,
  `IRelationalKeyOverrides`). Internal-API members keep the standard internal-API boilerplate
  summary, matching the precedent exactly.
- **Discipline:** did not add `GetOverrides`/`RemoveOverrides` extension methods (explicitly
  excluded by the controller). Did not touch the baseline JSON. Did not restructure anything
  outside this task's file list plus the API-consistency-fixture files the controller called out.
- **Testing:** followed the brief's exact TDD step order (write failing test → confirm RED →
  implement → confirm GREEN). The test bodies are unchanged in substance from the brief — only
  syntax fixes for `FakeRelationalTestHelpers.Instance`, `[Fact]`, and the `Metadata.Internal`
  using were applied, as detailed above. Verified via the `.trx` file that all three new tests
  actually ran and passed (not skipped), and that the full 1484-test suite is green with 0
  warnings.

## Concerns

None outstanding. The three environment-drift fixes to the test file's syntax (test-helper class
name, attribute, and using directive) are mechanical and match established sibling-file
conventions in the same directory; none altered what the tests assert or how they exercise the
new type. I'm confident in the correctness of the `Key.Properties.Format()` substitution for the
`Builder` exception message, since it's an existing extension whose doc comment names this exact
use case.
