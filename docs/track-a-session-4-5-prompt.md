# Track A · Session 4.5: Adaptive poller scheduling + trace-test convention

**Goal of this session.** Two related cleanups before Session 5 closes Track A:

1. **Adaptive poller scheduling (Option D)** — add per-user gating to the existing one-shot cron-triggered poller so it skips users who don't need a poll. Implements what `whoop-architecture.docx` originally described — wake-window logic, stop-conditions — without introducing a long-running scheduler service.

2. **Codify the trace-test parallelism convention.** Session 4 introduced `TracingCollection` to fix xUnit parallelism contamination across trace-capturing tests. The pattern is correct; this session writes it down in conventions so future sessions don't have to rediscover it.

Bounded scope. Neither item touches handlers or ingestion logic.

---

## Context

### Why Session 4.5 exists

Session 4 surfaced a gap between the architecture doc and the code: `whoop-architecture.docx` describes adaptive per-user scheduling (cold start 4am-11am local; warm = typical wake −60min to +4hr; stop once SCORED recovery is in OR user opens the app) but none of that logic lives in code. The poller is a one-shot cron job that walks all active connections every tick. At three internal users this works; at any meaningful scale it's wasteful and slightly noisy toward WHOOP.

The architectural decision (made before this session): keep the deployment shape — one-shot cron-triggered Container Apps Job, no long-running scheduler service. Add per-user gating logic inside the job. Quartz.NET-style dynamic scheduling earns its keep at much higher scale; at our shape it would be over-built. Reversible later if observation shows otherwise.

### Why the trace-test convention matters

Session 4 hit deterministic flakiness when two trace-capturing test classes ran in parallel — xUnit's default parallel-across-classes execution let `TraceAssertions.Capture()` listeners in one test class observe spans from another. The fix (`[Collection(nameof(TracingCollection))]` opt-in to a non-parallel collection) is correct, but it's not yet codified in conventions. Future sessions adding trace-capturing tests need to know to join the collection.

---

## Reference materials

**Read first:**
- **`CLAUDE.md`** — repo standing brief. The trace-test convention lands here (or in `docs/conventions.md` — pick whichever fits the existing structure).
- **`docs/conventions.md`** — async-all-the-way, Serilog, idempotency, etc.
- **`whoop-architecture.docx`** (project knowledge) — the adaptive scheduling description this session catches up to. Note: the architecture doc says "every 30 minutes" — that's the polling cadence within the wake window, not the cron tick rate. This session keeps the existing cron cadence; the gating logic determines whether a given tick does anything for a given user.
- **`docs/decisions/0006-three-layer-whoop-ingestion.md`** — the three-layer pattern. Polling is the second layer; this session improves it, doesn't replace it.

**Existing code to mirror:**
- `src/SomaCore.IngestionJobs/Jobs/ReconciliationPoller.cs` — Session 4's one-shot job. This session adds the gating logic at the top of the per-connection loop.
- `src/SomaCore.Domain/ExternalConnections/ExternalConnection.cs` — where per-user scheduling state belongs.
- `src/SomaCore.Domain/WhoopRecoveries/WhoopRecovery.cs` — the latest SCORED recovery is a query input for stop-conditions.
- `tests/SomaCore.IntegrationTests/Observability/TracingCollection.cs` — the convention this session codifies.

---

## Architectural decisions (locked before this session)

**Option D — hybrid hourly tick with per-user gating** (chosen from the design-space exploration that preceded this prompt):

- Keep the cron-triggered one-shot job. No long-running scheduler service.
- On each tick, the job enumerates active connections and applies a small set of gating checks per connection.
- Skip the connection if any of the following hold:
  - **Stop-condition:** the latest SCORED recovery for the user's current cycle is already in
  - **Too-recent:** the last successful poll was less than 30 minutes ago
  - **Outside wake window:** the user is currently outside their typical wake window (with cold-start fallback for users with no history)
- Otherwise: poll as Session 4 already does. Existing fan-out, existing trace contract.

**What "wake window" means in practice:**
- **Cold start (no history):** poll between 4am and 11am local time
- **Warm (history exists):** poll from (typical wake − 60 min) to (typical wake + 4 hours)
- "Typical wake" is the median sleep `end_at` from the last 14 days of `whoop_sleeps` rows for that user, converted to local time via `timezone_offset` on the most recent sleep

**What this session does NOT introduce:**
- No new scheduler library (Quartz, Hangfire, etc.)
- No new long-running service
- No webhook-triggered scheduling (out of scope — Track A is closing out)
- No backfill of historical wake times beyond what's already in `whoop_sleeps`

---

## Scope

### 1. Schema: per-connection scheduling state

Two new columns on `external_connections`:

| Column | Type | Purpose |
|---|---|---|
| `last_polled_at` | `timestamptz NULL` | Updated at the end of every successful poll pass for that connection (whether work was done or not — successful tick, not successful ingest) |
| `last_poll_outcome` | `text NULL` | One of `Skipped` / `Polled` / `Failed`. Useful for observability and dashboarding. Schema CHECK constraint enforces the vocabulary. |

Migration: `0003_connection_polling_state.sql` (or whatever number is next).

Domain entity (`ExternalConnection.cs`) and EF configuration get the new fields. No nav changes elsewhere.

**Not adding a `next_poll_due_at` column** — that's the Quartz-style "external scheduler" pattern we're explicitly not building. Per-tick computation is cheaper than maintaining state.

### 2. Helper: `PollerGating` — pure logic, easily testable

Location: `src/SomaCore.Infrastructure/Polling/PollerGating.cs`.

A small static class with one entry point:

```csharp
public enum PollDecision { Skip, Poll }

public static class PollerGating
{
    // Pure function — no IO, no time source except the one passed in.
    public static PollDecision Evaluate(
        ExternalConnection connection,
        WhoopRecovery? latestRecovery,    // most recent recovery for this connection, or null
        WhoopSleep? mostRecentSleep,       // most recent sleep for this connection, or null (for wake-window computation)
        DateTimeOffset now,                // current UTC time, injected
        TimeSpan minimumPollInterval);     // default 30 min, configurable
}
```

The logic:

1. **Too-recent check.** If `connection.LastPolledAt` is set and `now - LastPolledAt < minimumPollInterval` → `Skip`.
2. **Stop-condition check.** If `latestRecovery != null` AND `latestRecovery.ScoreState == "SCORED"` AND `latestRecovery.CycleEndAt > now - TimeSpan.FromHours(36)` → `Skip`. (The 36-hour bound covers a generous cycle duration — a SCORED recovery that's days old means we're outside the active cycle and should poll again.)
3. **Wake-window check.**
   - If `mostRecentSleep == null` → cold start. If local time (computed via `mostRecentSleep` doesn't apply here, so use UTC offset of zero, or per-connection-stored offset if you have one — see open question below) is between 4am and 11am local → `Poll`. Otherwise → `Skip`.
   - If `mostRecentSleep != null` → warm. Compute typical wake = median of recent sleep `end_at` values. Convert to local via `mostRecentSleep.TimezoneOffset`. If `now` (in that local) is within `[typicalWake - 60min, typicalWake + 4hr]` → `Poll`. Otherwise → `Skip`.
4. **Otherwise** → `Poll`.

The function is pure — all dependencies are injected. Makes it unit-testable without Docker.

**Open question this session must answer:** for cold-start users, what local time zone do we use? The cold-start case is "no history yet." Options:
- Use UTC (simplest but wrong for non-UTC users)
- Use a stored `default_timezone_offset` on `external_connections` (requires schema add + onboarding capture)
- Use the timezone of the most recent sleep across ALL users as a proxy (terrible)
- Accept that cold-start users may poll outside their preferred window for the first day or two, and let warm-mode take over once they have one sleep cycle

**Recommend the last option** — cold start is by definition short-lived. After one full cycle of WHOOP data, the user is warm. The mis-alignment for the first 24 hours doesn't materially hurt anything. Document this trade-off inline and in the post-session summary.

### 3. ReconciliationPoller integration

At the top of the per-connection loop in `ReconciliationPoller.cs`:

1. Load the connection's most recent recovery and most recent sleep (one query each, indexed reads)
2. Call `PollerGating.Evaluate(...)`
3. If `Skip`: update `last_polled_at = now`, `last_poll_outcome = 'Skipped'`, log a structured Serilog event with the reason (which check fired), continue to next connection
4. If `Poll`: run the existing cycle-pull + workout-pull fan-out. On success: update `last_polled_at = now`, `last_poll_outcome = 'Polled'`. On failure: `last_poll_outcome = 'Failed'`.

**Trace contract.** Per-(user, cycle) and per-(user, workout) trace roots stay as Session 4 built them. Skipped connections do NOT emit ingestion trace roots — they're a no-op from an ingestion perspective. The Serilog log line is sufficient observability for the skip path. This keeps Application Insights ingestion budget aligned with actual ingestion volume.

### 4. Codify the trace-test parallelism convention

Add a short section to `docs/conventions.md` (or `CLAUDE.md` — read both, decide which fits the existing structure). Content:

> **Trace-capturing tests must opt into the non-parallel collection.** xUnit's default parallel-across-classes execution lets `TraceAssertions.Capture()` listeners observe spans from concurrently-running tests in other classes. Any test class that calls `TraceAssertions.Capture()` MUST be annotated `[Collection(nameof(TracingCollection))]`. Tests outside this collection still run in parallel. Cost: ~30s added to the integration test run; eliminates the flakiness entirely.

Cross-reference `tests/SomaCore.IntegrationTests/Observability/TracingCollection.cs` for the definition.

### 5. Tests

**Unit tests** (Docker not needed):
- `PollerGatingTests.cs` in `tests/SomaCore.UnitTests/Polling/`
- Cover each branch: too-recent skip, stop-condition skip (SCORED + recent cycle), stop-condition NOT triggered (UNSCORABLE or PENDING_SCORE), cold-start in-window, cold-start out-of-window, warm in-window, warm out-of-window, edge cases at window boundaries
- Pure function tests — fast, no IO

**Integration tests:**
- `ReconciliationPollerGatingTests.cs` or extend `ReconciliationPollerTests.cs`
- One test per outcome: connection is skipped (too-recent) → no ingestion, `last_poll_outcome = 'Skipped'`, `last_polled_at` updated; connection is polled (in window) → ingestion happens, `last_poll_outcome = 'Polled'`
- These tests are NOT trace-capturing — they assert database state, not span shape. So they don't need to join `TracingCollection`.

---

## Out of scope

- ❌ Quartz, Hangfire, or any external scheduler library
- ❌ Long-running scheduler services
- ❌ Schema migration for default timezone (the cold-start trade-off above handles this)
- ❌ Changes to handler interfaces or implementations
- ❌ Changes to fan-out methods (drainer or poller)
- ❌ Backfill (Session 5)
- ❌ `/me` view changes (Session 5)
- ❌ Changes to `IngestionTracing` or `TraceAssertions`

---

## Deliverables

1. Migration `0003_connection_polling_state.sql` (or next number) adding `last_polled_at` and `last_poll_outcome` to `external_connections`
2. Updated `ExternalConnection` domain entity and EF configuration
3. `PollerGating` static class in `src/SomaCore.Infrastructure/Polling/`
4. `ReconciliationPoller.cs` updated to call `PollerGating.Evaluate` at the top of each per-connection iteration; updates `last_polled_at` and `last_poll_outcome` accordingly
5. Structured Serilog event on every skip with the gating reason
6. Convention added to `docs/conventions.md` (or `CLAUDE.md`) documenting `[Collection(nameof(TracingCollection))]` requirement for trace-capturing tests
7. Unit tests for `PollerGating` covering every branch
8. Integration tests for the poller's skip path and poll path
9. Track A checklist updated: Session 4.5 marked Merged, the "in-process stop-conditions" gap from Session 4's post-session notes marked resolved

---

## Validation steps

```bash
# Build
dotnet build src/SomaCore.sln

# Unit tests (fast — PollerGating is pure function)
dotnet test tests/SomaCore.UnitTests/SomaCore.UnitTests.csproj

# Integration tests (Docker required)
dotnet test tests/SomaCore.IntegrationTests/SomaCore.IntegrationTests.csproj

# Manual sanity check on local Postgres:
# - Run the poller against a seeded connection with a SCORED recovery from this morning
# - Confirm last_poll_outcome = 'Skipped' and last_polled_at updated
# - Delete the recovery, re-run, confirm last_poll_outcome = 'Polled' and ingestion happened
```

---

## Exit criteria

- [ ] Migration adds `last_polled_at` and `last_poll_outcome` to `external_connections` with cascade-safe defaults
- [ ] `PollerGating.Evaluate` implemented as a pure function with injectable time source
- [ ] `ReconciliationPoller` calls `PollerGating.Evaluate` at the top of each per-connection loop and acts on the decision
- [ ] Skip path: no ingestion happens, no trace root emitted, `last_polled_at` + `last_poll_outcome` updated, Serilog event logged with reason
- [ ] Poll path: existing Session 4 fan-out runs unchanged, `last_poll_outcome` set to `Polled` (or `Failed` on exception)
- [ ] Cold-start trade-off documented inline and in post-session notes
- [ ] Trace-test parallelism convention added to `docs/conventions.md` (or `CLAUDE.md`)
- [ ] Unit tests cover every `PollerGating` branch
- [ ] Integration tests cover skip path and poll path
- [ ] No regression on Session 4's existing integration tests
- [ ] `dotnet build`, all unit tests, all integration tests pass
- [ ] No changes outside the scope listed above

---

## Summary expected at the end

1. Filenames added or modified (full paths)
2. Any deviation from this prompt (with reason)
3. How the cold-start timezone trade-off was resolved in code — whether the recommended "accept misalignment for first 24h" approach was used or something different
4. Anything Session 5 (backfill + `/me`) needs to know:
   - Whether backfill should bypass `PollerGating` entirely (likely yes — backfill is on-demand, not scheduled)
   - Whether the new `last_poll_outcome` field surfaces on `/me` in any form (probably no for MVP, but worth confirming)
5. Any surprises in the per-connection wake-window computation — particularly around users with sparse sleep history (one or two sleeps total)
6. Operational note: with the gating in place, how often does the poller actually do work for Tai per day? Rough order-of-magnitude estimate based on the test data.
