# 0006. Three-layer WHOOP ingestion (webhook + poller + on-open)

Date: 2026-05-02
Status: Accepted

## Context

The morning plan in our long-term architecture depends on having today's WHOOP recovery score available with low latency when the user opens the app. WHOOP's docs are explicit that webhook delivery is **not guaranteed** and that we should not rely on webhooks as the sole source of truth. They recommend a reconciliation path.

The under-10-second budget for plan generation means we cannot wait for an in-line WHOOP fetch on every app open as the primary path — it has to be a fallback.

## Decision

Three layers, all feeding the same ingestion handler:

1. **Webhook (primary).** WHOOP POSTs `recovery.updated` to `/webhooks/whoop`. We validate the HMAC signature, return 2XX in <1 sec, enqueue an event row, async worker fetches the actual recovery and stores it. Real-time pre-warm.
2. **Reconciliation poller (catch-net).** Container Apps Job, cron schedule. Pulls latest cycle for users with no scored recovery for the current cycle. Catches webhooks that were missed.
3. **On-open synchronous pull (last resort).** When the `/me` page is opened and no fresh recovery is in the store, the page triggers an in-line fetch. Acceptable degradation in latency (4–8 sec instead of <2 sec) for the rare case where both prior layers lagged.

**All three converge on a single `IRecoveryIngestionHandler`** with shared idempotency (dedupe by webhook event ID and WHOOP cycle ID), shared logging, shared downstream effects.

## Consequences

- Reliability is the explicit win. We expect webhook misses; we plan for them.
- One handler to test, one to monitor. Convergence is the architectural commitment, not the implementation detail.
- Operational complexity is higher than "just use webhooks" — but the cost is mostly upfront (build the poller once) and the downside of the simpler approach (silent missed plans) is unacceptable for the product's morning-routine claim.
- The pattern is reusable for Oura and Strava in phase 2 with minimal reshape.

## Alternatives considered

- **Webhooks only.** Simplest, but WHOOP's own docs say not to. Reject.
- **Polling only, no webhooks.** Adequate, but 30-min polling latency means the morning plan is sometimes 30 min late. Bad UX. Reject.
- **Webhooks + on-open only, no poller.** No catch-net for users who don't open the app right away — recovery might silently never arrive. Reject.
- **Webhooks + poller, no on-open.** Adequate for most cases. We add the on-open layer because it costs little and covers the cold-start edge cases (new user, just-cleared cache, both webhook and poller lagging). Accept.

## See also

- `whoop-architecture.docx` (project knowledge) — the full integration spec
- `docs/architecture.md#the-three-layer-ingestion-pattern`
