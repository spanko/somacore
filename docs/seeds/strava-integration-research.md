# Strava integration — research pass

**Status.** PROMOTED to [`docs/session-strava-integration.md`](../session-strava-integration.md) on 2026-07-01. This research doc stays as the "why" reference. Research pass conducted 2026-07-01, answers every open question in [`strava-integration.md`](strava-integration.md) against the baseline framework Adam ratified 2026-07-01.

**Author.** Claude, with primary-source verification.

**Date.** 2026-07-01.

**Rebuttable.** Every factual claim carries a URL in the Verification appendix.

**Verified.** 2026-07-01. All load-bearing claims re-verified against primary sources: Strava OAuth flow + refresh-token rotation, rate limits, webhooks, June 2026 developer program changes (including $11.99/mo subscription), November 2024 API agreement update, Strava-to-HealthKit write behavior + 2022 incident.

---

## 0. Framework the seed answers to

Same as MFP: baseline (iOS companion + CSV upload) is the floor for every non-Function-Health seed; direct alternative only ships if it clears the ~2x-operational-complexity budget on net benefit. Each seed produces one comparison-chart row.

**Strava is the first seed where the direct alternative wins on the load-bearing dimension** (granularity). This one is a hybrid: baseline for coverage + antifragility, direct for the data that actually unlocks coach reasoning WHOOP can't do.

---

## 1. Executive summary (90-second read)

**Recommended: hybrid.** Ship the iOS companion reading HealthKit AND ship a direct Strava OAuth + webhook integration. Dedupe at the ingest layer. Baseline covers availability; direct covers fidelity.

Why hybrid and not baseline-only:

- **HealthKit's Strava round-trip is demonstrably incomplete.** Strava writes some fields to HealthKit (activity type, distance, time, calories, route summary) but not others (elevation gain/loss "not always passed"). Only Apple Watch–recorded activities from the past 30 days back-sync from HealthKit to Strava. Third-party writes to HealthKit do not round-trip to Strava. And Strava had a documented 2022 incident where they "abruptly ended" third-party data sync via Apple Health (DC Rainmaker). The path exists but is neither complete nor stable.
- **The coach unlock story for Strava is fidelity-dependent.** WHOOP already gives us duration and average HR. What Strava adds is HR zones over duration, splits, terrain / elevation, power (cycling), cadence — the shape data that lets the coach reason about zone-3 accumulation, taper timing, and threshold-vs-endurance mix. That data lives in the Strava API's `getActivityById` detailed endpoint, not in HealthKit.
- **Aggregator path is closed.** Strava's November 2024 API agreement update prohibits intermediary platforms from routing Strava data downstream. Terra publicly confirmed they were kicked out (Terra's own blog post on Strava discontinuation). This makes Strava the ONE seed where "no aggregator middleman" is literally enforced by Strava rather than being our choice.

**Costs and risks that hybrid buys us into:**

- **$11.99/month Strava subscription** for our developer account (Standard tier, effective June 1, 2026 for new devs / June 30, 2026 for existing). That's $144/year — real but small vs. WHOOP-scale complexity we're already carrying.
- **Refresh-token rotation.** Strava's refresh tokens rotate on every access-token exchange. Same problem WHOOP has. Our existing `WhoopAccessTokenCache` race-rescue pattern applies directly.
- **Policy risk MEDIUM.** Strava is in the middle of an aggressive developer-program overhaul (subscription + endpoint deprecations + agreement changes since Nov 2024 + more coming June 2027). No current terms explicitly prohibit our use case (personal coaching for the user themselves via LLM), but the direction of travel is restrictive.
- **~70% code reuse from WHOOP.** ADR 0006 three-layer pattern applies directly: webhook receiver + reconciliation poller + on-open synchronous pull. ADR 0011 trace contract applies. Existing OAuth token cache with race-rescue applies. `AgentActionCategory.TrainingIntensity` and `.WorkoutStructure` already exist in bounds.

**Lift.** ~2 Track A sessions for the direct integration on top of the iOS companion (which is built once, in the MFP session, and reused). Total marginal cost for Strava specifically: 2 sessions + $144/year.

---

## 2. Strava API — current state (July 2026)

### 2.1 Public API is open but changing

- **REST API + webhooks + OAuth 2.0** — mature, well-documented, in active development.
- **Rate limits** (per app, not per user):
  - Overall: 200 req / 15 min, 2,000 req / day
  - Non-upload subset: 100 req / 15 min, 1,000 req / day
  - Single Player Mode is the default (personal use only cap); Tier 2 requires an approved app with capacity for 10 athletes. Higher tiers via application.
  - **Three-user scale fits the default tier easily.** We'd want Tier 2 status before opening to broader users; not blocking for phase 1.
  - Rate-limit violations return 429; 15-min and daily caps compound.
- **OAuth 2.0 three-legged flow.** Authorize on Strava's web, exchange code for tokens at `/oauth/token`.
- **Refresh tokens rotate.** Every access-token exchange returns a new refresh token; the old one is immediately invalidated. Same problem class as WHOOP; same race-rescue solution applies (`WhoopAccessTokenCache` pattern is directly transferable).
- **Access token lifetime:** 6 hours. Refresh token: long-lived.
- **Deauthorization endpoint change:** as of June 1, 2026, `POST /oauth/revoke` is optional; will be mandatory June 1, 2027.

### 2.2 Webhooks — per-app, at-most-3 retries

- **Subscription is per-application**, not per-user. One callback URL receives events for every athlete who authorized our app. Matches the WHOOP model.
- **Events:** athlete deauthorization, activity create / update / delete, activity title-and-type-and-privacy updates.
- **Retries:** up to 3 total attempts if we don't return 200 within 2 seconds. Same "not guaranteed" delivery posture as WHOOP → three-layer ingestion (webhook + reconciliation poller + on-open pull) is the correct pattern.
- **Payload shape:** `{ object_type: 'activity'|'athlete', object_id, aspect_type: 'create'|'update'|'delete', updates: {...}, owner_id, subscription_id, event_time }`.

### 2.3 Data granularity — what a detailed activity read includes

`GET /activities/{id}` (detailed) returns:

- **Core metrics:** distance, moving_time, elapsed_time, total_elevation_gain, average_speed, max_speed, average_cadence
- **Performance:** average_watts, weighted_average_watts, device_watts, max_watts, has_heartrate, average_heartrate, max_heartrate
- **Location:** start_latlng, end_latlng, map with polyline
- **Structure:** segment_efforts, splits, laps
- **Social / metadata:** kudos_count, comment_count, gear_id, description, trainer / commute flags

**Compare to what HealthKit surfaces from Strava's writes:** activity type, distance, time, calories, sometimes route, elevation not always. Missing: HR zones distribution, split-by-split HR, power, cadence, laps, segments. **This granularity gap is why direct-API wins the fidelity axis.**

### 2.4 June 2026 developer-program changes — the load-bearing recent event

Summarized from Strava's community-hub announcement + third-party coverage:

1. **$11.99/month Standard-tier developer subscription.** Effective June 1, 2026 for new registrations, June 30, 2026 for existing developers. Extended Access developers are exempted but that tier's qualification isn't publicly documented.
2. **Intermediary platforms banned.** "Apps that route data through third-party intermediary platforms are no longer supported because the company cannot verify downstream data access." Terra publicly confirmed they were kicked out — see the Terra blog post cited in the appendix.
3. **AI access routed through Strava's own MCP Connector.** This is a **consumer-facing product** (subscribers query their own data through Claude Desktop), NOT a platform third-party developers can build against. It's not our path.
4. **Endpoint deprecations.** Club Activities, Club Admins, Club Members, Segments Explore endpoints deprecated September 1, 2026. New API base URL + header-based token auth required by June 1, 2027. None of these hit our workout-ingestion use case, but the deprecation cadence is a signal about pace of change.

### 2.5 November 2024 API agreement update — what it actually says

**Restricts DISPLAY of user data to other users.** Verbatim: *"Strava Data provided by a specific user can only be displayed or disclosed in your Developer Application to that user."* This is about their social/leaderboard concerns — competitors showing your friends' data to strangers, not about you feeding your own data into a personal LLM coach.

**Does not explicitly address LLM/AI use** for the case of "user asks their own coach app to analyze their own data." The agreement text does not prohibit it; nor does it explicitly permit it. The direction of travel (November 2024 restrictions + June 2026 subscription + MCP routing for AI) suggests Strava is asserting more control over how their data flows, but current terms don't disallow our shape.

**Policy risk assessment:** MEDIUM. Not blocking today. Worth monitoring the Strava API Policy document (linked from the agreement) for updates.

---

## 3. Strava → HealthKit — why the baseline is partial for this source

MFP writes cleanly to HealthKit with meal-slot metadata preserved — so baseline wins for MFP. Strava does not have the same story.

### 3.1 What Strava writes to HealthKit

Per Strava's own support docs:

- Route information, activity type, distance, time, calories → written to HealthKit as `HKWorkout` records with associated `HKQuantityType` samples.
- Elevation gain / loss **not always passed**.
- Some fields Strava has on their end never make it into HealthKit (splits, HR zones, power detail, laps, segments).

### 3.2 What Strava reads from HealthKit

- Only past 30 days of Apple Watch–recorded activities back-sync from HealthKit to Strava.
- **Third-party writes to HealthKit do NOT sync back to Strava.** So if our iOS companion could write to HealthKit (we don't; we read only), Strava wouldn't pick it up. Not relevant for our design but worth naming for completeness.

### 3.3 The 2022 incident and why "stable HealthKit round-trip" is not the read

DC Rainmaker documented that on 2022-03-15, Strava "abruptly ends 3rd party data sync to Apple Health." Historically Strava has been willing to change what they publish to HealthKit without notice. That's a MEDIUM access-risk signal — HealthKit-as-a-substrate is Apple-stable; Strava's writes into HealthKit are Strava-controlled and have precedent for changing.

### 3.4 What baseline still buys us for Strava

Not zero. Baseline gives us:
- **Fallback availability.** If our Strava OAuth breaks or the subscription lapses, iOS companion still catches whatever Strava has written to HealthKit.
- **Cross-checking.** We can compare what Strava writes to HealthKit vs. what the direct API returns — flags data-loss issues on Strava's side.
- **Apple Watch–native workouts** that Strava doesn't have (user forgot to open Strava, wore watch instead) — captured directly via HealthKit's `HKWorkout` from `com.apple.workout`.

So baseline stays in the pipeline as a secondary source. Direct API is the primary.

---

## 4. Comparison chart — the deliverable

### 4.1 Chart

| Dimension | **Baseline (iOS + CSV) alone** | **Direct alternative (Strava API + webhooks)** | Winner |
|---|---|---|---|
| **Granularity** | Activity type, distance, time, calories, partial elevation. Missing HR zones, splits, power, cadence detail. | Full detail: split-by-split HR, power, cadence, laps, segments, kudos, gear. | **Direct wins decisively.** This is the load-bearing dimension — coach unlock requires it. |
| **Latency** | HealthKit background delivery when phone unlocks | Webhook push (2s ack, 3 retries) | Direct wins slightly. Both fine for daily-card cadence. |
| **Access risk** | HealthKit substrate: stable. Strava's writes into HealthKit: unstable (2022 incident precedent). | MEDIUM. Nov 2024 display restrictions + June 2026 subscription + more changes coming through June 2027. Not blocking today but active vendor churn. | **Baseline wins on substrate stability**, direct is medium-risk. |
| **User onboarding friction** | Install iOS app + grant HealthKit (already done for MFP session) + separately authorize Strava → HealthKit in Strava app | OAuth click once. Same shape as WHOOP. | **Direct wins.** OAuth is simpler than layered HealthKit setup for the Strava-specific consent path. |
| **Operational complexity for us** | HealthKit reads (already built) + no new secrets | Strava OAuth + webhook + reconciliation poller + token rotation race-rescue. **~70% code reuse from WHOOP** — ADR 0006 three-layer, ADR 0011 traces, `WhoopAccessTokenCache` race-rescue pattern all apply. | Baseline is simpler in isolation. Direct is marginal added complexity given WHOOP-pattern reuse. |
| **Cost floor** | $0 | $11.99/mo ($144/year) Standard-tier subscription | **Baseline wins**, but the delta is small compared to aggregators. Roughly a coffee-per-week for the direct integration. |
| **Coverage multiplier** | iOS companion already amortized across seeds | 1x for Strava | **Baseline wins** — same as MFP. |
| **Consent + compliance shape** | Simplest — no third-party data flow. | Strava becomes a direct data source; token custody + refresh-rotation + Nov 2024 display restrictions to respect. Aggregator middlemen banned so no processor there. | Baseline wins on clarity. Direct is manageable (same shape as WHOOP). |
| **Failure mode when access breaks** | Strava changes what they write to HealthKit → we lose fidelity, still get availability from HealthKit-native Apple Watch workouts. | Strava changes API terms or hikes price → we lose the primary source; baseline still catches Apple Watch data. | **Hybrid wins on antifragility.** Either source failing degrades gracefully to the other. |

### 4.2 Verdict — HYBRID

**Ship both. Direct Strava API as primary source for granularity; iOS-companion HealthKit reads as secondary source for availability + fallback.**

The 2x-operational-complexity budget check: direct integration alone (WHOOP-pattern reuse) is roughly 1.2x the operational complexity of baseline-only. Adding it on TOP of baseline (which we're building for MFP anyway) is a small marginal cost for a large fidelity gain. And baseline stays useful even after direct ships — it catches Apple Watch native workouts, cross-checks Strava writes for silent degradation, and provides fallback if the subscription lapses or terms change.

The dedup rule (§5.3) is the load-bearing detail — done right, hybrid gives cleaner data than either source alone.

---

## 5. Implementation shape

### 5.1 Direct Strava integration — mirrors WHOOP

- **OAuth flow.** Standard three-legged. `activity:read` scope minimum; `activity:read_all` if we want private activities. Redirect URI needs to be added to our Container Apps ingress.
- **Secrets in Key Vault.** `strava-client-id`, `strava-client-secret`, per-user access + refresh tokens in the existing `external_connections` shape.
- **Token cache with race-rescue.** Reuse the `WhoopAccessTokenCache` pattern with a `StravaAccessTokenCache`. Strava's refresh-token rotation is identical to WHOOP's; the concurrency lesson (2026-06-11 incident) applies directly.
- **Webhook receiver.** New endpoint `POST /api/webhooks/strava`. HMAC validation against Strava's subscription verify challenge. Match ADR 0006 signature-check pattern. On 200-in-2-seconds requirement: enqueue-and-return-immediately, do the actual ingest work off the request thread.
- **Webhook drainer.** Same `webhook_events` table + drainer pattern as WHOOP.
- **Reconciliation poller.** New Container Apps Job or extend the existing one. Query `GET /athlete/activities?after=<last_seen>` per user, once every 15 minutes (well under the rate limit).
- **On-open synchronous pull.** Same shape as WHOOP: when user opens `/me`, pull last 3 days of activities if any are stale.
- **Trace contract.** ADR 0011 applies — `ingestion.source=strava`, per-user, per-batch. Cross-references our `whoop.*` traces for correlation.

### 5.2 Data model

Mirrors `whoop_workouts` structure. Baseline is a WHOOP-shaped table, extended with Strava's finer-grained fields.

```sql
CREATE TABLE strava_activities (
    id                     uuid PRIMARY KEY,
    user_id                uuid NOT NULL REFERENCES users(id) ON DELETE CASCADE,
    external_connection_id uuid REFERENCES external_connections(id) ON DELETE SET NULL,
    strava_activity_id     bigint NOT NULL,       -- Strava's own id
    strava_athlete_id      bigint NOT NULL,
    activity_type          text NOT NULL,          -- 'Run' / 'Ride' / 'Swim' / 'WeightTraining' / etc
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
    hr_zones               jsonb,                  -- Array of zone durations from detail endpoint
    splits                 jsonb,                  -- Array of split-by-split HR/pace/elevation
    laps                   jsonb,                  -- Array of lap data
    raw_summary_payload    jsonb,                  -- Raw webhook / list-endpoint response
    raw_detail_payload     jsonb,                  -- Raw detail-endpoint response (on demand)
    detail_fetched_at      timestamptz,
    ingested_via           text NOT NULL,          -- 'webhook' / 'reconciliation_poller' / 'on_open_pull'
    ingested_at            timestamptz NOT NULL DEFAULT now(),
    trace_id               text
);
CREATE UNIQUE INDEX idx_strava_activities_activity_id
    ON strava_activities(strava_activity_id);
CREATE INDEX idx_strava_activities_user_started
    ON strava_activities(user_id, started_at DESC);
```

**Cascade contract:** same as `whoop_workouts` per privacy doc Section A. User delete cascades; connection delete SET NULL.

**Detail-fetch policy:** Webhook and list-endpoint responses give the summary shape. The detail endpoint (`GET /activities/{id}`) gives HR zones + splits + laps, but costs one API call per activity. Policy: fetch detail synchronously on webhook create for activities > 20 minutes; skip for short activities where the extra detail doesn't inform the coach. Rate-limit posture at three users: nowhere near the cap.

### 5.3 Dedup rule for WHOOP + Strava overlap

If a real-world activity is captured by both WHOOP (via strap) and Strava (via Apple Watch or dedicated GPS), we have two rows:

- `whoop_workouts` row with strain + HR average + duration
- `strava_activities` row with distance + splits + HR zones + duration

**Rule** (implemented in the coach input-window builder, not the ingest layer):

1. Group candidates by `(user_id, activity_start_time ± 5min, activity_type_family)`. Activity-type family maps: WHOOP's `Running` ≈ Strava's `Run`; WHOOP's `Cycling` ≈ Strava's `Ride`. Full mapping table lives in `WorkoutTypeMap.cs`.
2. If both sources match, emit a merged workout to the coach snapshot:
   - `distance_meters`, `total_elevation_gain_m`, `average_speed_mps`, `splits`, `hr_zones` from Strava (finer granularity).
   - `strain_score` from WHOOP (the metric Strava doesn't compute).
   - `average_hr` — prefer Strava if `has_heartrate=true`, else WHOOP.
   - `elapsed_seconds` — use max of the two (WHOOP sometimes truncates; Strava sometimes over-runs on auto-pause).
3. If only one source has the activity, use it as-is.
4. Preserve provenance: each merged workout carries `sources: ['strava', 'whoop']` so `/admin/agent` can show which fields came from where.

**Baseline path (HealthKit reads) participates in this dedup too** — Apple Watch native workouts (`sourceName=com.apple.workout`) that neither WHOOP nor Strava captured land in a third source and feed the same dedup logic.

### 5.4 iOS companion contribution (baseline layer)

- Same iOS companion built for MFP reads `HKWorkoutType` samples in addition to nutrition.
- Filter by `sourceName`:
  - `com.apple.workout` → Apple Watch native → post to `/api/ingest/healthkit` with `source_bundle_id=com.apple.workout`
  - `com.strava.stravaride` (verify during on-device spike) → Strava's HealthKit writes → post with `source_bundle_id=com.strava.stravaride`
- Backend classifies these as `healthkit_workout` rows in a new table (§5.5), NOT as `strava_activities` rows — keeps direct-API data cleanly separated from HealthKit-round-tripped data for observability.

### 5.5 `healthkit_workouts` table (baseline output)

```sql
CREATE TABLE healthkit_workouts (
    id                     uuid PRIMARY KEY,
    user_id                uuid NOT NULL REFERENCES users(id) ON DELETE CASCADE,
    source_bundle_id       text NOT NULL,          -- 'com.apple.workout' / 'com.strava.stravaride' / 'com.whoop.iphone' / etc
    hk_sample_uuid         uuid NOT NULL,          -- Idempotency key
    workout_type           text NOT NULL,          -- Apple's HKWorkoutActivityType enum name
    started_at             timestamptz NOT NULL,
    elapsed_seconds        int NOT NULL,
    total_energy_kcal      numeric,
    total_distance_m       numeric,
    average_hr             int,
    hk_metadata            jsonb,                  -- Raw HKMetadata for post-hoc analysis
    ingested_at            timestamptz NOT NULL DEFAULT now(),
    trace_id               text
);
CREATE UNIQUE INDEX idx_healthkit_workouts_sample_uuid
    ON healthkit_workouts(hk_sample_uuid);
CREATE INDEX idx_healthkit_workouts_user_started
    ON healthkit_workouts(user_id, started_at DESC);
```

**This table exists even without a Strava direct integration** — it's the iOS-companion output for workouts in general, feeding Apple Watch native workouts to the coach. The Strava dedup rule uses it as a third-source input.

### 5.6 Bounds validator — no changes

`AgentActionCategory.TrainingIntensity` and `.WorkoutStructure` already exist. `AgentActionSource.UserDataInformed` covers Strava-grounded actions. No new enum values.

---

## 6. Coach unlock story — concrete examples

### 6.1 What Strava adds that WHOOP alone can't reason about

**WHOOP-only card, today:**
> Yesterday's workout logged 42 min at avg 158 bpm. That's the third session this week over 150 bpm avg. Recovery today is 71 — moderate. Target zone-2 today: keep HR under 145 bpm during your session.

**With Strava data merged in:**
> Yesterday's tempo run held 158 bpm avg over 42 min — but your splits show you crossed threshold in miles 3-5 (avg 165 bpm) and cooled down for the last 2. That's ~22 min of zone-3 accumulated across three sessions this week. Recovery today is 71. Today's plan is a 45-min zone-2 endurance: target 138-145 bpm the whole time. Skip the tempo. Cadence trended low (168 spm) in yesterday's zone-3 block — worth watching.

The delta: **split-level HR + zone accumulation + cadence trend**. WHOOP flattens all of that into an average.

### 6.2 What baseline-alone (HealthKit reads) delivers

Same as WHOOP-only, plus availability of Apple Watch native workouts that WHOOP didn't catch. Strava's HealthKit writes give us the workout envelope but not the split-level or zone-level detail.

### 6.3 Why hybrid matters

Baseline catches the workouts we'd miss without an iOS companion (Apple Watch native, forgot-to-open-Strava sessions). Direct captures the full-fidelity data that makes Strava worth having as a source. Neither alone gives us the merged shape.

---

## 7. Answers to every open question in the seed

### 7.1 Sequencing

Three-layer ingestion per ADR 0006: webhook (real-time) + reconciliation poller (15-min cadence, 200 req/15min cap is 60x our need) + on-open synchronous pull. Same shape as WHOOP.

### 7.2 Engineering lift

~2 Track A sessions marginal, assuming iOS companion is already built (from MFP session). Breakdown:
- **Session 1:** Strava OAuth flow + webhook receiver + subscription registration + drainer + Key Vault secrets + Bicep bind.
- **Session 2:** Reconciliation poller + detail-fetch policy + dedup rule in coach input-window builder + `healthkit_workouts` HK-side extension in the iOS companion + privacy-doc delta.

~70% code reuse from WHOOP, as the seed anticipated. Confirmed via source verification.

### 7.3 Coach unlock story

Concrete example in §6.1. Load-bearing delta is split-level HR + zone accumulation. Also: gear-specific patterns (long-run shoes vs. everyday shoes), route-based effort inference, kudos-driven training bumps.

### 7.4 Data model

`strava_activities` per §5.2 + `healthkit_workouts` per §5.5. Both cascade-delete on `users` per WHOOP contract.

### 7.5 Overlap handling

Dedup rule per §5.3. Grouped by (user, start_time ±5min, activity_type_family). Prefer Strava for granularity; prefer WHOOP for strain; use max duration; preserve provenance.

### 7.6 Privacy delta

Two additions to privacy doc Section D.1:

- **What gets sent:** activity type, duration, distance (rounded to nearest 100m), HR zones summary (percent-time per zone), and split summary (splits count + fastest split pace). NO lat/lng, NO polyline / route data, NO segment names, NO gear model, NO photos/descriptions.
- **What does NOT get sent:** exact GPS coordinates, complete route polylines, kudos/comments, gear identifiers, activity descriptions/notes, photo URLs. All of these stay server-side; the coach reasons about training effort without needing to know where the user ran.

Route data would be sensitive under HIPAA-adjacent frameworks (home address inferrable, patterns visible) so we strip explicitly.

### 7.7 Rate limits — do we fit?

Yes. 200 req / 15 min at three users doing ~5 workouts / week each is ~15 activities / week total. Even with detail-fetch per activity, ~30 req / week. Nowhere near the cap. Extended Access needed if we scale past 10 users.

### 7.8 Activity types coverage

- **Full detail** (HR + splits + power + cadence available depending on device): Run, Ride, TrailRun, VirtualRide, MountainBikeRide, GravelRide.
- **Partial detail** (HR + duration, less splits): Swim, WeightTraining, Yoga, HIIT, Rowing.
- **Envelope only**: Walk, Hike, Elliptical, most non-cardio.

Coach reads what's there; degrades gracefully if a field is null.

### 7.9 Webhook reliability

Strava explicitly documents 3-retry cap with 2-second ack requirement. Same "not guaranteed" posture as WHOOP. Reconciliation poller is the safety net.

### 7.10 Strava-only user

**Support it.** OAuth flow allows connecting Strava without WHOOP. The coach persona already handles missing recovery data gracefully (WHOOP disconnect is a supported state today). A Strava-only user gets training-oriented card outputs but not recovery-scored ones.

### 7.11 De-authorization

Webhook fires `object_type='athlete'`, `updates.authorized=false`. Our drainer marks the `external_connections` row as `revoked_at=now()`, purges cached tokens, stops the reconciliation poller for that user. Same flow as WHOOP disconnect. Historical `strava_activities` rows retained (cascade contract §5.2).

---

## 8. Recommended path forward

**Phase 1 (~2 Track A sessions, after MFP iOS companion ships):**
1. Direct Strava integration — OAuth + webhook + reconciliation poller + on-open pull. Reuses WHOOP patterns end-to-end.
2. `strava_activities` migration + detail-fetch policy.
3. `healthkit_workouts` extension in iOS companion (extends the MFP session's HealthKit permission set to include `HKWorkoutType`).
4. Dedup rule in coach input-window builder.
5. Privacy doc Section D.1 additions (no lat/lng, no polyline, HR-zone summary only).

**Not-a-code-problem gates:**
- **Register for Strava developer subscription.** $11.99/mo. Adam creates the developer account, subscribes, adds `strava-client-id` + `strava-client-secret` to Key Vault.
- **Register a webhook subscription** with Strava (one-time API call; Strava sends a challenge to verify our callback URL). Blocking on prod deploy of the webhook endpoint.
- **Tai signoff on privacy Section D.1 additions** covering location-data handling.
- **iOS companion must be built first** (from MFP session) — Strava's HealthKit-side reads extend that same companion.

**Parallel side-track (~zero cost, non-blocking):**
- Adam monitors Strava's API policy changes through 2026-2027 endpoint deprecation window. Subscribe to their community-hub developer channel.

**When to reconsider:**
- If Strava changes API terms to explicitly prohibit LLM use — we retreat to baseline-only, degrading fidelity. This is the highest-probability risk.
- If Strava price hikes the subscription materially — reassess vs. baseline-only. At $11.99 today, unproblematic.
- If Extended Access qualification opens up and lets us skip the Standard subscription — free upgrade.

---

## 9. Verification appendix — sources

### Strava API terms + agreement

- **Strava API Agreement (2026), effective terms.** — https://www.strava.com/legal/api
- **API Agreement Update explanation (support article).** — https://support.strava.com/hc/en-us/articles/31798729397773-API-Agreement-Update-How-Data-Appears-on-3rd-Party-Apps
- **Strava Community Hub — 2026 Developer Program announcement.** — https://communityhub.strava.com/insider-journal-9/an-update-to-our-developer-program-13428
- **Third-party summary of 2026 changes ($11.99/mo, MCP consumer product, endpoint deprecations).** — https://appsforstrava.com/blog/strava-developer-program-changes-2026/
- **TechRepublic on scraping crackdown.** — https://www.techrepublic.com/article/news-strava-api-scraping-crackdown/
- **Terra blog on Strava discontinuation of intermediaries.** — https://tryterra.co/blog/strava-discontinues-api

### Strava API mechanics

- **Rate limits — 200 req/15min, 2000/day; Tier 2 = 400/15min, 4000/day; 429 on breach.** — https://developers.strava.com/docs/rate-limits/
- **Webhook subscription — per-app, 3 retries, 2-second ack, event types include athlete deauth + activity create/update/delete.** — https://developers.strava.com/docs/webhooks/
- **OAuth 2 — three-legged; refresh tokens rotate on every access-token exchange; access-token lifetime 6h; new deauthorization endpoint effective June 2026 mandatory June 2027.** — https://developers.strava.com/docs/authentication/
- **Reference — Get Activity Endpoint fields (HR, splits, power, cadence, GPS, kudos, gear).** — https://developers.strava.com/docs/reference/

### Strava ↔ Apple Health

- **Strava Help Center: Health app and Strava — what syncs which direction.** — https://support.strava.com/hc/en-us/articles/216917527-Health-App-and-Strava
- **Strava Help Center: Power and Cadence data from Apple Health (2025 update).** — https://support.strava.com/hc/en-us/articles/35028940753165-Power-and-Cadence-Data-from-Apple-Health
- **DC Rainmaker — Strava abruptly ends 3rd-party data sync to Apple Health (2022-03-15 incident precedent).** — https://www.dcrainmaker.com/2022/03/strava-abruptly-health.html

### Strava's own MCP Connector (rejected — consumer-only)

- **Community Hub — Strava MCP Connector announcement.** — https://communityhub.strava.com/insider-journal-9/an-update-to-our-developer-program-13428 (linked from same June 2026 announcement)

### Sources deliberately not used

- Marketing pages from aggregators claiming Strava support (they've been banned per §2.4).
- Reverse-engineered Strava scrapers (TOS violation risk; irrelevant given direct API is open).
- Aggregator pricing pages — moot, aggregator path is closed for Strava.

---

## Change log

- **2026-07-01 (initial):** Research pass conducted directly in main-loop context with primary-source verification. Framework verdict is HYBRID (baseline + direct), materially different from MFP where baseline-alone won. Load-bearing new finding: intermediary aggregators explicitly banned by Strava's Nov 2024 agreement, confirmed by Terra's own blog. $11.99/mo Standard-tier subscription (June 2026) is a small cost that unlocks the fidelity axis; baseline stays valuable as fallback + Apple Watch native workout catcher.
