# Final polish report -- Fix A and Fix B

Branch: `worktree-feature+per-store-object-constraint-names`, based on `18c706c6a0`.

## Fix A -- order-independent Fix 2 validator test

**File:** `test/EFCore.Relational.Tests/Metadata/RelationalForeignKeyOverridesTest.cs`,
test `Conflicting_overrides_are_rejected_even_when_the_first_candidate_is_incompatible`.

### Approach chosen: explicit order assertion (the task's second option)

I first worked through whether the first option -- setting conflicting per-store-object
overrides on all three foreign keys so the defect is reachable "regardless of which lands
first" -- can be made to hold for *every* possible enumeration order with only three
structurally-identical foreign keys. It cannot, and the reason is structural, not just a
matter of picking better override values:

The pre-fix algorithm keeps a single `existing` candidate per structure (whichever foreign
key was recorded first) and compares every later foreign key only against that one. With
three foreign keys A, B, C, whichever one lands first gets directly compared against *both*
of the other two. For the pre-fix bug to be reproduced, the first-recorded one must be
incompatible with both others (so those two direct comparisons are skipped), and the real
conflict must live in the pair that never gets compared -- the two non-first ones. But
compatibility is a fixed, symmetric property of the foreign keys themselves, not something
that changes with enumeration order. So whichever candidate is "incompatible with the other
two" is fixed by the model, and if a *different* foreign key ends up first in some other
enumeration order, the pre-fix code's direct comparisons against *that* first element will
include the "real conflict" pair directly (since it's now being compared against, not
skipped), and the exception fires anyway -- even under the pre-fix implementation. That
means for any fixed compatibility/override topology among exactly three foreign keys, there
is always at least one enumeration order under which the pre-fix code *also* throws,
indistinguishable from the fixed code. Order-invariant detection via override topology alone
is not achievable at this arity without controlling the order.

Given that, I chose the second option: anchor the premise explicitly rather than trying to
engineer around it. The test now replicates the validator's own foreign-key enumeration
(`model.GetEntityTypes()` order, `SelectMany(GetDeclaredForeignKeys)`, filtered to the FKs
pointing at `SharedCustomer`) and asserts `Assert.Same(orderFk, orderedCustomerForeignKeys[0])`
*before* the existing pairwise `AreCompatible` sanity checks and the throw assertion. The
comment explains *why* `SharedOrder` is first today: `Model` stores entity types in a
`SortedDictionary<string, EntityType>` keyed by full CLR name, ordinal comparer (confirmed by
reading `src/EFCore/Metadata/Internal/Model.cs:27`), and `"SharedOrder"` is a strict prefix of
`"SharedOrderDetails"`/`"SharedOrderExtra"`, so it sorts first -- an incidental consequence of
these classes' names, not a promise the validator makes. If a future rename, an added entity
type, or a change to `Model`'s enumeration ever shifts that order, this assertion now fails
loudly at that exact line, telling the next engineer the premise no longer holds, instead of
the test silently continuing to pass while it stops exercising the first-candidate-incompatible
scenario.

### Confirming the test still fails against the pre-fix validator

Verified by temporary revert, not just static reasoning:

1. Confirmed `141fabd84e` ("Fix 2: retain every candidate...") is the only commit touching
   `RelationalModelValidator.cs` since it was introduced, and no commit since then touches it
   either (`git log --oneline 141fabd84e..HEAD -- src/EFCore.Relational/Infrastructure/RelationalModelValidator.cs`
   returned nothing), so `141fabd84e^` is exactly "pre-fix" relative to current HEAD.
2. `git show 141fabd84e^:src/EFCore.Relational/Infrastructure/RelationalModelValidator.cs` into
   a scratch file, saved a copy of the current (post-fix) file, then overwrote the working-tree
   file with the pre-fix content.
3. Rebuilt `EFCore.Relational.Tests` and ran the filtered test:
   - `total: 16 / failed: 1 / succeeded: 15` -- the single failure was exactly
     `Conflicting_overrides_are_rejected_even_when_the_first_candidate_is_incompatible`
     (`Assert.Throws() Failure: No exception was thrown`). No other test in the class regressed,
     confirming the new order assertion doesn't itself trip on the pre-fix code path (it can't --
     the order assertion is about enumeration order, which the pre-fix code doesn't change).
4. Restored the post-fix file from the saved copy (`git diff --stat` on that file showed no
   changes afterward, confirming a byte-identical restore).
5. Rebuilt and reran: `total: 16 / failed: 0 / succeeded: 16`.

## Fix B -- `GetForeignKeyExpression`'s ownership branch

**File:** `src/EFCore.Design/Migrations/Design/CSharpSnapshotGenerator.cs`.

### Part 1 -- corrected the XML remark

The old `<summary>` claimed the returned expression was "equivalent to... `HasOne/WithOwner`
followed by `WithOne/WithMany` and `HasForeignKey`, plus `HasPrincipalKey`" for *all* foreign
keys, including ownership ones. That's false for ownership: `GenerateForeignKey`'s own
statement (the `else` branch around lines 1843-1871) still writes `.HasForeignKey(...)` and
conditionally `.HasPrincipalKey(...)` for ownership foreign keys too -- only the `WithMany(...)`
call is skipped when `IsOwnership` is true. The ownership branch of `GetForeignKeyExpression`
returns only `entityTypeBuilderName.WithOwner(nav)`, omitting both.

I did not change the emission (per the instructions, only "fix" it if the short form can
genuinely misresolve). I found no such case: an owned entity type has exactly one ownership
relationship by construction -- that's what makes it "owned." Within the
`entityTypeBuilderName` scope (already the specific owned entity type's builder),
`WithOwner(...)` has only one foreign key it can possibly resolve to; there's no second
ownership foreign key on the same builder for `HasForeignKey(...)`/`HasPrincipalKey(...)` to
disambiguate between. I added a new test (below) constructing the closest thing to a
counter-example -- two foreign keys from the same dependent entity type to the same principal
type -- and it passed against the existing (short) emission, corroborating this.

Rewrote the `<summary>` to describe the two branches separately (non-ownership reproduces the
full chain; ownership reproduces only `WithOwner(...)`, deliberately) and added a `<remarks>`
`<para>` explaining why the shorter ownership form is still sufficient (uniqueness of the
ownership relationship per owned entity type). Wrapping the pre-existing remark content in a
matching `<para>` was required too -- StyleCop's `DOC101` ("use child blocks consistently")
failed the build otherwise once a `<para>` was introduced anywhere in the `<remarks>` block;
fixed by wrapping the original paragraph as well. Confirmed via
`.dotnet/dotnet build src/EFCore.Design/EFCore.Design.csproj` (0 errors after the fix; 1 DOC101
error before).

### Part 2 -- new tests

Both added to `test/EFCore.Design.Tests/Migrations/Design/CSharpMigrationsGeneratorTest.ModelSnapshot.cs`
via the `CSharpMigrationsGeneratorTestBase.Test(...)` round-trip harness (build model -> generate
snapshot source -> compile it -> assert against the recompiled model -> assert the generated
source contains the expected fragment).

1. **`Snapshot_round_trips_annotation_only_foreign_key_override_on_ownership_foreign_key`** --
   an owned type (`OverridesCustomerWithAddress.Address`, split into its own table) with a
   `HasOverrides(...)` carrying only `.HasAnnotation("Test:Comment", "note")` and no name, on the
   ownership foreign key. Asserts `IsOwnership` is true, the override round-trips with
   `IsNameOverridden == false`, and the annotation value survives. This is the first coverage of
   an annotation-only override reaching `GetForeignKeyExpression`'s ownership (`WithOwner`-only)
   branch -- the existing Fix 6 annotation-only tests only covered non-ownership foreign keys, and
   the existing ownership-override test (Review Fix 1) only covered a *named* override, which takes
   a different code path (`GenerateForeignKeyOverrides`, chained off the main statement's builder,
   not `GetForeignKeyExpression`).

2. **`Snapshot_round_trips_foreign_key_override_when_two_foreign_keys_share_dependent_and_principal_types`**
   -- new fixture `OverridesOrderWithTwoCustomerLinks` with `BillingCustomerId` and
   `ShippingCustomerId`, each configured via `HasOne<OverridesCustomer>().WithMany().HasForeignKey(...)`
   with **no navigation on either side**, so the two relationships are distinguished only by their
   FK properties -- the sharpest version of "two foreign keys from the same dependent entity type to
   the same principal entity type" reachable through the fluent API. Only the shipping link gets a
   name override and an annotation-only override. After the round trip, asserts there are still
   exactly two foreign keys, the shipping one carries the name (`"fk_orders_customers_shipping"`)
   and the annotation (`"shipping-note"`), and the billing one has no overrides at all.

   **This case is constructible** and reaches the code path: the test passes, meaning
   `GetForeignKeyExpression`'s recomputed `.HasOne("Customer", null).WithMany(null).HasForeignKey(...)`
   chain correctly re-resolves to the shipping foreign key specifically (disambiguated by its FK
   property list in the final `.HasForeignKey(...)` call) and does not bind the override to the
   billing one or create a spurious third foreign key. No defect found -- this was the scenario
   that, per the task, would have needed to "stop and report" rather than be silently fixed, and it
   didn't arise.

## Test commands and raw counts

All counts are from direct DLL invocation (primary evidence per the testing rules), not
`build.sh`.

```
.dotnet/dotnet build test/EFCore.Relational.Tests/EFCore.Relational.Tests.csproj -v q --nologo
.dotnet/dotnet artifacts/bin/EFCore.Relational.Tests/Debug/net11.0/Microsoft.EntityFrameworkCore.Relational.Tests.dll --filter-class '*RelationalForeignKeyOverridesTest*'
```
Expected: 16 total (no new tests added by Fix A, only an existing test body changed).
Actual (post-fix validator): `total: 16 / failed: 0 / succeeded: 16 / skipped: 0`.
Actual (pre-fix validator, temporary revert): `total: 16 / failed: 1 / succeeded: 15 / skipped: 0`
-- the one failure was the target test, confirming it still fails against pre-fix code.

```
.dotnet/dotnet build test/EFCore.Design.Tests/EFCore.Design.Tests.csproj -v q --nologo
.dotnet/dotnet artifacts/bin/EFCore.Design.Tests/Debug/net11.0/Microsoft.EntityFrameworkCore.Design.Tests.dll --filter-class '*CSharpMigrationsGeneratorTest*'
```
Actual: `total: 179 / failed: 0 / succeeded: 179 / skipped: 0`. Also ran both new tests by
exact name individually (`--filter-method`) to confirm the filter matched them (not silently
0-matched): each reported `total: 1 / failed: 0 / succeeded: 1`.

Full suite runs (direct DLL, no filter), before committing:

```
.dotnet/dotnet artifacts/bin/EFCore.Relational.Tests/Debug/net11.0/Microsoft.EntityFrameworkCore.Relational.Tests.dll
```
Expected: 1509 total (baseline, unchanged -- Fix A added no new test). Actual:
`total: 1509 / failed: 0 / succeeded: 1508 / skipped: 1`. Matches baseline exactly.

```
.dotnet/dotnet artifacts/bin/EFCore.Design.Tests/Debug/net11.0/Microsoft.EntityFrameworkCore.Design.Tests.dll
```
Expected: 1283 total (baseline 1281 + 2 new Fix B tests), failures still capped at the 5
pre-existing `OperationExecutorTest` Windows-path failures (must not grow). Actual:
`total: 1283 / failed: 5 / succeeded: 1277 / skipped: 1`. Count moved by exactly +2 as expected;
failures unchanged at 5, all in `OperationExecutorTest.cs` (untouched by this work).

```
.dotnet/dotnet build test/EFCore.ApiBaseline.Tests/EFCore.ApiBaseline.Tests.csproj -v q --nologo
.dotnet/dotnet artifacts/bin/EFCore.ApiBaseline.Tests/Debug/net11.0/Microsoft.EntityFrameworkCore.ApiBaseline.Tests.dll
```
Expected: 14/14 (no public API surface changed -- Fix B only touched a `protected virtual`
method's doc comments, no signature change). Actual: `total: 14 / failed: 0 / succeeded: 14 / skipped: 0`.

`build.sh --test` was not run: the Sqlite test suite is explicitly out of scope for this box
(~38,000 tests, environmental `mod_spatialite` failures), and the direct-DLL runs above already
satisfy "run all three" with fresh, moving, and expected-matching counts -- the strongest
available evidence per the stated testing rules.
