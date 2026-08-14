# Spike 1 — Prune-Based Temporal Split Query Mechanism: Findings

**Date:** 2026-08-14
**Gates:** decision D3 of [the design spec](2026-08-14-temporal-entity-splitting-design.md) (§3.4 mechanism, §7 item 1)
**Environment:** dotnet/efcore `main` @ `5e8896500e`, linux-arm64, .NET 11 preview.7 runtime (test host's pinned `rc.1.26409.102` daily runtime not installed locally; runtimeconfig pointed at `11.0.0-preview.7.26381.103` — translation-layer behavior unaffected)
**Artifacts:** `spike1.patch` (the two-file provider patch, ~90 lines), `spike1-harness.cs.txt` (test harness), both committed alongside this report. All spike code is throwaway and has been reverted from the working tree.

## Verdict: PASS

The §3.4 mechanism — translate normally, stamp the temporal-operation annotation only on temporal tables, let the existing pruner remove unreferenced fragment joins, reject any select where a fragment table survives — works end to end. **14/14 harness tests green.** No exercised query shape produced an internal exception; every rejection surfaced the guided error.

## What was patched (2 sites)

1. `SqlServerQueryableMethodTranslatingExpressionVisitor.VisitExtension` — wrapped the temporal `annotationApplyingFunc` to stamp only the query root entity's principal table (spike shortcut: matched by table name/schema; the real implementation resolves per-table temporal state via the §3.1 consensus reader).
2. `SqlServerQueryTranslationPostprocessor.Process` — after `base.Process` (which prunes), a survivor check: collect table expressions carrying `TemporalOperationType`; for each, ban the mapping-fragment tables of its mapped entity types; throw a guided `InvalidOperationException` if any banned table survived pruning.

## Results by shape

| Shape | Result |
|---|---|
| `TemporalAsOf` + root-only projection | ✅ single-table `FROM [Users] FOR SYSTEM_TIME AS OF ...`; fragment join pruned |
| `TemporalAll` + root projection | ✅ `FOR SYSTEM_TIME ALL`, single table |
| `Where` root + root projection | ✅ |
| Projection of fragment member | ✅ guided error naming `UserLockout` |
| `Where` on fragment member | ✅ guided error |
| `OrderBy` fragment / root | ✅ error / clean SQL respectively |
| `GroupBy` root + aggregate | ✅ single table |
| Full-entity materialization (tracking default) | ✅ guided error (shaper demands fragment columns → join survives) |
| `Count()` / `Any(predicate)` (terminal, execution attempted) | ✅ translation clean (failure was connection-level only, never the guided error) |
| Set operation (`Concat` of two temporal root projections) | ✅ single-table SQL both sides |
| Correlated subquery, root-only / touching fragment | ✅ clean SQL / guided error |
| Non-temporal query over the same split entity | ✅ untouched — fragment `INNER JOIN` present, no `FOR SYSTEM_TIME`, no rejection |
| `EF.CompileQuery` + temporal | ⚠️ blocked upstream — see finding 3 |

Sample output (root projection):

```sql
SELECT [u].[Email], [u].[Phone]
FROM [Users] FOR SYSTEM_TIME AS OF '2024-01-01T00:00:00.0000000Z' AS [u]
```

## Findings

1. **The prunable machinery does the heavy lifting.** `CreateSelect` already marks entity-splitting fragment joins `prunable: true` and the `SqlTreePruner` genuinely removes them when only principal columns are referenced (join-predicate references correctly don't count). Key/identifier columns bind to the principal table, so `Count`/`Any`/set-op identifier plumbing never resurrects the fragment.
2. **Prune-survival is a sound and complete dependency test** across every exercised shape: projection, predicate, ordering, grouping, subquery, materialization all reduce to "did a fragment column reference survive," with zero shape-specific code.
3. **Pre-existing upstream bug (out of scope, worth filing):** `EF.CompileQuery` + any temporal operator fails for **all** temporal entities — split or not — with `ArgumentException` in `ExpressionTreeFuncletizer.VisitMethodCall` (it rewrites the `DbSet<T>` source to an `IQueryable<T>`-typed node, then can't rebuild the `TemporalAsOf(DbSet<T>, DateTime)` call). Confirmed by a non-split control entity. No existing dotnet/efcore issue found; candidate bug report. Consequence: compiled-query coverage of the new feature is blocked on that upstream fix, not on this design.
4. **Spike shortcuts to replace in the real implementation:** stamping keyed off root-table name equality (real: per-table temporal facet resolution); survivor ban-list treats *all* mapping fragments as non-temporal (correct for tier 1 by definition, but the real check consults fragment metadata so tier 2 composes).

## Residual risks (not exercised; cover in implementation tests)

- `Include`/navigation expansion into or out of a split temporal entity (harness model had no navigations).
- Owned types sharing the split tables; `EF.Property` access to period columns.
- The NativeAOT/precompiled-query pipeline (distinct from `EF.CompileQuery`).
- Error-message quality: the spike error names the surviving table; the real one should name the offending member(s).
