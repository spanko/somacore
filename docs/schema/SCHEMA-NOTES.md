# Phase 1 schema notes

This file is the companion to `0001_initial_schema.sql`. It captures the design decisions, constraint reasoning, and EF Core mapping guidance.

When EF Core entities are generated (via Claude Code or by hand), they should match this schema exactly. The SQL file is the source of truth; the entities mirror it.

## The five tables in one paragraph

A SomaCore user (`users`) has zero or more `external_connections`. A connection is the OAuth-state-and-metadata for one source (WHOOP today, Oura/Strava later); the actual refresh token lives in Key Vault, not here. WHOOP recovery events stream into `whoop_recoveries`, deduplicated on `(external_connection_id, whoop_cycle_id)` so all three ingestion paths (webhook, poller, on-open) converge on a single row. Every inbound webhook is recorded in `webhook_events` — this table doubles as audit trail and as the work queue (see ADR 0009). Every OAuth-related action we take is recorded in `oauth_audit` — outbound activity, distinct from the inbound webhooks table.

## Decisions worth re-stating

### UUID v7 primary keys

Every primary key is UUID v7. App-generated on Postgres 16/17 (we pass them in via `INSERT`), can move to native `uuidv7()` defaults on Postgres 18+. The C# side uses `Guid.CreateVersion7()` (.NET 9+) or a tiny helper — no extra package needed.

EF Core mapping note: configure the `id` columns with `.HasDefaultValueSql("...")` only when on PG18+. For PG16/17, generate in C# and don't set a database default — the entity sets the value before insert.

### External connections as a separate table, not columns on users

Users own *connections*, plural. Each connection has its own status, scopes, refresh state, and Key Vault secret pointer. Adding Oura in phase 2 = one row in `external_connections` with `source = 'oura'`. No schema migration. See ADR 0005 for the identity-model rationale this extends.

### Tokens never live in the database

`external_connections.key_vault_secret_name` is a pointer, not a token. Phase-1 naming convention: `whoop-refresh-{user_id}` (the SomaCore user UUID, not the WHOOP user ID). The application reads from Key Vault on demand with a 5-min in-process cache.

### Raw payloads stored alongside parsed columns

`whoop_recoveries.raw_payload` and `webhook_events.raw_body` hold the exact JSON we received. This makes the pipeline recomputable per the architecture principle — if a WHOOP API change adds a field, or we discover we should have been parsing differently, we replay from raw without re-fetching.

Storage cost is negligible (a few KB per recovery, under 2MB per user-year). EF Core maps `jsonb` to either `string` or a typed model via Npgsql's JSON support. Phase 1 keeps it as `JsonDocument` or `string` — we don't need typed access on the raw side.

### Hard delete only

No soft-delete columns. The privacy policy commits to actual deletion. Cascade rules:

- `users` → `external_connections`: `ON DELETE CASCADE`
- `users` → `whoop_recoveries`: `ON DELETE CASCADE`
- `external_connections` → `whoop_recoveries`: `ON DELETE CASCADE`
- `users` → `webhook_events`: `ON DELETE SET NULL` (preserve audit trail; webhooks are tied to connection IDs anyway)
- `users` → `oauth_audit`: `ON DELETE SET NULL` (same reasoning)

When a user deletes their account, recoveries go with them; the audit trail keeps a redacted record (no user ID, but the event existed).

### Text + CHECK over Postgres ENUM types

We use `text` columns with `CHECK` constraints rather than `CREATE TYPE ... AS ENUM`. Reasons:

1. Adding a new value to a Postgres ENUM cannot be used in the same transaction it was added in. Adding a new value to a CHECK constraint is a one-step migration.
2. EF Core's ENUM mapping for Postgres requires extra wiring; string mapping is the default.
3. Evolution cost is negligible for CHECK; modest for ENUM.

Tradeoff: typo'd values in code can pass type-check but fail at insert. Mitigation: define the allowed values as `static readonly` constants in C# domain types.

### Timestamps managed by the application layer

Every table has `created_at` and `updated_at` (also `last_seen_at`, `ingested_at`, `received_at`, `processed_at`, `occurred_at` where the semantics differ from "row last touched"). The application sets them on insert and update — no triggers.

Reasoning: triggers are invisible behavior. Anyone reading the schema sees defaults of `now()` for `created_at`, but `updated_at` only gets `now()` on insert; updates touch it via the application. EF Core's `SaveChangesAsync` interceptor is the right home for that — one place, easy to test.

### Partial unique index for active connections

```sql
CREATE UNIQUE INDEX idx_external_connections_user_source_active
    ON external_connections (user_id, source)
    WHERE status = 'active';
```

This enforces "one active WHOOP connection per user" while letting revoked or pending rows coexist. Without the partial index, re-authorization (after a user revokes and reconnects) would either require deleting the old row (losing audit trail) or a soft-delete pattern (which we explicitly rejected).

### Partial index on the work queue

```sql
CREATE INDEX idx_webhook_events_pending
    ON webhook_events (received_at)
    WHERE status IN ('received', 'processing');
```

The work queue path is "give me the next batch of unprocessed events." A full index over `webhook_events.received_at` would grow with all-time webhook volume; the partial index stays small (only the actively-pending rows) and serves the query path that matters.

## EF Core mapping guidance

When entities are generated, they should:

- Use `Guid` for all UUID columns. Set the value in the constructor (or via a value generator) using `Guid.CreateVersion7()` on .NET 9+.
- Map `text` enum-like columns to `string` properties **and** define typed constants in the domain layer:

  ```csharp
  public static class ScoreState
  {
      public const string Scored = "SCORED";
      public const string PendingScore = "PENDING_SCORE";
      public const string Unscorable = "UNSCORABLE";
  }
  ```

  Use these constants in code; the schema's CHECK constraint catches drift at the DB layer.
- Map `jsonb` columns to `JsonDocument` or `string` for raw payloads. We don't need typed access in phase 1.
- Configure timestamps with `ValueGeneratedNever()` and set them in the application layer (interceptor or domain).
- Configure all read queries with `.AsNoTracking()` per `docs/conventions.md`.

## What to test

The integration tests for the data layer should cover, at minimum:

- The unique index on `(external_connection_id, whoop_cycle_id)` does what we expect (idempotency)
- The partial unique index on `external_connections` enforces single-active-per-source
- The score_state CHECK constraint rejects rows where SCORED is missing recovery_score
- A user delete cascades correctly — connections and recoveries removed, audit rows have NULL user_id
- Insert + update both touch `updated_at` (via the EF Core interceptor)

These tests run against a real Postgres via Testcontainers, per `docs/conventions.md`.

## What's NOT in this schema, deliberately

- **No `cycles`, `sleeps`, or `workouts` tables.** Phase 2.
- **No physiological state snapshots.** That's a derived shape we'll define when the rules engine exists.
- **No plan output tables.** Same.
- **No adherence event tables.** Same.
- **No partition keys.** We have one user worth of data in phase 1; partitioning is a future-volume concern.
- **No row-level security.** We have three trusted users in phase 1. RLS becomes interesting when external users exist.

Each of these will get its own ADR + migration when the time comes.
