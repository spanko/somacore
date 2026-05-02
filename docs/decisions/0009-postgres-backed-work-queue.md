# 0009. Postgres-backed work queue for phase 1

Date: 2026-05-02
Status: Accepted

## Context

The webhook handler returns 2XX in <1 sec and processes asynchronously. The `whoop-architecture.docx` spec calls for Azure Service Bus between webhook receipt and async processing.

Phase 1 has three users producing maybe 10 recovery webhooks per day total. Service Bus is engineering-correct but operationally heavier than the workload requires.

## Decision

**Phase 1 uses a Postgres-backed work queue** in the `webhook_events` table:

- Webhook handler validates the HMAC, inserts a row with `status = 'received'`, returns 200.
- A background `BackgroundService` in the same API process polls for `status = 'received'` rows, processes them, sets `status = 'processed'` (or `'failed'` with retry logic).
- Idempotency via `(event_id, trace_id)` unique constraint.
- **Phase 2 will replace this with Azure Service Bus** when multi-user load and reliability requirements demand it.

## Consequences

- Fewer Azure resources to provision and pay for.
- Single deployable for the API (background service runs in-process).
- Loses the durability + retry semantics of Service Bus, accepted because at three users the queue is essentially empty.
- **Migration debt is bounded.** The interface between the webhook handler and the queue (`IWebhookEventQueue` or similar) is what swaps. Implementations: `PostgresWebhookEventQueue` (phase 1), `ServiceBusWebhookEventQueue` (phase 2).
- We must build the interface in phase 1 even though there's only one implementation. Otherwise the migration is rewriting, not swapping.

## Alternatives considered

- **Azure Service Bus from day one.** Right answer for production; over-engineered for three users on a dev environment. Reject for phase 1.
- **In-memory queue (`Channel<T>`).** Lost on restart, lost on scale-out. We restart for every deploy. Reject.
- **Storage Queues.** Cheaper than Service Bus, but the gap to Postgres-as-a-queue is small and we'd rather not introduce a third Azure resource for this scale.

## When to revisit

Move to Service Bus when **any** of:
- Webhook volume exceeds ~100/min sustained
- We need delayed delivery, dead-letter handling, or fan-out
- We have more than one consumer of the same event stream
- We deploy a separate worker process for ingestion (rather than in-process `BackgroundService`)

Document the migration in a follow-up ADR (0XXX) when triggered.
