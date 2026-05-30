# Track A · Session 5: Backfill + recovery-with-sleep-timestamp display

**Goal of this session.** The Track A close-out. Three deliverables:

1. **30-day backfill** of Tai's WHOOP history (recovery, sleep, workout) so Track B's rules engine has real data to operate against from day one
2. **`/me` view enhancement** showing the sleep timestamp alongside the recovery score (the backlog item from prior notes — disambiguates "which night" a given recovery reflects)
3. **ADR 0011 backfill trace shape** — the last remaining open item in the trace contract

Pure cleanup pass. No new architectural patterns, no ingestion logic changes.

---

## Context

Sessions 1-4.5 built the ingestion path (schema, three handlers, drainer fan-out, adaptive poller scheduling). Sessions 2-4 have been ingesting Tai's WHOOP data live since merge, but only forward from each session's merge time. The rules engine (Track B) needs 30 days of history to operate against meaningfully — comparisons against baseline recovery, trailing strain, sleep consistency over time.

Backfill is the bridge from "data flows in real time now" to "we have enough history to model from."

**Patterns established by prior sessions that this session reuses:**

- Ingestion handlers (`IRecoveryIngestionHandler`, `IWhoopSleepIngestionHandler`, `IWhoopWorkoutIngestionHandler`) with the `(Inserted / Updated / NoOp / SkippedNoData)` outcome vocabulary
- `raw_payload` is canonical for recomputability; `score` and typed columns derive from it
- Trace contract per ADR 0011 — root + fetch + handler spans, rollup outcome tags, pre-seed pattern
- `TracingCollection` for any new trace-capturing tests

---

## Architectural decisions (locked before this session)

### 1. Backfill bypasses the poller

Backfill is on-demand, not scheduled. It invokes the ingestion handlers directly (same pattern as the drainer's fan-out and the poller's per-handler calls). It does NOT go through `ReconciliationPoller.RunAsync`. It does NOT touch `external_connections.last_polled_at` or `last_poll_outcome` (those are scheduled-poller state).

### 2. New `IngestedVia.Backfill` constant

Backfill needs to be distinguishable in the data and the traces. Add `Backfill` to `SomaCore.Domain.WhoopRecoveries.IngestedVia` (the shared vocabulary used by all three handlers per Session 1's convention). Existing constants stay: `Webhook`, `Poller`, `OnOpenPull`. New: `Backfill`.

### 3. Backfill uses list endpoints + payload-bypass

Per Session 3's note and Session 4's confirmation: WHOOP v2's list endpoints (`/cycle`, `/activity/workout`) return the full envelope inline, including the `score` object. The current handlers fetch by id, but the payload they operate on is already in the list response — fetching again is wasteful when we have the data in hand.

This session adds a payload-bypass entry point on each handler:

```csharp
public interface IWhoopWorkoutIngestionHandler
{
    Task<Result<WorkoutIngestionOutcome>> IngestAsync(WorkoutIngestionRequest request, CancellationToken ct);

    // NEW: skip the redundant API fetch when the caller already has the payload
    Task<Result<WorkoutIngestionOutcome>> UpsertFromPayloadAsync(
        Guid externalConnectionId,
        string ingestedVia,
        WhoopWorkoutPayload payload,
        string? upstreamTraceId,
        CancellationToken ct);
}
```

Same shape on `IWhoopSleepIngestionHandler` and `IRecoveryIngestionHandler`. The bypass is mechanical — the handler's existing internal upsert logic already takes a payload; the bypass just exposes that path skipping the fetch span.

**Why bypass, not just "fetch and let the cache decide":** the list endpoints return the canonical payload directly from WHOOP. Re-fetching by id would be a second HTTP round-trip for data we already have. At 30 days × 1-2 workouts/day × 3 entity types per workout/sleep/recovery, that's ~100 redundant calls per user — 99% of backfill's API traffic would be wasted.

### 4. Backfill trace shape (resolves ADR 0011 open item)

**Decision: per-entity trace roots, same shape as poller.** Each ingested entity (recovery, sleep, workout) gets its own `whoop.ingestion` trace root with `ingestion.trigger=backfill` and `ingestion.event_type=cycle.backfill` or `workout.backfill`. No "job-with-children" alternative.

Reasoning (per Session 4's post-session note):
- Per-(user, day) generalizes cleanly from the poller's per-(user, cycle) shape — same outcome rollup tags, same dashboard queries work
- 30-day backfill emits ~120 root spans per user (recovery + sleep + ~2 workouts/day × 30) — well within Application Insights ingestion budget at our scale
- "One backfill-job trace with per-cycle children" would create a new top-level span shape that dashboards would need to know about specifically. Avoiding that is the whole point of having a uniform contract.

The backfill *job* itself does not open its own outer span. Per-entity roots, no enclosing parent. Same shape as the poller.

### 5. Backfill is a one-off, not a recurring job

This session ships backfill as either an admin-only API endpoint (`POST /admin/backfill?connectionId=X&days=30`) or a console command in `SomaCore.IngestionJobs` invoked manually. Not on a schedule, not in the cron-triggered job.

**Recommend: admin API endpoint** for two reasons: (a) Tai can trigger it from the browser when she's ready, no console access needed; (b) easier to add audit logging via the existing OAuth audit table (`Backfill` joins the action vocabulary alongside `Authorize` / `Refresh` / `Disconnect`).

The endpoint is admin-gated via the same Entra middleware that protects `/admin/users` and `/admin/health`. Not user-callable.

### 6. Date filtering on the list endpoints

WHOOP v2's list endpoints accept `start` and `end` query params. Per Session 4's post-session note: `ListRecentWorkoutsAsync` doesn't currently expose these; backfill needs them. Add overloads (or optional params) for `start` and `end` to all three list endpoints (`ListRecentRecoveriesAsync`, `ListRecentSleepsAsync` if it exists — Session 5 may need to add it, `ListRecentWorkoutsAsync`).

---

## Reference materials

**Read first:**
- **`CLAUDE.md`** — repo standing brief
- **`docs/conventions.md`** — code style, idempotency, the new TracingCollection convention (point 6 under Testing, added in Session 4.5)
- **`docs/decisions/0011-ingestion-trace-contract.md`** — the trace contract. Backfill is the last open item this session resolves.
- **`docs/decisions/0006-three-layer-whoop-ingestion.md`** — for context, but backfill is the fourth path that wasn't in the original three (webhook + poller + on-open). It's the historical-data analog of those paths.

**Existing code to mirror:**
- `src/SomaCore.Infrastructure/Whoop/WhoopApiClient.cs` — has `ListRecentRecoveriesAsync` and `ListRecentWorkoutsAsync`. Session 5 may need to add `ListRecentSleepsAsync` if it doesn't exist (check first — sleep may currently only have the cycle-keyed `GetSleepByCycleAsync`).
- `src/SomaCore.Infrastructure/Recovery/RecoveryIngestionHandler.cs`, `Sleep/WhoopSleepIngestionHandler.cs`, `Workout/WhoopWorkoutIngestionHandler.cs` — the three handlers that get a bypass entry point added
- `src/SomaCore.Api/Pages/Me.cshtml.cs` — current `/me` view; reads `dbContext.WhoopRecoveries`. Session 5 enhances it to surface the sleep timestamp.
- `src/SomaCore.Infrastructure/Observability/IngestionTracing.cs` — call the helpers for backfill traces; don't reach for `ActivitySource` directly.
- `src/SomaCore.Domain/WhoopRecoveries/IngestedVia.cs` — add `Backfill` constant here
- `src/SomaCore.Domain/OAuthAudit/` — the audit action vocabulary. `Backfill` joins it.

---

## Scope

### 1. New: `IngestedVia.Backfill` constant

Add to `SomaCore.Domain.WhoopRecoveries.IngestedVia`. Update the schema CHECK constraint on `ingested_via` for all three WHOOP tables (`whoop_recoveries`, `whoop_sleeps`, `whoop_workouts`) to accept the new value. New migration: `0004_ingested_via_backfill.sql` (or next available number).

This is a small additive migration — `ALTER TABLE … DROP CONSTRAINT … ADD CONSTRAINT …` per table. No data backfill needed (no existing rows have `Backfill` set).

### 2. Handler bypass methods

Add `UpsertFromPayloadAsync` to all three ingestion handler interfaces and implementations. Each takes the payload directly (`WhoopRecoveryPayload` / `WhoopSleepPayload` / `WhoopWorkoutPayload`), skips the API fetch, runs the existing upsert logic.

The bypass:
- Opens a handler trace span via `IngestionTracing.StartHandlerScope` (same as the existing entry point)
- Skips opening a `whoop.cycle.fetch` or equivalent fetch span (no fetch happens)
- Records the same outcome vocabulary (`Inserted` / `Updated` / `NoOp` / `SkippedNoData`)
- Persists the same `raw_payload` and `score` columns the existing path does

The existing `IngestAsync` method stays unchanged (the webhook and poller paths still need it). The two methods share the same internal upsert helper.

### 3. New: WHOOP API client list-endpoint enhancements

- Confirm/add `ListRecentSleepsAsync(token, limit, nextToken, start?, end?, ct)` — check whether this exists; if not, add it mirroring `ListRecentWorkoutsAsync`
- Add optional `start` / `end` parameters (or overloads) to `ListRecentRecoveriesAsync` and `ListRecentWorkoutsAsync`
- All three list endpoints should support cursor pagination via `next_token` and date filtering

If sleep doesn't have a list endpoint in WHOOP v2 (worth verifying — the API surface differs between cycle-keyed and entity-keyed access), backfill iterates cycles and pulls sleep via the existing cycle-keyed path. Document the chosen approach.

### 4. New: `WhoopBackfillService` in `SomaCore.Infrastructure`

Location: `src/SomaCore.Infrastructure/Backfill/WhoopBackfillService.cs` (mirroring the per-feature folder convention).

Single entry point:

```csharp
public interface IWhoopBackfillService
{
    Task<BackfillSummary> RunAsync(
        Guid externalConnectionId,
        DateTimeOffset start,
        DateTimeOffset end,
        CancellationToken ct);
}

public sealed record BackfillSummary(
    int RecoveriesInserted, int RecoveriesUpdated, int RecoveriesNoOp,
    int SleepsInserted, int SleepsUpdated, int SleepsNoOp,
    int WorkoutsInserted, int WorkoutsUpdated, int WorkoutsNoOp,
    int FailedEntities, // 0 in the happy path
    TimeSpan Duration);
```

The service:

1. Validates the connection exists, is active, and resolves an access token via the existing token cache
2. For each entity type, paginates the list endpoint with the requested date window:
   - Recovery list (per-page, cursor)
   - Sleep list (per-page, cursor) — OR cycle iteration if sleep has no list endpoint
   - Workout list (per-page, cursor)
3. For each entity in each page: opens a `whoop.ingestion` root trace span with `trigger=backfill`, calls the handler's bypass method, records outcome
4. Aggregates outcomes into `BackfillSummary` for the caller
5. Logs an OAuth audit entry with action=`Backfill`

**Trace shape per entity:**
- Root: `whoop.ingestion` with `ingestion.source=whoop`, `ingestion.trigger=backfill`, `ingestion.event_type=cycle.backfill` or `workout.backfill`, `external_connection_id`, no upstream trace id (backfill is initiated by us, not WHOOP)
- Pre-seed all three `outcomes.*` rollup tags to `NotInvoked` per the established pattern
- One handler child span; the bypass method handles this
- No fetch child span (no fetch happens)

**Rate limiting:** WHOOP doesn't publish hard rate limits but courtesy matters. The service should add a small delay between paginated requests (recommend 100ms). At three list endpoints × ~2 pages each × 100ms, total overhead is well under a second. If WHOOP returns 429, backoff exponentially with a max retry count.

### 5. New: admin backfill endpoint

`POST /admin/backfill` in `src/SomaCore.Api/Admin/BackfillEndpoint.cs` (or wherever admin endpoints live — check `Pages/Admin/` for the existing pattern).

Request body:
```json
{
  "connectionId": "uuid",
  "days": 30
}
```

Validates `days` is 1-90 (cap on the upper end to prevent accidentally requesting a year of history). Computes `start` = `now - days` and `end` = `now`. Calls `IWhoopBackfillService.RunAsync`. Returns the `BackfillSummary`.

Gated by the existing admin Entra middleware. Same auth pattern as `/admin/users` and `/admin/health`.

### 6. `/me` view enhancement: recovery with sleep timestamp

Update `Me.cshtml.cs` and `Me.cshtml` to display the sleep start time alongside each recovery score. Per Session 4.5's note, the join is:

- Primary: `WhoopRecovery.WhoopSleepId == WhoopSleep.WhoopSleepId` when the recovery row has a sleep UUID
- Fallback: `WhoopSleep.StartAt BETWEEN WhoopRecovery.CycleStartAt AND CycleEndAt` (cycle-window overlap)

Display format: under each recovery row, "Sleep: Mon May 19, 11:42pm – 7:18am MT" (using the existing `MountainTime.cs` formatting). Travel days with non-standard sleep windows now disambiguate clearly.

**Out of scope for this view change:** workout display, plan display, anything beyond the recovery + sleep-timestamp pairing. Other surfaces are Track B / Phase 3.

### 7. Add `Backfill` to OAuth audit action vocabulary

The `oauth_audit` table's action constants currently include `Authorize`, `Refresh`, `Disconnect`. Add `Backfill`. Same `text + CHECK` pattern, same audit-row shape — one row per backfill invocation with `context` jsonb carrying the requested window and the resulting summary.

Migration: same as item 1 above (combine into `0004_…sql` for atomicity).

---

## Out of scope

- ❌ Scheduled / recurring backfill (this is a one-off; ad-hoc admin trigger only)
- ❌ Backfill resume-from-checkpoint (if it fails halfway, the admin re-runs it; handler idempotency means re-running is safe)
- ❌ User-facing backfill UI (admin-only for MVP)
- ❌ Workout display on `/me` (Track B / Phase 3)
- ❌ Plan display on `/me` (Track B / Phase 3)
- ❌ Changes to ingestion handlers' existing `IngestAsync` paths (only the new bypass methods are added)
- ❌ Changes to drainer, poller, or webhook receiver
- ❌ Changes to `PollerGating` or `last_poll_outcome` semantics

---

## Deliverables

1. Migration `0004_ingested_via_backfill.sql` — extends the `ingested_via` CHECK constraint on three WHOOP tables, extends the `oauth_audit` action CHECK constraint
2. `IngestedVia.Backfill` constant added to `SomaCore.Domain.WhoopRecoveries.IngestedVia`
3. `OAuthAudit` action constant added: `Backfill`
4. `UpsertFromPayloadAsync` added to all three ingestion handler interfaces + implementations, mirroring the existing trace pattern (handler span only, no fetch span)
5. WHOOP API client enhancements: date-window params on the existing list endpoints; `ListRecentSleepsAsync` added if not present
6. `IWhoopBackfillService` + `WhoopBackfillService` in `src/SomaCore.Infrastructure/Backfill/`
7. `/admin/backfill` endpoint in `src/SomaCore.Api/`
8. `/me` view enhancement: recovery score now displays paired sleep window
9. ADR 0011 update: backfill trace shape resolved (per-entity roots with `ingestion.trigger=backfill`), open items section reflects this
10. Unit tests:
    - Backfill summary aggregation (pure-function-style)
    - Handler bypass methods produce same outcome shape as the IngestAsync path for equivalent inputs
11. Integration tests:
    - `WhoopBackfillServiceTests.cs` — full 30-day backfill against a seeded WHOOP API mock, asserts row counts, trace shapes, audit row
    - Backfill is idempotent — running it twice over the same window produces all `NoOp` outcomes on second run
    - Backfill respects rate-limit retry on 429
    - `/admin/backfill` endpoint exercises the service end-to-end
    - `/me` view shows sleep window correctly when sleep is present and gracefully when it's absent
12. Track A checklist updated: Session 5 marked Merged, Track A closed

---

## Validation steps

```bash
# Build
dotnet build src/SomaCore.sln

# Unit tests
dotnet test tests/SomaCore.UnitTests/SomaCore.UnitTests.csproj

# Integration tests (Docker required)
dotnet test tests/SomaCore.IntegrationTests/SomaCore.IntegrationTests.csproj

# Manual end-to-end against Tai's actual WHOOP connection:
# 1. POST /admin/backfill with connectionId=<tai's>, days=30
# 2. Verify whoop_recoveries, whoop_sleeps, whoop_workouts have ~30 days of new rows with ingested_via='Backfill'
# 3. Verify oauth_audit has one row with action='Backfill' and a populated context jsonb
# 4. Open /me and confirm recovery rows now show sleep windows
# 5. Application Insights: query for ingestion.trigger='backfill' and confirm ~100+ root spans with the expected shape
```

---

## Exit criteria

- [ ] Migration extends `ingested_via` and `oauth_audit.action` CHECK constraints
- [ ] `IngestedVia.Backfill` and `OAuthAudit.Action.Backfill` constants exist
- [ ] All three ingestion handlers expose `UpsertFromPayloadAsync` with trace integration
- [ ] WHOOP list endpoints support date-window filtering; sleep has a list endpoint or backfill iterates cycles (documented)
- [ ] `WhoopBackfillService` paginates all three entity types, calls handler bypass methods, emits trace per entity per ADR 0011, writes audit row
- [ ] Rate-limit handling on 429 with exponential backoff
- [ ] Backfill is idempotent — re-running over the same window yields `NoOp` outcomes
- [ ] `/admin/backfill` endpoint is admin-gated and exercises the service
- [ ] `/me` view shows recovery + paired sleep window using MT formatting; graceful when sleep is absent
- [ ] ADR 0011 backfill open item closed with documented decision
- [ ] No regression on prior session tests (45 unit, 28 integration as of Session 4.5)
- [ ] `dotnet build`, all unit tests, all integration tests pass
- [ ] No changes outside the scope listed above

---

## Summary expected at the end

1. Filenames added or modified (full paths)
2. Any deviation from this prompt (with reason)
3. Whether WHOOP v2 has a sleep list endpoint or backfill iterated cycles
4. Operational observation: running the backfill against Tai's connection, what's the total elapsed time, the count of each entity type, the number of `NoOp` outcomes (data we already had from the live ingestion path during Sessions 2-4)
5. Any surprises in WHOOP v2's date filtering — particularly around timezone boundaries (is `start`/`end` UTC, the user's local, or WHOOP's own?)
6. State of the codebase at Track A close: total tests, total handlers, total trace roots emitted across normal operation. Used by Track B kickoff to ground its reuse calculations.
7. Anything Track B (rules engine) needs to know about the data model now that history exists — particularly around joins between recovery/sleep/workout that the rules engine will perform
