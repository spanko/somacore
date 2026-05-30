# ADR 0011 — Ingestion trace contract

**Status:** Accepted (amended 2026-05 after Session 2; 2026-05 after Session 4 resolved poller open item; 2026-05 after Session 5 resolved backfill open item)
**Date:** May 2026
**Context:** Phase 2 Track A, Session 2 (introduces the first multi-handler trace shape)

---

## Context

Track A Session 2 introduces the first ingestion path where a single event produces multiple handler invocations: a `recovery.updated` webhook now triggers a cycle fetch followed by both `IRecoveryIngestionHandler` and `IWhoopSleepIngestionHandler`. Sessions 3-4 will add a third handler (workout) and a second trace root (poller).

Without a documented contract, each new ingestion handler will invent its own span shape and tag vocabulary. Diagnostic queries in Application Insights will fragment — different filters for "how many recoveries did we ingest" vs. "how many sleeps" vs. "what events failed."

We need a single shape across all WHOOP ingestion paths (and later, Apple Health / aggregator paths) so dashboards, alerts, and ad-hoc KQL queries work uniformly.

## Decision

Document the trace contract as a convention plus a small set of helpers in `SomaCore.Infrastructure`. **Do not enforce it via inheritance.**

### Why not a base class

The trace contract spans three different code locations: webhook drainer dispatch, upstream API fetch (`WhoopApiClient`), and ingestion handlers. No single base class covers all three without forcing unrelated code to share a parent. Inheritance also fights the established pattern that ingestion handlers are siblings, not children of a shared abstract.

A convention + helpers + tests enforces the same uniformity without the coupling:

- The convention is discoverable (this ADR + `docs/conventions.md` reference)
- The helpers make doing the right thing easy
- Per-handler integration tests assert the span shape is emitted correctly via `TraceAssertions.AssertIngestionSpanShape`
- New ingestion sources opt in by calling the helpers; the test enforces conformance

### The contract

Every ingestion path emits one root span with one or more child spans:

```
root span: whoop.ingestion              (or healthkit.ingestion, etc.)
├── child:  whoop.cycle.fetch           (one per upstream fetch; may be 1-N HTTP calls)
├── child:  recovery.ingest             (when handler is invoked)
├── child:  sleep.ingest                (when handler is invoked)
└── child:  workout.ingest              (when handler is invoked)
```

Not every event type produces every handler child. A `workout.updated` webhook produces only a `workout.ingest` child; a `recovery.updated` produces recovery and (when present) sleep. The root span's outcome rollup tags reflect every handler that *could* have been invoked, using `NotInvoked` where appropriate (see below).

**Root span tags (always present):**

| Tag | Meaning |
|---|---|
| `ingestion.source` | `whoop`, `healthkit`, etc. |
| `ingestion.trigger` | `webhook`, `poller`, `on_open`, `backfill` |
| `ingestion.event_type` | `recovery.updated`, `sleep.updated`, `workout.updated`, `cycle.pull`, etc. |
| `external_connection_id` | The connection this ingestion is for |
| `trace_id` | The upstream-provided trace ID (e.g. WHOOP's `trace_id`) for correlation across systems |

**Root span rolled-up outcome tags (always present, one per known handler for the source):**

| Tag | Meaning |
|---|---|
| `outcomes.recovery` | `Inserted` / `Updated` / `NoOp` / `SkippedNoData` / `NotInvoked` |
| `outcomes.sleep` | same vocabulary |
| `outcomes.workout` | same vocabulary |

**`NotInvoked` semantics (amended).** Use `NotInvoked` whenever a handler did not run for any reason, including:

- **Short-circuit:** the upstream fetch returned no data for this handler's entity (e.g. cycle response has no sleep block). The handler was eligible but skipped.
- **Out of scope for event type:** the event type doesn't include this handler at all (e.g. a `workout.updated` event never invokes recovery or sleep handlers — those outcomes are `NotInvoked`).

The intent is that root span outcome tags are always present for every known handler in the source, so a single KQL query against `outcomes.workout` works identically regardless of whether the trigger was a cycle event or a workout event. This keeps dashboards uniform at the cost of slightly stretching the semantics of `NotInvoked` — but the alternative ("only the tags relevant to this event type are present") forces every dashboard to know which event types include which handlers, which is exactly the fragmentation this contract exists to prevent.

**Distinguishing `NotInvoked` from `SkippedNoData`:** `SkippedNoData` means the handler ran, fetched, and chose not to write. `NotInvoked` means the handler never ran. The orchestrator (drainer or poller) sets `NotInvoked` on the root span; the handler itself sets `SkippedNoData` via its outcome return.

**Fetch span tags:**

| Tag | Meaning |
|---|---|
| `http.url` (or `whoop.endpoint`) | Which endpoint was called |
| `whoop.cycle_id` (when applicable) | The natural key being fetched |
| `http.status_code` | Outcome |
| `fetch.duration_ms` | Latency |

A single `whoop.cycle.fetch` span may cover multiple HTTP calls when the logical "cycle pull" expands to 2-3 sub-endpoint calls (cycle envelope + recovery + sleep). The span represents the logical fetch, not a single HTTP call. Individual HTTP calls can be observed separately via auto-instrumentation when that's enabled.

**Handler span tags:**

| Tag | Meaning |
|---|---|
| `handler.name` | `recovery_ingestion`, `sleep_ingestion`, `workout_ingestion` |
| `handler.outcome` | `Inserted` / `Updated` / `NoOp` / `SkippedNoData` |
| `entity.natural_key` | WHOOP UUID for the entity ingested |
| `score_state` | `SCORED` / `PENDING_SCORE` / `UNSCORABLE` / `null` (when applicable) |

### The helpers

A static class in `src/SomaCore.Infrastructure/Observability/IngestionTracing.cs` plus `Tags` and `Outcomes` constants exposes:

```csharp
public static class IngestionTracing
{
    // Wraps ActivitySource.StartActivity with required root-span tags pre-applied.
    public static Activity? StartIngestionScope(
        string source,           // "whoop"
        string trigger,          // "webhook" / "poller" / "on_open" / "backfill"
        string eventType,        // "recovery.updated" / "cycle.pull" / ...
        Guid externalConnectionId,
        string? upstreamTraceId);

    public static Activity? StartFetchScope(
        string endpoint,
        string? naturalKey);

    public static Activity? StartHandlerScope(
        string handlerName,
        string naturalKey);

    // Sets handler.outcome on the current handler span.
    public static void RecordHandlerOutcome(Activity? handlerSpan, string outcome);

    // Sets http.status_code and fetch.duration_ms on the current fetch span.
    public static void RecordFetchOutcome(Activity? fetchSpan, int statusCode);

    // Sets the rolled-up outcome tag on the root span.
    // Called by each handler after completing, OR by the orchestrator with NotInvoked
    // for handlers that were skipped or out of scope.
    public static void RecordOutcome(
        Activity? rootSpan,
        string handlerName,      // "recovery" / "sleep" / "workout"
        string outcome);         // "Inserted" / "Updated" / "NoOp" / "SkippedNoData" / "NotInvoked"
}
```

`ActivitySource` is registered as a singleton via `AddSomaCoreTelemetry()` in `SomaCore.Infrastructure`. Each host (`SomaCore.Api`, `SomaCore.IngestionJobs`) wires its own exporter (Azure Monitor or otherwise) in its `Program.cs` so that hosts which don't need the exporter aren't forced to depend on it.

Callers own disposal via `using` — the helpers don't fight the framework idiom.

### Test enforcement

Each ingestion handler's integration test asserts:
1. The handler emits exactly one span named `<handler>.ingest`
2. The span has the required tags
3. The outcome tag matches the returned `Result<>` outcome

A shared test helper (`TraceAssertions` in `tests/SomaCore.IntegrationTests/Observability/TraceAssertions.cs`) provides `Capture()` plus `AssertIngestionSpanShape(activities, root, children)` so per-handler tests stay short. This is what enforces the contract — not the type system.

## Consequences

**Positive:**

- Application Insights dashboards work the same for every ingestion source. Adding a new source means matching the contract, not designing new tags.
- Outcome attribution is observable from the root span without joining children, while detailed per-handler queries remain available.
- A single KQL query like `outcomes.workout != "NotInvoked"` filters cleanly across all event types.
- Helpers prevent the most common mistakes (forgetting required tags, mis-naming spans) without forcing inheritance.

**Negative / acknowledged trade-offs:**

- The contract is enforced by test discipline rather than the type system. A handler that forgets to call `RecordOutcome` won't fail compilation — it'll fail its integration test. This is acceptable because the alternative (a base class) couples handlers in ways the codebase has deliberately avoided.
- The rolled-up outcome tags on the root span duplicate data already present in child spans. This is intentional for query ergonomics; not all dashboards should need a join.
- `NotInvoked` carries two meanings (short-circuit AND out-of-scope-for-event-type). The reasoning above explains why; for queries that care to distinguish, the presence or absence of the corresponding `<handler>.ingest` child span is the discriminator.

## Resolved items

- **Poller-as-trace-root shape** (resolved Session 4, 2026-05). The poller emits a separate trace root per (user, cycle) for the cycle pull, and a separate trace root per (user, workout) for each workout enumerated from `ListRecentWorkoutsAsync`. The `ingestion.trigger=poller` tag is what distinguishes poller traces from webhook traces in dashboards. The job-level (`ReconciliationPoller` invocation) does NOT itself open a root span — the root-per-entity shape keeps dashboard queries against `outcomes.*` tags uniform across triggers. `ingestion.event_type` is `cycle.pull` for cycle roots and `workout.pull` for workout roots.

- **Backfill traces** (resolved Session 5, 2026-05). `WhoopBackfillService` emits one `whoop.ingestion` trace root per ingested entity (per-recovery, per-sleep, per-workout) with `ingestion.trigger=backfill`. `ingestion.event_type` is `cycle.backfill` for recovery and sleep roots and `workout.backfill` for workout roots. The backfill job itself opens no enclosing parent — same root-per-entity shape as the poller. Reasoning: a 30-day window emits ~120 root spans per user (well within ingestion budget at our scale) and keeps `outcomes.*` dashboard queries uniform across triggers. The single-job-trace alternative would have created a new top-level span shape every dashboard would have to know about specifically — exactly the fragmentation the rest of this ADR exists to prevent.

## Open items

These are left for amendment once they actually surface:

- **Multi-source traces** (Track C). When HealthKit and WHOOP data converge on the same rules engine invocation, the ingestion trace and the rules-engine trace will be different roots. Linking them via a shared correlation ID is out of scope for this ADR.

## References

- ADR 0006 — three-layer WHOOP ingestion (defines the architectural shape this contract is observability for)
- ADR 0009 — postgres-backed work queue (provides the `webhook_events` idempotency layer that sits upstream of these traces)
- `docs/conventions.md` — async-all-the-way, Serilog, idempotency principles
- `src/SomaCore.Infrastructure/Observability/IngestionTracing.cs` — the helper implementation
- `tests/SomaCore.IntegrationTests/Observability/TraceAssertions.cs` — the test enforcement helper
