# Fix report: six verified defects on the per-store-object constraint-name branch

Branch: `worktree-feature+per-store-object-constraint-names`, started at `7d3a0a1a49`.
All fixes committed as follow-up commits (see `git log`); nothing pushed, nothing
touching the PR itself.

Evidence rule followed throughout: direct-dll `dotnet exec ... .dll --filter-...`
output is the primary evidence. `build.sh` was **not** run — running it unscoped
risks pulling in the excluded ~38,000-test Sqlite suite, which the brief explicitly
says not to run, so I relied on the three named suites' direct-dll runs instead,
which is the primary evidence tier anyway.

For every fix I stashed just that fix's source file(s) (keeping the new test in
place), rebuilt, and ran the new test to confirm it failed against the pre-fix
code, then restored the fix and confirmed it passed. Commands and raw counters
below.

---

## Fix 1 — `CanSetName`/`CanSetConstraintName` compare stored override, not resolved name

**Files:** `src/EFCore.Relational/Extensions/RelationalKeyBuilderExtensions.cs`,
`src/EFCore.Relational/Extensions/RelationalForeignKeyBuilderExtensions.cs`

**Change:** `CanSetName`/`CanSetConstraintName` now check
`configurationSource.Overrides(...)` first; if that fails, they look up the
existing override via `RelationalKeyOverrides.Find`/`RelationalForeignKeyOverrides.Find`
and compare the proposed name against the override's **stored** `Name`, not the
resolved `GetName(storeObject)`/`GetConstraintName(storeObject, principal)`. When
no override exists at all, the resolved-name comparison is used (nothing to
compare against).

**Reproduced before fix:** Yes. Added
`Explicit_null_key_name_override_refuses_a_convention_proposing_the_default_name`
and the FK-side equivalent. Ran them against the pre-fix source (stashed the two
source files):

```
.dotnet/dotnet artifacts/bin/EFCore.Relational.Tests/Debug/net11.0/Microsoft.EntityFrameworkCore.Relational.Tests.dll \
  --filter-method '*Explicit_null_key_name_override_refuses_a_convention_proposing_the_default_name' \
  --filter-method '*Explicit_null_foreign_key_constraint_name_override_refuses_a_convention_proposing_the_default_name'
```
Result: `total: 2  failed: 2  succeeded: 0` — both `Assert.Null()` failures
(`HasName`/`HasConstraintName` returned a non-null builder instead of being
refused).

**After fix:** same filter → `total: 2  failed: 0  succeeded: 2`.
Full class run (`*RelationalKeyOverridesTest` + `*RelationalForeignKeyOverridesTest`):
`total: 21  failed: 0  succeeded: 21` (baseline 19 + 2 new).

## Fix 2 — validator retains only the first candidate per structural bucket

**File:** `src/EFCore.Relational/Infrastructure/RelationalModelValidator.cs`

**Change:** `ValidateSharedForeignKeyNameOverrides`'s `namesByStructure` is now
`Dictionary<ConstraintStructure, List<(IForeignKey, string)>>` instead of a single
tuple per structure. Each new FK is compared against **every** prior candidate
recorded for that structure, not just the first, before being appended to the
list. **Approach chosen:** retain every candidate and compare pairwise (over the
alternative of a second grouping key by compatibility class) — it's the smaller
diff, keeps the single structural key, and directly fixes the reported gap
(comparing only against one retained "existing" entry) without introducing a new
concept.

**Reproduced before fix:** Yes. Added
`Conflicting_overrides_are_rejected_even_when_the_first_candidate_is_incompatible`
(three structurally identical FKs sharing a table; the first differs in delete
behavior from the second and third, which carry conflicting per-store-object
overrides). Against pre-fix source:
```
total: 1  failed: 1  succeeded: 0
```
`Assert.Throws()` failure — no exception was thrown; the conflict between the
second and third FKs was never checked because both were skipped as incompatible
against the first.

**After fix:** `total: 1  failed: 0  succeeded: 1`.
`*RelationalForeignKeyOverridesTest`+`*RelationalKeyOverridesTest`:
`total: 22  failed: 0  succeeded: 22`. Full `*RelationalModelValidatorTest`:
`total: 439  failed: 0  succeeded: 439` (including
`Differing_global_constraint_names_on_a_deduplicated_constraint_are_not_rejected`,
which stays green — the override-presence gate inside the loop is untouched).

## Fix 3 — reattach conventions ignore `oldAnnotation`

**Files:** `src/EFCore.Relational/Metadata/Conventions/KeyOverridesConvention.cs`,
`src/EFCore.Relational/Metadata/Conventions/ForeignKeyOverridesConvention.cs`

**Change:** After the existing back-pointer-fixup loop (which reattaches entries
present in the *new*, incoming set), both conventions now also walk
`oldAnnotation.Value` (cast to `IReadOnlyStoreObjectDictionary<IConventionRelationalKeyOverrides>`
/ `IReadOnlyStoreObjectPairDictionary<IConventionRelationalForeignKeyOverrides>`)
and re-add via `RelationalKeyOverrides.Attach`/`RelationalForeignKeyOverrides.Attach`
any entry for a store object (pair) **not already present** in the current set.

**Precedence rule adopted:** on a same-store-object collision, the incoming
(already-reattached) entry wins — it's what `MergeAnnotationsFrom` just wrote and
reflects the caller's intent for the attach. This is implemented simply by
checking `Find(key, storeObject) == null` before restoring the old entry: if the
incoming set already has that store object, the old one is skipped entirely.

**Reproduced before fix:** Yes. Added
`Key_overrides_survive_when_attached_onto_an_existing_key_with_its_own_overrides`
and the FK-side equivalent, both first confirming (via `Assert.Same`) that the
target key/FK really is *reused* (not recreated) in the reattach scenario, then
asserting both the reused target's own pre-existing override and the incoming
detached override (at different store objects) survive. Against pre-fix source:
```
total: 1  failed: 1  succeeded: 0
```
`NullReferenceException` on `RelationalKeyOverrides.Find(newKey, fromRoot)!.Name`
(FK side: same on `RelationalForeignKeyOverrides.Find(newFk, fromRootPair)!.Name`)
— the reused target's own override was gone.

**After fix:** `total: 1  failed: 0  succeeded: 1` (each). Full
`*RelationalKeyOverridesTest`+`*RelationalForeignKeyOverridesTest`:
`total: 24  failed: 0  succeeded: 24`.

## Fix 4 — linked-FK traversal ignores per-store-object overrides

**File:** `src/EFCore.Relational/Metadata/Internal/RelationalForeignKeyExtensions.cs`
(`GetDefaultName`'s shared-table traversal)

**Change:** Before checking `otherForeignKey.FindAnnotation(RelationalAnnotationNames.Name)`,
the traversal now checks `RelationalForeignKeyOverrides.Find(otherForeignKey, new StoreObjectPair(storeObject, principalStoreObject))`.
If `IsNameOverridden: true` and `Name` is non-null, that name is returned
immediately (mirroring the global-annotation propagation just below it). An
override present but explicitly null is treated as "no forcing name from this
link" and falls through to the annotation check and then the traversal, per the
explicit-null-means-default semantic used everywhere else in this feature.

**Reproduced before fix:** Yes. Added
`Foreign_key_override_on_one_fragment_propagates_to_the_linked_foreign_key`
(two entity types sharing a table via a table-splitting identifying relationship,
each independently declaring a structurally-identical FK to the same principal;
override set on only one). Against pre-fix source:
```
total: 1  failed: 1  succeeded: 0
```
`Assert.Equal` failure: expected `"fk_shared"`, actual `"FK_Orders_Customers_CustomerId"`
— the linked FK resolved its own independent default instead of the override.

**After fix:** `total: 1  failed: 0  succeeded: 1`. Combined with Fix 1/2/3 tests:
`total: 464  failed: 0  succeeded: 464` (`*RelationalKeyOverridesTest` +
`*RelationalForeignKeyOverridesTest` + `*RelationalModelValidatorTest`).

## Fix 5 — parameterless `GetConstraintName()` not override-aware (plus a follow-up)

**File:** `src/EFCore.Relational/Extensions/RelationalForeignKeyExtensions.cs`

**Change:** `GetConstraintName()` (parameterless) now resolves
`StoreObjectIdentifier.Create` for both the declaring and principal entity types
and, when both resolve, delegates to the store-object overload — mirroring
`RelationalKeyExtensions.GetName()`. If either table can't be determined, it falls
back to the original (global-annotation-or-default) resolution.

**Regression found and fixed in the same area:** the first version of this fix
always returned whatever the store-object overload produced, including `null`.
For a "redundant" self-referential relationship (an owned type in table
splitting, where dependent and principal share the same table and key columns),
`GetDefaultName`'s shared-table logic deliberately returns `null` — no separate
constraint materializes — and the store-object overload propagates that `null`
even when a global name (via the plain `HasConstraintName(name)`) was configured.
This broke three pre-existing `CSharpMigrationsGeneratorTest` owned-type
round-trip tests (`Owned_types_are_stored_in_snapshot` and two others):
`Assert.Equal("FK_Custom", ownership1.GetConstraintName())` returned `null`.
Fixed by only returning the store-object overload's result when it's non-null,
falling through to the original resolution otherwise (see the follow-up commit).
Confirmed this reproduces against the *original* Fix 5 commit (all three tests
failed with the same mismatch) and passes after the follow-up.

**Reproduced before fix:** Yes. Added
`Parameterless_GetConstraintName_agrees_with_the_store_object_overload`. Against
pre-fix source:
```
total: 1  failed: 1  succeeded: 0
```
`Assert.Equal` failure: expected `"fk_post_blog_override"`, actual
`"FK_Post_Blog_BlogId"`.

**After fix (with the follow-up):** `total: 1  failed: 0  succeeded: 1`.
`*CSharpMigrationsGeneratorTest*`: `total: 173  failed: 0  succeeded: 173`
(includes the three owned-type tests the regression had broken).

## Fix 6 — snapshot generation drops annotations on override objects

**Files:** `src/EFCore.Design/Migrations/Design/CSharpSnapshotGenerator.cs`,
`src/EFCore.Relational/Design/IAnnotationCodeGenerator.cs`,
`src/EFCore.Relational/Design/AnnotationCodeGenerator.cs`,
new `src/EFCore.Relational/Metadata/Builders/KeyOverridesBuilder.cs`,
new `src/EFCore.Relational/Metadata/Builders/ForeignKeyOverridesBuilder.cs`,
new extension methods in `RelationalKeyBuilderExtensions`/`RelationalForeignKeyBuilderExtensions`.

**Why a scoped builder type was needed (not just a code-gen tweak):** chaining an
annotation directly onto `key.HasName(name, storeObject)`/`.HasConstraintName(...)`
would set it on the *key/foreign key itself* — those types already have their own
`HasAnnotation(string, object?)` for that purpose — not on the per-store-object
override. Property overrides avoid this because `TableBuilder.Property(name)`
returns a `ColumnBuilder` that is *already* scoped to the override object. There
was no equivalent for key/FK overrides, so I added two new public types mirroring
`ColumnBuilder`'s shape (`Overrides` property + `HasAnnotation(string, object?)`
+ the `ToString`/`Equals`/`GetHashCode` boilerplate every other `*Builder` in this
family carries), obtained via new `HasOverrides(storeObject[, principalStoreObject])`
extensions — one on `KeyBuilder`, three on `ReferenceCollectionBuilder`/
`ReferenceReferenceBuilder`/`OwnershipBuilder` (matching `HasConstraintName`'s
three relationship-builder shapes). Neither type exposes a name-setting method,
so the existing `.HasName(...)`/`.HasConstraintName(...)` emission for the
already-tested name-override case is completely unchanged; annotations are
emitted as their own separate statement(s), only when `GetAnnotations(overrides)`
is non-empty.

`GenerateKeyOverrides`'s gate changed from `.Any(o => o.IsNameOverridden)` to
`.Any()` so an annotation-only override (no name) still forces the `var key = `
local. `IAnnotationCodeGenerator`/`AnnotationCodeGenerator` gained
`GenerateFluentApiCalls(IRelationalKeyOverrides/IRelationalForeignKeyOverrides, ...)`
overloads (default `[]`, mirroring the `ISequence`/`IRelationalPropertyOverrides`
precedent) and switch arms in the `IAnnotatable` dispatcher — without these, the
dispatcher's default case throws `ArgumentException(UnhandledAnnotatableType)`
for any override carrying an annotation.

**A second, independently-discovered problem on the FK side:** `GenerateForeignKey`'s
own `foreignKeyBuilderName` local captures only the leading `HasOne(...)`/
`WithOwner(...)` call (a `ReferenceNavigationBuilder`, no `HasOverrides` overload)
— the rest of the relationship statement (`WithMany/WithOne`, `HasForeignKey`,
`HasPrincipalKey`) is written directly to the shared `IndentedStringBuilder`
rather than accumulated in a reusable string. Reusing `foreignKeyBuilderName` for
the new override-annotation statement therefore produced code that failed to
*compile* (`CS1929: 'ReferenceNavigationBuilder' does not contain a definition
for 'HasOverrides'`), caught by the annotation-only-override test (which has no
name-override statement to "borrow" a working receiver from). Fixed by a new
`GetForeignKeyExpression` helper that independently recomputes the full
relationship-configuring expression as its own string, used only for this new
statement; the existing statement-writing code path is untouched.

**Reproduced before fix:** Yes, in the strongest available sense — the four new
tests don't even *compile* against the pre-Fix-6 source, since `HasOverrides`
doesn't exist yet:
```
error CS1061: 'KeyBuilder' does not contain a definition for 'HasOverrides' ...
error CS1061: 'ReferenceCollectionBuilder<...>' does not contain a definition for 'HasOverrides' ...
```
(4 errors, one per new test's `buildModel` action, confirmed by stashing all
Fix-6 source files — including the two new builder-type files via `git stash -u`
— and rebuilding.) The FK-side `GetForeignKeyExpression` sub-fix was itself
caught the same way, mid-development: before adding it, the two FK-side tests
failed with `CS1929` (wrong receiver type) even with the rest of Fix 6 applied.

**After fix:** all four new tests pass:
```
total: 4  failed: 0  succeeded: 4
```
Full `*CSharpMigrationsGeneratorTest*`: `total: 177  failed: 0  succeeded: 177`
(baseline 173 + 4 new).

**Public API / baseline:** `test/EFCore.ApiBaseline.Tests` auto-regenerated
`src/EFCore.Design/EFCore.Design.baseline.json` (3 new protected
`CSharpSnapshotGenerator` members: `GenerateKeyOverridesAnnotations`,
`GenerateForeignKeyOverridesAnnotations`, `GetForeignKeyExpression`) and
`src/EFCore.Relational/EFCore.Relational.baseline.json` (the two new builder
types, their `Overrides`/`HasAnnotation` members, the four `HasOverrides`
extensions, and the `IAnnotationCodeGenerator`/`AnnotationCodeGenerator`
`GenerateFluentApiCalls`/`GenerateFluentApi` overloads). Diffed both baseline
files by hand after the auto-rewrite — only these expected members appear, no
surprises.

## Explicitly not fixed

`RelationalKeyOverrides.RemoveNameOverride`'s `_nameConfigurationSource.Overrides(configurationSource)`
operand order — left exactly as-is, per instruction (byte-identical to upstream's
`RelationalPropertyOverrides.RemoveColumnNameOverride`).

## Final verification (fresh builds, direct-dll)

```
.dotnet/dotnet build test/EFCore.Relational.Tests/EFCore.Relational.Tests.csproj -v q --nologo
.dotnet/dotnet build test/EFCore.Design.Tests/EFCore.Design.Tests.csproj -v q --nologo
.dotnet/dotnet build test/EFCore.ApiBaseline.Tests/EFCore.ApiBaseline.Tests.csproj -v q --nologo

.dotnet/dotnet artifacts/bin/EFCore.Relational.Tests/Debug/net11.0/Microsoft.EntityFrameworkCore.Relational.Tests.dll
.dotnet/dotnet artifacts/bin/EFCore.Design.Tests/Debug/net11.0/Microsoft.EntityFrameworkCore.Design.Tests.dll
.dotnet/dotnet artifacts/bin/EFCore.ApiBaseline.Tests/Debug/net11.0/Microsoft.EntityFrameworkCore.ApiBaseline.Tests.dll
```

| Suite | Expected (baseline) | Actual | Delta |
|---|---|---|---|
| EFCore.Relational.Tests | total 1502, executed 1501, passed 1501, failed 0, notExecuted 1 | **total: 1509  failed: 0  succeeded: 1508  skipped: 1** | +7 tests (Fix 1×2, Fix 2×1, Fix 3×2, Fix 4×1, Fix 5×1), all passing, same 1 skip |
| EFCore.Design.Tests | 1277 total, 5 pre-existing failures (`OperationExecutorTest`, untouched) | **total: 1281  failed: 5  succeeded: 1275  skipped: 1** | +4 tests (Fix 6), all passing; failure count unchanged at 5, same tests |
| EFCore.ApiBaseline.Tests | 14/14 | **total: 14  failed: 0  succeeded: 14** | baseline auto-rewrite happened once (during Fix 6 development), stable on the final run with no further diff |

No count failed to move where a new test was expected to move it. The
Design-suite failure count did not grow. `git status --short` is clean after
every commit; no `.superpowers/` files were staged.

## Commits (newest first)

```
a7e90f966c Add regression tests for Fix 6 (override annotations in the snapshot)
bcb79b835e Fix 6: emit annotations on key and foreign key overrides in the snapshot
81397bc382 Fix 5 follow-up: fall back to the global name for redundant self-referential FKs
639f332d86 Add regression tests for per-store-object override review fixes 1-5
d442b09f03 Fix 3: merge oldAnnotation's overrides when a key/FK is reused on attach
141fabd84e Fix 2: retain every candidate per structure in the shared-FK-override validator
3a3751b811 Fix 1: compare the stored override name in CanSetName/CanSetConstraintName
d7c67383b9 Fix 5: make the parameterless GetConstraintName() override-aware
03b1133c82 Fix 4: propagate per-store-object overrides across linked foreign keys
```

Grouping rationale: source fixes are one commit each (independently
reviewable/revertable); all new tests for Fixes 1–5 are grouped into a single
commit because they live interleaved in the same two shared test files
(`RelationalKeyOverridesTest.cs`, `RelationalForeignKeyOverridesTest.cs`) and
splitting them by hunk would not add reviewability. Fix 6's tests are a separate
commit since they're in a different file. The Fix 5 follow-up is its own commit
(a regression discovered and fixed during Fix 6's testing, not part of the
original Fix 5 diagnosis) rather than an amend, per the no-amend-unless-asked
rule.

---

## Review response (post-wave correction)

Review of the six-fix wave found five clean, plus one Important that blocked the
push and two cheap Minors. All three addressed below, committed separately from
the original fix wave, not amended.

### Important — Fix 5's collateral change to the diagnostic gate: reverted

**File:** `src/EFCore.Relational/Metadata/Internal/RelationalForeignKeyExtensions.cs`
(inside `GetDefaultName`'s "properties mapped to unrelated tables" diagnostic).

The reviewer was right on both points. My justification for widening the gate
(`foreignKey.IsConstrained && foreignKey.DeclaringEntityType.GetTableName() != null`)
was that the plain `foreignKey.GetConstraintName() != null` upstream condition
would go through the same per-store-object machinery after Fix 5 and return
`null` for exactly the models that reach this branch, silently suppressing the
warning. That was true at the point I made the change (commit `d7c67383b9`), but
the Fix 5 follow-up (`81397bc382`, made later in the same session to fix an
unrelated round-trip regression) added `if (name != null) return name;` before
falling back to the original annotation-or-default resolution — which means the
parameterless getter no longer returns `null` in the case my widened condition
was written to guard against. I hadn't gone back to re-check whether the earlier
change was still load-bearing once the follow-up landed. It wasn't, and the wider
condition was a genuine, unnecessary divergence from upstream that could flip a
clean-on-`main` TPC-plus-entity-splitting model into an `Error`-level throw
(`ForeignKeyPropertiesMappedToUnrelatedTables` is `WarningBehavior.Throw` by
default).

**Restored exactly:**
```csharp
if (foreignKey.GetConstraintName() != null
    && derivedTables.All(t => foreignKey.GetConstraintName(
            t!.Value,
            principalTable)
        == null))
```
byte-identical to upstream, no explanatory comment needed since it's no longer a
divergence. Confirmed with a diff that this is the *only* change in the file (see
below) — no other narrower guard was needed anywhere else, since the follow-up's
`if (name != null) return name;` already makes the parameterless getter agree
with upstream's expectations for every model that reaches this branch.

**`Detects_unmapped_foreign_keys_in_TPT` / `Detects_unmapped_foreign_keys_in_entity_splitting`:**
stayed green.
```
.dotnet/dotnet artifacts/bin/EFCore.Relational.Tests/Debug/net11.0/Microsoft.EntityFrameworkCore.Relational.Tests.dll \
  --filter-method '*Detects_unmapped_foreign_keys_in_TPT' \
  --filter-method '*Detects_unmapped_foreign_keys_in_entity_splitting' \
  --filter-method '*Key_overrides_survive_when_attached_onto_an_existing_key_with_its_own_overrides' \
  --filter-method '*Foreign_key_overrides_survive_when_attached_onto_an_existing_foreign_key_with_its_own_overrides'
```
```
total: 4  failed: 0  succeeded: 4  skipped: 0
```
(Ran together with the two Fix-3 reattach tests as a spot check that the Minor-2
guard below didn't regress them — all four independent scenarios, all green.)

### Minor 1 — doc comment moved off its method: fixed

**File:** `src/EFCore.Design/Migrations/Design/CSharpSnapshotGenerator.cs`.
`StoreObjectIdentifierLiteral`'s new `<summary>` had been inserted *above* the
pre-existing `<summary>` for `AppendStoreObjectIdentifierLiteral`, leaving both
attached to `StoreObjectIdentifierLiteral` and `AppendStoreObjectIdentifierLiteral`
undocumented. Moved the original summary back down so it sits directly above
`AppendStoreObjectIdentifierLiteral`. No behavior change; confirmed the project
still builds clean (StyleCop docs-as-errors would have caught a genuinely missing
summary, but XML-doc-on-wrong-member isn't itself a compile error, so this was
only visible on inspection).

### Minor 2 — gate Fix 3's merge on `annotation != null`: fixed

**Files:** `KeyOverridesConvention.cs`, `ForeignKeyOverridesConvention.cs`.
Added `annotation != null &&` to the front of each merge-back condition, so a
wholesale removal of the overrides annotation (`annotation == null`) skips the
merge instead of resurrecting every entry from `oldAnnotation`. Confirmed (per
the reviewer's own note) that no in-tree path reaches that today —
`RelationalKeyOverrides.Remove`/`RelationalForeignKeyOverrides.Remove` mutate the
dictionary in place and never write the annotation, so a removal never dispatches
this convention — so this closes a latent trap rather than fixing a live bug; no
new test needed since there's no reachable path to exercise it, and the existing
reattach-survival tests (Fix 3) confirm the guard doesn't affect the paths that
*are* reachable.

### Final verification (fresh builds, direct-dll, after all three corrections)

```
.dotnet/dotnet build src/EFCore.Relational/EFCore.Relational.csproj -v q --nologo
.dotnet/dotnet build src/EFCore.Design/EFCore.Design.csproj -v q --nologo
.dotnet/dotnet build test/EFCore.Relational.Tests/EFCore.Relational.Tests.csproj -v q --nologo
.dotnet/dotnet build test/EFCore.Design.Tests/EFCore.Design.Tests.csproj -v q --nologo
.dotnet/dotnet build test/EFCore.ApiBaseline.Tests/EFCore.ApiBaseline.Tests.csproj -v q --nologo

.dotnet/dotnet artifacts/bin/EFCore.Relational.Tests/Debug/net11.0/Microsoft.EntityFrameworkCore.Relational.Tests.dll
.dotnet/dotnet artifacts/bin/EFCore.Design.Tests/Debug/net11.0/Microsoft.EntityFrameworkCore.Design.Tests.dll
.dotnet/dotnet artifacts/bin/EFCore.ApiBaseline.Tests/Debug/net11.0/Microsoft.EntityFrameworkCore.ApiBaseline.Tests.dll
```

| Suite | Held state | Actual | Match |
|---|---|---|---|
| EFCore.Relational.Tests | total 1509, 0 failed, 1 skipped | **total: 1509  failed: 0  succeeded: 1508  skipped: 1** | exact |
| EFCore.Design.Tests | total 1281, 5 pre-existing failures | **total: 1281  failed: 5  succeeded: 1275  skipped: 1** | exact — same `OperationExecutorTest` cases, count did not grow |
| EFCore.ApiBaseline.Tests | 14/14 | **total: 14  failed: 0  succeeded: 14** | exact — no baseline diff (`git status` shows the two `.baseline.json` files untouched by this correction, as expected since none of the three fixes touch public API) |

`git status --short` after the corrections, before this commit, showed exactly
the four touched files (`RelationalForeignKeyExtensions.cs`,
`CSharpSnapshotGenerator.cs`, `KeyOverridesConvention.cs`,
`ForeignKeyOverridesConvention.cs`) — no baseline JSON, no stray changes.

### Commit

```
18c706c6a0 Review correction: restore upstream diagnostic gate, fix doc placement, gate Fix 3 merge
```
Committed separately from the original six-fix wave (not amended), per
instruction. Nothing pushed.
