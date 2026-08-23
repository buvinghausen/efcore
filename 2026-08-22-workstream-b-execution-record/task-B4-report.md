# Task B4 report: FK constraint name resolution seam

## What was implemented

`GetConstraintName(this IReadOnlyForeignKey foreignKey, in StoreObjectIdentifier storeObject, in StoreObjectIdentifier principalStoreObject, IDiagnosticsLogger<DbLoggerCategory.Model.Validation>? logger)` in
`src/EFCore.Relational/Metadata/Internal/RelationalForeignKeyExtensions.cs` now consults
`RelationalForeignKeyOverrides.Find` (Task B3) between the `defaultName` computation and the global
`Relational:Name` annotation read, exactly as the brief specified:

```csharp
var defaultName = foreignKey.GetDefaultName(storeObject, principalStoreObject, logger);

// Gated on defaultName: an override only applies where the constraint actually materializes,
// which keeps override storage from inventing constraints on unmapped store objects.
if (defaultName != null
    && RelationalForeignKeyOverrides.Find(foreignKey, new StoreObjectPair(storeObject, principalStoreObject))
        is { IsNameOverridden: true } overrides)
{
    return overrides.Name ?? defaultName;
}

var annotation = foreignKey.FindAnnotation(RelationalAnnotationNames.Name);
return annotation != null && defaultName != null
    ? (string?)annotation.Value
    : defaultName;
```

Precedence is per-pair override → global `Relational:Name` annotation → default generated name, with an
explicit `null` override landing on the default (not falling through to the global annotation) via
`overrides.Name ?? defaultName`.

**`GetConstraintName` contains exactly one global-name fallback site** — the `annotation != null &&
defaultName != null ? ... : defaultName` expression shown above. I confirmed this by grep: there is exactly
one `public static string? GetConstraintName(...)` definition taking `(foreignKey, storeObject,
principalStoreObject, logger)` in the file, and it has one such expression. (There is also a separate,
unrelated 0-arg `GetConstraintName(this IReadOnlyForeignKey)` in
`src/EFCore.Relational/Extensions/RelationalForeignKeyExtensions.cs` — the *public* API surface, not
store-object-aware, out of scope per the brief — and a 3-arg public overload that simply forwards to the
4-arg internal one with `logger: null`.) I covered the one site that exists.

## TDD evidence

### RED

After writing the brief's Step 1 tests (with one construction fix — see "Deviation" below), before adding
the resolution code:

```
./build.sh --test --projects /home/buvy/code/buvinghausen/efcore/.claude/worktrees/feature+per-store-object-constraint-names/test/EFCore.Relational.Tests/EFCore.Relational.Tests.csproj
```

```
<Counters total="1493" executed="1492" passed="1491" failed="1" error="0" ... notExecuted="1" ... />
```

Failing test output:

```
Assert.Equal() Failure: Strings differ
           ↓ (pos 0)
Expected: "fk_user_lockout"
Actual:   "FK_UserLockout_Users_Id"
           ↑ (pos 0)
```

This is the expected failure: without the override lookup, `GetConstraintName` falls straight through to
the default generated name, ignoring the override I had attached.

### GREEN

After adding the resolution code and finishing the test-scoping work described below:

```
./build.sh --test --projects /home/buvy/code/buvinghausen/efcore/.claude/worktrees/feature+per-store-object-constraint-names/test/EFCore.Relational.Tests/EFCore.Relational.Tests.csproj
```

```
Tests succeeded: .../EFCore.Relational.Tests/Debug/net11.0/Microsoft.EntityFrameworkCore.Relational.Tests.dll [net11.0|arm64]
Build succeeded.
```

Runner summary line (verbatim, from the .trx):

```
<Counters total="1494" executed="1493" passed="1493" failed="0" error="0" timeout="0" aborted="0" inconclusive="0" passedButRunAborted="0" notRunnable="0" notExecuted="1" disconnected="0" warning="0" completed="0" inProgress="0" pending="0" />
```

1494 total vs. the 1491 baseline = 3 new tests added, all passing; the 1 `notExecuted` is the same
pre-existing skip noted in the dispatch as present at baseline. Zero failures, zero errors.

## Files changed

- `src/EFCore.Relational/Metadata/Internal/RelationalForeignKeyExtensions.cs` — the resolution seam (10
  lines added, matches the brief verbatim).
- `test/EFCore.Relational.Tests/Metadata/RelationalForeignKeyOverridesTest.cs` — three new tests appended
  to Task B3's fixtures (`User`, plus a new minimal `Blog`/`Post` pair — see below).

Commit: `73dc2c7381` "Resolve FK constraint names through per-pair overrides".

## Deviation from the brief, and why (read this first)

The brief's Step 1 test is not directly runnable and its "should pass with no further changes" assumption
about the relational-model half does not hold for the entity-splitting linking FK. Both are addressed
below; this is exactly the "investigate before continuing" case the brief flagged.

**1. Construction fix (Trap #2, expected).** The brief's literal snippet does
`entityType.GetForeignKeys().Single(fk => fk.PrincipalEntityType == entityType)` on the *mutable* model —
but the linking FK doesn't exist until `EntitySplittingConvention.ProcessModelFinalizing` runs during
`FinalizeModel()`. This throws `InvalidOperationException: Sequence contains no matching element`. Fixed by
building the FK manually the way Task B3's `Foreign_key_overrides_are_keyed_by_the_dependent_principal_pair`
test does (`entityType.Builder.HasRelationship(entityType, pk.Properties, pk)!.IsUnique(true)!.Metadata`),
per the dispatch's Trap #2. This alone produced the clean RED state shown above.

**2. Root-caused finding: the "reach the relational model" half cannot pass with this construction, and
this is not a bug in my resolution code.** After implementing the resolution logic, the brief's full test
(pair resolution *and* `modelBuilder.FinalizeModel().GetRelationalModel()` reflecting the override) still
failed — but only on the relational-model assertion; the direct `GetConstraintName` calls on the manually-built
FK already passed. I instrumented `EntitySplittingConvention.ProcessModelFinalizing` temporarily (reverted
before committing; `git diff` on that file is empty) and confirmed:

- `ModelCleanupConvention.RemoveNavigationlessForeignKeys` (`src/EFCore/Metadata/Conventions/ModelCleanupConvention.cs:80`)
  is a **Core** `IModelFinalizingConvention` registered in `ProviderConventionSetBuilder.CreateConventionSet()`
  — which `RelationalConventionSetBuilder.CreateConventionSet()` calls via `base.CreateConventionSet()`
  *before* adding `EntitySplittingConvention`. Finalizing conventions dispatch in registration order.
- My manually pre-built linking FK has no navigations (matching the convention's own construction, which
  also sets none). `ModelCleanupConvention` removes any foreign key with `PrincipalToDependent == null &&
  DependentToPrincipal == null`, so it strips my pre-built FK — and everything attached to it, including
  the overrides — *before* `EntitySplittingConvention.ProcessModelFinalizing` ever runs.
- `EntitySplittingConvention` then finds no matching self-referential FK and creates a **fresh** one from
  scratch. This fresh FK is exactly what ends up in the relational model, and it carries none of my
  overrides. Debug output confirmed: `existingSelfRefBefore=` (null — my FK was already gone) and
  `overridesAfter=-1` (the fresh FK has no overrides annotation at all).
- I verified this is specific to a FK created *during* finalization, not a general problem with my
  resolution code or with annotation survival through `FinalizeModel()`: a **regular** (non-splitting) FK,
  built normally and never touched by any finalizing convention, keeps its override through
  `FinalizeModel().GetRelationalModel()` without issue (see `Foreign_key_name_override_reaches_the_relational_model`,
  passing).
- I also confirmed there is no loophole within B4's scope: setting the override *after* `FinalizeModel()`
  on the real linking FK is not possible, because `Model.IsReadOnly` is exactly `_conventionDispatcher ==
  null`, set unconditionally by `FinalizeModel()`, and `RelationalForeignKeyOverrides.SetName` calls
  `EnsureMutable()`. And giving the manually pre-built FK a navigation to survive `ModelCleanupConvention`
  doesn't work either: EF Core throws `InvalidOperationException` for a shadow navigation on a non-shadow
  CLR entity type, and `User` has no real navigation property that fits.

This is exactly the class of problem Task B5 ("Attach, merge, and survival through model rebuilding") is
scoped to address — except B5's own brief describes a subtly different case (annotation values survive a
detach/re-attach via `MergeAnnotationsFrom`, only the override object's back-pointer goes stale). Here the
old FK is *removed* (`HasNoRelationship`) and the new one is *added* independently by a different
convention — there is no detach/re-attach link between them for `MergeAnnotationsFrom` to ride along on. I
don't think B5 as scoped closes this gap either; flagging that as a concern for the controller rather than
guessing at a fix, since it's an architectural question (how does anything — a user, or a naming-convention
plugin — attach an override to a linking FK that doesn't exist until mid-finalization?) outside this task's
brief.

**Resolution taken:** rather than leave a known-failing assertion in the tree or silently drop coverage, I
split the brief's one test into three, keeping everything that is real and provable:

- `Foreign_key_name_overrides_resolve_per_pair` — the brief's pair-resolution assertions, unchanged in
  substance, with a comment explaining why it stops short of calling `FinalizeModel()`.
- `Foreign_key_name_override_reaches_the_relational_model` — new, using a minimal `Blog`/`Post` fixture, to
  prove Spike 2 finding 2 (RelationalModel builds `ForeignKeyConstraint` names through this same function)
  for a FK whose identity is stable across finalization. This is real coverage of the same code path
  (`RelationalModel` calling `GetConstraintName`) that the brief's dropped assertion would have exercised,
  just not on the specific entity-splitting-linking-FK identity that the cleanup-convention ordering
  defeats.
- `Foreign_key_override_does_not_apply_where_the_constraint_does_not_materialize` — unchanged from the
  brief, already passed even before implementation (defaultName is null for an unmapped store object
  regardless of override lookup), included for completeness per the brief.

No production code outside `RelationalForeignKeyExtensions.cs` was touched; `EntitySplittingConvention.cs`
was instrumented only temporarily for diagnosis and reverted (`git diff` confirms it is byte-identical to
HEAD~1).

## Self-review

- **Completeness:** the single global-name fallback site is covered; confirmed by grep there's only one.
- **Precedence:** override → global annotation → default, with `overrides.Name ?? defaultName` landing an
  explicit-null override on the default rather than falling through to the global annotation — matches B2's
  pattern for keys exactly.
- **Discipline:** no new public members, no extension methods added, no baseline.json touch, control flow
  in `GetConstraintName` preserved (only the two lines between `defaultName` and `annotation` changed).
  Nothing under `.superpowers/` was staged.
- **Testing:** all three new tests exercise real behavior (verified by their RED/pass history during
  development, not just inspection). Output is pristine — 0 warnings, 0 errors in both the standalone
  `dotnet build` and the full `build.sh --test` run.

## Concerns

I am confident in the implementation itself (`DONE`), but flag one open question for the controller: the
entity-splitting linking FK cannot receive a name override via any mechanism I could find within B4's (or,
as far as I can tell, B5's stated) scope, because the FK is created mid-finalization and there is no
detach/re-attach relationship between whatever stand-in exists before finalization and the FK the
convention actually builds. If a later task needs "override the linking FK's constraint name end-to-end"
to work, it will need either a convention hook timed to run during `OnModelFinalizing` after
`EntitySplittingConvention` (while the model is still mutable), or some other mechanism not yet described
in the task briefs I've read (B1–B6). I did not invent one, since that would be scope creep past a
resolution-seam task and is exactly the kind of architectural call the dispatch told me to escalate rather
than guess at.
