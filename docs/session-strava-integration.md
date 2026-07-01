# Session brief — Strava integration

**Status.** Session prompt. Promoted from [`docs/seeds/strava-integration-research.md`](seeds/strava-integration-research.md) after that research pass produced a HYBRID verdict against the baseline framework. This document is the ship-worthy plan; the research doc stays as the "why" reference.

**Track / phase.** Phase 2. Suggest **"Track D — external data sources, Session 3"** (Session 1 = Function Health, Session 2 = MFP). Naming is Adam's call.

**Framework decision made 2026-07-01.** For Strava specifically, the comparison chart resolved HYBRID (not baseline-alone). Direct Strava API + webhook integration is the primary source for granularity; iOS-companion HealthKit reads are the secondary source for Apple Watch native workouts and fallback availability. See research doc §4 for the full chart.

**Prerequisites.**
- MFP session (Track D Session 2) has shipped — iOS companion exists with HealthKit permission flow and `POST /api/ingest/healthkit` endpoint. This session extends that companion to workout types.
- WHOOP ingestion is live and stable — this session reuses `WhoopAccessTokenCache`, ADR 0006 three-layer pattern, ADR 0011 trace contract, `webhook_events` drainer.
- Adam has created a Strava developer account with paid Standard-tier subscription ($11.99/mo) — see Not-a-code-problem gates.

---

## Goal

Land finer-grained training signal on the daily card by ingesting Strava activity data (splits, HR zones, elevation, cadence, power) that WHOOP alone can't provide, deduped intelligently with WHOOP workouts and Apple Watch native captures.

Unlocks two coach categories with real signal:
- **`TrainingIntensity`** — currently reasons about WHOOP average HR. With Strava splits + HR-zone accumulation, the coach can reason about "22 minutes of zone-3 across three sessions this week" vs. WHOOP's flattened averages.
- **`WorkoutStructure`** — currently blind to intra-workout structure. Strava splits + laps let the coach reason about tempo vs. steady-state distribution, threshold accumulation, taper timing.

**Coach unlock example (from research §6.1):**

> Yesterday's tempo run held 158 bpm avg over 42 min — but your splits show you crossed threshold in miles 3-5 (avg 165 bpm) and cooled down for the last 2. That's ~22 min of zone-3 accumulated across three sessions this week. Recovery today is 71. Today's plan is a 45-min zone-2 endurance: target 138-145 bpm the whole time. Skip the tempo. Cadence trended low (168 spm) in yesterday's zone-3 block — worth watching.

That's the signal WHOOP alone cannot produce — it flattens splits into an activity average.

---

## Approach — why hybrid and not baseline-alone

MFP session ratified baseline-alone because MFP writes meal-slot-preserving summaries to HealthKit — baseline gave us functionally equivalent data.

**Strava does NOT.** Verified from Strava's own docs (research §3):

- Strava writes SOME fields to HealthKit (activity type, distance, time, calories, route) but NOT the finer detail the coach needs (HR zones, splits, laps, power, cadence).
- Elevation gain/loss "not always passed" to HealthKit per Strava's own support articles.
- Third-party writes to HealthKit do not round-trip to Strava.
- Only past 30 days of Apple Watch–recorded activities back-sync from HealthKit.
- DC Rainmaker documented a 2022 incident where Strava "abruptly ended" third-party HealthKit sync — access-risk precedent on the HealthKit round-trip specifically.

So baseline gives us availability but not fidelity. Direct API gives us both, at the cost of:
- **$11.99/month Standard-tier developer subscription** (June 2026 change).
- **Refresh-token rotation race** — same problem class as WHOOP, same race-rescue solution applies.
- **Policy risk MEDIUM** — Strava's Nov 2024 API agreement restricts *display* of user data to that user only (fine — we're a personal coach app), but doesn't explicitly address LLM use. Direction of travel is restrictive; not blocking today.
- **Intermediary aggregators are banned** by Strava (Terra publicly confirmed being kicked out). This makes Strava the seed where "no aggregator" is enforced by the vendor, not a design choice we're making.

The 2x-operational-complexity budget check: direct integration reuses ~70% of WHOOP infrastructure (OAuth flow, webhook drainer, token cache race-rescue, three-layer ingestion, trace contract). Marginal complexity vs. baseline-alone is small; fidelity gain vs. baseline-alone is large. Ship both.

---

## Phase 1 — what ships this session

### 1.1 Strava developer account setup (not-a-code-problem, gates the session)

- Adam registers a Strava developer application at `developers.strava.com`.
- Adam subscribes to Strava Standard tier ($11.99/mo) — required for Standard-tier developers as of June 30, 2026 for existing devs, June 1, 2026 for new.
- Adam adds two secrets to Key Vault: `strava-client-id`, `strava-client-secret`.
- Adam configures the redirect URI in Strava's app settings to point at our Container Apps ingress `/oauth/strava/callback`.
- Adam registers a webhook subscription for the app (one-time API call). Strava sends a challenge to the callback URL; must be handled by the deployed webhook endpoint.

The webhook registration is the classic chicken-and-egg (need endpoint deployed before Strava will accept the subscription). Order of operations:
1. Deploy webhook endpoint stub that responds to the verify challenge.
2. Register subscription via `POST https://www.strava.com/api/v3/push_subscriptions`.
3. Fill in the actual ingest logic (§1.4).

### 1.2 OAuth flow + token cache

**Deliverable:** `StravaOAuthService` in `SomaCore.Infrastructure.Strava`, matching `WhoopOAuthService` shape.

- Redirect user to Strava authorize URL with `activity:read_all` scope (read private too; users can grant lower scope if they prefer).
- Exchange code for access + refresh tokens at `/oauth/token`.
- Persist tokens in `external_connections` row with `source='strava'`, `strava_athlete_id` in an indexed metadata column.
- `StravaAccessTokenCache` implementation — reuse the `WhoopAccessTokenCache` race-rescue pattern verbatim. Strava's refresh tokens rotate identically to WHOOP's, so the concurrency lesson (2026-06-11 incident) applies.
- Access token lifetime is 6 hours; refresh proactively when < 1 hour remaining.
- On deauth webhook (§1.4), null tokens and mark connection as `revoked_at=now()`.

### 1.3 Webhook receiver + drainer

**Deliverable:** `POST /api/webhooks/strava` endpoint + entry in `webhook_events` table + drainer worker.

- Verify challenge: on GET with `hub.mode=subscribe`, respond with the `hub.challenge` value. No auth on this path (it's the subscription verify).
- Event handler: on POST, verify subscription_id matches ours, insert one `webhook_events` row per event, return 200 within 2 seconds (Strava's ack requirement). Do all ingest work off the request thread.
- Event payload shape: `{ object_type, object_id, aspect_type, updates, owner_id, subscription_id, event_time }`.
- Drainer worker (same pattern as WHOOP's) picks up rows and routes:
  - `object_type='activity'` + `aspect_type='create'` → fetch summary via `GET /activities/{id}` and detail if activity > 20 min (§1.5) → upsert `strava_activities` row.
  - `object_type='activity'` + `aspect_type='update'` → refetch → upsert.
  - `object_type='activity'` + `aspect_type='delete'` → mark row deleted (soft delete via new `deleted_at` column) OR hard delete depending on privacy commitment; we soft-delete for now, retain trace_id.
  - `object_type='athlete'` + `updates.authorized=false` → mark connection revoked, purge tokens, stop poller for that user.
- Idempotency: dedupe on `(subscription_id, object_id, aspect_type, event_time)`.
- Trace contract: per ADR 0011, emit `ingestion.source=strava.webhook`, per-event.

### 1.4 Reconciliation poller

**Deliverable:** `SomaCore.IngestionJobs.Jobs.StravaReconciliationPoller` in a Container Apps Job.

- Cadence: every 15 minutes. At three users × ~15 activities/week each × occasional detail fetches, we're at ~50 req/day total — well under the 100 non-upload req / 15 min cap.
- Per user: query `GET /athlete/activities?after=<last_seen_epoch>` where `last_seen_epoch` = max(started_at) from `strava_activities` for that user.
- For any activity not already in `strava_activities`, upsert as a new row (source: `reconciliation_poller`).
- Emit trace `ingestion.source=strava.poller`.

### 1.5 Detail-fetch policy

- Webhook `create` and reconciliation-poller list responses give the summary shape (missing HR zones, splits, laps).
- Detail endpoint (`GET /activities/{id}`) gives the finer data — one API call per activity.
- **Policy:** fetch detail synchronously when the activity's `elapsed_seconds > 1200` (20 min). Skip for shorter — for a 10-min run the extra granularity doesn't inform coach reasoning.
- Store detail response in `strava_activities.raw_detail_payload` and extract `hr_zones`, `splits`, `laps` into typed columns. Set `detail_fetched_at`.
- If detail fetch fails (429, network, etc.): log at warn, retry on next reconciliation-poller pass. Don't block webhook ack.

### 1.6 iOS companion extension (`HKWorkoutType` reads)

**Extends the MFP session's companion.** No new app; adds workout-type permission + observer to the existing Swift codebase.

- Add `HKWorkoutType` to the requested read permission set. iOS prompts user for consent on next app open post-update.
- Register `HKObserverQuery` for `HKWorkoutType.workoutType()`.
- On callback, use `HKAnchoredObjectQuery` to fetch new `HKWorkout` samples since last anchor.
- Filter by `sourceBundleIdentifier`:
  - `com.apple.workout` → Apple Watch native workouts. Post to `/api/ingest/healthkit` with source_bundle_id set.
  - `com.strava.stravaride` (verify exact bundle ID during on-device spike) → Strava's HealthKit writes. Post similarly; server-side classification distinguishes.
- Backend classifies HealthKit-workout posts into `healthkit_workouts` (§1.8), NOT into `strava_activities` (which is direct-API-only). This keeps source provenance clean.

### 1.7 Data model — new tables

From research §5.2 and §5.5:

```sql
CREATE TABLE strava_activities (
    id                     uuid PRIMARY KEY,
    user_id                uuid NOT NULL REFERENCES users(id) ON DELETE CASCADE,
    external_connection_id uuid REFERENCES external_connections(id) ON DELETE SET NULL,
    strava_activity_id     bigint NOT NULL,
    strava_athlete_id      bigint NOT NULL,
    activity_type          text NOT NULL,
    started_at             timestamptz NOT NULL,
    elapsed_seconds        int NOT NULL,
    moving_seconds         int,
    distance_meters        numeric,
    total_elevation_gain_m numeric,
    average_speed_mps      numeric,
    max_speed_mps          numeric,
    average_hr             int,
    max_hr                 int,
    average_cadence        numeric,
    average_watts          numeric,
    max_watts              int,
    weighted_avg_watts     int,
    device_watts           boolean,
    kudos_count            int,
    calories               numeric,
    hr_zones               jsonb,
    splits                 jsonb,
    laps                   jsonb,
    raw_summary_payload    jsonb,
    raw_detail_payload     jsonb,
    detail_fetched_at      timestamptz,
    deleted_at             timestamptz,
    ingested_via           text NOT NULL,
    ingested_at            timestamptz NOT NULL DEFAULT now(),
    trace_id               text
);
CREATE UNIQUE INDEX idx_strava_activities_activity_id
    ON strava_activities(strava_activity_id);
CREATE INDEX idx_strava_activities_user_started
    ON strava_activities(user_id, started_at DESC);
```

```sql
CREATE TABLE healthkit_workouts (
    id                     uuid PRIMARY KEY,
    user_id                uuid NOT NULL REFERENCES users(id) ON DELETE CASCADE,
    source_bundle_id       text NOT NULL,
    hk_sample_uuid         uuid NOT NULL,
    workout_type           text NOT NULL,
    started_at             timestamptz NOT NULL,
    elapsed_seconds        int NOT NULL,
    total_energy_kcal      numeric,
    total_distance_m       numeric,
    average_hr             int,
    hk_metadata            jsonb,
    ingested_at            timestamptz NOT NULL DEFAULT now(),
    trace_id               text
);
CREATE UNIQUE INDEX idx_healthkit_workouts_sample_uuid
    ON healthkit_workouts(hk_sample_uuid);
CREATE INDEX idx_healthkit_workouts_user_started
    ON healthkit_workouts(user_id, started_at DESC);
```

**Cascade contract:** user delete cascades everything. Connection delete SET NULL on `strava_activities.external_connection_id`. `healthkit_workouts` doesn't reference `external_connections` (HealthKit isn't an external connection in that sense).

### 1.8 Dedup rule for WHOOP + Strava + HealthKit-workout overlap

Implemented in the coach input-window builder (`LiveDailyAgentService.BuildWorkoutSnapshot`), NOT in the ingest layer — ingest stays source-of-truth per source; the merged view is what the coach reads.

**Rule (from research §5.3):**

1. Query workouts across `whoop_workouts`, `strava_activities` (where `deleted_at IS NULL`), and `healthkit_workouts` for the user in the coach's input window (last 7 days).
2. Group by `(activity_start_time ± 5 minutes, activity_type_family)`. Activity-type family maps live in a new `WorkoutTypeMap.cs`: WHOOP `Running` ≈ Strava `Run` ≈ HealthKit `HKWorkoutActivityTypeRunning`; WHOOP `Cycling` ≈ Strava `Ride` ≈ HealthKit `HKWorkoutActivityTypeCycling`; etc. Fall through to "Other" for unmapped types.
3. For each group:
   - If Strava row present: use for `distance`, `elevation_gain_m`, `average_speed_mps`, `splits`, `hr_zones`, `average_cadence`, `average_watts`.
   - If WHOOP row present: use for `strain_score` (the metric Strava doesn't compute).
   - `average_hr`: prefer Strava when `has_heartrate=true`, else WHOOP, else HealthKit.
   - `elapsed_seconds`: max of all sources present (WHOOP truncates, Strava sometimes over-runs on auto-pause).
   - `activity_type`: prefer Strava, fall through to WHOOP, then HealthKit.
4. If only one source has the workout, use it as-is.
5. Emit merged workout with `sources: []` array preserving provenance.

### 1.9 Coach input-window extension

- `LiveDailyAgentService` appends a `latest_workouts` section to the input snapshot JSON: last 7 days of merged workouts per §1.8.
- Per workout: `activity_type, started_at, elapsed_seconds, distance_meters, elevation_gain_m, average_hr, max_hr, hr_zones_summary, splits_summary, average_cadence, average_watts, strain_score, sources`.
- **`hr_zones_summary`** is a percent-time-per-zone rollup (5 zones), NOT raw zone samples. Coach doesn't need per-second HR.
- **`splits_summary`** is `{ split_count, fastest_split_pace_seconds_per_km, slowest_split_pace_seconds_per_km, splits_over_threshold_hr: n }`, NOT the full split array. Coach can reason about "3 fast splits followed by 2 recovery splits" without seeing raw HR per split.
- Cap: 20 workouts total in the snapshot. Prioritize by recency + `strain_score` descending.
- **Strip GPS, polyline, kudos, gear_id, description, photos before adding to snapshot.** These stay server-side per privacy commitment §1.10.

### 1.10 Privacy doc updates

From research §7.6.

**Section D.1 addition** — add to "What gets sent":

> **Strava activity data (Track D Session 3).** When a user has connected Strava, the daily-card agent's input snapshot includes: activity type, duration, distance (rounded to nearest 100m), total elevation gain, average and max HR, HR-zone percent-time summary, split-count and fastest/slowest split pace, average cadence, average watts, strain score (from WHOOP). NO GPS coordinates, NO polyline route data, NO segment names, NO gear identifiers, NO kudos counts, NO activity descriptions, NO photo URLs.

**Section D.2 reinforcement** — add to "What does NOT get sent":

> **Location or route data of any kind from Strava.** Exact GPS coordinates, complete route polylines, start/end lat-lng pairs, segment names, and any other information that could identify where the user has been. Route data can reveal home address, workplace, running-partner network — treated as maximally sensitive. Stays server-side always; the coach reasons about training effort without needing to know where the user ran.

**Section G** (processor disclosure) — NO change. Strava is a data source, not a processor; we hold tokens directly. Intermediary aggregators are banned by Strava, so there's no third-party middle-tier to disclose.

### 1.11 Exit criteria for Phase 1

- [ ] `dotnet build`, `dotnet test` green
- [ ] Migrations apply cleanly to dev DB (`strava_activities`, `healthkit_workouts`)
- [ ] Adam has created Strava developer account, subscribed at $11.99/mo, added client-id + client-secret to Key Vault
- [ ] Webhook subscription registered with Strava; verify-challenge round-trip confirmed
- [ ] Adam authorizes his personal Strava; ingest flow round-trips one real activity end-to-end into `strava_activities`
- [ ] Reconciliation poller runs successfully and picks up any activity webhooks missed
- [ ] iOS companion updated to read `HKWorkoutType`; Apple Watch native workout observed to flow into `healthkit_workouts`
- [ ] Dedup rule test: Adam records an activity captured by BOTH WHOOP and Strava (e.g., a run wearing both) — merged view shows one workout with Strava's splits/HR zones + WHOOP's strain
- [ ] Coach card generated for a user with Strava data references split-level or zone-level reasoning (concrete example per §Goal / research §6.1)
- [ ] Privacy doc Section D.1/D.2 updates in the repo AND Tai has signed off in writing (specifically on the "no GPS / no polyline" commitment)
- [ ] `/admin/agent` surfaces the `latest_workouts` merged view for any invocation that referenced training data — makes it possible to debug source provenance
- [ ] Trace contract compliance: `ingestion.source=strava.webhook`, `strava.poller`, `strava.on_open` emitted per ADR 0011

## Phase 1 is **NOT** in scope of the following

- Strava's own MCP Connector as a data path. That's a consumer-facing product (Claude Desktop users querying their own data); it's not something we build against.
- Aggregator middleware (Terra, Junction, Rook). Strava's Nov 2024 API agreement bans intermediaries; Terra publicly confirmed being kicked out. Not on the table.
- Route visualization or GPX rendering. The coach reads training signal from Strava; user experiences Strava at Strava.
- Kudos-driven training-bump reasoning. Interesting but not scoped — kudos_count is stripped before the coach sees it, per privacy commitment.
- Gear-specific pattern analysis (long-run shoes vs. everyday). Requires gear_id which we're also stripping.
- Segment PR reasoning ("you PR'd on segment X"). Segments carry rich social/location data; out of scope for the coach for privacy reasons.
- Strava Live Segments API. Not a data source we consume.
- Extended Access tier application. Fits Standard tier easily at three users; revisit when scaling past 10.
- Any change to WHOOP ingestion. This is additive; dedup happens in the coach input-window builder, not upstream.
- Any change to bounds validator or agent voice/persona docs. `TrainingIntensity` and `WorkoutStructure` categories already exist.

---

## Phase 2 — no follow-up session planned

Unlike Function Health (where Phase 2 is the MCP-driven trigger layer) and MFP (where Phase 2 is a contingent aggregator layer), Strava's hybrid shape is complete after Phase 1. Both the direct API path and the HealthKit-companion path ship together; there's no obvious deferred layer.

**Watchlist for future re-evaluation:**
- **Strava terms change to explicitly prohibit LLM use** → retreat to baseline-only, degrading fidelity. Highest-probability risk.
- **Strava Extended Access qualification opens up** → could waive the $11.99/mo subscription. Free upgrade.
- **Coach behavior grows to need sub-daily latency** on training data (e.g., a live "you're crossing threshold — back off" nudge on Apple Watch) — that would spawn a new session on the iOS companion side, not on the ingestion side.

---

## Reference material

- **[`docs/seeds/strava-integration-research.md`](seeds/strava-integration-research.md).** The research pass this session brief was promoted from. Contains the full comparison chart, coach unlock example, all source citations.
- **[`docs/seeds/strava-integration.md`](seeds/strava-integration.md).** The original seed from Tai's 2026-06-28 feedback. Kept for provenance.
- **[`docs/agent-voice-and-persona.md`](agent-voice-and-persona.md) + [`docs/agent-bounds.md`](agent-bounds.md).** Voice / bounds. `TrainingIntensity` and `WorkoutStructure` categories are what this session unblocks with real signal.
- **[`docs/decisions/0006-three-layer-whoop-ingestion.md`](decisions/0006-three-layer-whoop-ingestion.md).** The three-layer pattern this session's direct integration mirrors verbatim.
- **[`docs/decisions/0011-ingestion-trace-contract.md`](decisions/0011-ingestion-trace-contract.md).** Trace contract compliance.
- **[`docs/decisions/0012-llm-card-before-rules-engine.md`](decisions/0012-llm-card-before-rules-engine.md).** The overall LLM-first architecture.
- **[`docs/privacy-data-handling.md`](privacy-data-handling.md).** The doc §1.10 revises. Tai signs off on the "no GPS / no polyline" commitment before ship.
- **[`docs/session-myfitnesspal-integration.md`](session-myfitnesspal-integration.md).** The prior Track D session brief. This session's iOS-companion work extends the MFP session's companion; permission scopes stack.
- **[`docs/session-function-health-integration.md`](session-function-health-integration.md).** Earlier Track D session. Structural pattern mirror.
- **WHOOP integration codebase.** `SomaCore.Infrastructure.Whoop` — `WhoopOAuthService`, `WhoopAccessTokenCache`, `WhoopWebhookReceiver`, `WhoopReconciliationPoller`. All of these have direct Strava counterparts in this session.
