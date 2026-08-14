# Temporal Entity Splitting and Per-Store-Object Facets — Design

**Date:** 2026-08-14
**Author:** Brian Buvinghausen (with Claude)
**Status:** Approved design, pre-implementation
**Upstream anchors:** [dotnet/efcore#26457](https://github.com/dotnet/efcore/issues/26457) (workstream A), [dotnet/efcore#27972](https://github.com/dotnet/efcore/issues/27972) + [dotnet/efcore#27971](https://github.com/dotnet/efcore/issues/27971) (workstream B)
**Related:** [dotnet/efcore#30366](https://github.com/dotnet/efcore/issues/30366) (NRE repro, closed as dup of #26457), [efcore/EFCore.NamingConventions#396](https://github.com/efcore/EFCore.NamingConventions/pull/396)
**Code evidence:** all file:line references are against dotnet/efcore `main` @ `5e8896500e` (2026-08).

---

## 1. Problem statement

SQL Server temporal table support in EF Core stores every temporal facet (`IsTemporal`, history table name/schema, period property names) as **entity-type-level** annotations. Every scenario in #26457 breaks on exactly that: an entity mapped to more than one table (TPT, TPC, or entity splitting) has one set of temporal facets but N tables.

The driving use case (asymmetric temporality) has **no workaround today**: make the ASP.NET Core Identity `Users` table temporal — history as the compliance artifact for field-level crypto-shredding (NIST SP 800-88 cryptographic erase; PII ciphertext under per-subject keys) — while splitting rate-limiter/ephemeral columns (`PasswordHash`, `SecurityStamp`, `ConcurrencyStamp`, `LockoutEnd`, `AccessFailedCount`) to a **non-temporal** fragment table so failed-login churn does not mint history rows. Today `IsTemporal()` + `SplitToTable` is neither supported nor cleanly blocked: all fragments get stamped temporal with one shared history table name and migration generation throws an NRE (#30366).

A second per-store-object gap sits on the same fault line: key and FK constraint **names** are single global annotations, so distinct-per-table constraint names are inexpressible for split entities. EFCore.NamingConventions PR #396 had to *remove* rewritten names for fragment-bearing entities instead of rewriting them per table. Upstream tracks this as #27972 (key facets per table) / #27971 (FK facets per table).

One thesis, two workstreams: **entity splitting needs per-store-object facets, and EF already has the patterns** — `UseSqlOutputClause` (the one existing per-fragment SQL Server facet) and `RelationalPropertyOverrides` (per-store-object column names). Both workstreams instantiate existing precedent rather than inventing new architecture.

## 2. Decisions (locked 2026-08-14)

| # | Decision | Choice | Rationale |
|---|----------|--------|-----------|
| D1 | Scope | **Design the per-store-object temporal facet against the full issue (TPT/TPC provably buildable on it); implement only tier 1 now**: temporal root + explicitly non-temporal fragments | One contained, reviewable PR; unblocks the crypto-erasure work; this doc is the durable memory so the later tiers are additive, not a redesign |
| D2 | API default for unconfigured fragments | **Inherit + require explicit opt-out.** Entity-level `IsTemporal()` defaults every table temporal (consistent with `UseSqlOutputClause` inheritance); tier 1 validation requires an explicit `IsTemporal(false)` per fragment, clear error otherwise | Precedent-consistent; lifting the error later enables temporal fragments without silently changing existing models |
| D3 | Query semantics | **Block: projection-only.** Temporal operators build the select from the principal table alone; the fragment join is never emitted; touching any fragment-mapped member (including full-entity materialization) throws a purpose-built translation error | Joining current fragment rows next to historical root rows is a disingenuous answer; blocking extends EF's existing "don't silently mix time frames" philosophy (`TemporalNavigationExpansionBetweenTemporalAndNonTemporal`) |
| D4 | Interim delivery | **Companion NuGet package + fork as PR vehicle only.** Apps reference official `Microsoft.EntityFrameworkCore.SqlServer`; companion replaces services. Fork exists solely to carry the upstream PRs | No forked base packages to version-manage; rides upstream servicing releases. Workstream B is fork-only (not companionable, see §5) |
| D5 | Workstream B in scope | **Yes** — per-store-object key/FK constraint name overrides in `EFCore.Relational`, separate upstream PR | Same thesis, provider-neutral (fixes the PostgreSQL side too), lets NamingConventions #396 graduate from delete-the-names to rewrite-per-table |
| D6 | Spec home | Orphan `specs` branch on the buvinghausen/efcore fork; moves to the companion package repo when created | Fork `main` stays byte-identical to upstream; upstream custom is that the issue thread is the design record (distilled comments link back here) |

## 3. Workstream A — asymmetric temporal entity splitting (SQL Server)

### 3.1 Metadata: temporal becomes a per-store-object facet

Modeled move-for-move on `UseSqlOutputClause`, the one existing per-fragment SQL Server facet (template: `SqlServerEntityTypeExtensions.cs:355–429`, `SqlServerEntityTypeMappingFragmentExtensions.cs:12–52`, `SqlServerTableExtensions.cs:20–28`, fluent overloads `SqlServerTableBuilderExtensions.cs:327,348` routed through the private helper at `:395–407`).

- **Fragment storage.** `EntityTypeMappingFragment` derives from `ConventionAnnotatable` (`EFCore.Relational/Metadata/Internal/EntityTypeMappingFragment.cs:12–17`), so fragments can carry the existing `SqlServer:IsTemporal` annotation with zero storage changes. New extensions in `SqlServerEntityTypeMappingFragmentExtensions`: `IsTemporal(this IReadOnlyEntityTypeMappingFragment)`, `SetIsTemporal(...)`, `GetIsTemporalConfigurationSource(...)`.
- **Store-object resolution.** New overloads in `SqlServerEntityTypeExtensions`: `IsTemporal(this IReadOnlyEntityType, in StoreObjectIdentifier)` — fragment override wins, else the entity-level value (D2 inheritance). The full-issue design also defines (tier 1 declares, later tiers consume): `GetHistoryTableName/Schema(entityType, storeObject)` and period-column-name store-object overloads on the same pattern.
- **Table-level reader.** `SqlServerTableExtensions.IsTemporal(this ITable)` resolving across `EntityTypeMappings`. This becomes the single downstream question: "is *this table* temporal," never "is this entity temporal."
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
- **Validation** — three new checks in `SqlServerModelValidator.ValidateTemporalTable` (`:456–479`) when a temporal entity has table mapping fragments:
  1. Every fragment must have an **explicitly configured** `IsTemporal(false)`. Error (new resource, name indicative): *"Entity type 'User' is mapped to a temporal table and uses entity splitting. Fragment table 'UserLockout' must be explicitly configured as non-temporal using 'IsTemporal(false)'; temporal split fragments are not supported."*
  2. A fragment explicitly configured `IsTemporal(true)` → the same not-supported error. This converts the #30366 NRE into an intentional, documented error and is the exact error lifted when temporal fragments ship (tier 2).
  3. Period properties must remain mapped to the principal table — configuring `SplitToTable` to carry a period property to a fragment is an error (it would otherwise produce a temporal root with no period columns).
- Existing `TemporalOnlyOnRoot` (`:465–468`) and TPH-only (`:473–478`) checks stay as-is in tier 1; §7 defines their replacement criteria for the TPT phase.

### 3.3 Migrations pipeline

The entire fix is making the annotation provider tell the truth per table; everything downstream already works.

- **`SqlServerAnnotationProvider.For(ITable)`** (`:89–159`): resolve the entity type from the *principal* mapping instead of the unfiltered `table.EntityTypeMappings.First()` (`:101` — the #30366 root cause), then gate every temporal annotation on `entityType.IsTemporal(StoreObjectIdentifier.Table(table.Name, table.Schema))`. A fragment table resolves `false`, receives zero temporal annotations, and is a plain table to the differ and SQL generator. Same fix in `For(IColumn)` (`:373` has the same unfiltered `.First()`).
- **`SqlServerMigrationsSqlGenerator`: expected zero changes.** `MigrationsModelDiffer` has no temporal special-casing (copies `ITable` annotations verbatim), and the `RewriteOperations` state map is already keyed `(TableName, Schema)` (`:3061`). The NRE at `:757–761` (delimiting the null period column name on a fragment's `CreateTableOperation`) dies because that operation no longer carries temporal annotations.
- **Snapshot fidelity is load-bearing.** The fragment's explicit `IsTemporal(false)` must survive the model snapshot round-trip or `migrations add` produces phantom diffs / re-fails validation. The relational snapshot generator already serializes fragment annotations; the SQL Server design-time codegen emits the sugar form. Dedicated snapshot round-trip tests (§6) because this is where silent breakage would hide.

### 3.4 Query pipeline

- **Temporal query roots over a split entity build the select from the principal mapping only** — the fragment join is never emitted. The SQL Server `VisitExtension` for temporal roots (`SqlServerQueryableMethodTranslatingExpressionVisitor.cs:351–393`) detects mapping fragments and constructs the single-table select itself instead of the generic `CreateSelect` (which unconditionally inner-joins every fragment: `RelationalQueryableMethodTranslatingExpressionVisitor.CreateSelect.cs:385–432`). `TemporalAnnotationApplyingExpressionVisitor` (`:1108–1116`) then stamps `FOR SYSTEM_TIME` on what is now guaranteed to be exactly one table.
- **Fragment-mapped member access under a temporal operator throws a purpose-built translation error** naming the member, the fragment table, and the remedy (project members mapped to the principal table). Full-entity materialization is the general case (the shaper demands every property) and produces the same guided error, never a generic translation failure.
- **Navigation expansion and set-operation guards unchanged** (`SqlServerNavigationExpansionExtensibilityHelper.cs:35–124`) — they key off entity-level `IsTemporal`, which remains the entity-level truth.
- **SaveChanges untouched**: writes target current tables through existing split-entity machinery; SQL Server maintains history itself.

## 4. Workstream B — per-store-object key/FK constraint names (EFCore.Relational)

- **Gap**: `IKey`/`IForeignKey` name annotations are single global values; `GetName(storeObject)` returns the explicit annotation for fragment tables too, so distinct-per-table constraint names are inexpressible. Symptoms: PostgreSQL `42P07` duplicate-relation on split-entity PK names, colliding linking-FK names (NamingConventions #396, dotnet/efcore#27972/#27971).
- **Design template**: mirror `RelationalPropertyOverrides` (per-store-object column names, stored as a `StoreObjectDictionary` under `RelationalAnnotationNames.RelationalOverrides`) for keys and FKs: `RelationalKeyOverrides` / `RelationalForeignKeyOverrides` storage, `GetName(storeObject)` consults the override before the global annotation, convention-builder surface (`key.Builder.HasName(name, storeObject)` — the exact seam NamingConventions needs) plus the public fluent equivalent.
- **Provider-neutral**: fixes the PostgreSQL trigger-based-history side of the same product story.
- **Upstream shape**: separate PR anchored on #27972/#27971, independent of and sequenced before the temporal PR (§7) — smaller, no API-shape controversy.
- **Not companionable**: `GetName(storeObject)` is a static extension consumed during relational model construction; no DI service replacement can intercept it. Interim delivery is fork-only. Practically acceptable: NamingConventions #396's fallback (per-table default names) already unblocks the collision; workstream B is what makes the *renamed* seam expressible, and NamingConventions gets a follow-up PR to consume it once merged.

## 5. Companion package (workstream A interim delivery)

- Single package (name TBD at repo creation, shape: `*.EntityFrameworkCore.SqlServer.TemporalEntitySplitting`), wired by one call after `UseSqlServer(...)`: `optionsBuilder.UseSqlServerTemporalEntitySplitting()`.
- Ships: the `SplitTableBuilder.IsTemporal(...)` overloads and fragment/store-object getters (new statics, no upstream collision), plus subclass-and-replace for exactly the services the fork patches: `SqlServerModelValidator`, `SqlServerAnnotationProvider`, the queryable-method-translating visitor factory, and an `IDesignTimeServices` registration so `dotnet ef migrations add` sees identical behavior.
- EF1001 (internal API usage) accepted and pinned: package versions track EF minors (`[10.0.0,11.0.0)` style) with a CI canary compiling against each new EF patch release.
- **Fork-first rule**: shared logic is written in the fork (the upstream PR is the single source of truth) and transplanted into the companion's service subclasses. The companion is a delivery adapter, not a second implementation.

## 6. Testing

**Workstream A (fork)** — follow existing patterns located in the code map:

- Validator: `test/EFCore.SqlServer.Tests/Infrastructure/SqlServerModelValidatorTest.cs` temporal region (`:1075–1261`) — unconfigured-fragment error, explicit-true error, period-property-split error, happy path.
- Model building: `SqlServerModelBuilderTestBase` temporal tests (`:1355–1770`) + `IsTemporal` shims for `TestSplitTableBuilder` in `SqlServerTestModelBuilderExtensions` (`:65–123` is where the existing shims live).
- Migrations: `MigrationsSqlServerTest.TemporalTables.cs` split-entity matrix — create (temporal root DDL + plain fragment DDL coexisting), drop, rename, add column to root (history mirror) vs. fragment (no mirror), temporal↔regular conversion of an already-split entity.
- Snapshot round-trip: fragment `IsTemporal(false)` survives snapshot → re-read → no phantom diff.
- Query: every temporal operator × {root-only projection succeeds with single-table `FOR SYSTEM_TIME` SQL, fragment-member access errors with the guided message, full-entity materialization errors identically}.

**Workstream B (fork)**: override storage/resolution unit tests in `EFCore.Relational.Tests`; differ tests proving per-table PK/linking-FK names flow into migration operations; a NamingConventions-shaped end-to-end (rewrite per store object, no collision).

**Companion**: integration suite against SQL Server with the ASP.NET Identity shape as the canonical fixture (temporal `Users`, non-temporal `UserLockout` fragment); migration-generation smoke test through the design-time pipeline.

## 7. Upstream strategy and sequencing

1. **Workstream B PR first** — provider-neutral, mirrors `RelationalPropertyOverrides` exactly, anchored on #27972/#27971.
2. **Design comment on #26457 before the workstream A PR** — the issue thread is the EF team's design record. The comment distills: per-store-object facet on the `UseSqlOutputClause` precedent, inherit-with-explicit-opt-out (D2), projection-only query semantics (D3), and the roadmap (§8), explicitly inviting API review before code lands.
3. **Workstream A PR** against #26457.
4. **NamingConventions follow-up PR** consuming workstream B once merged.
5. Timeline expectation: too late for .NET 11; target the .NET 12 development window. Fork + companion cover the interim.

## 8. Roadmap — what builds on this later (deliberately out of scope now)

Each future phase is "which validation error gets lifted and what new code consumes the already-specced metadata":

- **Tier 2 — temporal fragments** (each split table with its own history table): lift validation error §3.2(2); consume the per-store-object `GetHistoryTableName/Schema(entityType, storeObject)` overloads; per-fragment period columns and DDL; annotation provider emits per-fragment history names. No metadata redesign.
- **Tier 3 — TPT/TPC temporal**: retire `TemporalOnlyOnRoot` + the TPH-only check (`SqlServerModelValidator.cs:465–478`); derived-type configuration surface; per-hierarchy-table history via the same store-object overloads; query semantics — `AsOf` composes naturally (the annotation visitor already stamps every table expression; with all hierarchy tables temporal that is correct), range operators (`FromTo`/`Between`/`ContainedIn`/`All`) over joined histories have row-multiplication semantics that need their own design and will likely stay restricted, mirroring today's AsOf-only navigation rule.
- **Non-temporal-fragment query refinement**: if a real use case appears, D3's block can be relaxed to an explicit opt-in current-row join; the blocking error is the reserved extension point.

## 9. Evidence appendix — primary touch points

| Concern | File | Lines |
|---|---|---|
| Temporal annotation names | `src/EFCore.SqlServer/Metadata/Internal/SqlServerAnnotationNames.cs` | 188–324 |
| Entity-level temporal getters/setters | `src/EFCore.SqlServer/Extensions/SqlServerEntityTypeExtensions.cs` | 65–300 |
| Per-fragment facet template (`UseSqlOutputClause`) | same file; `SqlServerEntityTypeMappingFragmentExtensions.cs`; `SqlServerTableExtensions.cs` | 355–429; 12–52; 20–28 |
| Fluent `IsTemporal` overloads (none for split builders today) | `src/EFCore.SqlServer/Extensions/SqlServerTableBuilderExtensions.cs` | 13–188; split-builder template 327–407 |
| `SplitTableBuilder` internals | `src/EFCore.Relational/Metadata/Builders/SplitTableBuilder.cs` | 22–132 |
| Temporal convention | `src/EFCore.SqlServer/Metadata/Conventions/SqlServerTemporalConvention.cs` | 16–152 |
| Validation | `src/EFCore.SqlServer/Infrastructure/Internal/SqlServerModelValidator.cs` | 456–690 |
| Annotation provider (#30366 root cause) | `src/EFCore.SqlServer/Metadata/Internal/SqlServerAnnotationProvider.cs` | 89–159 (`:101`), 313–416 (`:373`) |
| Migrations SQL generator (NRE site; per-table rewrite map) | `src/EFCore.SqlServer/Migrations/SqlServerMigrationsSqlGenerator.cs` | 720–877 (`:757–761`), 3041–3752 (`:3061`) |
| Temporal query root translation + annotation visitor | `src/EFCore.SqlServer/Query/Internal/SqlServerQueryableMethodTranslatingExpressionVisitor.cs` | 351–393, 1108–1116 |
| Split-entity select construction | `src/EFCore.Relational/Query/RelationalQueryableMethodTranslatingExpressionVisitor.CreateSelect.cs` | 385–432 |
| Navigation-expansion temporal guards | `src/EFCore.SqlServer/Query/Internal/SqlServerNavigationExpansionExtensibilityHelper.cs` | 35–124 |
| Fragment metadata / per-store-object storage | `src/EFCore.Relational/Metadata/Internal/EntityTypeMappingFragment.cs`; `RelationalEntityTypeExtensions.cs` | 12–260; 1017–1201 |
| Column-name-per-store-object precedent (workstream B template) | `src/EFCore.Relational/Extensions/RelationalPropertyExtensions.cs` (`RelationalOverrides`) | 45–71 |
| Design-time codegen | `src/EFCore.SqlServer/Design/Internal/SqlServerAnnotationCodeGenerator.cs` | 344–432 |
