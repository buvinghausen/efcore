## Drafted PR — title and body, ready to paste

**Title:**
```
Support per-store-object key and foreign key constraint names
```

**Body:**
```markdown
Addresses #27972 and #27971.

Key overrides are keyed by a single store object; foreign key overrides are
keyed by the (dependent, principal) store-object pair, because one model
foreign key serves every entity-splitting fragment's linking constraint.

Unblocks efcore/EFCore.NamingConventions#396, which currently has to remove
rewritten names for fragment-bearing entities rather than rewrite them per
table.

## Metadata checklist (spec §4)

Covered:
- Global-name fallback precedence and explicit-null semantics (an override set
  to null suppresses a global rewritten name down to the default) — both the
  in-memory `RelationalRuntimeModelConvention.Create` path and the NativeAOT
  codegen `Create` helpers are covered separately, since they are independent
  conversion paths.
- Shared-constraint conflict handling: conflicting overrides on a constraint
  that deduplicates across table-sharing entity types are a validation error
  rather than last-writer-wins.
- `Attach`/`MergeInto` on key and FK re-creation (entity-type re-parenting,
  detach/reattach during model building).
- Convention-versus-explicit configuration sources.
- Runtime-model (compiled-model) generation and migrations-snapshot generation.
- Debug output (`ToDebugString`).

See `RelationalKeyOverridesTest` and `RelationalForeignKeyOverridesTest` for
the metadata-layer coverage, and `CompiledModelSqliteTest`'s four
`*survive*_the_compiled_model` tests plus `CSharpMigrationsGeneratorTest`'s
snapshot tests for the round-trip coverage.

Deliberately left open:
- TPT root-FK behaviour.
- View and function store-object types: the resolvers early-return for
  non-table store objects today, and the overrides inherit that.

## Proof it solves the driving problem

`Rewriting_constraint_names_per_store_object_produces_no_collisions`
(`MigrationsModelDifferTest`) reproduces the EFCore.NamingConventions#396
collision shape end-to-end: a model-finalizing convention appended after
`EntitySplittingConvention` attaches per-store-object names to the linking FK
the instant it's created, and the finalized `RelationalModel` comes out with
zero duplicate constraint names across the split tables.

## Drive-by findings (not fixed on this branch)

- `IConventionRelationalPropertyOverrides.cs:20` has the same "Gets the builder
  that can be used to configure this function" / "If the function has been
  removed" copy/paste error that this PR fixes on the two interfaces it added
  (`IConventionRelationalKeyOverrides`, `IConventionRelationalForeignKeyOverrides`).
  Left alone here so this PR's diff stays single-subject; worth a follow-up.
- `TestReferenceCollectionBuilder`'s Generic/NonGeneric implementations
  (`ModelBuilderTest.Generic.cs:1496,1630`) implement neither
  `IInfrastructure<ReferenceCollectionBuilder<,>>` nor the non-generic form,
  making the pre-existing single-arg `HasConstraintName(string)` test wrapper
  (`RelationalTestModelBuilderExtensions.cs:1586-1603`) dead code. Worked around
  via `GetInfrastructure()` bridging for this PR's new tests; not fixed upstream.

## Test coverage

- `EFCore.Relational.Tests`: 1509 run, **0 failed**, 1 skipped, including
  18 new tests in `RelationalKeyOverridesTest`/`RelationalForeignKeyOverridesTest`.
- `EFCore.Sqlite.FunctionalTests`: 38227 run, **0 failed**, 286 skipped.
  The 4 new compiled-model round-trip tests pass.
- `EFCore.Design.Tests`: 1249 run, **0 failed**. The tests in
  `CSharpMigrationsGeneratorTest` (including this PR's snapshot-generation
  tests) all pass.
- `EFCore.ApiBaseline.Tests`: 14 run, **0 failed**.
- `EFCore.SqlServer.FunctionalTests`: 50861 run, **0 failed**, 425 skipped.
  All seven projects in the repo's `SQLSERVER_TEST_PROJECTS` CI list were run
  (SqlServer.FunctionalTests, SqlServer.HierarchyId.Tests, CrossStore,
  OData, AspNet.SqlServer, VisualBasic, FSharp): 51118 tests, **0 failed**.
  Run against SQL Server 2025 CU8 (17.0.4075.5) with full-text search
  installed, using the connection string from `.github/workflows/Build.yml`.

All figures are from the branch rebased onto `main` @ ecd0ba4c23, with the
runner argument `--filter-not-trait category=failing` that the build normally
supplies. 92117 tests across 11 projects, 0 failures.

🤖 Generated with [Claude Code](https://claude.com/claude-code)
```
