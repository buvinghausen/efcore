# Temporal Entity Splitting and Per-Store-Object Facets — Design

**Date:** 2026-08-14 (rev. 3 after second design review)
**Author:** Brian Buvinghausen (with Claude)
**Status:** Spike-validated — both §7 gates passed 2026-08-14 ([Spike 1](2026-08-14-spike1-query-findings.md): prune-based query mechanism, 14/14; [Spike 2](2026-08-14-spike2-constraint-overrides-findings.md): composite key/FK overrides, 5/5); ready for implementation planning. Migrations zero-generator-change remains a tested hypothesis (§3.3); TPT/temporal-fragment period ownership remains deferred (§8)
**Upstream anchors:** [dotnet/efcore#26457](https://github.com/dotnet/efcore/issues/26457) (workstream A), [dotnet/efcore#27972](https://github.com/dotnet/efcore/issues/27972) + [dotnet/efcore#27971](https://github.com/dotnet/efcore/issues/27971) (workstream B)
**Related:** [dotnet/efcore#30366](https://github.com/dotnet/efcore/issues/30366) (NRE repro, closed as dup of #26457), [efcore/EFCore.NamingConventions#396](https://github.com/efcore/EFCore.NamingConventions/pull/396)
**Code evidence:** all file:line references are against dotnet/efcore `main` @ `5e8896500e` (2026-08).

---

## 1. Problem statement

SQL Server temporal table support in EF Core stores every temporal facet (`IsTemporal`, history table name/schema, period property names) as **entity-type-level** annotations. Every scenario in #26457 breaks on exactly that: an entity mapped to more than one table (TPT, TPC, or entity splitting) has one set of temporal facets but N tables.

The driving use case (asymmetric temporality) has **no workaround today**: make the ASP.NET Core Identity `Users` table temporal — history as the durable record backing a field-level crypto-shredding scheme (NIST SP 800-88 cryptographic erase; PII ciphertext under per-subject keys) — while splitting rate-limiter/ephemeral columns (`PasswordHash`, `SecurityStamp`, `ConcurrencyStamp`, `LockoutEnd`, `AccessFailedCount`) to a **non-temporal** fragment table so failed-login churn does not mint history rows. Today `IsTemporal()` + `SplitToTable` is neither supported nor cleanly blocked: all fragments get stamped temporal with one shared history table name and migration generation throws an NRE (#30366).

*Scope of the compliance claim:* EF Core + SQL Server temporal tables supply **historical storage** for this scheme — they are not inherently immutable or tamper-evident, and crypto-erasure guarantees depend on key custody, backups, and rotation outside this design. Nothing here constitutes compliance certification or an audit-log security boundary; the erasure architecture owns those properties.

A second per-store-object gap sits on the same fault line: key and FK constraint **names** are single global annotations, so distinct-per-table constraint names are inexpressible for split entities. EFCore.NamingConventions PR #396 had to *remove* rewritten names for fragment-bearing entities instead of rewriting them per table. Upstream tracks this as #27972 (key facets per table) / #27971 (FK facets per table).

One thesis, two workstreams: **entity splitting needs per-store-object facets, and EF already has the patterns** — `UseSqlOutputClause` (the one existing per-fragment SQL Server facet) and `RelationalPropertyOverrides` (per-store-object column names). Workstream A instantiates the first precedent directly; workstream B is *patterned on* the second but is a new metadata family with its own identity semantics (§4).

## 2. Decisions (locked 2026-08-14; D1/D2/D3 revised same day after design review)

| # | Decision | Choice | Rationale |
|---|----------|--------|-----------|
| D1 | Scope | **Implement tier 1 now** (temporal root + explicitly non-temporal fragments) **with metadata designed to preserve a path toward TPT/TPC** — see the mapping matrix in §8; TPT period-property ownership is an open design question there, deliberately not resolved here | One contained, reviewable PR; unblocks the crypto-erasure work; this doc is the durable memory. "TPT is provably buildable on this metadata" is *not* claimed — §8 records what is proven vs. open |
| D2 | Fragment temporal state | **Metadata fallback is inherited temporality (for forward compatibility); tier-1 validation accepts only fragments with an explicit non-temporal override.** The user-visible tier-1 behavior is rejection of anything else — see the state machine in §3.2 | Resolution order is precedent-consistent with `UseSqlOutputClause`; lifting a validation error later enables temporal fragments without silently changing existing models |
| D3 | Query semantics | **Block: root-table-only.** Temporal operators translate normally; after SQL tree pruning, any query in which a non-temporal fragment table remains referenced is rejected with a purpose-built error. Queries whose final SQL touches only the principal table succeed | Joining current fragment rows next to historical root rows is a disingenuous answer; blocking extends EF's existing "don't silently mix time frames" philosophy. Mechanism is prune-based (§3.4) because a principal-only projection is unsound (§3.4, "rejected alternative") — **spike-gated, see §7** |
| D4 | Interim delivery | **Companion NuGet package + fork as PR vehicle only.** Apps reference official `Microsoft.EntityFrameworkCore.SqlServer`; companion replaces services. Fork exists solely to carry the upstream PRs | No forked base packages to version-manage; rides upstream servicing releases. Workstream B is fork-only (not companionable, see §5) |
| D5 | Workstream B in scope | **Yes** — per-store-object key/FK constraint name overrides in `EFCore.Relational`, separate upstream PR | Same thesis, provider-neutral (fixes the PostgreSQL side too), lets NamingConventions #396 graduate from delete-the-names to rewrite-per-table |
| D6 | Spec home | Orphan `specs` branch on the buvinghausen/efcore fork; moves to the companion package repo when created | Fork `main` stays byte-identical to upstream; upstream custom is that the issue thread is the design record (distilled comments link back here) |

## 3. Workstream A — asymmetric temporal entity splitting (SQL Server)

### 3.1 Metadata: temporal becomes a per-store-object facet

Modeled move-for-move on `UseSqlOutputClause`, the one existing per-fragment SQL Server facet (template: `SqlServerEntityTypeExtensions.cs:355–429`, `SqlServerEntityTypeMappingFragmentExtensions.cs:12–52`, `SqlServerTableExtensions.cs:20–28`, fluent overloads `SqlServerTableBuilderExtensions.cs:327,348` routed through the private helper at `:395–407`).

- **Fragment storage.** `EntityTypeMappingFragment` derives from `ConventionAnnotatable` (`EFCore.Relational/Metadata/Internal/EntityTypeMappingFragment.cs:12–17`), so fragments can carry the existing `SqlServer:IsTemporal` annotation with zero storage changes. New extensions in `SqlServerEntityTypeMappingFragmentExtensions`: `IsTemporal(this IReadOnlyEntityTypeMappingFragment)`, `SetIsTemporal(...)`, `GetIsTemporalConfigurationSource(...)`.
- **Store-object resolution — minimal public surface.** Tier 1 publishes exactly two things: the fragment `IsTemporal(bool)` fluent overloads (below) and the fragment metadata extension surface — for API review, explicitly: `IsTemporal(this IReadOnlyEntityTypeMappingFragment)` (getter), `SetIsTemporal(this IMutableEntityTypeMappingFragment, bool?)`, `SetIsTemporal(this IConventionEntityTypeMappingFragment, bool?, bool fromDataAnnotation)`, and `GetIsTemporalConfigurationSource(this IConventionEntityTypeMappingFragment)`. Store-object-aware resolution — `IsTemporal(entityType, storeObject)`: fragment override wins, else the entity-level value (D2) — and the table-level reader are **internal** helpers. Public store-object overloads for history table name/schema and period column names are **deferred until tier 2 consumes them**: their semantics are still open (a missing fragment history override cannot inherit the root's history name — two temporal tables cannot share one history table; property-name vs. column-name return types stop being interchangeable under the unresolved TPT period-ownership design; behavior for a non-temporal store object is undefined). Freezing that API now would lock in answers §8 deliberately leaves open; the shapes are recorded in §8 as design notes, and the final public read surface is settled at upstream API review.
- **Table-level reader — validated consensus, no canonical mapping.** The internal `IsTemporal(ITable)` reader must be safe when an `ITable` carries multiple entity-type mappings (table splitting, owned types sharing the table, entity-splitting fragments). The existing `ValidateTemporalTableSplitting` (`SqlServerModelValidator.cs:627–690`) is **not sufficient**: it compares entity-level `IsTemporal()` (which stays `true` for a non-temporal fragment of a temporal entity), checks period *column names* but not history table name/schema, and only runs its detailed checks when multiple root types share the table. That validator is therefore **revised** to establish, per `ITable`: (1) every mapping resolves to the same store-object temporal state; (2) if temporal, history table name and schema agree across mappings; (3) if temporal, period start/end column identity and hidden-state facets agree; (4) division of labor for non-temporal tables: validation guarantees coherent per-table metadata, the annotation provider guarantees zero temporal table/column annotations when the resolved table state is non-temporal, and tests enforce both halves. With complete consensus proven, the reader and the annotation provider consume the **single distinct validated value** — no canonical-mapping selection rule exists to pin, and where an actual property object is needed (period properties), it is located through the table's column mappings, never enumeration order. Shared-table and owned-type combinations are explicit test cases (§6).
- **Fluent surface.** `IsTemporal(bool)` overloads on `SplitTableBuilder`, `SplitTableBuilder<TEntity>`, `OwnedNavigationSplitTableBuilder`, `OwnedNavigationSplitTableBuilder<,>` in `SqlServerTableBuilderExtensions`, writing to the fragment. Parameter is `bool` for forward compatibility; in tier 1 only `false` survives validation (D2).
- **Design-time fidelity.** `SqlServerRuntimeModelConvention` (annotation stripping, currently `:134–139`), `SqlServerCSharpRuntimeAnnotationCodeGenerator`, and `SqlServerAnnotationCodeGenerator` (`:344–432`) learn the fragment facet so compiled models, migration snapshots, and scaffolded configurations round-trip it. Public API additions go in `EFCore.SqlServer.baseline.json`; `SqlServerApiConsistencyTest` covers the new builder surface.

Canonical configuration:

```csharp
builder.ToTable("Users", t => t.IsTemporal());
builder.SplitToTable("UserLockout", s =>
{
    s.IsTemporal(false);
    s.Property(u => u.PasswordHash);
    s.Property(u => u.SecurityStamp);
    s.Property(u => u.ConcurrencyStamp);
    s.Property(u => u.LockoutEnd);
    s.Property(u => u.AccessFailedCount);
});
```

### 3.2 Conventions and validation

- **Conventions: no changes.** `SqlServerTemporalConvention` keeps its current behavior (period property creation, join-entity propagation `:68–79`, history-table-name materialization at model finalizing `:141–152`). Fragment inheritance is implicit in resolution order, not a propagation step.
- **Validation: the complete tier-1 state machine.** The existing `ValidateTemporalTable` early-outs for non-temporal entities (`SqlServerModelValidator.cs:460`), which would let a fragment-level `IsTemporal(true)` on a non-temporal entity through unexamined. Fragment validation therefore runs **regardless of the entity-level flag** (structurally: a fragment-aware pass over every entity type with mapping fragments, alongside — not beneath — the existing early return):

| Entity-level | Fragment override | Tier-1 outcome |
|---|---|---|
| non-temporal | absent | ✅ accepted (plain split entity, today's behavior) |
| non-temporal | `false` | ✅ accepted (explicit no-op) |
| non-temporal | `true` | ❌ rejected — temporal fragments not supported; also incoherent without entity-level period/history metadata |
| temporal | absent | ❌ rejected — *"Entity type 'User' is mapped to a temporal table and uses entity splitting. Fragment table 'UserLockout' must be explicitly configured as non-temporal using 'IsTemporal(false)'; temporal split fragments are not supported."* |
| temporal | `true` | ❌ rejected — same not-supported error (the exact error lifted when tier 2 ships) |
| temporal | every fragment `false` | ✅ accepted — the tier-1 feature |
| any | temporal annotation on unsupported mapping kinds (views, functions, sprocs) | ❌ rejected |

- Additionally: period properties must remain mapped to the principal table — configuring `SplitToTable` to carry a period property to a fragment is rejected (it would otherwise produce a temporal root with no period columns).
- **Shared-table consensus validation** is revised per §3.1 (the four per-`ITable` guarantees), replacing rev 2's incorrect claim that the existing `ValidateTemporalTableSplitting` already establishes them.
- Existing `TemporalOnlyOnRoot` (`:465–468`) and TPH-only (`:473–478`) checks stay as-is in tier 1; §8 defines their replacement criteria for the TPT phase.

### 3.3 Migrations pipeline

**Initial hypothesis: the annotation provider is the only required change and the SQL generator needs zero edits.** This is a hypothesis, not a claim — the migration matrix below is the acceptance gate, and any transition operation that falsifies it expands the work.

- **`SqlServerAnnotationProvider.For(ITable)`** (`:89–159`): replace the unfiltered `table.EntityTypeMappings.First()` (`:101` — the #30366 root cause) with the consensus contract from §3.1: gate every temporal annotation on the validated per-table resolution, consuming the single distinct validated value for history/period metadata (period property objects located through the table's column mappings). A fragment table resolves non-temporal, receives zero temporal annotations, and is a plain table to the differ and SQL generator. Same fix in `For(IColumn)` (`:373` has the same unfiltered `.First()`).
- **Why zero generator changes is plausible**: `MigrationsModelDiffer` has no temporal special-casing (copies `ITable` annotations verbatim), and the `RewriteOperations` state map is already keyed `(TableName, Schema)` (`:3061`). The NRE at `:757–761` (delimiting the null period column name on a fragment's `CreateTableOperation`) dies because that operation no longer carries temporal annotations.
- **Acceptance-gate migration matrix** (each row both directions where applicable): initial create (temporal root DDL + plain fragment DDL coexisting); adding a split fragment to an existing temporal table / removing one (fragment table create/drop + explicit backfill obligation / data-loss warning); moving a property between root and fragment (column drop/add with history mirror on the root side only); renaming root table, fragment table, and history table; temporal↔regular conversion of an already-split entity; changing history table name/schema while split; idempotent script generation; diffing a snapshot model against runtime and compiled models.
- **Populated-database transition contract.** Splitting columns out of an already-populated temporal table (the driving Identity scenario) is more than differ output: the fragment table must be created, backfilled one row per existing entity (`INSERT ... SELECT`), and only then may the original columns be dropped from the current table (the temporal rewrite pass handles the history-side column drops under `SYSTEM_VERSIONING = OFF`). EF's differ does not — and per long-standing EF convention will not — generate data movement. **Contract chosen: generated migration scaffolding is structurally correct; the backfill is documented hand-authored SQL** (`migrationBuilder.Sql(...)` inserted between the fragment-create and the column-drops, with a canonical recipe in the companion README). A companion migration helper is a YAGNI-gated follow-up only if the recipe proves painful in practice. A dedicated integration test starts from a populated, already-temporal, unsplit `Users` table, applies the edited migration, and verifies current data integrity plus intended history behavior — fresh-database DDL alone does not prove the driving use case.
- **Snapshot fidelity is load-bearing.** The fragment's explicit `IsTemporal(false)` must survive the model snapshot round-trip or `migrations add` produces phantom diffs / re-fails validation. The relational snapshot generator already serializes fragment annotations; the SQL Server design-time codegen emits the sugar form. Dedicated snapshot round-trip tests (§6) because this is where silent breakage would hide.

### 3.4 Query pipeline

**Mechanism (prune-based; spike-gated per §7):**

1. **Translate normally.** The generic `CreateSelect` path is untouched: it builds the principal `TableExpression` plus one inner join per fragment, and — verified — already marks those joins `prunable: true` (`RelationalQueryableMethodTranslatingExpressionVisitor.CreateSelect.cs:419`). Key properties bind to the **principal** table's columns (`CreateSelect.cs:399–409`), so PK access never creates a fragment dependency.
2. **Stamp selectively.** `TemporalAnnotationApplyingExpressionVisitor` (`SqlServerQueryableMethodTranslatingExpressionVisitor.cs:1108–1116`) changes from "annotate every `TableExpression`" to "annotate only table expressions whose `ITableBase` resolves temporal" via the §3.1 table-level reader.
3. **Prune.** The existing `SqlTreePruner` (`RelationalQueryTranslationPostprocessor.cs:53,86–87`; prunable-join removal `SqlTreePruner.cs:220–235`) removes any fragment join no column references. A query touching only principal-table members compiles to clean single-table `FOR SYSTEM_TIME` SQL with no new machinery.
4. **Reject survivors — precisely scoped.** A postprocessing check (SQL Server query translation postprocessor) walks the pruned tree. "Temporal-annotated" means a table *expression* carrying a temporal-operation annotation (`TemporalOperationType`), **not** an `ITable` whose model metadata is temporal — otherwise ordinary current-time queries over temporal entities would be rejected. Correlation is by entity, not by table shape: for each temporal-annotated table expression, resolve its `ITableBase` → the split entity type(s) mapped to it → the set of that entity's non-temporal fragment `ITableBase`s; a surviving `TableExpression` is rejected **only if its `ITableBase` is in that set**. Unrelated explicit joins to ordinary tables are untouched. The error names the member(s)/table and the remedy ("project members mapped to the principal table").

**The contract for "fragment dependency" falls out of step 4 with no separate definition needed:** any use of a fragment-mapped member in projection, predicates, ordering, grouping, includes, correlated subqueries, set operations, or full-entity materialization (tracking queries included — the shaper demands every property, so fragment columns are referenced and the join survives) is uniformly rejected, because each of these keeps a column reference to the fragment alive through pruning. Explicitly allowed: primary-key access (binds principal, verified above) and `EF.Property` access to period columns (principal-mapped by §3.2).

**Rejected alternative (recorded for reviewers):** constructing a principal-only select/projection for temporal roots is unsound — `StructuralTypeProjectionExpression.BindProperty` directly indexes its property map (`StructuralTypeProjectionExpression.cs:290–295`) and `CreateSelect` enumerates every entity property into that map, so omitting fragment properties produces `KeyNotFoundException`-class failures ahead of any friendly diagnostic.

- **Navigation expansion and set-operation guards unchanged** (`SqlServerNavigationExpansionExtensibilityHelper.cs:35–124`) — they key off entity-level `IsTemporal`, which remains the entity-level truth.
- **SaveChanges untouched**: writes target current tables through existing split-entity machinery; SQL Server maintains history itself.

## 4. Workstream B — per-store-object key/FK constraint names (EFCore.Relational)

- **Gap**: `IKey`/`IForeignKey` name annotations are single global values; `GetName(storeObject)` returns the explicit annotation for fragment tables too, so distinct-per-table constraint names are inexpressible. Symptoms: PostgreSQL `42P07` duplicate-relation on split-entity PK names, colliding linking-FK names (NamingConventions #396, dotnet/efcore#27972/#27971).
- **This is a new metadata family patterned on `RelationalPropertyOverrides`** (per-store-object column names stored as a `StoreObjectDictionary` under `RelationalAnnotationNames.RelationalOverrides`), not a mechanical mirror of it:
  - **Key overrides**: single-store-object identity (`RelationalKeyOverrides`, keyed like property overrides) — a PK/AK constraint materializes per table.
  - **FK overrides**: FK constraint-name resolution is a **table-pair** relationship — `GetConstraintName(in StoreObjectIdentifier storeObject, in StoreObjectIdentifier principalStoreObject)` (`RelationalForeignKeyExtensions.cs:50–53`, verified). Override identity is therefore composite `(dependentStoreObject, principalStoreObject)`; a single-store-object key has the wrong dimensionality. The NamingConventions linking-FK case is the degenerate instance (principal = main table, dependent = fragment), but the design must carry the general identity.
  - **Must-specify semantics** (the full metadata-family checklist): global-name fallback precedence; explicit-null meaning (suppress vs. unset); shared-constraint/root-FK behavior when constraints are deduplicated across tables; `Attach`/`Detach`/`MergeInto` behavior on entity-type re-parenting; convention vs. explicit configuration-source precedence; runtime-model (compiled model) generation; snapshot generation; debug/`ToDebugString` surfacing.
- **Provider-neutral**: fixes the PostgreSQL trigger-based-history side of the same product story.
- **Convention seam**: `key.Builder.HasName(name, storeObject)` / the FK pair equivalent — exactly what NamingConventions needs — plus public fluent equivalents.
- **Upstream shape**: separate PR anchored on #27972/#27971, sequenced before the temporal PR (§7).
- **Not companionable**: `GetName(storeObject)` is a static extension consumed during relational model construction; no DI service replacement can intercept it. Interim delivery is fork-only. Practically acceptable: NamingConventions #396's fallback (per-table default names) already unblocks the collision; workstream B is what makes the *renamed* seam expressible, and NamingConventions gets a follow-up PR to consume it once merged.

## 5. Companion package (workstream A interim delivery)

- Single package (name TBD at repo creation, shape: `*.EntityFrameworkCore.SqlServer.TemporalEntitySplitting`), wired by one call after `UseSqlServer(...)`: `optionsBuilder.UseSqlServerTemporalEntitySplitting()`.
- Ships: the `SplitTableBuilder.IsTemporal(...)` overloads and fragment/store-object getters (new statics, no upstream collision), plus subclass-and-replace for exactly the services the fork patches: `SqlServerModelValidator`, `SqlServerAnnotationProvider`, the queryable-method-translating visitor factory and query translation postprocessor factory, and design-time services.
- **Design-time discovery is explicit, not assumed**: implementing `IDesignTimeServices` in a referenced package is *not* auto-discovered. The package ships a `buildTransitive` `.targets` that injects `DesignTimeServicesReferenceAttribute` into the consuming assembly at compile time via `WriteCodeFragment` — the exact mechanism EF's own extension packages use (verified: `src/EFCore.SqlServer.HierarchyId/build/net11.0/*.targets`, likewise Sqlite/SqlServer NTS). Test matrix includes `dotnet ef` runs where startup project ≠ migrations project.
- **One package line per EF major** (e.g., companion 11.x ↔ EF 11.x, companion 12.x ↔ EF 12.x), each pinned `[major.0.0, major+1.0.0)`; EF1001 (internal API usage) accepted, with a CI canary compiling against each new EF patch release.
- **Fork-first rule**: shared logic is written in the fork (the upstream PR is the single source of truth) and transplanted into the companion's service subclasses. The companion is a delivery adapter, not a second implementation.

## 6. Testing

**Workstream A (fork)** — follow existing patterns located in the code map:

- Validator: `test/EFCore.SqlServer.Tests/Infrastructure/SqlServerModelValidatorTest.cs` temporal region (`:1075–1261`) — the **full §3.2 state machine** (all seven rows), period-property-split rejection, unsupported-mapping-kind rejection.
- Model building: `SqlServerModelBuilderTestBase` temporal tests (`:1355–1770`) + `IsTemporal` shims for `TestSplitTableBuilder` in `SqlServerTestModelBuilderExtensions` (`:65–123` is where the existing shims live).
- Table-level reader consensus: shared-table (table splitting) + owned-types-sharing-table + fragment combinations, asserting results are independent of `EntityTypeMappings` enumeration order.
- Migrations: `MigrationsSqlServerTest.TemporalTables.cs` — the **full §3.3 acceptance-gate matrix**, plus the populated-database transition integration test (§3.3): populated already-temporal unsplit table → edited migration with hand-authored backfill → current data and history behavior verified.
- Snapshot round-trip: fragment `IsTemporal(false)` survives snapshot → re-read → no phantom diff; compiled-model equivalence.
- Query: every temporal operator × {root-only projection succeeds with single-table `FOR SYSTEM_TIME` SQL and a pruned fragment join; fragment member in projection / predicate / ordering / grouping / Include / correlated subquery / set operation errors with the guided message; full-entity materialization and tracking queries error identically; `Any`/`Count` over the split entity succeed (identifier = principal PK); compiled queries}.

**Workstream B (fork)**: override storage/resolution unit tests in `EFCore.Relational.Tests` covering the §4 checklist (fallback precedence, composite FK identity, attach/merge, snapshot, runtime model); differ tests proving per-table PK/linking-FK names flow into migration operations; a NamingConventions-shaped end-to-end (rewrite per store object, no collision).

**Companion**: integration suite against SQL Server with the ASP.NET Identity shape as the canonical fixture (temporal `Users`, non-temporal `UserLockout` fragment); migration-generation smoke tests through the design-time pipeline including separate startup/migrations projects.

## 7. Sequencing — spikes first, then PRs

1. **Spike 1 (query, gates D3):** throwaway prototype of §3.4 steps 2–4 proving: `Select` projections, `Where`, `OrderBy`, `Any`, `Count`, `GroupBy`, correlated subqueries, set operations, tracking vs. no-tracking, and compiled queries all either produce correct single-table temporal SQL or hit the guided error — never an internal exception. Output is a findings report; code is discarded.
2. **Spike 2 (workstream B metadata shape):** prototype the FK composite-identity override storage through attach/snapshot/runtime-model paths to validate the §4 checklist before the upstream PR is drafted.
3. **Workstream B PR** — provider-neutral, anchored on #27972/#27971. Preferred first (smaller, least controversial), but **A has no implementation dependency on B**: if B stalls in API review, the temporal PR proceeds independently rather than delaying the primary use case.
4. **Design comment on #26457** before the workstream A PR — the issue thread is the EF team's design record. Distills: per-store-object facet on the `UseSqlOutputClause` precedent, D2 resolution/validation split, prune-based root-table-only query semantics, and the §8 matrix, explicitly inviting API review before code lands.
5. **Workstream A PR** against #26457.
6. **NamingConventions follow-up PR** consuming workstream B once merged.
7. Timeline expectation: too late for .NET 11; target the .NET 12 development window. Fork + companion cover the interim.

## 8. Roadmap — mapping matrix and what builds on this later

What tier 1's metadata *proves* vs. deliberately leaves open:

| Mapping | Temporal owner | History facet storage | Period-property ownership | Status |
|---|---|---|---|---|
| Entity splitting | root + per-fragment override | entity-level + fragment override (§3.1, proven by tier 1) | principal table only (validated §3.2) | **Tier 1 — this design** |
| Temporal fragments (tier 2) | each fragment | fragment-level history facets (deferred API — see notes below) | per-fragment period columns — property-mapping design needed | Open: period properties per fragment |
| TPT (tier 3) | each hierarchy table | entity/store-object overloads per hierarchy table | **Open question**: an inherited period property maps to one table; each hierarchy table needs its own period columns — distinct shadow properties per type, repeated mapping of inherited properties, or table-only columns. Not resolved by this design | Open: period-property ownership is the crux |
| TPC (tier 3) | each concrete table | concrete-type store-object overloads | likely per-concrete-type properties (TPC already duplicates columns) | Open, appears more tractable than TPT |

**Deferred public API shapes (recorded, not frozen — see §3.1):** store-object overloads for history table name/schema and period column names are design notes only until tier 2 consumes them. Open semantic questions to be answered at that point: (a) a fragment with no history override must **not** inherit the root's history name — two temporal tables cannot share one history table — so is the fallback a generated per-fragment default or a required explicit value? (b) do the period overloads return property names or column names (these diverge under TPT)? (c) where do fragment-specific history *setters* live (fragment builder vs. a fragment-scoped temporal builder)? (d) what do the overloads return for a non-temporal store object — `null`, default, or throw?

Future phases as error-lifts *where proven*: tier 2 lifts §3.2's temporal-fragment rejection and consumes the deferred store-object history facets (property-mapping design still required); tier 3 retires `TemporalOnlyOnRoot` + the TPH-only check (`SqlServerModelValidator.cs:465–478`) once the period-ownership question is settled. Query-side for TPT: `AsOf` composes naturally under the §3.4 selective-stamping model (all hierarchy tables temporal → all stamped); range operators over joined histories have row-multiplication semantics needing their own design and will likely stay restricted, mirroring today's AsOf-only navigation rule. The D3 block for non-temporal fragments is itself the reserved extension point: a future explicit opt-in current-row join can relax it if a real use case appears.

## 9. Evidence appendix — primary touch points

| Concern | File | Lines |
|---|---|---|
| Temporal annotation names | `src/EFCore.SqlServer/Metadata/Internal/SqlServerAnnotationNames.cs` | 188–324 |
| Entity-level temporal getters/setters | `src/EFCore.SqlServer/Extensions/SqlServerEntityTypeExtensions.cs` | 65–300 |
| Per-fragment facet template (`UseSqlOutputClause`) | same file; `SqlServerEntityTypeMappingFragmentExtensions.cs`; `SqlServerTableExtensions.cs` | 355–429; 12–52; 20–28 |
| Fluent `IsTemporal` overloads (none for split builders today) | `src/EFCore.SqlServer/Extensions/SqlServerTableBuilderExtensions.cs` | 13–188; split-builder template 327–407 |
| `SplitTableBuilder` internals | `src/EFCore.Relational/Metadata/Builders/SplitTableBuilder.cs` | 22–132 |
| Temporal convention | `src/EFCore.SqlServer/Metadata/Conventions/SqlServerTemporalConvention.cs` | 16–152 |
| Validation (early-out `:460`; TPH check `:473`; table-sharing consensus `:627`) | `src/EFCore.SqlServer/Infrastructure/Internal/SqlServerModelValidator.cs` | 456–690 |
| Annotation provider (#30366 root cause) | `src/EFCore.SqlServer/Metadata/Internal/SqlServerAnnotationProvider.cs` | 89–159 (`:101`), 313–416 (`:373`) |
| Migrations SQL generator (NRE site; per-table rewrite map) | `src/EFCore.SqlServer/Migrations/SqlServerMigrationsSqlGenerator.cs` | 720–877 (`:757–761`), 3041–3752 (`:3061`) |
| Temporal query root translation + annotation visitor | `src/EFCore.SqlServer/Query/Internal/SqlServerQueryableMethodTranslatingExpressionVisitor.cs` | 351–393, 1108–1116 |
| Split-entity select construction (prunable fragment joins `:419`; principal-bound keys `:399–409`) | `src/EFCore.Relational/Query/RelationalQueryableMethodTranslatingExpressionVisitor.CreateSelect.cs` | 385–432 |
| Projection binding (why principal-only projection is unsound) | `src/EFCore.Relational/Query/StructuralTypeProjectionExpression.cs` | 290–295 |
| SQL tree pruning (prunable-join removal) | `src/EFCore.Relational/Query/SqlTreePruner.cs`; `RelationalQueryTranslationPostprocessor.cs` | 220–235; 53, 86–87 |
| Navigation-expansion temporal guards | `src/EFCore.SqlServer/Query/Internal/SqlServerNavigationExpansionExtensibilityHelper.cs` | 35–124 |
| Fragment metadata / per-store-object storage | `src/EFCore.Relational/Metadata/Internal/EntityTypeMappingFragment.cs`; `RelationalEntityTypeExtensions.cs` | 12–260; 1017–1201 |
| Column-name-per-store-object precedent (workstream B pattern source) | `src/EFCore.Relational/Extensions/RelationalPropertyExtensions.cs` (`RelationalOverrides`) | 45–71 |
| FK constraint-name pair resolution (workstream B composite identity) | `src/EFCore.Relational/Extensions/RelationalForeignKeyExtensions.cs` | 24–99 |
| Design-time discovery mechanism (targets-injected attribute) | `src/EFCore.SqlServer.HierarchyId/build/net11.0/*.targets`; `src/EFCore/Design/DesignTimeServicesReferenceAttribute.cs` | — |
| Design-time codegen | `src/EFCore.SqlServer/Design/Internal/SqlServerAnnotationCodeGenerator.cs` | 344–432 |
