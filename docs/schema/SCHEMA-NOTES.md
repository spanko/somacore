# Schema notes

This file is the companion to the SQL spec files under `docs/schema/`. It captures the design decisions, constraint reasoning, and EF Core mapping guidance.

When EF Core entities are generated (via Claude Code or by hand), they should match these specs exactly. The SQL files are the source of truth; the entities mirror them.

- `0001_initial_schema.sql` — phase 1 (users, external_connections, whoop_recoveries, webhook_events, oauth_audit; later: job_runs)
- `0002_whoop_sleep_workout.sql` — phase 2 track A extension (whoop_sleeps, whoop_workouts)

## The phase-1 tables in one paragraph

A SomaCore user (`users`) has zero or more `external_connections`. A connection is the OAuth-state-and-metadata for one source (WHOOP today, Oura/Strava later); the actual refresh token lives in Key Vault, not here. WHOOP recovery events stream into `whoop_recoveries`, deduplicated on `(external_connection_id, whoop_cycle_id)` so all three ingestion paths (webhook, poller, on-open) converge on a single row. Every inbound webhook is recorded in `webhook_events` — this table doubles as audit trail and as the work queue (see ADR 0009). Every OAuth-related action we take is recorded in `oauth_audit` — outbound activity, distinct from the inbound webhooks table.

## The phase-2 track-A additions in one paragraph

`whoop_sleeps` holds one row per WHOOP sleep object (one main sleep per cycle plus zero or more naps; `nap` is the discriminator). `whoop_workouts` holds one row per WHOOP workout — v2 returns sport as `sport_name` (string) rather than the legacy `sport_id` (int), so we store the name. Both tables mirror `whoop_recoveries` exactly: UUID v7 PK, `user_id` cascade FK, `external_connection_id` set-null FK, score_state CHECK constraint, full WHOOP `score` object preserved as jsonb alongside lifted columns, `raw_payload` for recomputability, application-managed timestamps, and uniqueness scoped to `(external_connection_id, whoop_*_id)` so the three ingestion paths converge on one row. Indexes on `(user_id, start_at DESC)` serve the rules-engine access pattern.

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
- `external_connections` → `whoop_recoveries`: `ON DELETE SET NULL` — *user disconnect* (delete the connection row) preserves their recovery history; recoveries are tied to the user via `user_id` and a disconnect is severing the *integration*, not deleting *data*. Recoveries cascade only on full account deletion via the user FK above.
- `users` → `whoop_sleeps`: `ON DELETE CASCADE` (phase 2 track A — identical contract to `whoop_recoveries`)
- `external_connections` → `whoop_sleeps`: `ON DELETE SET NULL` (same disconnect-severs-integration-not-data reasoning)
- `users` → `whoop_workouts`: `ON DELETE CASCADE` (same)
- `external_connections` → `whoop_workouts`: `ON DELETE SET NULL` (same)
- `users` → `webhook_events`: `ON DELETE SET NULL` (preserve audit trail; webhooks are tied to connection IDs anyway)
- `users` → `oauth_audit`: `ON DELETE SET NULL` (same reasoning)
- `external_connections` → `oauth_audit`: `ON DELETE SET NULL` (audit row survives the connection it audited; preserves "an event happened at this time" without retaining the identifier)
- `external_connections` → `webhook_events`: `ON DELETE SET NULL` (same)

When a user deletes their account, recoveries / sleeps / workouts go with them; the audit trail keeps a redacted record (no user ID, but the event existed). This matches what `docs/privacy-data-handling.md` (Section B) commits to: disconnect severs the integration, full-account deletion removes the user's data, audit rows survive both with the FK nulled.

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

## Phase 2 track A: whoop_sleeps and whoop_workouts

Both tables mirror `whoop_recoveries` exactly in shape, audit columns, raw-payload retention, and cascade rules. The only structural differences from `whoop_recoveries`:

- **Natural key.** `whoop_recoveries` uses `whoop_cycle_id` (bigint) because recoveries hang off cycles. Sleep and workout each have their own UUID, so `whoop_sleeps.whoop_sleep_id` and `whoop_workouts.whoop_workout_id` are `uuid NOT NULL`. The uniqueness index lives on `(external_connection_id, whoop_*_id)` — same dedupe contract as recoveries.
- **Time window.** `whoop_recoveries.cycle_start_at / cycle_end_at` describe the cycle the score belongs to. `whoop_sleeps` and `whoop_workouts` use `start_at / end_at` with both required — every sleep and workout has a defined window. The hot-path index is `(user_id, start_at DESC)` instead of `(user_id, cycle_start_at DESC)`.
- **Timezone offset.** WHOOP returns the wearer's wall-clock offset alongside UTC `start`/`end`. We store it as the literal string (e.g. `"-07:00"`) in `timezone_offset` so the rules engine can reason about local-time scheduling without re-deriving it. Recoveries do not carry this today because cycles are already day-shaped.
- **`score` jsonb.** Both new tables carry the WHOOP `score` object as `jsonb` alongside the lifted columns (sleep performance/efficiency/consistency + in-bed/asleep ms; strain + avg/max HR + kilojoule). Two reasons: schema-change insulation from WHOOP, and the ability to backfill new derived columns later from raw without re-fetching. `score` is nullable — `PENDING_SCORE` / `UNSCORABLE` rows do not carry a score.
- **`sport_name` (workouts only).** WHOOP v2 returns sport as a string name rather than the v1 integer `sport_id`. We persist the name verbatim.
- **No `whoop_sleep_id` cross-link on workouts.** Workouts in WHOOP v2 are independent of sleeps; there is no equivalent of `whoop_recoveries.whoop_sleep_id`.

CHECK-constraint vocabulary (`score_state`, `ingested_via`) is identical to `whoop_recoveries`. The constants in `SomaCore.Domain.WhoopRecoveries.ScoreState` and `IngestedVia` are reused — no parallel constants in the sleep/workout namespaces. If the rules engine later wants to filter recoveries+sleeps+workouts uniformly on these, the shared vocabulary keeps that mechanical.

## What's NOT in this schema, deliberately

- **No `cycles` table.** Phase 2 track A added sleep and workout. A standalone cycles table may come in a later session if we find a use case the existing recovery row doesn't already serve.
- **No physiological state snapshots.** That's a derived shape we'll define when the rules engine exists.
- **No plan output tables.** Same.
- **No adherence event tables.** Same.
- **No partition keys.** We have one user worth of data in phase 1; partitioning is a future-volume concern.
- **No row-level security.** We have three trusted users in phase 1. RLS becomes interesting when external users exist.

Each of these will get its own ADR + migration when the time comes.
