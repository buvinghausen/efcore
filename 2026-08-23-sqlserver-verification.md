# SQL Server verification — closing Workstream B's last coverage gap

Run 2026-08-23 on an x64 machine. Closes the gap recorded in
[`2026-08-22-workstream-b-execution-record/README.md`](2026-08-22-workstream-b-execution-record/README.md):
*"SQL Server functional tests have never run — this is the one real coverage hole."*

## Result

Branch `feature/per-store-object-constraint-names` @ `992f05fab6`, rebased onto `main` @ `ecd0ba4c23`.

| Group | Projects | Tests | Failed |
|---|---|---|---|
| CI's `SQLSERVER_TEST_PROJECTS` | 7 | 51,118 | **0** |
| Suites covering the `EFCore.Relational` changes | 4 | 40,999 | **0** |
| **Total** | **11** | **92,117** | **0** |

Per project: SqlServer.FunctionalTests 50861/0, SqlServer.HierarchyId.Tests 96/0,
CrossStore 46/0, OData 17/0, AspNet.SqlServer 80/0, VisualBasic 16/0, FSharp 2/0,
Relational.Tests 1509/0, ApiBaseline.Tests 14/0, Design.Tests 1249/0,
Sqlite.FunctionalTests 38227/0.

## Why it could not run before

Microsoft publishes no arm64 image for `mcr.microsoft.com/mssql/server`. The machine that
executed Workstream B was an aarch64 Surface, so a local instance was not merely inconvenient —
it was impossible. This is also why Phase A (SQL Server temporal entity splitting) was never
started.

## Two things this run got wrong before it got them right

Both are worth more than the green numbers, because both made a *wrong* result look like a
*real* one.

### 1. The server version silently gated 894 tests

The first sweep ran on SQL Server **2022** and produced 934 failures. Every one was a missing
server feature, not a defect:

| Count | Cause |
|---|---|
| 880 | `Cannot find data type json` — the native JSON type is 2025+/Azure SQL |
| 14 | `Cannot find data type vector` — likewise 2025+ |
| 37 | `Full-Text Search is not installed` |
| 3 | Azure-only DDL |

931 of those died in class-fixture `InitializeAsync`, so no test body ever executed. Moving to
**2025 CU8** dropped it to 40. Full-text needs a derived image — the stock image carries only the
packages.microsoft.com `prod` feed, which does not publish `mssql-server-fts`; adding the
versioned `mssql-server-2025` feed and installing it closes the last 37.

`.github/workflows/Build.yml` runs a `[2025, 2022, 2019]` matrix, so **2025 is the right local
target** and the older legs are CI's business, not a local dev loop's.

### 2. Running the dll directly dropped an argument the build supplies

This is the more important one, and it invalidated three separately-reported results.

`[ConditionalFact]` and `[ConditionalClass]` **do not skip anything themselves.** They tag
non-matching tests with `category=failing`, and the actual skip comes from a runner argument that
`test/Directory.Build.props` injects:

```
--filter-not-trait category=failing --ignore-exit-code 8
```

That argument is supplied by MSBuild. Executing the test dll directly — the fast inner loop this
project adopted for speed, recorded as ruling F9 — bypasses MSBuild and therefore drops it. Every
environment-gated test then executes and fails.

Three previously-recorded "failures" dissolved once the argument was restored:

- the 3 `SqlAzureDatabaseCreationTest` failures (gated on `IsAzureSql`; server reports
  `EngineEdition=3`),
- the 177 Sqlite `mod_spatialite` failures,
- the 5 `EFCore.Design.Tests` "pre-existing platform failures".

None of them were real. They were the harness reporting tests that were never meant to run.

**Standing rule, superseding F18's:** a direct-dll run is only evidence if it carries
`--filter-not-trait category=failing`. Without it, green is not green and red is not red. F18's
warning — that `build.sh --test` can report green off a stale assembly — still stands; the two
together mean *neither* driver is self-certifying, and a count that fails to move when tests were
added remains the cheapest tell.

## Work done on the branch

One commit, `992f05fab6`: the SQL Server compiled-model baselines for the four
`*_the_compiled_model*` override tests. Those tests live on `CompiledModelRelationalTestBase`, so
`CompiledModelSqlServerTest` inherits them, and each provider project keeps its own baseline
directory under `Scaffolding/Baselines/<TestName>/`. Only the Sqlite baselines had been
generated, so all four failed on SQL Server with a missing-baseline `FileNotFoundException`.

Regenerated with `EF_TEST_REWRITE_BASELINES=1`. The emitted override blocks are identical to the
Sqlite baselines — including the explicit-null
`new RuntimeRelationalKeyOverrides(key, StoreObjectIdentifier.Table("CustomerDetails", null), true, null)`
shape that proves `IsNameOverridden: true, Name: null` survived code generation. The remainder of
the diff is provider type mappings and `SqlServer:ValueGenerationStrategy` annotations.

**Lesson for Phase A:** a test added to a shared relational base class needs baselines regenerated
for *every* provider that inherits it, not just the one being developed against.

## Rebase

The branch was 19 commits behind `main`. Those 19 commits touch exactly one file —
`eng/Versions.props`, dependency bumps — with zero overlap against anything the branch touches.
The rebase of all 29 commits was clean, and all 11 projects rebuilt with 0 warnings / 0 errors.

Note for anyone reading a fork's GitHub page: syncing the fork's `main` does **not** advance a
feature branch cut earlier. "In sync" there refers to `origin/main` vs `upstream/main`, which is a
different comparison from the branch's merge-base.

## Environment, for reproduction

```dockerfile
FROM mcr.microsoft.com/mssql/server:2025-latest
USER root
RUN echo "deb [arch=amd64 signed-by=/usr/share/keyrings/microsoft-prod.gpg] https://packages.microsoft.com/ubuntu/24.04/mssql-server-2025 noble main" \
      > /etc/apt/sources.list.d/mssql-server-2025.list \
    && apt-get update && apt-get install -y --no-install-recommends mssql-server-fts \
    && apt-get clean && rm -rf /var/lib/apt/lists/*
USER mssql
```

Connection string taken verbatim from `Build.yml` so the numbers are comparable to CI:

```
Server=localhost;Database=master;User=SA;Password=...;Connect Timeout=60;ConnectRetryCount=0;Trust Server Certificate=true
```

Verify the server actually has what the tests probe for before trusting a run:
`FULLTEXTSERVICEPROPERTY('IsFullTextInstalled')` plus `sys.types` lookups for `json` and `vector`.

## Still open

- The PR itself. Pushing the branch and opening it upstream was a designated stop-and-ask
  (ledger, B10 Step 3). The branch is pushed to the fork; **no PR has been opened.**
- The 2019 and 2022 matrix legs were not reproduced locally. With the trait filter applied the
  version-gated tests should skip rather than fail, but that is reasoning, not a measurement.
