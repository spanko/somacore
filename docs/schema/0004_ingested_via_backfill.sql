-- =============================================================================
-- SomaCore — Phase 2, Session 5: Extend vocabularies for backfill
-- =============================================================================
--
-- Adds the 'backfill' value to two CHECK-constraint vocabularies so the
-- Session 5 backfill service can write rows tagged as backfill-origin:
--
--   - whoop_recoveries.ingested_via:  +'backfill'
--   - whoop_sleeps.ingested_via:      +'backfill'
--   - whoop_workouts.ingested_via:    +'backfill'
--   - oauth_audit.action:             +'backfill'   (one row per backfill run)
--
-- This file is the target representation of the EF migration
-- `ExtendIngestedViaAndAuditActionForBackfill`. The SQL spec is the
-- human-reviewable artifact; the migration is what executes. When they
-- drift, this file is the spec we reconcile against.
--
-- Why this is a tiny additive migration:
--   - CHECK constraint vocabulary changes are cheap (per ADR-style note in
--     0001_initial_schema.sql: "Changing CHECK constraints is cheap;
--     evolving ENUM types is annoying").
--   - No data backfill needed — no existing rows carry the new values yet.
--   - C# constants in `SomaCore.Domain.WhoopRecoveries.IngestedVia` and
--     `SomaCore.Domain.OAuthAudit.OAuthAuditAction` already expose `Backfill`;
--     this migration unlocks them at the DB layer.
-- =============================================================================


ALTER TABLE whoop_recoveries
    DROP CONSTRAINT chk_whoop_recoveries_ingested_via;
ALTER TABLE whoop_recoveries
    ADD CONSTRAINT chk_whoop_recoveries_ingested_via
        CHECK (ingested_via IN ('webhook', 'poller', 'on_open_pull', 'backfill'));

ALTER TABLE whoop_sleeps
    DROP CONSTRAINT chk_whoop_sleeps_ingested_via;
ALTER TABLE whoop_sleeps
    ADD CONSTRAINT chk_whoop_sleeps_ingested_via
        CHECK (ingested_via IN ('webhook', 'poller', 'on_open_pull', 'backfill'));

ALTER TABLE whoop_workouts
    DROP CONSTRAINT chk_whoop_workouts_ingested_via;
ALTER TABLE whoop_workouts
    ADD CONSTRAINT chk_whoop_workouts_ingested_via
        CHECK (ingested_via IN ('webhook', 'poller', 'on_open_pull', 'backfill'));

ALTER TABLE oauth_audit
    DROP CONSTRAINT chk_oauth_audit_action;
ALTER TABLE oauth_audit
    ADD CONSTRAINT chk_oauth_audit_action
        CHECK (action IN (
            'authorize',
            'callback_success',
            'callback_failed',
            'token_refresh_success',
            'token_refresh_failed',
            'revoke_detected',
            'manual_disconnect',
            'backfill'));


-- =============================================================================
-- End of session 5 vocabulary migration
--
-- Changed constraints: 4 (one per affected table)
-- No new columns, no new indexes, no data backfill.
-- =============================================================================
