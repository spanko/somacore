# Track A · Session 4: Reconciliation poller extension

**Goal of this session.** Extend the per-user adaptive poller (currently recovery-only) to fan out across all three event types: recovery, sleep, workout. Wire OpenTelemetry into `SomaCore.IngestionJobs` per the established Session 2 pattern. Close out the deferred drainer end-to-end integration test from Session 2.

---

## Context

Sessions 2 and 3 brought sleep and workout ingestion online via the webhook path. The reconciliation poller exists today and pulls recovery-only on a per-user adaptive schedule (per `whoop-architecture.docx`'s three-layer ingestion architecture). This session extends that poller to also pull sleep and workout, so missed webhooks for any of the three layers are caught.

The poller is the second of three ingestion triggers per ADR 0006. The on-open synchronous pull is Session 5 territory; this session locks down the poller path.

---

## Architectural decisions (locked before this session)

**Three patterns Sessions 2 and 3 established — apply unchanged here:**

1. **Pre-seed all `outcomes.*` rollup tags to `NotInvoked` at the top of each fan-out**, then handlers overwrite their own when they execute. Session 3's drainer adopted this pattern after Session 2 hand-rolled per-error-site `NotInvoked` recording. Pre-seed is the codified shape going forward — robust by construction.

2. **Two fan-out methods, not one shared abstraction.** Cycle-fan-out (recovery + sleep) and workout-fan-out (workout only) are sibling methods in the drainer. The poller's equivalent should mirror this — separate cycle-pull and workout-pull logic, not a forced unification. Refactor only if a fourth call site appears.

3. **Telemetry split: source registration in Infrastructure, exporter wired per-host.** `AddSomaCoreTelemetry()` in `SomaCore.Infrastructure` registers the `ActivitySource`. Each host's `Program.cs` wires its own Azure Monitor exporter conditionally. `SomaCore.IngestionJobs` follows this exactly — exporter package added to its csproj, wired in its `Program.cs`, not lifted into Infrastructure.

**Poller invocation pattern (clarified by Session 2 and 3 post-session notes):**

- Poller invokes `IRecoveryIngestionHandler`, `IWhoopSleepIngestionHandler`, and `IWhoopWorkoutIngestionHandler` **directly** with `IngestedVia.Poller` and an explicit natural key (CycleId or WorkoutId).
- Poller does NOT call `WhoopWebhookDrainer.FanOutCycleAsync` or `FanOutWorkoutAsync` — those are intentionally webhook-shaped (take `IServiceProvider`, open webhook-flavored root spans, dispatch from event-type switch).
- If the fan-out shape ends up obviously shared between drainer and poller, lift the shared bit into a small `CycleFanOut` / `WorkoutFanOut` internal helper in Infrastructure. Don't force this — start with the poller calling handlers directly.

**Trace contract for the poller (per ADR 0011 open item, now resolved):**

- Poller emits a **separate trace root per (user, cycle)** and **per (user, workout)** with `ingestion.trigger = poller`. Not one root per tick.
- Root span name: `whoop.ingestion` (same as webhook). The `ingestion.trigger` tag is what distinguishes poller traces from webhook traces in dashboards.
- All three `outcomes.*` rollup tags always present per amended ADR 0011 — pre-seed to `NotInvoked` at the top of each fan-out.
- Update the "Open items" section of ADR 0011 noting that the poller-as-trace-root shape has been resolved.

---

## Reference materials

**Read first:**
- **`CLAUDE.md`** — repo standing brief.
- **`docs/conventions.md`** — code style, async-all-the-way, Serilog, idempotency.
- **`docs/decisions/0006-three-layer-whoop-ingestion.md`** — webhook + poller + on-open pattern. The poller is the second layer.
- **`docs/decisions/0011-ingestion-trace-contract.md`** — trace contract. Amended `NotInvoked` semantics + pre-seed pattern.
- **`whoop-architecture.docx`** (project knowledge) — adaptive polling schedule, wake-window logic, stop-conditions.

**Existing code to mirror exactly:**
- `src/SomaCore.IngestionJobs/` — the current poller host. Find the existing recovery-poll loop; extend it, don't rewrite it.
- `src/SomaCore.Api/Whoop/WhoopWebhookDrainer.cs` — Session 3's pre-seed-then-execute pattern is the template for how the poller's fan-outs should be structured. `FanOutCycleAsync` and `FanOutWorkoutAsync` are the closest analogs.
- `src/SomaCore.Infrastructure/Whoop/WhoopApiClient.cs` — has `ListRecentRecoveriesAsync` and `GetSleepByCycleAsync` and `GetWorkoutByIdAsync`. **Session 4 adds `ListRecentWorkoutsAsync`** (cursor-paginated via `next_token`, mirrors `ListRecentRecoveriesAsync`).
- `src/SomaCore.Api/Program.cs` — Session 2's OTel wiring (source via `AddSomaCoreTelemetry`, exporter conditional on config). `SomaCore.IngestionJobs/Program.cs` mirrors this.
- `src/SomaCore.Infrastructure/Observability/IngestionTracing.cs` — call these helpers, don't reach for `ActivitySource` directly.

---

## Scope

### 1. New: `ListRecentWorkoutsAsync` in `WhoopApiClient`

Mirror `ListRecentRecoveriesAsync` shape. Cursor pagination via `next_token` (per Session 3's post-session note about WHOOP v2's workout list shape: `GET /developer/v2/activity/workout?limit=N&nextToken=...`, response is `{records: [...], next_token: string?}`). Returns `Result<WhoopWorkoutListResponse>`.

This is needed for the workout poll loop — single-id `GetWorkoutByIdAsync` doesn't fit the poller's "what's recent for this user" question.

### 2. Extend: poller cycle-pull loop

The poller currently pulls recovery for each user in their wake window. Extend each per-user iteration to:

1. Open a `whoop.ingestion` root span with `trigger=poller`, `event_type=cycle.pull` per (user, cycle).
2. Pre-seed all three `outcomes.*` rollup tags to `NotInvoked`.
3. Resolve the cycle id via `ListRecentRecoveriesAsync` (or whatever the existing poller does today — preserve its cycle-id resolution).
4. Open a fetch span; pull recovery via the existing API client method; ingest via `IRecoveryIngestionHandler` with `IngestedVia.Poller`; record outcome.
5. Open another fetch span; pull sleep via `GetSleepByCycleAsync`; ingest via `IWhoopSleepIngestionHandler` with `IngestedVia.Poller`; record outcome. 404 → `SkippedNoData`.
6. Stop-condition logic (existing): stop polling for the day once a SCORED recovery is in for the current cycle, OR the user has opened the app.

### 3. New: poller workout-pull loop

Separate from the cycle pull (workouts aren't cycle-keyed). For each user:

1. Open a `whoop.ingestion` root span with `trigger=poller`, `event_type=workout.pull` per (user, workout).
2. Pre-seed all three `outcomes.*` rollup tags to `NotInvoked` — workout-pull root spans set `outcomes.recovery = NotInvoked` and `outcomes.sleep = NotInvoked` (same semantic as workout webhook).
3. Call `ListRecentWorkoutsAsync` to enumerate recent workouts for the user.
4. For each new workout (one the user doesn't have in `whoop_workouts` yet, or one in `PENDING_SCORE` state that may have updated): ingest via `IWhoopWorkoutIngestionHandler` with `IngestedVia.Poller`.

**Open question for this session to resolve:** should the workout poll run on the same adaptive wake-window schedule as the cycle poll, or on a different cadence? Workouts can happen any time of day, not just around wake. Recommend: **same schedule** for MVP — Tai's pattern is unlikely to have unscored mid-day workouts that languish without webhook delivery; the poller's job is the safety net, not real-time delivery. Revisit if observation shows missed workouts.

### 4. Adaptive schedule logic — preserve unchanged

The per-user wake-window logic (cold start 4am-11am local; warm = typical wake −60min to +4hr) stays as-is. The stop-conditions stay as-is. This session adds fan-out within each tick, not new scheduling.

### 5. New: OTel wiring in `SomaCore.IngestionJobs`

- Add `Azure.Monitor.OpenTelemetry.Exporter` to `SomaCore.IngestionJobs.csproj` (Directory.Packages.props already has the version pinned from Session 2).
- In `SomaCore.IngestionJobs/Program.cs`: call `AddSomaCoreTelemetry()` (from Infrastructure, shared); wire Azure Monitor exporter conditionally on `Telemetry:ApplicationInsightsConnectionString` per the Api pattern.
- Do NOT pull the exporter into Infrastructure (the architectural reason: forces dependency on hosts that don't need it).

### 6. New (deferred from Session 2): drainer end-to-end integration test

This is the test Session 2 deferred. Now's the time — Session 4 introduces the poller, which shares the fan-out shape, so the scaffolding is reusable.

Test shape: drive a webhook event from `WhoopWebhookEndpoint` through the queue (`webhook_events`), through `WhoopWebhookDrainer`, ending in the expected rows and trace shape. One test per event type:

- `recovery.updated` → both `whoop_recoveries` and `whoop_sleeps` rows + trace with `outcomes.recovery`, `outcomes.sleep`, `outcomes.workout=NotInvoked`
- `sleep.updated` → same as above (cycle fan-out path)
- `workout.updated` → `whoop_workouts` row + trace with `outcomes.workout`, `outcomes.recovery=NotInvoked`, `outcomes.sleep=NotInvoked`

Location: `tests/SomaCore.IntegrationTests/WhoopWebhookDrainerTests.cs`.

The scaffolding choice (running the `BackgroundService` lifecycle vs. extracting `ProcessOneAsync` to `internal`) is your call — pick whichever is cleaner. The post-session summary should note which one and why, so future tests follow the pattern.

### 7. Poller integration tests

Mirror the drainer test pattern for the poller:

- One test per fan-out: cycle-pull (recovery + sleep ingest, trace shape correct), workout-pull (workout ingest, trace shape correct)
- Wake-window stop-condition test (poller stops once SCORED recovery is in)
- 404-on-sleep test (cycle has no sleep → handler returns `SkippedNoData`, root span records correctly)

---

## Out of scope (do NOT do any of this in this session)

- ❌ Backfill scripts (Session 5)
- ❌ `/me` view changes (Session 5)
- ❌ On-open synchronous pull path — the third ingestion trigger. Not in Track A; the existing on-open behavior is fine for now.
- ❌ Adding nav collections to `User` or `ExternalConnection`
- ❌ Changes to the ingestion handler interfaces (only their callers — the poller — are new)
- ❌ Changes to the drainer's existing fan-out methods, except to read them as the template

---

## Deliverables

1. `ListRecentWorkoutsAsync` added to `IWhoopApiClient` + `WhoopApiClient`, plus `WhoopWorkoutListResponse` record in `WhoopModels.cs`
2. Poller cycle-pull loop extended to ingest both recovery + sleep via direct handler calls with pre-seeded trace rollups
3. New poller workout-pull loop with same trace pattern
4. OTel wiring in `SomaCore.IngestionJobs/Program.cs` per Session 2 pattern
5. `Azure.Monitor.OpenTelemetry.Exporter` added to `SomaCore.IngestionJobs.csproj`
6. ADR 0011 amended: poller-as-trace-root open item resolved (root-per-(user, cycle), root-per-(user, workout))
7. Drainer end-to-end integration test (deferred from Session 2): one test per event type, asserting rows + trace shape
8. Poller integration tests: cycle-pull, workout-pull, stop-condition, 404-on-sleep

---

## Validation steps to run before declaring done

```bash
# 1. Build
dotnet build src/SomaCore.sln

# 2. Unit tests
dotnet test tests/SomaCore.UnitTests/SomaCore.UnitTests.csproj

# 3. Integration tests (Docker required)
dotnet test tests/SomaCore.IntegrationTests/SomaCore.IntegrationTests.csproj

# 4. Run the IngestionJobs host locally (or in test mode) and verify:
#    - AddSomaCoreTelemetry registers the ActivitySource
#    - Exporter wires only when connection string is set
#    - No regression on existing recovery-only polling behavior

# 5. Verify trace shape for poller events:
#    - Root span "whoop.ingestion" with ingestion.trigger=poller
#    - Cycle-pull root has all three outcomes.* tags
#    - Workout-pull root has all three outcomes.* tags
#    - Separate roots per (user, cycle) and per (user, workout)
```

---

## Exit criteria

- [ ] `ListRecentWorkoutsAsync` added; existing API client patterns preserved
- [ ] Poller cycle-pull ingests recovery + sleep with `IngestedVia.Poller` and correct trace shape
- [ ] Poller workout-pull ingests workouts with `IngestedVia.Poller` and correct trace shape
- [ ] Pre-seed pattern applied: all three `outcomes.*` rollup tags always present on poller root spans
- [ ] Adaptive wake-window schedule preserved unchanged
- [ ] Stop-conditions preserved unchanged
- [ ] OTel source registered in IngestionJobs via shared `AddSomaCoreTelemetry`
- [ ] Azure Monitor exporter wired conditionally in `SomaCore.IngestionJobs/Program.cs` (not in Infrastructure)
- [ ] ADR 0011 amended: poller-as-trace-root open item resolved with documented decision
- [ ] Drainer end-to-end integration test added: one per event type, asserting rows + full trace contract
- [ ] Poller integration tests cover both fan-outs + stop-condition + 404-on-sleep
- [ ] No regression on existing webhook, recovery, sleep, workout integration tests
- [ ] `dotnet build`, all unit tests, all integration tests pass
- [ ] No changes outside scope

---

## Summary expected at the end

1. Filenames added or modified (full paths)
2. Any deviation from this prompt (with reason)
3. Drainer-test scaffolding decision (which approach — `BackgroundService` lifecycle vs. `ProcessOneAsync` extracted to `internal` — and why). Establishes the pattern for future end-to-end tests.
4. Anything Session 5 (backfill + `/me` display) needs to know:
   - Whether `ListRecentWorkoutsAsync` paginates as expected; any pagination edge cases
   - Whether the workout-list response includes the full score envelope (per Session 3's note — backfill may not need per-workout fetches)
   - Whether the poller's per-(user, cycle) trace root shape generalizes cleanly to per-(user, day) for backfill, or whether backfill needs its own shape decision
5. Any surprises in the WHOOP v2 workout list endpoint — pagination behavior, response shape, edge cases around `start`/`end` query params
6. Observation on whether the cycle-fan-out shape between drainer and poller is now obviously duplicative enough to warrant a shared helper, or whether the two-method pattern is still right with three call sites in hand (drainer cycle, drainer workout, poller cycle, poller workout — actually four call sites now)
