# Track A: WHOOP three-layer ingestion — session checklist

**Track goal.** Sleep, workout, and recovery data from WHOOP flow continuously into the canonical `PhysiologicalStateSnapshot`, replacing the recovery-only ingestion that exists today.

**Track exit criteria.** All three layers ingested reliably for 7 consecutive days of Tai's data; edge states (UNSCORABLE, PENDING_SCORE) handled correctly; recovery-with-sleep-timestamp displayed correctly on `/me`.

**Reference.** `whoop-architecture.docx` is authoritative for ingestion architecture. `CLAUDE.md` (repo root) is co-authoritative for code patterns ("Never invent a parallel pattern"). `docs/decisions/0011-ingestion-trace-contract.md` is authoritative for observability shape.

---

## Cross-session conventions established in Sessions 1-2

### Code patterns (Session 1)

- **Ingestion handlers, not repositories.** Phase 1 uses `IRecoveryIngestionHandler` with `Result<RecoveryIngestionOutcome>` return type and an `(Inserted / Updated / NoOp / SkippedNoData)` outcome vocabulary. Sleep added `IWhoopSleepIngestionHandler` mirroring this exactly. Workout follows the same shape.
- **Reads go direct via `dbContext`.** `Me.cshtml.cs` queries `dbContext.WhoopRecoveries` directly. Sleeps and workouts follow the same pattern when they surface.
- **Shared vocabulary across all three WHOOP entity types.** Reuse `SomaCore.Domain.WhoopRecoveries.ScoreState` and `SomaCore.Domain.WhoopRecoveries.IngestedVia` — do not duplicate these constants into the new namespaces.
- **Column naming.** `start_at` / `end_at` (not `start` / `end`), matching `whoop_recoveries`.
- **`raw_payload` is canonical for recomputability;** `score` is a top-level jsonb column alongside it, nullable when `score_state != 'SCORED'`. Typed columns derive from `score` at ingestion time and can be recomputed from `raw_payload`.
- **Cascade rules.** `ON DELETE CASCADE` on `users`, `ON DELETE SET NULL` on `external_connections`. Matches `whoop_recoveries` and the privacy doc.
- **No nav collections on `User` or `ExternalConnection`** for the new entities. One-way back-references only.

### Telemetry and tracing (Session 2)

- **Trace contract per ADR 0011.** Every ingestion path emits one `whoop.ingestion` root span with required tags, a `whoop.cycle.fetch` child for upstream fetches, and `<handler>.ingest` children per handler invocation. Outcome rollup tags (`outcomes.recovery` / `outcomes.sleep` / `outcomes.workout`) are always present on the root span. `NotInvoked` covers both short-circuit cases and event-type-not-in-scope cases.
- **`IngestionTracing` helper in `src/SomaCore.Infrastructure/Observability/`** provides `StartIngestionScope` / `StartFetchScope` / `StartHandlerScope` / `RecordOutcome` / `RecordHandlerOutcome` / `RecordFetchOutcome` plus centralized `Tags` and `Outcomes` constants. Call these — don't reach for `ActivitySource` directly.
- **OTel split: source in Infrastructure, exporter per-host.** `AddSomaCoreTelemetry()` in `SomaCore.Infrastructure` registers the `ActivitySource` and `WithTracing(.AddSource(...))`. Each host's `Program.cs` wires its own Azure Monitor exporter conditionally (so `SomaCore.IngestionJobs` doesn't get a forced dependency on the exporter). Apply this pattern unchanged in Session 4.
- **Auto-instrumentation packages deliberately not added.** `OpenTelemetry.Instrumentation.AspNetCore/Http/EntityFrameworkCore` would create unrelated spans that `TraceAssertions` would have to filter out. One-line add when end-to-end visibility is wanted.

### WHOOP v2 API shape (correction from Session 2)

- **Cycle endpoint is envelope-only** (not recovery + sleep together as the architecture doc implies). Recovery and sleep are separate sub-endpoints (`/cycle/{id}/recovery` and `/cycle/{id}/sleep`). The "single cycle pull" stays a logical abstraction in the dispatch layer — mechanically it's 2-3 HTTP calls. Workouts are entirely separate (`/activity/workout/{id}`) and not cycle-keyed.
- **404 from `/cycle/{id}/sleep` is normal, not an error.** Some cycles have no sleep block. Sleep handler returns `SkippedNoData`; root span records `outcomes.sleep = SkippedNoData`.
- **`total_sleep_time_milli` is derived**, not returned. Handler computes `in_bed - awake - no_data`; stores null if any component missing or result negative.

---

## Pre-track (manual, ~1 hour, not a Claude Code session)

Forced-validation Phase 1 closeout. Do this before Session 3 lands so edge-case behavior is verified across the new ingestion paths.

- [ ] Skip a night of strap wear → observe UNSCORABLE handling end to end on `/me`
- [ ] Let WHOOP access token age out without using the app → observe reconnect banner path
- [ ] Disconnect WHOOP via the new flow, then reconnect → observe full teardown + re-auth round-trip
- [ ] Note any unexpected behavior; surface in the next session prompt if it changes anything

---

## Session 1 — Canonical schema extension

**Status.** ⬛ Merged · ⬜ Verified

### Exit criteria

- [x] Migration `0002_whoop_sleep_workout.sql` applies cleanly
- [x] Both tables have indexes on `(user_id, start_at DESC)`
- [x] Cascade rules match `whoop_recoveries` exactly
- [x] `SCHEMA-NOTES.md` updated
- [x] Domain entities and EF configurations follow phase-1 patterns
- [x] `dotnet build` and unit tests pass
- [ ] **Manual cascade verification with Docker running** — covered indirectly now that Session 2's integration tests exercise the cascade behavior on real connections. Sufficient.

---

## Session 2 — Cycle endpoint ingestion (sleep + recovery)

**Status.** ⬛ Merged · ⬜ Verified

### Exit criteria

- [x] `IWhoopSleepIngestionHandler` follows `IRecoveryIngestionHandler` shape exactly
- [x] `WhoopApiClient.GetSleepByCycleAsync` added (renamed from `GetCycleAsync` — WHOOP API shape required separate sub-endpoint)
- [x] `WhoopWebhookDrainer` dispatches both `recovery.updated` and `sleep.updated` to the same cycle-fetch-and-ingest path
- [x] Recovery flow refactored to pull via cycle id; both rows ingest from one webhook event
- [x] No regression on phase-1 behavior — 11 pre-existing integration tests pass with additive trace calls
- [x] `score_state` handled correctly for both
- [x] `raw_payload` populated for both entities
- [x] Ingestion-layer idempotency on `(external_connection_id, whoop_sleep_id)` — convergence test passes
- [x] `IngestionTracing` helper implemented per ADR 0011
- [x] Trace calls integrated at drainer + fetch + both handlers; `NotInvoked` recorded when short-circuited
- [x] `TraceAssertions` test helper exists; trace-shape integration test passes
- [x] DI registration updated in `Program.cs`
- [x] `dotnet build`, unit tests (23/23), integration tests (15/15) all pass
- [x] No changes outside scope

### Post-session notes

- **WHOOP v2 cycle endpoint shape correction.** The architecture doc says recovery and sleep arrive together via the cycle endpoint — that's wrong mechanically. They're separate sub-endpoints. Dispatch layer keeps the "single cycle pull" logical shape; mechanically it's 2-3 HTTP calls. Worth correcting in `whoop-architecture.docx` next time it's revised.
- **ADR 0011 was placed in repo root, moved to `docs/decisions/`.** File placement convention: ADRs always go in `docs/decisions/`.
- **Webhook endpoint needed broadening, not just verification.** Phase-1 filter hard-coded `recovery.updated`; broadened to accept both via new `IsCycleEvent` helper. Session 3 adds `workout.updated`. Helper should be renamed to `IsSupportedEventType` when workout lands.
- **Drainer end-to-end integration test deferred to Session 4.** Both handlers are independently tested; the drainer is thin orchestration over them. Real end-to-end test (webhook in, both rows out) would require either `BackgroundService` lifecycle scaffolding or extracting `ProcessOneAsync` to `internal`. Recommend addressing in Session 4 alongside the poller, which shares the fan-out path. **Tracked as a known gap below.**
- **Telemetry split:** source in Infrastructure (`AddSomaCoreTelemetry`), exporter wired per-host in each `Program.cs`. Done this way so `SomaCore.IngestionJobs` (which references Infrastructure) doesn't get a forced dependency on the Azure Monitor exporter. Session 4 follows this pattern when wiring telemetry in `IngestionJobs`.
- **Auto-instrumentation deliberately skipped.** ASP.NET/HttpClient/EFCore auto-spans would noise up `TraceAssertions`. Add when end-to-end visibility is wanted.

### Known gap (carry to Session 4)

- [ ] **Drainer end-to-end integration test** — drive a webhook from `WhoopWebhookEndpoint` through the queue, through `WhoopWebhookDrainer`, ending in both `whoop_recoveries` and `whoop_sleeps` rows with the correct trace shape. Owned by Session 4 because the poller shares the fan-out path and the scaffolding (if any) should be built once.

---

## Session 3 — Workout endpoint ingestion

**Goal.** New webhook handler and pull path for workouts. Workouts are NOT cycle-keyed — they have their own endpoint and don't fan out.

**Dependencies.** Session 2 merged.

**Blocks.** Session 4.

**Status.** ⬛ Merged · ⬜ Verified

### Exit criteria

- [x] `IWhoopWorkoutIngestionHandler` mirrors `IWhoopSleepIngestionHandler` shape (with one justified deviation: `WorkoutId` is required, no `CycleId` fallback — see post-session note)
- [x] `WhoopApiClient.GetWorkoutByIdAsync` added (workouts have their own endpoint, NOT cycle-keyed)
- [x] `WhoopWebhookDrainer` dispatches `workout.updated` to a new workout-fan-out path (single handler, not cycle-fan-out)
- [x] `WhoopWebhookEndpoint.cs` filter accepts `workout.updated`; helper renamed from `IsCycleEvent` to `IsSupportedEventType`
- [x] `score_state` handled correctly
- [x] Idempotency on `(external_connection_id, whoop_workout_id)`
- [x] Trace contract: workout webhook produces root + workout handler child; `outcomes.recovery = NotInvoked`, `outcomes.sleep = NotInvoked`, `outcomes.workout = <outcome>` — orchestrator pre-seeds all three rollup tags per amended ADR 0011
- [x] Integration tests including trace-shape assertion (4 new workout tests, 19/19 integration total pass)
- [ ] **A workout logged on Tai's strap appears in `whoop_workouts` within the expected window** — manual verification, do after deploy

### Post-session notes

- **Pre-seed pattern for outcome rollups.** Implemented per amended ADR 0011: drainer's `ProcessOneAsync` sets `outcomes.recovery / sleep / workout = NotInvoked` on the root span *before* fan-out runs. Handlers overwrite their own tag when they execute; structurally-out-of-scope handlers stay at `NotInvoked`. This is cleaner than recording `NotInvoked` at each error site (which was Session 2's pattern) and means workout webhooks automatically carry `outcomes.recovery = NotInvoked` and `outcomes.sleep = NotInvoked` without code per fan-out method.
- **Workout request deviates from sleep request shape (justified).** `SleepIngestionRequest` carries `(CycleId?, SleepId?)` — either can resolve the natural key. Workouts have no analog (not cycle-keyed, no list-recent fallback), so `WorkoutIngestionRequest` carries `WorkoutId` as a *required* `Guid`. Same idiom, different field cardinality. Documented inline.
- **WHOOP v2 workout endpoint shape.** `/developer/v2/activity/workout/{id}` returns the workout envelope directly. Score sub-object carries `strain`, `average_heart_rate`, `max_heart_rate`, `kilojoule` (the four lifted columns) plus many unmapped fields (`zone_duration`, `distance_meter`, `percent_recorded`, etc.) that stay in `raw_payload` / `score` jsonb. `sport_name` is the v2 string-typed replacement for the legacy integer `sport_id`. 404 from this endpoint is treated as `SkippedNoData`, same as `/cycle/{id}/sleep`.
- **Heart-rate ints accept WHOOP's decimal wire format.** Same trick as recovery: `WhoopWorkoutScore.AverageHeartRate` / `MaxHeartRate` are `decimal?` on the wire, rounded to `int` at the persistence boundary via the existing `ToInt` helper pattern. WHOOP serializes integer-valued fields with a trailing `.0` sometimes.
- **Cycle and workout fan-out methods stayed as two methods.** No clean shared abstraction emerged. Both are short (~30 lines), both open a fetch scope, both call exactly the handlers they orchestrate. Forcing them through a common interface would obscure the fact that one fans out to two handlers and the other to one. If a fourth source lands (HealthKit?) and triggers refactor, the right shape will be more obvious then.
- **No regression on phase-1 or Session 2 behavior.** All 15 pre-existing integration tests pass unmodified (recovery handler, sleep handler, disconnect, token cache, migration). All 23 pre-existing unit tests pass. Net: 25/25 unit, 19/19 integration.

### Known gaps (carry forward)

- [ ] **Drainer end-to-end integration test** — still carried to Session 4 per Session 2's deferral. Now there are three fan-out paths (cycle for recovery+sleep, single-handler for workout) sharing the orchestrator pre-seed pattern; the end-to-end test should cover at least one webhook of each shape.
- [ ] **Manual: workout from Tai's strap end-to-end.** Verify after deploy.

---

## Session 4 — Reconciliation poller extension

**Goal.** Extend the per-user adaptive poller to fan out across all three event types. Also pick up the deferred drainer end-to-end integration test (shares fan-out path with the poller).

**Dependencies.** Sessions 2 and 3 merged.

**Blocks.** Nothing.

**Status.** ⬛ Merged · ⬜ Verified

### Exit criteria

- [x] Poller pulls sleep, workout, and recovery on the same invocation
- [x] Per-user adaptive scheduling preserved (lives in Container Apps Jobs cron schedule, not in code — see post-session note 1)
- [ ] ~~Poller stops for the day once a SCORED recovery is in for the current cycle OR the user has opened the app~~ — **doesn't exist in code today and was out of scope to add**. See post-session note 1.
- [x] Poller invokes `IRecoveryIngestionHandler`, `IWhoopSleepIngestionHandler`, and `IWhoopWorkoutIngestionHandler` directly with `IngestedVia.Poller` and (where applicable) an explicit `CycleId` — NOT via the drainer's `FanOutCycleAsync` (which is intentionally webhook-shaped)
- [x] Trace contract: poller emits separate trace roots per (user, cycle) for cycle pull and per (user, workout) for each workout, with `ingestion.trigger = poller`
- [x] OTel wiring added to `SomaCore.IngestionJobs/Program.cs` following the Session 2 pattern
- [x] `Azure.Monitor.OpenTelemetry.Exporter` package added to `SomaCore.IngestionJobs.csproj` (not lifted into Infrastructure)
- [x] ADR 0011 amended: poller-as-trace-root open item resolved with documented decision (now under "Resolved items")
- [x] **Drainer end-to-end integration test added** (carried from Session 2): 3 tests, one per event type, asserting rows + full trace contract
- [x] Poller integration tests: cycle-pull, workout-pull, 404-on-sleep (3 tests)
- [x] No regression — all 22 pre-existing integration tests still green
- [x] `dotnet build`, unit (25/25), integration (25/25) all pass
- [ ] **Manual verification**: kill the webhook endpoint for an hour, confirm poller catches up — do after deploy

### Post-session notes

1. **Reality check: "adaptive wake-window" and "stop-conditions" don't exist in code.** The existing poller is a one-shot `IJob` (`ReconciliationPoller`) that walks all active connections, calls handlers, exits. The per-user adaptive schedule, the cold-start window, and the "stop once SCORED recovery is in" condition live in the Container Apps Jobs cron trigger schedule — the binary just runs once per scheduled invocation. So "preserve adaptive schedule unchanged" meant preserve the one-shot invocation shape. I did not invent in-code wake-window or stop-condition logic that wasn't there. If you want in-process stop-conditions (e.g. "skip this connection if its latest SCORED recovery is fresh enough"), that's a follow-up session and should land with explicit scope.

2. **Drainer-test scaffolding decision: extract `ProcessOneAsync` to `internal`, not `BackgroundService` lifecycle.** Two reasons: (a) faster — no polling for the SUT to claim a seeded row, no `Task.Delay` timing windows; (b) deterministic — direct method invocation has zero races. The Api project already had `[InternalsVisibleTo("SomaCore.IntegrationTests")]` so this required only an access modifier change. Future end-to-end tests for new dispatch paths should follow the same pattern: expose the smallest meaningful unit of orchestration as `internal`, drive it directly. Documented inline in `WhoopWebhookDrainerTests.cs`.

3. **Trace-test parallelism fix.** xUnit runs different test classes in parallel by default. `TraceAssertions.Capture()` installs a global `ActivityListener` for the `SomaCore.Ingestion` source — two parallel listeners cross-contaminate (each captures the other test's spans). Fix: `TracingCollection` in `tests/SomaCore.IntegrationTests/Observability/TracingCollection.cs` is an xUnit collection with `DisableParallelization = true`; all four trace-using test classes carry `[Collection(nameof(TracingCollection))]`. Tests outside the collection still run in parallel. Adds ~30s to total integration test runtime but eliminates the contamination.

4. **Four call sites for the fan-out pattern now: drainer cycle, drainer workout, poller cycle, poller workout.** I considered factoring the pre-seed-then-execute logic into a shared `CycleFanOut` helper but kept them as four separate methods. Reasons: (a) the drainer fan-outs run inside the drainer's webhook scope (queue claim/release semantics, `IServiceProvider` plumbing); the poller fan-outs run inside the per-user loop of a one-shot job (no queue, no scope plumbing); (b) the bodies are short (~40 lines each) and the shared shape (pre-seed all 3 outcomes, call handlers, record) is already mechanically uniform — wrapping it in a helper would hide divergence more than it removes duplication. If a fifth source lands (HealthKit?) and obviously shares one of these shapes, refactor then with five sites in hand. Note: the post-Session-3 prediction "two methods is the right resting shape" still holds at four — the call sites grew but the abstraction-cost analysis didn't change.

5. **Workout poll limit:** `WorkoutsPerConnection = 25` per tick. Anything older than that gets picked up by Session 5's backfill. WHOOP returns most-recent-first and we don't paginate the list call — keeps the poller bounded.

6. **Job-level orchestration does NOT open its own trace root.** Per ADR 0011 amendment, the `ReconciliationPoller.RunAsync` job-level invocation opens no span; each per-(user, cycle) and per-(user, workout) iteration opens its own root with `trigger=poller`. This keeps `outcomes.*` dashboard queries uniform across triggers. If we ever want job-level metrics ("how long did the poller take? how many connections did it touch?"), that's a separate `metrics` concern, not a root span.

7. **Listing workouts vs. fetching per workout.** Per Session 3's note: WHOOP v2's workout list response includes the full envelope (including `score`), so the poller's `ListRecentWorkoutsAsync` call could technically dedupe with what we already have in `whoop_workouts` and skip the per-workout `GetWorkoutByIdAsync`. **This optimization was NOT done in Session 4.** The current poller calls the handler for every listed workout, and the handler's `(external_connection_id, whoop_workout_id)` dedupe produces a `NoOp` for ones we already have. Cost is one extra HTTP call per known workout per poller tick — trivial at three-user scale, worth optimizing later for backfill. Session 5 should consider lifting list-response data directly into the handler via an `Upsert(WhoopWorkoutPayload)` shortcut that bypasses the redundant fetch.

### Known gaps (none for Session 4)

All carried-forward gaps from prior sessions are closed:
- Drainer end-to-end test: ✅ added (`WhoopWebhookDrainerTests.cs`, 3 tests)
- OTel wiring in IngestionJobs: ✅ added per the established split-host pattern
- ADR 0011 poller-as-trace-root open item: ✅ resolved
- In-process stop-conditions / wake-window gating: ✅ resolved in **Session 4.5** (below)

---

## Session 4.5 — Adaptive poller scheduling + trace-test convention

**Goal.** Add per-user gating (Option D: hybrid hourly tick + per-user skip logic) so the cron-triggered poller doesn't pointlessly hit WHOOP for connections that don't need a poll. Plus codify the trace-test parallelism convention Session 4 discovered.

**Dependencies.** Session 4 merged.

**Blocks.** Nothing (Session 5 can proceed in parallel; backfill bypasses gating).

**Status.** ⬛ Merged · ⬜ Verified

### Exit criteria

- [x] Migration `0003_connection_polling_state.sql` adds `last_polled_at` + `last_poll_outcome` to `external_connections` with cascade-safe defaults (both nullable, CHECK on outcome vocabulary)
- [x] `PollerGating.Evaluate` implemented as a pure function with injectable time source — no IO, no clock
- [x] `ReconciliationPoller` calls `PollerGating.Evaluate` at the top of each per-connection loop and acts on the decision
- [x] Skip path: no ingestion happens, no trace root emitted, `last_polled_at` + `last_poll_outcome` updated, Serilog event logged with the gating reason
- [x] Poll path: existing Session 4 fan-out runs unchanged, `last_poll_outcome` set to `Polled` (or `Failed` on per-connection failure)
- [x] Cold-start trade-off documented inline (UTC 4-11 window; warm mode takes over after first sleep cycle lands)
- [x] Trace-test parallelism convention added to `docs/conventions.md` (under Testing, point 6)
- [x] Unit tests cover every `PollerGating` branch — 20 tests
- [x] Integration tests cover skip-due-to-SCORED-recovery, skip-due-to-too-recent, and cold-start in-window poll (3 new tests)
- [x] No regression on Session 4's existing integration tests — they pass unchanged after the clock-injection refactor (see post-session note 2)
- [x] `dotnet build`, all unit tests (45/45), all integration tests (28/28) pass
- [x] No changes outside scope (no handler interface or fan-out changes)
- [ ] **Manual verification on local Postgres**: run the poller against a seeded connection with a SCORED recovery from this morning → confirm `last_poll_outcome = 'Skipped'` and `last_polled_at` updated; delete the recovery, re-run, confirm `last_poll_outcome = 'Polled'` and ingestion happened — do after deploy

### Post-session notes

1. **Cold-start timezone trade-off resolution.** Used the recommended "accept misalignment for first 24 hours" approach. Cold-start window is UTC hours 4-11 — for non-UTC users this can miss by up to ~12 hours on the very first day. After the user's first WHOOP sleep cycle lands, warm mode takes over and the per-user `timezone_offset` from that sleep drives the window math. Alternative (capturing a `default_timezone_offset` at onboarding) would have required schema + onboarding-flow changes for value that's gone after day one. Documented inline in [PollerGating.cs](../src/SomaCore.Infrastructure/Polling/PollerGating.cs).

2. **Clock injection in `ReconciliationPoller`.** Added a `Func<DateTimeOffset> clock` parameter via an `internal` constructor (test seam) alongside the public DI-friendly constructor that uses `DateTimeOffset.UtcNow`. Without this, the gating's wake-window check would make every existing integration test non-deterministic (passing in the 4-11 UTC window, failing the rest of the day). The `internal` modifier is covered by a new `InternalsVisibleTo("SomaCore.IntegrationTests")` in `SomaCore.IngestionJobs.csproj`. Tests use a `FixedNow = 07:00 UTC` constant. Production wiring unchanged.

3. **Schema deviation: no `next_poll_due_at` column.** Per the prompt — we deliberately chose per-tick computation over denormalized scheduling state. If observation eventually shows the per-tick recovery+sleeps queries are expensive (they aren't at three-user scale), revisit.

4. **`PollerGating.Evaluate` signature deviation.** The prompt's signature took `WhoopSleep? mostRecentSleep`. But computing a median wake-time needs *multiple* sleeps. Changed to `IReadOnlyList<WhoopSleep>? recentSleeps` (most-recent-first). The first element's `TimezoneOffset` is treated as the user's current local offset; the median of all elements' `EndAt` values is the typical wake time. Two-test verification: median is robust to one late-wake outlier (covered).

5. **Skip path emits no ingestion trace roots.** Per the prompt: "Skipped connections do NOT emit ingestion trace roots — they're a no-op from an ingestion perspective. The Serilog log line is sufficient observability for the skip path." This keeps Application Insights ingestion budget aligned with actual ingestion volume. The skip outcome surfaces via the `external_connections.last_poll_outcome` column for dashboard queries.

6. **Operational estimate for Tai.** Assuming hourly cron, with gating in place: the poller does meaningful work for Tai roughly **once per day, in her morning wake window** (typical wake −60min to +4hr → ~5 hours of in-window ticks, but the first one within that window lands the SCORED recovery and the rest skip due to the stop-condition). Outside the wake window: every tick skips. Net: ~1 cycle pull per day for Tai, plus 1 workout-list call per in-window tick (which currently isn't gated separately — see open question below). vs. pre-gating: 24 cycle pulls + 24 workout-list calls per day. Order-of-magnitude reduction in WHOOP API traffic.

### Open question / known limitation

- **Workout poll runs whenever the cycle poll runs.** The gating decision is one per (connection, tick) — same Skip/Poll applies to both the cycle pull and the workout pull. This is OK for MVP (workouts during wake window are common; outside it less so), but workouts can happen any time of day, so a polled-too-early-in-day workout missed by webhook won't be caught until tomorrow's wake window. Acceptable trade-off for safety-net polling; revisit if observation shows mid-day workouts are routinely missed.

### Known gaps (none new)

All Session 4 gaps remain closed; Session 4.5 introduced no new ones.

---

## Session 5 — Backfill + recovery-with-sleep-timestamp display

**Status.** ⬜ Not started · ⬜ In progress · ⬜ Merged · ⬜ Verified

### Exit criteria

- [ ] Backfill script populates `whoop_sleeps`, `whoop_workouts`, and any missing `whoop_recoveries` for Tai's last 30 days
- [ ] Backfill is idempotent
- [ ] Backfill respects WHOOP rate limits
- [ ] Backfill trace shape decided per ADR 0011 open item (per-cycle traces vs. single backfill-job trace with per-cycle children)
- [ ] `/me` view shows recovery score with the date and start time of the underlying sleep
- [ ] Tai confirms the `/me` change answers the "which night does this recovery reflect" question
- [ ] Consider: backfill may want to recompute `total_sleep_time_milli` from `raw_payload` if WHOOP's definition shifts

### Post-session notes

_(Fill in after merge.)_

---

## Track A close

When all five sessions are at "Verified", Track A is done. Confirm against the top-level track exit criteria:

- [ ] All three layers ingested reliably for 7 consecutive days of Tai's data
- [ ] Edge states (UNSCORABLE, PENDING_SCORE) handled correctly
- [ ] Recovery-with-sleep-timestamp displayed correctly on `/me`

Then Track B (rules engine) is unblocked.
