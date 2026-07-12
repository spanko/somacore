# Integrations queue — loop state

Plan + rules: [`integrations-loop.md`](integrations-loop.md). Source briefs: [`session-strava-integration.md`](../session-strava-integration.md), [`session-myfitnesspal-integration.md`](../session-myfitnesspal-integration.md). Patterns to mirror live in `SomaCore.Infrastructure.Whoop` (OAuth/token-cache/webhook/poller) and `SomaCore.Infrastructure.Labs` (canned-handler test shape).

Statuses: `todo` / `in_progress` / `done <commit>` / `blocked (see INBOX)`. One `in_progress` at a time. Items are dependency-ordered — do not reorder.

---

## S1 — Strava domain + options + migration — `done 2399506`

Per brief §1.7 (`strava_activities` DDL) minus fields we can't fill yet is NOT acceptable — create the full schema as specified (hr_zones/splits/laps jsonb, raw payloads, deleted_at soft delete, detail_fetched_at).

**Scope:** `SomaCore.Domain/StravaActivities/StravaActivity.cs`; EF configuration (check constraints: elapsed>0, hr ranges; unique idx on strava_activity_id; idx (user_id, started_at desc)); DbSet; migration `AddStravaActivities`; `StravaOptions` (SectionName "Strava": Enabled=false default, ClientId/ClientSecret empty, RedirectUri, WebhookVerifyToken, DetailFetchMinSeconds=1200); `ConnectionSource.Strava` constant added next to the existing Whoop constant.

**DoD:**
- [ ] Migration creates exactly one table matching brief §1.7 columns incl. `deleted_at`
- [ ] Cascade: user delete CASCADE; external_connection delete SET NULL (mirror `WhoopWorkoutConfiguration`)
- [ ] `StravaOptions.Enabled` defaults false; nothing reads it as true anywhere
- [ ] Build + full suite green (no behavior change yet)

## S2 — Strava OAuth + token cache — `done b4c3a97`

Mirror `WhoopOAuthService`/`WhoopOAuthClient`/`WhoopAccessTokenCache` including the refresh-rotation race-rescue (Strava rotates refresh tokens identically — brief §Approach). Token custody: Key Vault only (`strava-refresh-<userId>` naming per Whoop pattern), DB stores secret names + metadata in `external_connections` (source=strava, strava_athlete_id in connection_metadata).

**Scope:** `SomaCore.Infrastructure/Strava/`: `StravaOAuthClient` (authorize URL builder scope `activity:read_all`, code exchange at `https://www.strava.com/oauth/token`, refresh, deauthorize via `POST /oauth/revoke` best-effort), `StravaAccessTokenCache`, endpoints `/auth/strava` + `/auth/strava/callback` (mirror `WhoopAuthEndpoints` incl. state protection + oauth_audit rows), DI registration gated like Whoop's.

**DoD:**
- [ ] No secret VALUE in code; `grep -rn "sk-\|client_secret\s*=" src --include=*.cs` shows only config reads/wire field names
- [ ] Token cache race-rescue covered by a unit test that simulates the concurrent-refresh rotation (mirror the Whoop cache's test if present; write one regardless)
- [ ] OAuth audit rows written on authorize/callback/refresh/disconnect paths (assert in integration test with canned OAuth client)
- [ ] Tokens go to IKeyVaultSecretsClient, never the DB (grep: no token column/property persisted)
- [ ] Build + full suite green

## S3 — Webhook receiver + drainer routing — `done 1b57479`

Per brief §1.3. GET verify-challenge (echo `hub.challenge` when `hub.verify_token` matches `StravaOptions.WebhookVerifyToken`); POST enqueues to the existing `webhook_events` table and returns 200 within the handler (do work in the drainer, never inline — Strava's 2-second ack rule). Drainer routes: activity create/update → fetch+upsert (S4's service — stub the interface now if S4 not yet built, but prefer building S3+S4's seam together); activity delete → soft delete (`deleted_at`); athlete deauth → mark connection revoked + purge tokens + audit row. Idempotency: dedupe on (subscription_id, object_id, aspect_type, event_time).

**DoD:**
- [ ] Verify-challenge round-trip covered by integration test (correct token echoes; wrong token 403s)
- [ ] POST with duplicate event enqueued once (idempotency test)
- [ ] Deauth event revokes connection + does NOT delete strava_activities rows (test)
- [ ] Receiver does zero Strava API calls inline (code inspection: handler only writes webhook_events)
- [ ] Trace contract: `ingestion.source=strava.webhook` per ADR 0011 (assert Activity source/tags in test, mirroring existing Whoop trace tests if any)
- [ ] Build + full suite green

## S4 — Activity ingest + detail-fetch policy — `done 01463b5`

Per brief §1.5. `IStravaApiClient` (typed HttpClient: get activity by id, list athlete activities after epoch) + `StravaActivityIngestService`: upsert by `strava_activity_id`; detail fetch (hr_zones/splits/laps) synchronously when `elapsed_seconds > DetailFetchMinSeconds`, storing raw summary + raw detail payloads; detail-fetch failure logs warn + leaves row for poller retry, never fails the ingest.

**DoD:**
- [ ] Upsert idempotent: same activity ingested twice → one row, updated fields (test)
- [ ] Detail fetched only for > 20-min activities (both branches tested with canned client)
- [ ] Detail-fetch failure leaves a usable summary row with `detail_fetched_at` null (test)
- [ ] Build + full suite green

## S5 — Reconciliation poller — `todo`

Per brief §1.4: extend `SomaCore.IngestionJobs` with `StravaReconciliationPoller` (same `IJob` shape as `ReconciliationPoller`): per active strava connection, list activities after max(started_at) and ingest missing via S4's service. `job_runs` row per run. Trace `ingestion.source=strava.poller`.

**DoD:**
- [ ] Poller fills a deliberately-missed activity (test: seed one activity, canned client returns two, poller ingests the gap)
- [ ] Poller skips revoked connections (test)
- [ ] `job_runs` written (test) — note: the Whoop poller has a known gap here (track-a-checklist); do NOT copy that gap
- [ ] Build + full suite green

## S6 — Workout dedup + snapshot merge — `todo`

Per brief §1.8/§1.9. `WorkoutTypeMap` (WHOOP name ↔ Strava type ↔ HK type families); merged-workout builder in `AgentInputSnapshotBuilder`: group by (start ±5 min, type family) across whoop_workouts + strava_activities (deleted_at null) + healthkit_workouts; Strava wins distance/elevation/splits-summary/zones-summary/cadence/watts, WHOOP wins strain, max duration, `sources[]` provenance. Snapshot carries `hr_zones_summary` (pct per zone) + `splits_summary` (count, fastest/slowest pace) — NOT raw arrays.

**DoD:**
- [ ] Same run captured by WHOOP + Strava → ONE merged workout with Strava detail + WHOOP strain + `sources:["strava","whoop"]` (test)
- [ ] Privacy strip: snapshot JSON contains NO `start_latlng`, `polyline`, `map`, `kudos`, `gear_id`, `description` for a Strava activity seeded with all of them (test greps snapshot string — this is the Section D commitment, hard FAIL if present)
- [ ] Single-source activities pass through unmerged (test)
- [ ] Build + full suite green

## S7 — /me connect surface + flag wiring — `todo`

Connect/disconnect Strava on `/me` (mirror the WHOOP connect card, renders only when `Strava:Enabled`); `stravaEnabled` bicep param → `Strava__Enabled` env (mirror `labsEnabled` wiring) — **false in `parameters.dev.json`**; the two KV secret bindings added to bicep but `wireKeyVaultSecrets` handling must tolerate the secrets not existing yet (Adam hasn't created the account) — follow how bicep handles this today; if it can't tolerate absence, put the secret bindings behind their own toggle and note it in the queue Learnings.

**DoD:**
- [ ] `/me` shows no Strava UI when flag off (default) — test or rendered-conditional inspection
- [ ] `parameters.dev.json` has `stravaEnabled: false` (the loop never enables)
- [ ] `az bicep build --file infra/main.bicep` succeeds (syntax check only — NO deployment)
- [ ] Build + full suite green

## M1 — MFP CSV upload path — `todo`

Per `session-myfitnesspal-integration.md` §1.3 (CSV portion only — iOS is out of loop scope): `/me/food` accepts the MFP data-export ZIP; unpack (size cap 50 MB, zip-slip safe — validate entry paths); locate the meal-nutrition CSV; parse per-meal-slot rows into `mfp_food_entries` with `source='csv_upload'`, `ingested_via='csv_upload'`, idempotent on the existing (user, date, slot, source) index — re-upload of overlapping history must not duplicate or double-merge (upsert REPLACES a csv_upload row's values, unlike quick-log's manual merge — document why inline). New flag `Mfp:CsvUploadEnabled` default false, bicep-wired false in dev. Fixture: build a synthetic MFP-export-shaped ZIP in test assets from the MFP help-center column description; Adam's REAL export is the post-loop acceptance gate (INBOX note pre-written).

**DoD:**
- [ ] Synthetic ZIP fixture parses into expected rows (golden-output test)
- [ ] Re-upload same ZIP → identical row count + values (idempotency test)
- [ ] Zip-slip entry (`../evil.csv`) rejected (test)
- [ ] Food names land in `food_items` server-side but are absent from the agent snapshot (existing snapshot behavior — extend test to a csv_upload-sourced row)
- [ ] Flag off by default; `/me/food` upload UI renders only when enabled
- [ ] Build + full suite green

---

## Learnings

*(loop appends one line per surprise — next session reads this first)*

- 2026-07-10 (planning): `agent_actions` table from the Function Health brief doesn't exist — actions are jsonb on invocations; lab provenance became a validated JSON field. Expect similar brief-vs-code drift; trust the code.
- 2026-07-12 (S1): `ConnectionSource.Strava` and the `'strava'` value in `chk_external_connections_source` already existed — drift in the queue's favor this time.
- 2026-07-12 (S1): `dotnet dotnet-ef migrations add` emits CRLF files on Windows; CI's format-check fails on them. Run `dotnet format` after every migration add before committing.
- 2026-07-12 (S2): the queue's "deauthorize via POST /oauth/revoke" endpoint doesn't exist at Strava — the real one is `/oauth/deauthorize`. Made it configurable (`StravaOptions.DeauthorizeUri`) defaulting to the real endpoint.
- 2026-07-12 (S2): Strava reports granted scopes as a comma-separated `scope` query param on the callback redirect, NOT in the token body (unlike WHOOP); the athlete summary is inlined in the code-exchange response, so no separate profile fetch.
- 2026-07-12 (S2): the WHOOP token cache never writes token_refresh_success/failed audit rows (constants existed unused). Strava's cache does — S2 DoD required it; consider backporting to WHOOP.
- 2026-07-12 (S3): the shared webhook_events queue had a single consumer claiming ALL received rows — the WHOOP drainer would have claimed and discarded Strava events. Its claim SQL gained `AND source = 'whoop'` (one line, behavior-preserving, regression-tested). Inbox entry filed for Adam's review.
- 2026-07-12 (S3): Strava has no per-event id and does not sign webhook bodies; dedupe key (subscription_id, object_id, aspect_type, event_time) is composed into source_event_id + source_trace_id to reuse the existing unique index. Subscription-id check added via `StravaOptions.WebhookSubscriptionId` (0 = pre-registration, check skipped).
- 2026-07-12 (S3): the queue's "ingestion.source=strava.webhook" shorthand maps to ADR 0011's tag pair (ingestion.source=strava + ingestion.trigger=webhook), matching how the WHOOP drainer emits the same contract.
- 2026-07-12 (S4): the brief's "detail in one API call" is wrong for HR zones — GET /activities/{id} carries splits_metric + laps, but zones need GET /activities/{id}/zones. The detail unit = the zones call; zones 404 (no HR data) counts as a completed detail pass, not a retryable failure.
- 2026-07-12 (S1): evaluator caught a preexisting flaky test — `WhoopStateProtectorTests.Should_reject_a_tampered_state_token` intermittently fails (tamper-midpoint sometimes doesn't corrupt the AEAD tag). Unrelated to loop items; inbox entry filed.
