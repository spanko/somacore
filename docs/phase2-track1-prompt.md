# Track A · Session 1: Canonical schema extension for WHOOP sleep + workout

**Goal of this session.** Extend the database schema to hold WHOOP sleep and workout data alongside the existing recovery data. **Schema and migration only — no ingestion logic, no webhook handlers, no API calls.**

---

## Context

SomaCore is a personal health decision engine. Phase 1 (WHOOP recovery ingestion for three internal users) is complete. We are now in Phase 2, Track A: extending the WHOOP integration from recovery-only to all three layers (recovery, sleep, workout). This session is the foundation — the schema that subsequent sessions will write into.

**Stack.** .NET 9, Razor Pages, Postgres (hand-rolled SQL migrations in `docs/schema/`), Azure infrastructure.

**Reference materials in project knowledge.**
- `whoop-architecture.docx` — the full three-layer ingestion architecture, including how recovery/sleep/workout relate in WHOOP v2 (recovery and sleep arrive together via the cycle endpoint; workout has its own webhook).
- `docs/privacy-data-handling.md` — the ground truth on cascade rules. Honor these for the new tables (see "Cascade rules" below).

**Existing code to mirror.**
- `docs/schema/0001_initial_schema.sql` — the schema source of truth, including the `whoop_recoveries` table that the new tables should mirror in shape.
- `docs/schema/SCHEMA-NOTES.md` — needs to be updated as part of this session.
- `external_connections` table — the FK target for both new tables.

---

## Scope

Add two new tables to the schema, with their migration file, following the patterns established by `whoop_recoveries`:

1. **`whoop_sleeps`** — one row per WHOOP sleep object (each cycle has one main sleep plus zero or more naps).
2. **`whoop_workouts`** — one row per WHOOP workout.

Both tables must:

- Use the same audit-column pattern as `whoop_recoveries` (whatever that is — `created_at`, `updated_at`, etc.).
- FK to `users.id` and `external_connections.id`, with the same cascade rules as `whoop_recoveries` (see Cascade rules below).
- Carry the WHOOP-side identifier (sleep UUID, workout UUID) as a separate column from the local PK, with a uniqueness constraint scoped to the user.
- Carry `score_state` as a first-class column (enum or text constrained to `SCORED` / `PENDING_SCORE` / `UNSCORABLE`) so queries can filter on it without parsing jsonb.
- Carry the structured WHOOP `score` object as `jsonb`, even where individual fields are also lifted into columns. Two reasons: we don't have to chase schema changes from WHOOP, and we can backfill new derived columns later from the raw payload.
- Carry timezone offset as WHOOP returns it.

**Lift the following fields into typed columns** (the rules engine will query these heavily; the rest can stay in the `score` jsonb):

- **`whoop_sleeps`**: `start`, `end`, `nap` (bool), `score_state`, `sleep_performance_percentage`, `sleep_efficiency_percentage`, `sleep_consistency_percentage`, `total_in_bed_time_milli`, `total_sleep_time_milli`.
- **`whoop_workouts`**: `start`, `end`, `sport_name` (text, v2 uses names not IDs), `score_state`, `strain`, `average_heart_rate`, `max_heart_rate`, `kilojoule`.

If WHOOP v2 has renamed any of these, use the v2 name and leave a comment in the migration explaining the mapping.

Add indexes on `(user_id, start DESC)` for both tables — that's the access pattern the rules engine will use.

---

## Cascade rules (per privacy doc)

Both new tables must follow the same FK cascade contract as `whoop_recoveries`:

- FK from `whoop_sleeps.external_connection_id` → `external_connections.id` is **`ON DELETE SET NULL`**. Disconnect severs the integration, not the data.
- FK from `whoop_sleeps.user_id` → `users.id` is **`ON DELETE CASCADE`**. Full account deletion removes the user's sleep history.
- Same for `whoop_workouts`.

This matches `whoop_recoveries` exactly. If `whoop_recoveries` does something different, follow `whoop_recoveries` and flag the discrepancy with the privacy doc in your summary.

---

## Out of scope (do NOT do any of this in this session)

- ❌ Webhook handlers for `sleep.updated` or `workout.updated` (Sessions 2 and 3)
- ❌ Pull paths from the WHOOP API for sleep or workout (Sessions 2 and 3)
- ❌ Reconciliation poller changes (Session 4)
- ❌ Backfill scripts (Session 5)
- ❌ Any changes to `whoop_recoveries`, `oauth_audit`, `webhook_events`, `external_connections`, `users`
- ❌ Any changes to `Me.cshtml.cs` or any other Razor page
- ❌ EF Core model classes — this codebase uses raw SQL migrations and (probably) Dapper or similar; mirror what exists. Do not introduce EF Core if it isn't already present.

If you find yourself reaching for any of these, stop and surface it. The whole point of this session is a clean foundation that the next four sessions can build on without re-litigating schema decisions.

---

## Deliverables

1. **Migration file.** `docs/schema/0002_whoop_sleep_workout.sql` (or whatever number follows the existing convention — check first). Contains `CREATE TABLE` statements for both tables and the indexes, in a transaction.
2. **Schema notes updated.** `docs/schema/SCHEMA-NOTES.md` — add entries for both new tables under whatever structure that doc uses, including the cascade rules section.
3. **Repository/data-access layer skeleton.** If `whoop_recoveries` has a corresponding `WhoopRecoveryRepository` (or similar) class, create the analog for sleep and workout — interface and class skeleton only. Methods needed: `UpsertAsync`, `GetLatestByUserAsync`, `GetByExternalIdAsync`. Method bodies can be the minimum that compiles (e.g. `throw new NotImplementedException()`) — Sessions 2 and 3 will fill them in. The point of including them here is so the migration and the repository layer ship together as one coherent foundation.
4. **Verification.** Run the migration against a local Postgres instance, confirm it applies cleanly, confirm the constraints are correct (try a `DELETE` of a parent row and observe the cascade behavior matches `whoop_recoveries`).

---

## Validation steps to run before declaring done

```bash
# 1. Apply migration cleanly
dotnet run --project tools/MigrationRunner   # or whatever the existing migration command is

# 2. Inspect the new tables
psql $DATABASE_URL -c "\d whoop_sleeps"
psql $DATABASE_URL -c "\d whoop_workouts"

# 3. Verify cascade behavior matches whoop_recoveries
# (manual: insert a test row, delete the external_connection, confirm FK is nulled but row survives)

# 4. Build the solution — repository skeleton must compile
dotnet build

# 5. Run existing tests — nothing should break
dotnet test
```

---

## Exit criteria

- [ ] Migration `0002_whoop_sleep_workout.sql` exists and applies cleanly to a fresh Postgres database
- [ ] Migration also applies cleanly on top of the existing `0001_initial_schema.sql` against a database that already has Phase 1 data
- [ ] Both new tables have indexes on `(user_id, start DESC)`
- [ ] Both new tables follow the cascade rules in `docs/privacy-data-handling.md` exactly
- [ ] `docs/schema/SCHEMA-NOTES.md` updated with entries for both new tables
- [ ] Repository skeletons compile and follow the existing `WhoopRecoveryRepository` pattern
- [ ] `dotnet build` and `dotnet test` both pass
- [ ] No changes outside `docs/schema/`, `SCHEMA-NOTES.md`, and the new repository files

---

## Summary expected at the end

A short summary covering:

1. The exact filenames added or modified
2. Any deviation from the column lists above (with reason — e.g. WHOOP v2 renamed a field)
3. Any discrepancy noticed between this session and what's in the privacy doc or whoop-architecture.docx
4. Anything Sessions 2-5 will need to know that wasn't obvious from the kickoff (e.g. "the cycle endpoint returns sleeps with a different timestamp shape than I expected — Session 2 will need to handle this")

That summary feeds the prompt for Session 2.
