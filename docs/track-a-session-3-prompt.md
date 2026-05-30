# Track A · Session 3: Workout ingestion

**Goal of this session.** Add WHOOP workout ingestion. New `workout.updated` webhook event dispatch with single-handler invocation (no fan-out — workouts are independent of cycles). Mirrors the patterns established by Session 2's sleep handler.

**Schema and trace contract were done in Sessions 1 and 2.** Tables, entities, EF configurations, ingestion handler pattern, `IngestionTracing` helper, and `TraceAssertions` test helper all exist. This session pattern-copies them for workout.

---

## Context

Track A is extending WHOOP ingestion from recovery-only to all three layers. Sessions 1 and 2 brought sleep online alongside recovery via a shared cycle-fetch-and-fan-out path. This session brings workouts online.

**Workouts are not cycle-keyed.** Unlike recovery and sleep, which both relate to a single sleep/wake cycle, workouts are independent activities that can happen any time. WHOOP exposes them via a dedicated endpoint (`/developer/v2/activity/workout/{id}` and a list endpoint), not as sub-resources of a cycle. This means:

- The webhook drainer does NOT use the cycle-fetch-and-fan-out path for workout events
- A `workout.updated` event produces exactly one handler invocation (`IWhoopWorkoutIngestionHandler`)
- The trace contract still applies, but the root span has only one handler child plus a fetch child

---

## Reference materials

**Read first:**
- **`CLAUDE.md`** — repo-level standing brief. "Never invent a parallel pattern." If anything in this prompt conflicts with CLAUDE.md, CLAUDE.md wins.
- **`docs/conventions.md`** — code style + non-negotiables.
- **`docs/decisions/0006-three-layer-whoop-ingestion.md`** — webhook + poller + on-open architectural pattern.
- **`docs/decisions/0009-postgres-backed-work-queue.md`** — `webhook_events` idempotency layer.
- **`docs/decisions/0011-ingestion-trace-contract.md`** — trace contract. **Implement per the ADR; do not re-derive.** Note the amended `NotInvoked` semantics: workout webhooks set `outcomes.recovery = NotInvoked` and `outcomes.sleep = NotInvoked` on the root span.
- **`whoop-architecture.docx`** (project knowledge) — ingestion architecture overall.

**Existing code to mirror exactly:**
- `src/SomaCore.Infrastructure/Sleep/IWhoopSleepIngestionHandler.cs` and `WhoopSleepIngestionHandler.cs` — the most recent sibling, mirror its shape including request record fields, outcome vocabulary, idempotency checks, score parsing, and trace integration.
- `src/SomaCore.Infrastructure/Whoop/WhoopApiClient.cs` — the home for HTTP methods. `GetSleepByCycleAsync` is the existing shape for sleep; add `GetWorkoutByIdAsync` here following the same patterns (return shape, 404 handling, error wrapping).
- `src/SomaCore.Api/Whoop/WhoopWebhookDrainer.cs` — currently dispatches `recovery.updated` and `sleep.updated` to the cycle-fan-out path. Add a new dispatch case for `workout.updated` that calls a new `FanOutWorkoutAsync` (or equivalent single-handler-fan-out method).
- `src/SomaCore.Api/Whoop/WhoopWebhookEndpoint.cs` — has an `IsCycleEvent` helper that accepts `recovery.updated` and `sleep.updated`. Rename to `IsSupportedEventType` and add `workout.updated`.
- `src/SomaCore.Domain/WhoopRecoveries/ScoreState.cs` and `IngestedVia.cs` — **reuse these**, do not duplicate into a workout namespace.
- `src/SomaCore.Domain/WhoopWorkouts/WhoopWorkout.cs` — entity Session 1 created.
- `src/SomaCore.Infrastructure/Persistence/Configurations/WhoopWorkoutConfiguration.cs` — EF config Session 1 created.
- `src/SomaCore.Infrastructure/Observability/IngestionTracing.cs` — call these methods, don't reach for `ActivitySource` directly.
- `tests/SomaCore.IntegrationTests/WhoopSleepIngestionHandlerTests.cs` — the test pattern to mirror.

---

## Scope

### 1. New: `IWhoopWorkoutIngestionHandler` + implementation

Location: `src/SomaCore.Infrastructure/Workout/` (mirroring `Sleep/` and `Recovery/`).

Mirror `IWhoopSleepIngestionHandler` exactly — request record, outcome vocabulary, return type, idempotency check by `(external_connection_id, whoop_workout_id)`, score parsing, trace integration via `IngestionTracing.StartHandlerScope("workout", ...)` and `RecordHandlerOutcome` / `RecordOutcome`.

**Outcome vocabulary** is `Inserted` / `Updated` / `NoOp` / `SkippedNoData`, same as sleep and recovery.

**Persist both `raw_payload` and `score`** (per the conventions doc). Lift typed columns from `score` at ingestion time; null when `score_state != 'SCORED'`.

**PENDING_SCORE and UNSCORABLE rows still get persisted** with null `score` and null typed columns. Later, when score lands, the `Updated` path replaces nulls with real values.

### 2. New: `WhoopApiClient.GetWorkoutByIdAsync(token, workoutId, ct)` method

Add to the existing `WhoopApiClient` in `src/SomaCore.Infrastructure/Whoop/`. Returns `Result<WhoopWorkoutPayload?>` (404 → `Result.Success(null)`, mirroring how `GetSleepByCycleAsync` handles 404). Add `WhoopWorkoutPayload`, `WhoopWorkoutScore`, and any related record types to `WhoopModels.cs`.

**No `GetWorkoutByCycleAsync`** — workouts are not cycle-keyed in WHOOP v2.

### 3. New: workout fan-out method in `WhoopWebhookDrainer`

Add a `FanOutWorkoutAsync` method (or generalize the existing `FanOutCycleAsync` if a clean abstraction emerges naturally — but don't force one). Shape:

1. Open root ingestion scope (`IngestionTracing.StartIngestionScope` with `trigger="webhook"`, `eventType="workout.updated"`)
2. Open fetch scope, call `WhoopApiClient.GetWorkoutByIdAsync`, close fetch scope with outcome
3. If workout fetched: invoke `IWhoopWorkoutIngestionHandler`, record `outcomes.workout` on root
4. If workout not fetched (404): record `outcomes.workout = SkippedNoData`
5. **Always** record `outcomes.recovery = NotInvoked` and `outcomes.sleep = NotInvoked` on root span (per amended ADR 0011 semantics — outcome rollup tags always present)

Dispatch from the existing event-type switch in `WhoopWebhookDrainer` based on `event_type == "workout.updated"`. The current cycle event types (`recovery.updated`, `sleep.updated`) keep their existing path unchanged.

### 4. Modify: `WhoopWebhookEndpoint.IsCycleEvent` → `IsSupportedEventType`

Rename the helper (it's no longer cycle-specific) and add `workout.updated` to the accepted set. Behavior otherwise unchanged.

### 5. Update: drainer's existing cycle-fan-out path records new outcome rollup

The existing `recovery.updated` and `sleep.updated` traces currently set `outcomes.recovery` and `outcomes.sleep`. Per ADR 0011, they must also set `outcomes.workout = NotInvoked` on the root span so that all three rollup tags are always present. Small additive change in the cycle-fan-out method.

---

## Idempotency

Per the same two-layer pattern Sessions 1 and 2 established:

**Layer 1: webhook delivery (existing infrastructure, no work for this session).** `webhook_events` unique constraint on `(source, source_event_id, source_trace_id)` handles duplicate delivery at the queue boundary.

**Layer 2: ingestion (the new workout handler must implement this).** Check for existing row by `(external_connection_id, whoop_workout_id)` before inserting. Same WHOOP workout delivered twice → second one results in `NoOp` or `Updated`, never `Inserted` again.

---

## Out of scope (do NOT do any of this in this session)

- ❌ Reconciliation poller changes in `SomaCore.IngestionJobs` (Session 4)
- ❌ Backfill of historical workout data (Session 5)
- ❌ Drainer end-to-end integration test (deferred from Session 2, owned by Session 4 — see Track A checklist)
- ❌ Any change to `/me` view (`src/SomaCore.Api/Pages/Me.cshtml(.cs)`)
- ❌ Adding nav collections to `User` or `ExternalConnection`
- ❌ Changes to `IRecoveryIngestionHandler` or `IWhoopSleepIngestionHandler` interfaces or implementations (other than the small additive outcome-rollup change in the drainer's cycle-fan-out method — the handlers themselves don't change)
- ❌ Refactoring the drainer's cycle-fan-out and the new workout-fan-out into a shared abstraction unless an obvious clean factoring presents itself naturally. Two methods are fine.

---

## Deliverables

1. `IWhoopWorkoutIngestionHandler` interface + implementation in `src/SomaCore.Infrastructure/Workout/`, mirroring `Sleep/` exactly
2. `WhoopApiClient.GetWorkoutByIdAsync` method + `WhoopWorkoutPayload` / `WhoopWorkoutScore` records in `WhoopModels.cs`
3. `WhoopWebhookDrainer.FanOutWorkoutAsync` (or equivalent) for the workout dispatch path
4. Additive update to cycle-fan-out to record `outcomes.workout = NotInvoked` on root
5. `WhoopWebhookEndpoint.IsCycleEvent` renamed to `IsSupportedEventType`, accepting `workout.updated`
6. DI registration for `IWhoopWorkoutIngestionHandler` in `SomaCore.Infrastructure/DependencyInjection.cs`
7. Unit tests for workout handler invariants (mirror sleep unit tests in `tests/SomaCore.UnitTests/Domain/`)
8. Integration tests in `tests/SomaCore.IntegrationTests/`:
   - `WhoopWorkoutIngestionHandlerTests.cs` — idempotency, score_state transitions, outcome correctness (mirror `WhoopSleepIngestionHandlerTests.cs`)
   - Trace shape: workout webhook produces root + fetch child + workout handler child with `outcomes.workout = <outcome>`, `outcomes.recovery = NotInvoked`, `outcomes.sleep = NotInvoked`
   - Regression: existing recovery and sleep traces now include `outcomes.workout = NotInvoked` on root (small test addition or update to existing trace-shape assertions)

---

## Validation steps to run before declaring done

```bash
# 1. Build
dotnet build src/SomaCore.sln

# 2. Unit tests (no Docker needed)
dotnet test tests/SomaCore.UnitTests/SomaCore.UnitTests.csproj

# 3. Integration tests (Docker required)
dotnet test tests/SomaCore.IntegrationTests/SomaCore.IntegrationTests.csproj

# 4. Verify the trace for a workout.updated event in Application Insights (or via the trace-shape test):
#    - Root span "whoop.ingestion" with required tags per ADR 0011
#    - One "whoop.workout.fetch" (or equivalent) child span (GetWorkoutByIdAsync)
#    - "workout.ingest" child span with Inserted/Updated/NoOp/SkippedNoData outcome
#    - Root span has outcomes.workout = <outcome>, outcomes.recovery = NotInvoked, outcomes.sleep = NotInvoked
#    - All within a single TraceId span

# 5. Verify recovery.updated and sleep.updated traces now ALSO have outcomes.workout = NotInvoked on root
```

---

## Exit criteria

- [ ] `IWhoopWorkoutIngestionHandler` mirrors `IWhoopSleepIngestionHandler` shape exactly
- [ ] `WhoopApiClient.GetWorkoutByIdAsync` added; existing API client patterns preserved
- [ ] `WhoopWebhookDrainer` dispatches `workout.updated` to the new single-handler fan-out
- [ ] `WhoopWebhookEndpoint` helper renamed to `IsSupportedEventType` and accepts `workout.updated`
- [ ] No regression on phase-1 or Session 2 behavior — existing recovery and sleep integration tests pass (with at most additive trace-tag updates)
- [ ] `score_state` handled correctly — PENDING_SCORE/UNSCORABLE rows persist with null `score` column and null typed columns
- [ ] `raw_payload` populated (canonical replay source)
- [ ] Ingestion-layer idempotency on `(external_connection_id, whoop_workout_id)`
- [ ] Trace contract: workout webhook root span has all three `outcomes.*` tags with `outcomes.recovery = NotInvoked` and `outcomes.sleep = NotInvoked`
- [ ] Cycle-fan-out root spans (recovery.updated, sleep.updated) now also record `outcomes.workout = NotInvoked`
- [ ] DI registration updated
- [ ] `dotnet build`, unit tests, integration tests all pass
- [ ] No changes outside the scope listed above

---

## Summary expected at the end

1. Filenames added or modified (full paths)
2. Any deviation from this prompt (with reason)
3. Anything Session 4 (poller) will need to know — specifically:
   - The entry point the poller should call to trigger workout pulls for a user
   - Whether the workout list endpoint (for poller-style pulls of recent workouts) was implemented as part of this session or deferred
   - Whether `FanOutWorkoutAsync` is reusable from the poller path or needs adaptation
4. Anything Session 5 (backfill) will need to know about workout backfill — particularly the list-endpoint pagination shape
5. Any surprises in the WHOOP v2 workout endpoint response shape — score parsing, available fields, anything that differed from sleep
6. Whether the drainer's cycle-fan-out and workout-fan-out are duplicative enough to warrant a future refactor, or whether two methods is the right resting shape
