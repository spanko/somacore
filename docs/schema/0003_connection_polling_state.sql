-- =============================================================================
-- SomaCore — Phase 2, Session 4.5: Connection polling state
-- =============================================================================
--
-- Adds per-connection scheduling state to support the adaptive poller gating
-- (Option D: hybrid hourly tick with per-user gating). The poller is still a
-- one-shot cron-triggered Container Apps Job — no long-running scheduler. On
-- each tick the job enumerates active connections and evaluates these columns
-- to decide whether to do work for each one.
--
-- This file is the target representation of the EF migration
-- `AddConnectionPollingState`. The SQL spec is the human-reviewable artifact;
-- the migration is what executes. When they drift, this file is the spec we
-- reconcile against.
--
-- Why these columns are nullable:
--   - last_polled_at:   null until the first tick that touches this connection.
--   - last_poll_outcome: same. Also null for connections that pre-date this
--                       migration (no backfill — the next tick fills it in).
--
-- Why NOT a `next_poll_due_at` column:
--   We're deliberately not building Quartz-style external scheduler state.
--   Per-tick computation of "should we poll?" is cheaper than maintaining
--   denormalized scheduling state — and the gating logic lives in code where
--   it's testable and reviewable.
-- =============================================================================


ALTER TABLE external_connections
    ADD COLUMN last_polled_at      timestamptz NULL,
    ADD COLUMN last_poll_outcome   text NULL;

-- Vocabulary backed by C# constants in SomaCore.Domain.ExternalConnections.PollOutcome.
-- Backed at the database layer by CHECK so typos in code fail loudly at insert.
ALTER TABLE external_connections
    ADD CONSTRAINT chk_external_connections_last_poll_outcome
        CHECK (last_poll_outcome IS NULL
               OR last_poll_outcome IN ('Skipped', 'Polled', 'Failed'));


-- =============================================================================
-- End of session 4.5 schema
--
-- Added columns: last_polled_at, last_poll_outcome (both nullable)
-- Added constraints: 1 CHECK on last_poll_outcome vocabulary
-- No new indexes — the poller iterates all active connections per tick and
-- reads these columns row-by-row; no query pattern justifies an index yet.
-- =============================================================================
