# Spike 2 — Per-Store-Object Key/FK Constraint Name Overrides: Findings

**Date:** 2026-08-14
**Gates:** workstream B metadata shape of [the design spec](2026-08-14-temporal-entity-splitting-design.md) (§4, §7 item 2)
**Environment:** dotnet/efcore `main` @ `5e8896500e`, linux-arm64 (same test-host runtime note as Spike 1)
**Artifacts:** `spike2.patch` (two-file relational patch, ~40 lines), `spike2-harness.cs.txt`. All spike code reverted from the working tree.

## Verdict: PASS — composite identity confirmed, resolution seams are exactly two functions

**5/5 harness tests green.** Per-store-object key names and pair-keyed FK names are expressible with purely additive lookups in the two internal resolvers, and the relational model consumes them with zero further changes.

## What was patched (2 sites, both in `EFCore.Relational/Metadata/Internal`)

1. `RelationalKeyExtensions.GetName(key, storeObject, logger)` — inserted an override lookup (`Dictionary<StoreObjectIdentifier, string>` annotation on the key) ahead of the global `RelationalAnnotationNames.Name` read in both the fragment branch and the containing-type branch. These two expressions (`(string?)key[RelationalAnnotationNames.Name] ?? defaultName`) are the *entire* global-name fallback — a single seam.
2. `RelationalForeignKeyExtensions.GetConstraintName(fk, storeObject, principalStoreObject, logger)` — inserted a composite lookup (`Dictionary<(StoreObjectIdentifier, StoreObjectIdentifier), string>` annotation on the FK) ahead of the global annotation, gated on `defaultName != null` (override applies only where the constraint actually materializes).

## Results

| Check | Result |
|---|---|
| `pk.GetName(Users)` / `pk.GetName(UserLockout)` return distinct per-table overrides | ✅ |
| Overrides flow into relational model `UniqueConstraints` with **no** relational-model changes | ✅ `pk_users_custom` on `Users`, `pk_user_lockout_custom` on `UserLockout` |
| Override beats a *global* rewritten name (`SetName("pk_global_rewrite")` also present — the NamingConventions #396 collision shape) | ✅ both tables distinct; global name fully shadowed |
| FK pair override resolves via `GetConstraintName(Posts, Users)` and lands on the `Posts` table's `ForeignKeyConstraints` | ✅ |
| Fragment linking constraint addressable | ✅ see finding 1 |

## Findings

1. **The entity-splitting linking FK is a synthesized self-referential model FK — composite identity is confirmed as the right dimensionality.** The fragment table's constraint (`FK_UserLockout_Users_Id → Users`) reports exactly one mapped model FK: `SpikeUser.[Id] → SpikeUser`, `unique=true`. One FK object serves *every* fragment's linking constraint, so a single-store-object override could never name them apart; the `(dependent, principal)` pair key distinguishes `(UserLockout, Users)` from any further fragment naturally. This also tells NamingConventions' follow-up where to hang overrides: on the entity's self-referential row-internal FK.
2. **The relational model consumes the resolvers untouched.** `RelationalModel` builds `UniqueConstraint`/`ForeignKeyConstraint` names by calling these same two functions (including `PrincipalKey.GetName(principalStoreObject)` for cross-table wiring), so correct resolution propagates everywhere — differ input included — for free.
3. **The production shape is a metadata family, per the spec's §4 checklist.** Sites located for the real implementation to parallel (all verified present for `RelationalPropertyOverrides`):
   - Attach/merge machinery: `RelationalPropertyOverrides.Attach`/`MergeInto` (`Metadata/Internal/RelationalPropertyOverrides.cs:107–135`) — needed so overrides survive key/FK re-creation during model building.
   - Runtime model: `RelationalRuntimeModelConvention` converts the overrides annotation to its runtime representation — parallel conversion needed for key/FK overrides.
   - Design-time codegen: `AnnotationCodeGenerator` registers `RelationalOverrides` for handling, and the migrations snapshot generator (EFCore.Design `CSharpSnapshotGenerator`) emits property overrides as store-object-scoped fluent calls — both need key/FK equivalents, which in turn need the public fluent surface to emit.
4. **Spike shortcuts to replace:** raw `Dictionary` annotations (real: `StoreObjectDictionary`-style typed override objects with configuration sources); overrides set post-convention in `OnModelCreating` (real: convention-builder API `key.Builder.HasName(name, storeObject)` + public fluent, with precedence semantics); no explicit-null/unset distinction exercised.

## Residual risks (not exercised)

- Attach-path survival under entity re-parenting / key redefinition (structural finding only).
- Shared-constraint deduplication: two entities table-sharing with matching FKs collapse to one constraint — override conflict resolution across the sharing set is undesigned.
- TPT root-FK behavior and view/function store-object types (resolvers early-return non-`Table` types today; overrides inherit that).
- Snapshot round-trip and compiled-model generation (sites located, not executed).
