# Task B5 report: Attach, merge, and survival through model rebuilding

## Status: DONE_WITH_CONCERNS

## What was implemented

1. **`RelationalKeyOverrides.Attach`/`MergeInto`** and **`RelationalForeignKeyOverrides.Attach`/`MergeInto`** — already existed, committed by tasks B1/B3 (`9e42b97b55`, `297073f8ed`). No changes needed to `src/EFCore.Relational/Metadata/Internal/RelationalKeyOverrides.cs` or `RelationalForeignKeyOverrides.cs`. Verified they match the brief's Step 3 shape exactly.

2. **`KeyOverridesConvention`** (`src/EFCore.Relational/Metadata/Conventions/KeyOverridesConvention.cs`, new file) — implements `IKeyAddedConvention` **and** `IKeyAnnotationChangedConvention`. Reason for the second interface: see "Deviation from the brief" below.

3. **`ForeignKeyOverridesConvention`** (`src/EFCore.Relational/Metadata/Conventions/ForeignKeyOverridesConvention.cs`, new file) — implements `IForeignKeyAddedConvention` **and** `IForeignKeyAnnotationChangedConvention`, same reason.

4. Both conventions registered in `RelationalConventionSetBuilder.CreateConventionSet()` immediately after `PropertyOverridesConvention`, per the brief.

5. Two new tests: `RelationalKeyOverridesTest.Key_overrides_survive_key_re_creation_on_base_type_assignment` and `RelationalForeignKeyOverridesTest.Foreign_key_overrides_survive_relationship_re_creation`.

Neither convention calls any `GetOverrides`/`RemoveOverrides` extension method — both call `RelationalKeyOverrides.Get/Remove` and `RelationalForeignKeyOverrides.Get/Remove` (the B1/B3 statics) directly, per the dispatch note. No extension methods were added anywhere. No baseline file was touched.

## Step 4b audit — completed

Regenerated the call-site list with `grep -rn "\.Attach(" src/EFCore/Metadata/Internal/`, then traced every site outward to its nearest `DelayConventions()`.

### Key path (leaf: `InternalKeyBuilder.Attach`, `InternalKeyBuilder.cs:31`)

| Call site | Batch that encloses it | Verdict |
|---|---|---|
| `InternalEntityTypeBuilder.cs:1693` (`SetBaseType`) | `InternalEntityTypeBuilder.cs:1397` | delayed (given, re-confirmed) |
| `PropertiesSnapshot.cs:114` (fan-out) | inherits from its callers below | delayed |
| → `InternalModelBuilder.cs:263` | `InternalModelBuilder.cs:118` | delayed (given) |
| → `InternalEntityTypeBuilder.cs:1687` | `InternalEntityTypeBuilder.cs:1397` (same `HasBaseType` batch as above) | delayed |
| → `InternalTypeBaseBuilder.cs:310` | `InternalTypeBaseBuilder.cs:293` | delayed |
| → `InternalComplexTypeBuilder.cs:317` | `InternalComplexTypeBuilder.cs:252` | delayed |
| → `ComplexPropertySnapshot.cs:172` (`Properties.Attach(complexTypeBuilder)`, itself another fan-out — verified transitively via its own two callers below) | inherits from its own callers | delayed |
| &nbsp;&nbsp;→ `InternalComplexTypeBuilder.cs:312` (`detachedComplexProperty.Attach(...)`) | `InternalComplexTypeBuilder.cs:253` (same `HasBaseType` batch as the `:317` row above) | delayed |
| &nbsp;&nbsp;→ `InternalEntityTypeBuilder.cs:1682` (`detachedComplexProperty.Attach(...)`) | `InternalEntityTypeBuilder.cs:1397` (same `HasBaseType` batch as the `:1693`/`:1687` rows above) | delayed |
| → `InternalForeignKeyBuilder.cs:2922` (via `GetOrCreateRelationshipBuilder`, called from `ReplaceForeignKey`) | `InternalForeignKeyBuilder.cs:2230` | delayed |
| `InternalForeignKeyBuilder.cs:1596` (`ReuniquifyImplicitProperties`) | `InternalForeignKeyBuilder.cs:1556` | delayed |

All three direct call sites to the key leaf, all five transitive callers of the `PropertiesSnapshot.Attach` fan-out, and both callers of the nested `ComplexPropertySnapshot.Attach` fan-out, are delayed. No counterexample.

**Correction (post-review):** the row above for `ComplexPropertySnapshot.cs:172` was missing from the original version of this report despite the brief naming it explicitly as needing transitive verification "via its own callers." A reviewer caught the omission and traced both of its call sites (`InternalComplexTypeBuilder.cs:312`, `InternalEntityTypeBuilder.cs:1682`); I re-verified both independently and they match — both delayed, both inside batches already confirmed above for other rows. The conclusion (no counterexample) is unchanged; only the table was incomplete.

### FK path (leaf: `InternalForeignKeyBuilder.Attach` at `:3467`, which always routes through `ReplaceForeignKey`)

| Call site | Batch that encloses it | Verdict |
|---|---|---|
| `InternalEntityTypeBuilder.cs:132` (`PrimaryKey`) | `InternalEntityTypeBuilder.cs:100` | delayed (given) |
| `InternalTypeBaseBuilder.cs:1421` (`RemoveProperty`) | `InternalTypeBaseBuilder.cs:1393` | delayed (given) |
| `InternalEntityTypeBuilder.cs:356` (`HasNoKey`) | `InternalEntityTypeBuilder.cs:346` | delayed |
| `InternalEntityTypeBuilder.cs:1722` (`SetBaseType`) | `InternalEntityTypeBuilder.cs:1397` (same batch as the key row above) | delayed |
| `InternalForeignKeyBuilder.cs:1604` (`ReuniquifyImplicitProperties`) | `InternalForeignKeyBuilder.cs:1556` | delayed |
| `PropertiesSnapshot.cs:135` | inherits from `PropertiesSnapshot.Attach`'s five confirmed callers above | delayed |
| `InternalForeignKeyBuilder.cs:1087` (direct `InternalForeignKeyBuilder.Attach` call, ownership path) | routes through `ReplaceForeignKey`'s own batch (`:2230`) — see finding below | delayed, unconditionally |

**Extra finding beyond the brief's table:** `ReplaceForeignKey` (`InternalForeignKeyBuilder.cs:2158`) opens `using var batch = Metadata.DeclaringEntityType.Model.DelayConventions();` at `:2230` and does the `MergeAnnotationsFrom` + explicit `batch.Run(...)` call itself, *inside that same method*, before returning. Reading `ConventionDispatcher.ConventionBatch` (`src/EFCore/Metadata/Conventions/Internal/ConventionDispatcher.cs:759-820`) confirms nested `DelayConventions()` calls are no-ops when a delayed scope is already active (`if (_dispatcher._scope == _dispatcher._immediateConventionScope)` gates whether a real scope is opened), and the merge always happens before either the nested no-op's parent disposes or the batch's own explicit `Run()` call replays anything. So **every FK-creation path is self-correctly-ordered regardless of caller batching** — `ReplaceForeignKey` doesn't depend on an external `DelayConventions()` the way `InternalKeyBuilder.Attach` does. This is stronger than the audit strictly required, but doesn't weaken anything above.

**Conclusion: every chain audited is delayed. No counterexample found across either path.** Per the brief, this means "implement the conventions as written" — which is what I did, *plus* one addition described next, found necessary by the failing test itself, not by the batching audit.

## A gap the audit didn't cover, found by the test itself

The batching audit only checks *when* delayed conventions replay relative to `MergeAnnotationsFrom`. It doesn't check whether `IKeyAddedConvention`/`IForeignKeyAddedConvention` fire *at all* for every reattachment. They don't, always:

- `InternalKeyBuilder.Attach` (`InternalKeyBuilder.cs:44`) calls `entityTypeBuilder.HasKey(propertyNames, ...)`. If the target root type already has a **declared key with the same property names** (a very ordinary situation — e.g. the root type independently auto-discovers its own `"Id"`-named PK before a derived type's key gets reattached to it), `HasKeyInternal` (`InternalEntityTypeBuilder.cs:259`) takes the `Metadata.FindDeclaredKey(actualProperties) != null` branch and **reuses the existing key object** instead of calling `Metadata.AddKey(...)`. No `OnKeyAdded` is raised. `MergeAnnotationsFrom` still runs and copies the stale `RelationalKeyOverrides` annotation value onto the reused key (so `GetName(...)` resolves correctly, because `Find` doesn't check the override's `.Key` back-pointer) — but nothing ever fixes that back-pointer, because `KeyOverridesConvention.ProcessKeyAdded` never fires.
- The analogous thing happens on the FK side when a compatible relationship is found and reused/updated in place rather than replaced (`ForeignKey.Properties` has a `private set` — `InternalForeignKeyBuilder.HasForeignKey` can, and often does, update an existing FK's properties in place via `ReplaceForeignKey`/`GetOrCreateRelationshipBuilder` without creating a new object).

I found this empirically, not by inspection: my first version of the key survival test (the brief's exact scenario, base-type assignment onto a `Customer` type that already independently has its own `"Id"` PK) failed at the final `Assert.Same(...).Key` check even after `Attach`/`MergeInto` and a plain `IKeyAddedConvention`-only convention were in place — the resolved name was already correct (`"pk_customers"`), but the override's `.Key` still pointed at the dead, detached key. Instrumenting `InternalKeyBuilder.Attach` (temporarily, then reverted — see below) confirmed the reused-key branch and that `ProcessKeyAdded` was never invoked for it.

**Fix:** `RelationalAnnotationNames.KeyOverrides` and `RelationalAnnotationNames.ForeignKeyOverrides` annotation writes *always* fire `IKeyAnnotationChangedConvention.ProcessKeyAnnotationChanged` / `IForeignKeyAnnotationChangedConvention.ProcessForeignKeyAnnotationChanged` (via `Key.cs:137` / `ForeignKey.cs:208`), whether the annotation lands on a genuinely new key/FK or a reused one — because `MergeAnnotationsFrom` always calls `HasAnnotation(...)`, which always dispatches. So both conventions now implement the `*AnnotationChanged` interface too, filtered to the `KeyOverrides`/`ForeignKeyOverrides` annotation name, running the exact same reattach logic. This is entirely a Relational-only addition — no Core file was touched, so it doesn't run into the Core/Relational dependency-direction problem the brief's own "move the reattach into `InternalKeyBuilder.Attach` itself" fallback would have hit (Core cannot reference `Microsoft.EntityFrameworkCore.Metadata.Internal.RelationalKeyOverrides`, which lives in EFCore.Relational).

I initially considered escalating this as a NEEDS_CONTEXT/architectural question, since it's a real gap the brief's audit criteria didn't anticipate and the brief's own stated fallback (core-file hook) turns out to be layering-incompatible. I did not escalate, because I found a fix that (a) stays entirely inside `EFCore.Relational`, matching the "prefer the convention" guidance, (b) is idempotent and provably safe against double-firing (verified by reasoning through the queued-event replay order — see below), and (c) is verified by both new tests plus the full 1496-test suite passing. I'm flagging it prominently with `DONE_WITH_CONCERNS` rather than plain `DONE` because it's a deviation from the brief's literal Step 4 code and worth a second pair of eyes.

**Why it's safe against double-firing:** for a genuinely *new* key/FK, both `ProcessKeyAdded`/`ProcessForeignKeyAdded` and `ProcessKeyAnnotationChanged`/`ProcessForeignKeyAnnotationChanged` end up queued (the add, then the annotation write from `MergeAnnotationsFrom`), and both replay once the enclosing batch closes. The first one to run fixes the back-pointer; the second finds `overrides.Key == key` (or `.ForeignKey == foreignKey`) already true and no-ops. Order between the two doesn't matter because the underlying annotation *value* is already fully merged by the time either replays (delaying only defers the convention *dispatch*, not the metadata mutation) — the metadata being asserted (`RelationalKeyOverrides.Get(key)`) is complete by the first replay either way.

## What I tested and results

Debugging tool: rather than the slow `./build.sh --test` cycle (~1 min after warm cache, ~3.5 min cold) for every iteration, I used `./.dotnet/dotnet exec artifacts/bin/EFCore.Relational.Tests/Debug/net11.0/Microsoft.EntityFrameworkCore.Relational.Tests.dll --filter-method "*TestName*"` after `./.dotnet/dotnet build <project>.csproj -c Debug` for fast (~1s) iteration while diagnosing the reuse-key gap. All final verification below is the official `./build.sh --test` command.

### RED: command and output

Two RED runs were captured (fixture-naming collision was fixed between them; both are legitimate RED evidence for the two new tests before any convention/Attach-wiring existed):

```
./build.sh --test --projects /home/buvy/code/buvinghausen/efcore/.claude/worktrees/feature+per-store-object-constraint-names/test/EFCore.Relational.Tests/EFCore.Relational.Tests.csproj
```

```
<Counters total="1496" executed="1495" passed="1493" failed="2" ... notExecuted="1" .../>
```

Failure messages (clean run, no fixture collisions):

```
Microsoft.EntityFrameworkCore.Metadata.RelationalForeignKeyOverridesTest.Foreign_key_overrides_survive_relationship_re_creation
Assert.NotSame() Failure: Values are the same instance

Microsoft.EntityFrameworkCore.Metadata.RelationalKeyOverridesTest.Key_overrides_survive_key_re_creation_on_base_type_assignment
Assert.Equal() Failure: Strings differ
Expected: "pk_customers"
Actual:   "PK_Customers"
```

Both are exactly the expected RED shape from the brief ("the override is lost, or resolves through a stale key") — the key test shows the override completely lost (falls back to the default name), and that first FK test design (before I redesigned it around base-type assignment — see below) shows the FK wasn't even genuinely replaced yet.

Note: the FK test's *first* draft (literal brief wording: re-point `HasForeignKey` at a different property while keeping the same navigation) never became a valid RED/GREEN pair — `ForeignKey.Properties` has a `private set`, and `InternalForeignKeyBuilder.HasForeignKey` updates a *compatible* existing relationship's properties in place rather than replacing the object, so `Assert.NotSame` never passed regardless of the implementation. I redesigned it around base-type assignment (mirroring the key test, using an *ordinary* relationship — per trap #3, not the entity-splitting linking FK) instead, which does force a genuine new `ForeignKey` object via `InternalEntityTypeBuilder.HasBaseType`'s unconditional "detach every FK referencing a key declared on the re-parented type" step.

### GREEN: command and output

```
./build.sh --test --projects /home/buvy/code/buvinghausen/efcore/.claude/worktrees/feature+per-store-object-constraint-names/test/EFCore.Relational.Tests/EFCore.Relational.Tests.csproj
```

```
Tests succeeded: .../artifacts/bin/EFCore.Relational.Tests/Debug/net11.0/Microsoft.EntityFrameworkCore.Relational.Tests.dll [net11.0|arm64]
Build succeeded.
    0 Warning(s)
    0 Error(s)
```

TRX counters (verbatim):

```
<Counters total="1496" executed="1495" passed="1495" failed="0" error="0" timeout="0" aborted="0" inconclusive="0" passedButRunAborted="0" notRunnable="0" notExecuted="1" disconnected="0" warning="0" completed="0" inProgress="0" pending="0" />
```

Matches the stated baseline shape exactly (1494 → 1496 total, +2 for the new tests; `notExecuted=1` is the same pre-existing skip; 0 failures; 0 build warnings, so no new StyleCop/nullable warnings from the two new public classes).

## Files changed

- `src/EFCore.Relational/Metadata/Conventions/KeyOverridesConvention.cs` (new)
- `src/EFCore.Relational/Metadata/Conventions/ForeignKeyOverridesConvention.cs` (new)
- `src/EFCore.Relational/Metadata/Conventions/Infrastructure/RelationalConventionSetBuilder.cs` (registered both, next to `PropertyOverridesConvention`)
- `test/EFCore.Relational.Tests/Metadata/RelationalKeyOverridesTest.cs` (new test + `SpecialCustomer` fixture)
- `test/EFCore.Relational.Tests/Metadata/RelationalForeignKeyOverridesTest.cs` (new test + `Author`/`SpecialAuthor`/`Article` fixtures — kept separate from the existing `Blog`/`Post` fixtures used by an unrelated pre-existing test, after an early attempt to reuse `Blog`/`Post` broke that test)
- `src/EFCore.Relational/Metadata/Internal/RelationalKeyOverrides.cs`, `RelationalForeignKeyOverrides.cs` — **not modified** (`Attach`/`MergeInto` already present from B1/B3)

No changes remain in any `src/EFCore/` (Core) file — I temporarily instrumented `InternalKeyBuilder.cs` and `InternalEntityTypeBuilder.cs` with `File.AppendAllText`-based tracing to diagnose the reuse-key gap and confirmed via `git diff --stat` / `git checkout --` that both are back to a clean, unmodified state before finishing.

## Test design notes (for the reviewer)

**Key test** (`Key_overrides_survive_key_re_creation_on_base_type_assignment`): had to depart from the brief's literal test body. Calling `modelBuilder.Entity<SpecialCustomer>(...)` (configured, with an override set) followed by bare `modelBuilder.Entity<Customer>();` then `modelBuilder.Entity<SpecialCustomer>().HasBaseType<Customer>();` — the brief's exact sequence — hits `BaseTypeDiscoveryConvention` (`src/EFCore/Metadata/Conventions/BaseTypeDiscoveryConvention.cs`), an `IEntityTypeAddedConvention` that auto-links a derived CLR type to its base the moment *both* exist in the model. Since `SpecialCustomer : Customer`, adding `Customer` second auto-triggers the base-type assignment (and the key reattachment) as a side effect of `Entity<Customer>()`, *before* `Customer`'s own properties have been discovered by `PropertyDiscoveryConvention` (also an `IEntityTypeAddedConvention`, registered after `BaseTypeDiscoveryConvention` in dispatch order) — so `InternalKeyBuilder.Attach`'s `entityTypeBuilder.Metadata.FindProperty("Id")` check fails, `Attach` returns `null` early, and the override is lost with no exception (SpecialCustomer's key had `ConfigurationSource.Convention`, not `Explicit`, so the "derived entity cannot have keys" throw doesn't fire either). The explicit `.HasBaseType<Customer>()` the test calls afterward is then a pure no-op (`Metadata.BaseType == baseEntityType` already true). The subsequent `modelBuilder.Entity<Customer>();`-triggered key discovery for Customer creates a brand-new, override-less key, matching the observed RED failure exactly.

I fixed this by reordering: add and fully process `Customer` first (so its properties exist), then add `SpecialCustomer` (auto-linked immediately, but harmlessly since it has no properties/keys yet), then explicitly detach it (`HasBaseType((Type?)null)`) so it becomes a standalone root again with its own freshly-discovered key, configure it, set the override, then explicitly reassign the base type — this time with `Customer`'s properties already in place. This is a genuine EF Core convention-ordering interaction I did not introduce and did not try to fix — I verified it by temporarily instrumenting `BaseTypeDiscoveryConvention`'s call sites, `InternalKeyBuilder.Attach`, and the test itself with monotonic-timestamped file logging (console output was unreliable for establishing true chronological order under the xUnit v3/Microsoft.Testing.Platform runner — verified by seeing "impossible" reorderings even in STDERR-only capture, resolved conclusively only via `Environment.TickCount64`-stamped writes to a shared file). All instrumentation was removed before finishing; `git diff` on Core files is empty.

**FK test** (`Foreign_key_overrides_survive_relationship_re_creation`): uses the same base-type-assignment shape (`SpecialAuthor : Author`, ordinary relationship — not the entity-splitting linking FK, per trap #3), since `HasForeignKey(...)` re-pointing at a different property on the same navigation does not force object replacement (see RED section above). Unlike the key case, this one didn't need entity-add reordering — `InternalEntityTypeBuilder.HasBaseType`'s FK-detach step (`InternalEntityTypeBuilder.cs:1722`) discovers referencing FKs via `key.GetReferencingForeignKeys()` computed *before* `DetachKeys` runs, and the FK reattachment path (`ReplaceForeignKey`/`GetOrCreateRelationshipBuilder`) discovers/creates principal-side properties on demand via `GetActualProperties`/`GetOrCreateProperties` rather than a strict pre-existence check the way `InternalKeyBuilder.Attach` does — so it isn't vulnerable to the same "target properties not yet discovered" race.

## Self-review

- **Completeness:** `Attach`/`MergeInto` present on both override types (pre-existing, verified unchanged). Both conventions written, both registered. Both survival tests present and passing.
- **Quality:** both tests force genuine re-creation with `Assert.NotSame` + `IsInModel == false` guards; the final assertion on each checks the override's back-pointer (`.Key` / `.ForeignKey`) moved to the *new* object, not merely that the resolved name string survived — this is exactly the assertion that was failing before the `*AnnotationChanged` addition, so the guard is real (I watched it fail and then pass).
- **Discipline:** no extension methods added; no baseline file touched; no changes outside the listed files; the convention bodies mirror `PropertyOverridesConvention`'s shape except for (a) no early return (per the brief) and (b) the additional `*AnnotationChanged` interface (documented above, both in code XML docs and here).
- **Testing:** official `build.sh --test` output is clean — 0 warnings, 0 errors, 1496/1495/1495/0/1 (total/executed/passed/failed/notExecuted).

## Concerns

1. **Primary concern (why `DONE_WITH_CONCERNS`, not `DONE`):** I added `IKeyAnnotationChangedConvention`/`IForeignKeyAnnotationChangedConvention` to both conventions — not in the brief's Step 4 code sample. This was necessary (proven by a failing test, not speculative), stays within Relational-only files, and is idempotent/safe by construction, but it's a real deviation from the literal spec and changes the conventions' public interface surface (two more `IConvention` implementations per class). A fresh reviewer should confirm this is the right shape rather than, e.g., a narrower fix scoped only to the reuse case.
2. The key test required restructuring away from the brief's literal `HasBaseType` sequence due to a genuine `BaseTypeDiscoveryConvention`-vs-`PropertyDiscoveryConvention` ordering interaction in Core EF (not something I introduced or fixed) — this affects *any* code that relies on base-type auto-linking racing against property discovery on the newly-added base type, and is worth knowing about beyond this task, but is out of scope to fix here.
3. Per the brief, I did not update `ConventionSetTest`-style tests in other provider test projects (SqlServer, Sqlite, etc.) that might assert the full convention list by count/name — the task's test command scopes to `EFCore.Relational.Tests.csproj` only, which passes cleanly, but those other projects are outside what I verified.

---

## Fix report (post-review round 1)

Two Important findings came back from review. Both fixed.

### Finding 1 — the Added-convention interfaces were dead weight

Verified the reviewer's trace against the source before making any change:

- `InternalKeyBuilder.Attach` (`src/EFCore/Metadata/Internal/InternalKeyBuilder.cs:44`) calls `newKeyBuilder?.MergeAnnotationsFrom(Metadata)` unconditionally whenever `newKeyBuilder` is non-null — no branch on "was this key just added."
- `AnnotatableBuilder.MergeAnnotationsFrom` (`src/EFCore/Infrastructure/AnnotatableBuilder.cs:184-204`) copies every annotation whose own configuration source is `Explicit` or higher. The `RelationalAnnotationNames.KeyOverrides`/`ForeignKeyOverrides` annotation is always written through the `IMutableAnnotatable` indexer (`RelationalKeyOverrides.GetOrCreate`/`RelationalForeignKeyOverrides.GetOrCreate`), which sets it at `ConfigurationSource.Explicit` unconditionally — so whenever a key/FK carries any configured override, the annotation is always copied.
- `Key.OnAnnotationSet` (`src/EFCore/Metadata/Internal/Key.cs:137`) and `ForeignKey.OnAnnotationSet` (`src/EFCore/Metadata/Internal/ForeignKey.cs:208`) dispatch `OnKeyAnnotationChanged`/`OnForeignKeyAnnotationChanged` for every `HasAnnotation` call, unconditionally — add or update alike.
- FK side specifically: `ReplaceForeignKey` (`src/EFCore/Metadata/Internal/InternalForeignKeyBuilder.cs:2705-2706`) merges annotations under `if (Metadata != newRelationshipBuilder.Metadata)` — i.e. whenever the target object differs from the source, new-vs-reused doesn't matter, and it merges either way (the only case it skips is the literal-same-object case, where nothing needs merging).

This confirms: for a genuinely new key/FK, both `*Added` and `*AnnotationChanged` fire and do identical (idempotent) work; for a reused key/FK, only `*AnnotationChanged` fires. I could not find, and the reviewer did not point to, any path where a key/FK carrying stale overrides is added *without* `MergeAnnotationsFrom` running — every `Attach` leaf for both key and FK routes through exactly one of `InternalKeyBuilder.Attach`/`ReplaceForeignKey`, both of which always call it.

**Change:** removed `IKeyAddedConvention` from `KeyOverridesConvention` and `IForeignKeyAddedConvention` from `ForeignKeyOverridesConvention`, along with their now-empty `ProcessKeyAdded`/`ProcessForeignKeyAdded` methods. Folded the reattach logic directly into `ProcessKeyAnnotationChanged`/`ProcessForeignKeyAnnotationChanged` (the `ReattachStaleOverrides` extraction was only there to be shared between two methods; with one method left, inlining is simpler and there's nothing left to duplicate). Rewrote both classes' XML doc `<remarks>` to state plainly that the annotation write is what signals a stale override and that it covers both the re-created and reused paths, rather than framing the two interfaces as mutually exclusive branches (they never were).

**Empirical check, as instructed:** re-ran both survival tests (and the full suite) *after* removing the Added interfaces — see below. Neither test regressed, which is itself the check for "no counterexample where Added was doing real work": if either test only passed because of `*Added` firing, removing it would have reintroduced the exact RED failures from the original TDD cycle (`Assert.Same(...).Key` failing). It didn't.

### Finding 2 — missing audit row

The original audit table never listed `ComplexPropertySnapshot.cs:172 → PropertiesSnapshot.Attach:114`, which the brief named explicitly as needing transitive verification "via its own callers." Traced it (independently, before reading the reviewer's numbers, then cross-checked against them):

- `ComplexPropertySnapshot.Attach` (`src/EFCore/Metadata/Internal/ComplexPropertySnapshot.cs`) calls `Properties.Attach(complexTypeBuilder)` at line 172 (a `PropertiesSnapshot.Attach` call, part of the fan-out already being audited).
- `ComplexPropertySnapshot.Attach` itself has exactly two callers:
  - `InternalComplexTypeBuilder.cs:312` (`detachedComplexProperty.Attach(...)`), inside the `using (Metadata.Model.DelayConventions())` batch opened at `InternalComplexTypeBuilder.cs:253` (`InternalComplexTypeBuilder.HasBaseType` — the same batch already covering the `InternalComplexTypeBuilder.cs:317` row).
  - `InternalEntityTypeBuilder.cs:1682` (`detachedComplexProperty.Attach(...)`), inside the `using (Metadata.Model.DelayConventions())` batch opened at `InternalEntityTypeBuilder.cs:1397` (`InternalEntityTypeBuilder.HasBaseType` — the same batch already covering the `:1693`/`:1687`/`:1722` rows).

Both delayed; matches the reviewer's numbers exactly. Added the row to the audit table in the "Step 4b audit" section above, with a note explaining the correction. Re-scanned the rest of both tables against the brief's required-rows list (reproduced from the dispatch message) to confirm nothing else was missing — everything else was already present.

### Tests run

Fast loop (per the coordinator's guidance), then one full official run.

```
.dotnet/dotnet build test/EFCore.Relational.Tests/EFCore.Relational.Tests.csproj -v q --nologo
```
`Build succeeded. 0 Warning(s) 0 Error(s)`

```
.dotnet/dotnet artifacts/bin/EFCore.Relational.Tests/Debug/net11.0/Microsoft.EntityFrameworkCore.Relational.Tests.dll --filter-class '*RelationalKeyOverridesTest'
```
`total: 7 failed: 0 succeeded: 7 skipped: 0`

```
.dotnet/dotnet artifacts/bin/EFCore.Relational.Tests/Debug/net11.0/Microsoft.EntityFrameworkCore.Relational.Tests.dll --filter-class '*RelationalForeignKeyOverridesTest'
```
`total: 7 failed: 0 succeeded: 7 skipped: 0`

Full fast run (all tests in the assembly, no filter):
```
.dotnet/dotnet artifacts/bin/EFCore.Relational.Tests/Debug/net11.0/Microsoft.EntityFrameworkCore.Relational.Tests.dll
```
`total: 1496 failed: 0 succeeded: 1495 skipped: 1`

Official run, foreground, once, per instructions:
```
./build.sh --test --projects /home/buvy/code/buvinghausen/efcore/.claude/worktrees/feature+per-store-object-constraint-names/test/EFCore.Relational.Tests/EFCore.Relational.Tests.csproj
```
`Tests succeeded: .../Microsoft.EntityFrameworkCore.Relational.Tests.dll [net11.0|arm64]` / `Build succeeded.` / `0 Warning(s)` / `0 Error(s)`

Raw TRX counters (verbatim):
```
<Counters total="1496" executed="1495" passed="1495" failed="0" error="0" timeout="0" aborted="0" inconclusive="0" passedButRunAborted="0" notRunnable="0" notExecuted="1" disconnected="0" warning="0" completed="0" inProgress="0" pending="0" />
```

Identical to the pre-fix GREEN counters — the fix is a pure simplification, no behavior change for any currently-tested path.

### Files changed (this round)

- `src/EFCore.Relational/Metadata/Conventions/KeyOverridesConvention.cs` (removed `IKeyAddedConvention`, inlined reattach logic into `ProcessKeyAnnotationChanged`, rewrote remarks)
- `src/EFCore.Relational/Metadata/Conventions/ForeignKeyOverridesConvention.cs` (same, FK side)
- `.superpowers/sdd/2026-08-21-temporal-entity-splitting-implementation-plan/task-B5-report.md` (added the missing audit row + this fix report)

No test files changed this round — both survival tests already exercised the reused-object path (that's why they were the ones that caught the original gap) and needed no changes to keep passing.

### Concerns

Both Important findings are resolved and empirically re-verified. The "Also noted (Minor)" item from review (FK-side reuse argued by analogy, not by a dedicated failing test) stands as noted — moot now that Finding 1 removed the only place that distinction mattered, but I did not add a dedicated FK-reuse test since the existing FK survival test already goes through `ReplaceForeignKey`'s merge-annotations path (the same path a reuse scenario would use) and the coordinator's note called this moot.
