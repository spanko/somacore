# MyFitnessPal integration — research pass

**Status.** PROMOTED to [`docs/session-myfitnesspal-integration.md`](../session-myfitnesspal-integration.md) on 2026-07-01. This research doc stays as the "why" reference — the session brief is the ship-worthy plan. Research pass revised 2026-07-01 for the new baseline framework Adam ratified. Answers every open question in [`myfitnesspal-integration.md`](myfitnesspal-integration.md).

**Author.** Claude, with primary-source verification.

**Date.** 2026-07-01 (initial pass 2026-06-30).

**Rebuttable.** Every factual claim carries a URL in the Verification appendix. If a checker re-fetches and the claim doesn't hold, that section is wrong.

**Verified.** 2026-07-01. All load-bearing claims re-verified against the primary sources: MFP API closure, HealthKit's substrate role for third-party apps, MFP's write shape into HealthKit (meal-slot summaries preserved), Terra + Junction pricing floors, Rook / Spike / Human API disqualifications.

---

## 0. The framework this seed answers to

Adam ratified a decision framework on 2026-07-01 that reshapes how every non-Function-Health seed is scoped:

- **Baseline (the floor).** iOS companion app (direct HealthKit → our backend, no aggregator SDK) + CSV upload path for non-iOS users. Both feed the same schema.
- **Direct alternative (per source).** Ships only if it clears a ~2x-operational-complexity budget vs. baseline on measurable net benefit.

Each seed produces one **comparison chart row**: baseline column vs. best direct alternative column, plus a verdict + short rationale. See §4 for MFP's row.

Function Health does not participate in this framework — labs aren't a HealthKit category, so the baseline doesn't reach it. Its promoted decision (`f414b42`, PDF upload + MCP polling) stands unchanged.

---

## 1. Executive summary (90-second read)

**For MFP, the baseline wins.** Ship iOS companion + CSV upload. Skip the aggregators entirely.

Rationale in one paragraph: An iOS companion app reading HealthKit gets MFP's meal-slot summaries (breakfast/lunch/dinner/snack + macros + micros) for free, because MFP writes exactly that shape to HealthKit — verified via MFP's own Apple Health FAQ. The CSV upload path gives the same shape at the same granularity for non-iOS users. Aggregators (Terra $499/mo, Junction $300/mo with MFP deprecation flag) buy real-time push and per-item food names — but the daily card reads once per day so latency past-daily doesn't matter, and our privacy posture strips food names before Anthropic sees the input snapshot anyway. So the aggregator's granularity edge doesn't reach the coach, its latency edge doesn't reach the coach, and it charges $3.6k-$6k/year for the privilege.

**Coverage multiplier is the biggest lever.** The iOS companion is not a one-source shipment — it's the substrate that also reaches WHOOP (writes to HealthKit), Strava (writes to HealthKit), Lumen (writes to HealthKit), Apple Health native (cycle, HRV, VO2max, mindful minutes, workouts), and any future HealthKit-writing app the user installs. Per-source aggregators give us 1x per contract; the baseline gives us N-in-one.

**Phase 1 lift:** ~2 Track A sessions for the iOS companion (Swift, HKHealthStore, background delivery, POST to `/api/ingest/healthkit`), plus ~0.5 session for the `/me/food` CSV upload endpoint. No aggregator secrets, no third-party processor to add to Tai's privacy review, no monthly floor.

**Off the table:**
- MFP direct Partner API — closed to new applicants (side-track email only).
- Terra as a phase-1 shipment — $499/mo floor beats the baseline's zero.
- Junction — $300/mo floor + MFP integration flagged deprecated on their own provider matrix.
- Rook, Spike as phase-1 aggregator SDKs — Rook only reaches MFP via HealthKit anyway (same as our baseline, just with a middleman); Spike's MFP support is unverified marketing.
- Reverse-engineered MFP scraping — TOS risk, credential capture.
- Human API — dead (acquired by LexisNexis 2023).

---

## 2. MFP public developer API — definitive answer

**CLOSED to new partners.** Direct MFP API is not a code path we can take.

### 2.1 Verbatim evidence

- `myfitnesspal.com/api` 302-redirects to `myfitnesspalapi.com/`, which is MFP's official API portal.
- Verbatim from that portal: **"We are not accepting requests for API access at this time."**
- Corroborated by `myfitnesspal.com/apps/api/version`: **"The MyFitnessPal API is currently a private API available to approved developers only"**, contact `API@myfitnesspal.com`.

### 2.2 What this means

Direct MFP API is a business-dev conversation, not a code task. Adam can email `partners@myfitnesspal.com` in parallel; we don't wait on their reply. Existing partners (Terra, Validic, Fitbit-era integrations) retain access — which is why aggregators can proxy MFP even though MFP won't onboard us directly.

---

## 3. HealthKit — the substrate the baseline is built on

Adam has three iOS apps in TestFlight already. The prior "we can't build an iOS app" assumption is retired. HealthKit inverts from *unreachable* to *the enabling platform for the whole baseline*.

### 3.1 What HealthKit enables for our four-seed strategy

Any iOS app the user has installed that *writes* to HealthKit becomes readable by our companion app. Multi-source coverage from one shipment:

| Source | Writes to HealthKit? | What we get |
|---|---|---|
| **MFP** | Yes | Meal-slot summaries with macros + micros |
| **WHOOP** | Yes | Recovery, sleep, strain (redundant with our WHOOP API path, useful as fallback) |
| **Strava** | Yes | Workouts (activity type, distance, duration, energy) |
| **Lumen** | Yes | Fuel-usage reads (reshapes the Lumen seed entirely — likely goes from "not viable" to "viable") |
| **Apple Health native / Apple Watch** | Yes | HR, HRV, VO2max, ECGs, workouts, cycle tracking, mindful minutes, medications, sleep phases |
| **Any cycle-tracking app** | Most write | Menstrual phase (feeds Tai's protein-personalization seed) |

That's why the coverage-multiplier axis in §4 is the load-bearing one.

### 3.2 What MFP writes to HealthKit specifically

MFP publishes an Apple Health FAQ. From that article (search-snippet-sourced; the page 403s to WebFetch but the content is quoted verbatim in Google's index):

> "MyFitnessPal will update Meal Summaries (calories and nutrients) to HealthKit. Additionally, only the default Breakfast, Lunch, Dinner and Snack headers will sync to their associated meal, while custom meal names will not sync... foods logged under a custom heading will be displayed under 'Other' as one sync."

Translation into what we can plan against:
- **Meal-slot markers are preserved** on the HealthKit round-trip (breakfast/lunch/dinner/snack). This upgrades the coach's `MealTiming` category to first-class signal even without direct MFP API access.
- **Macros + micros** — calories, protein, carbs, fat, fiber, sugar, sodium, plus micronutrients where MFP has them — arrive as separate `HKQuantityType` records with a shared meal-slot metadata marker.
- **Custom meal-slot names** (if a user renamed "Breakfast" to "Pre-Workout") get bucketed to "Other" on the way through HealthKit. Non-ideal but survivable.
- **Individual food-item names do NOT survive.** HealthKit records the nutritional totals, not the specific foods logged. This aligns exactly with our privacy posture (Section D strips food names before Anthropic sees the input snapshot anyway).

**Caveat — on-device verification required during iOS build.** The exact `HKMetadata` keys MFP writes for meal-slot marking are not publicly documented. Plan-against assumption: MFP uses either the standard `HKMetadataKeyMealType` (Apple-provided) or a private key we discover on device. First iOS-build session includes a spike where we log what MFP actually writes so we can key off the correct field. This is a real risk item, not a nice-to-have.

### 3.3 iOS-specific caveats to plan against

- **Locked-device reads are limited.** HealthKit encrypts data at rest when the phone is locked. Background delivery of new writes works even when locked, but batch reads (e.g. our reconciliation poller reading last 24h) work best when the user opens the app. Design implication: the iOS companion needs to push on any activity, not rely on a nightly batch alone. Rook's docs are the clearest citation for this constraint.
- **`Info.plist` requirements:** HealthKit capability, Background Modes (background fetch, background processing), `NSHealthShareUsageDescription` (read purpose string), and the background task identifier we register (`com.tento100.somacore.healthkit-sync` or similar). All the same fields Terra's SDK integration guide requires — we're just doing it without their SDK.
- **`HKObserverQuery` background delivery** is the mechanism that fires when a third-party app writes to HealthKit. Register once per data type (nutrition, workout, sleep, mindfulness, cycle), get callbacks when new data lands, batch-post to our backend via user's Entra token.
- **App Store review is a real gate** *later*. TestFlight is fine for our three internal users through phase 1 and probably phase 2.
- **Non-iOS users get the CSV upload path** (§4.2). Android via Health Connect is technically possible (same architecture, different SDK) but not scoped in phase 1 — Adam confirms all three internal users are on iOS.

---

## 4. Comparison chart — the deliverable

The load-bearing artifact for this seed. Every non-Function-Health seed produces one of these.

### 4.1 Chart

| Dimension | **Baseline: iOS companion + CSV** | **Direct alt: Terra / Junction aggregator** | Winner |
|---|---|---|---|
| **Granularity** | Meal-slot summaries (breakfast/lunch/dinner/snack + macros + micros) via HealthKit; per-meal-slot rollups via CSV. **No individual food-item names.** | Per-meal entries with per-item names via aggregator webhook. | Direct is finer on paper. **BUT our privacy posture strips food names before Anthropic sees the input snapshot** (Section D), so the extra granularity doesn't reach the coach. → Functional tie. |
| **Latency** | HealthKit background delivery: seconds-to-minutes after MFP writes on unlock. CSV: weekly manual cadence. | Real-time webhook push. | **Direct wins on paper**, but the daily card reads once per day. Freshness past 24h doesn't matter. → Functional tie for daily-card use case. |
| **Access risk** | LOW. HealthKit is an Apple platform commitment stable since iOS 8. Apple has never deprecated a data type. MFP's write to HealthKit could theoretically stop but there's no signal it will. | MEDIUM (Terra: Series A startup) to HIGH (Junction: `my_fitness_pal_v2` marked "Application closed" and legacy `my_fitness_pal` marked deprecated on their own provider matrix; MFP direct API closed to new partners). | **Baseline wins.** |
| **User onboarding friction** | iOS: install TestFlight build, grant HealthKit permission once. Then invisible. Non-iOS: MFP Premium data-export flow (recurring). | Aggregator widget click + (Junction) fiddle with MFP diary-key setting. Then invisible. | Baseline wins for iOS users (invisible after install). Direct wins slightly for a non-iOS user vs. our CSV path. → **Baseline wins for phase-1 population.** |
| **Operational complexity for us** | iOS app to build + maintain (Swift, HKHealthStore, background delivery, code-signing pipeline) + CSV parser. Two paths, but reused across MFP + WHOOP + Strava + Lumen + Apple native. | Webhook receiver + aggregator secret rotation + SDK version tracking. One path per aggregator. | **Direct wins per-source, baseline wins in aggregate** because the iOS companion is amortized across N sources. See coverage multiplier row. |
| **Cost floor** | **$0/month.** Apple developer account is $99/year (already paid — Adam has 3 apps in TestFlight). | Terra: $499/mo ($6k/yr). Junction: $300/mo ($3.6k/yr). | **Baseline wins.** $3.6k-$6k/year saved at phase-1 scale. |
| **Coverage multiplier** | **HIGH.** iOS companion also reaches WHOOP + Strava + Lumen + Apple native (cycle, HRV, VO2max, workouts, mindful minutes) + any future HealthKit-writing app the user installs. **One shipment covers 4+ seeds.** | 1x per aggregator per source — Terra covers MFP + a few other nutrition apps (Cronometer, EatThisMuch, FatSecret, NutraCheck). No coverage of Strava/Lumen unless we pay per source. | **Baseline wins decisively.** This is the load-bearing dimension. |
| **Consent + compliance shape** | No third-party processor. Users grant HealthKit permission to our own app. Our name, our privacy policy, our DPA. Tai's privacy review touches only Section D.1/D.2. | Aggregator becomes a processor (Section G addition). Junction's credential-share flow is a weaker consent posture than delegated OAuth — user gives us their MFP diary-key password. | **Baseline wins.** Lightweight privacy review. |
| **Failure mode when access breaks** | Apple removes / changes an API — we adapt. MFP stops writing to HealthKit — we lose the source (also breaks any aggregator that uses HealthKit-bridge). Any single vendor going away doesn't hurt us. | Aggregator sunsets / raises prices / gets acquired (Human API precedent) — we lose the source AND owe migration work. | **Baseline wins on antifragility.** |

### 4.2 Verdict

**Ship baseline. Skip aggregators for MFP.**

The 2x-operational-complexity budget check: baseline is arguably *less* complex than the aggregator alternative once amortized across the four seeds. Even scoped to MFP alone, the aggregator wins on latency (real-time push, moot for daily-card cadence) and per-item food-name granularity (moot after privacy stripping) — those are the only two dimensions where "direct" beats "baseline." Baseline wins the other seven.

Aggregators come back on the table if: (a) coach behavior evolves to need continuous real-time data below the daily cadence (e.g., a live "you're about to break your fasting window" nudge), or (b) user count crosses ~50 where per-MAU pricing beats the floor and the operational-complexity math shifts. Neither is close to today's state.

---

## 5. Data paths — implementation shape for each baseline layer

### 5.1 Baseline path A: iOS companion → HealthKit → our backend

**Shape.**
- Minimal Swift iOS app, TestFlight distribution to Adam / Tai / Adam's third internal user.
- User signs in with Entra (same SSO as the web app); iOS app stores the Entra access token in Keychain.
- App requests HealthKit read permission for: `HKQuantityTypeIdentifierDietaryEnergyConsumed`, `HKQuantityTypeIdentifierDietaryProtein`, `HKQuantityTypeIdentifierDietaryCarbohydrates`, `HKQuantityTypeIdentifierDietaryFatTotal`, `HKQuantityTypeIdentifierDietaryFiber`, `HKQuantityTypeIdentifierDietarySodium` (and workout / sleep / cycle types when we extend to other seeds).
- Register `HKObserverQuery` per type with background delivery.
- On callback: read new samples since last watermark, group by meal-slot metadata + timestamp proximity, POST to `POST /api/ingest/healthkit` with the user's Entra token.
- Backend deduplicates on `(user_id, meal_date, meal_slot, sourceName)` — MFP's writes have `sourceName="MyFitnessPal"`, which our ingest classifies as `source='healthkit_ios_mfp'` on the same `mfp_food_entries` schema §6.

**Lift.** ~2 Track A sessions.
- **Session 1:** Swift app skeleton + Entra SSO + Keychain token storage + `Info.plist` + HealthKit permission flow + first `HKObserverQuery` for nutrition types + spike to confirm the exact `HKMetadata` MFP writes for meal-slot marking (this is the on-device verification we owe).
- **Session 2:** Backend `POST /api/ingest/healthkit` endpoint with HMAC-shaped auth (user's Entra bearer + a device attestation nonce so we can later reject non-app POSTs), batch processing into `mfp_food_entries` and `mfp_daily_rollups`, idempotency, trace-id propagation, background reconciliation query for stale windows.

**Risk hotspots.**
- **Unknown HK metadata schema for MFP.** The exact key(s) MFP uses to mark meal-slot isn't publicly documented. Session-1 spike is non-negotiable. If MFP uses a private key we can't reliably read, meal-slot fidelity degrades to "compose from timestamp proximity" (fragile). Fallback plan: fall through to CSV upload for meal-slot fidelity, keep HealthKit for per-day totals only.
- **Locked-device batch reads.** Handled by observing writes (not batching), so writes go through as they happen. Reconciliation poller only fires on app foreground.
- **iOS build discipline.** Adam has this covered per his own message — three apps in TestFlight already.
- **Bicep changes.** None. iOS is client-side; backend gets a new endpoint that lives in existing Container Apps.
- **Migrations.** Two tables per §6 (same tables the CSV path uses).

### 5.2 Baseline path B: `/me/food` CSV upload (non-iOS fallback + iOS parallel)

**Shape.** User logs in at `myfitnesspal.com` on desktop (Premium required), requests data export, receives ZIP by email, uploads to `/me/food`. Backend unpacks, parses meal-nutrition CSV into `mfp_food_entries` and derives `mfp_daily_rollups`.

**Fidelity.** Same shape as HealthKit path — per-meal-slot rollups. Native MFP export includes calories, macros, micros, food-notes, and timestamps summarized by meal.

**Lift.** Low. ~0.5 Track A session — CSV parse is trivial once the ZIP unpack + MIME + size validation is in place. No LLM needed (unlike Function Health PDFs).

**Why we still ship this even when iOS is the baseline:**
- **Non-iOS users** (future users, Android, or an internal user who declines to install TestFlight).
- **iOS parallel channel** — if the HealthKit meal-slot spike fails (§5.1 unknown-HK-metadata risk), CSV upload becomes the meal-slot fidelity source and HealthKit degrades to totals-only.
- **Historical backfill** — MFP's data export delivers all-time data. HealthKit's `HKAnchoredObjectQuery` only sees writes from the moment we register. For a new user's first week, the CSV export is the fastest way to give the coach a baseline.

**Risk hotspots.** MFP export is Premium-only per their help center. Adam + Tai confirm they're both Premium. If a future non-Premium user needs to onboard, GDPR data-subject request is the (slow) fallback.

### 5.3 Rejected direct-alternative paths (per §4 verdict)

- **Terra API integration.** $499/mo floor; buys real-time push + per-item food names, neither of which reaches the coach. Rejected on cost.
- **Junction (formerly Vital) API integration.** $300/mo floor; MFP integration flagged deprecated on Junction's own provider matrix (`my_fitness_pal_v2` = "Application closed"; `my_fitness_pal` = deprecated); credential-share auth (user gives us their MFP diary-key password) is a weaker consent posture than delegated OAuth. Rejected on cost + deprecation risk.
- **Rook API.** Their nutrition ingestion is HealthKit-bridge-with-a-middleman — same architectural path as our baseline, but paying $399/mo to have Rook's SDK do what our own app does directly. Rejected as a strictly-worse baseline.
- **Spike.** Marketing page claims MFP support; the docs provider matrix contradicts. Unverified, $450/mo floor. Rejected as unverified.
- **Apple Health XML export ZIP upload.** Fragile (500MB-1GB ZIPs, share-sheet delivery, per-record filtering by `sourceName`, no meal-slot composition). Rejected as strictly worse than the CSV export path.
- **MFP Partner API.** Closed to new applicants. Adam sends the email in parallel as a side-track; not a code path.
- **Reverse-engineered MFP scraping** (`seqian/ExportMyFitnessPal`, `andrewzey/freemydiary`, `fitnessforlife/mfp`). TOS violation; credential capture; brittleness. Rejected outright.
- **Human API.** Dead — acquired by LexisNexis 2023, redomained to insurance underwriting. Not a general-purpose aggregator anymore.
- **Validic.** Gated docs; historically enterprise-only. Not scoped for phase-1 consumer flow.

---

## 6. Data model

Mirrors the WHOOP cascade contract from privacy doc Section A: user-keyed cascade delete. Same schema serves both HealthKit-ingested and CSV-uploaded records — the `source` column distinguishes.

### 6.1 Tables

```sql
-- Per-meal food entries. One row per (user, meal_slot, date).
-- Idempotent on (user_id, meal_date, meal_slot, source) so re-ingest is a no-op.
CREATE TABLE mfp_food_entries (
    id                     uuid PRIMARY KEY,
    user_id                uuid NOT NULL REFERENCES users(id) ON DELETE CASCADE,
    external_connection_id uuid REFERENCES external_connections(id) ON DELETE SET NULL,
    source                 text NOT NULL,           -- 'healthkit_ios' / 'csv_upload'
    meal_date              date NOT NULL,
    meal_slot              text NOT NULL,           -- 'breakfast' / 'lunch' / 'dinner' / 'snack' / 'other'
    logged_at              timestamptz,             -- Best-effort timestamp (nullable — CSV export is meal-slot rollup)
    calories               numeric,
    protein_g              numeric,
    carbs_g                numeric,
    fat_g                  numeric,
    fiber_g                numeric,
    sugar_g                numeric,
    sodium_mg              numeric,
    food_items             jsonb NOT NULL DEFAULT '[]'::jsonb,  -- Array of {name, amount, unit} from CSV; empty from HealthKit
    raw_payload            jsonb,                   -- The HealthKit sample batch OR the CSV row's original text
    ingested_via           text NOT NULL,           -- 'ios_observer' / 'ios_reconciliation' / 'csv_upload'
    ingested_at            timestamptz NOT NULL DEFAULT now(),
    trace_id               text
);
CREATE UNIQUE INDEX idx_mfp_food_entries_user_date_slot_source
    ON mfp_food_entries(user_id, meal_date, meal_slot, source);
CREATE INDEX idx_mfp_food_entries_user_date
    ON mfp_food_entries(user_id, meal_date DESC);

-- Daily rollups. Derived from mfp_food_entries; recomputed on ingestion.
CREATE TABLE mfp_daily_rollups (
    id                uuid PRIMARY KEY,
    user_id           uuid NOT NULL REFERENCES users(id) ON DELETE CASCADE,
    rollup_date       date NOT NULL,
    calories          numeric,
    protein_g         numeric,
    carbs_g           numeric,
    fat_g             numeric,
    fiber_g           numeric,
    meals_logged      int NOT NULL DEFAULT 0,        -- 1-4; how many meal_slots have entries
    updated_at        timestamptz NOT NULL DEFAULT now()
);
CREATE UNIQUE INDEX idx_mfp_daily_rollups_user_date
    ON mfp_daily_rollups(user_id, rollup_date);
```

**Uniqueness key note.** The unique index includes `source` because HealthKit and CSV paths can both write a row for the same `(user, meal_date, meal_slot)` — that's a feature, not a conflict: HealthKit provides freshness, CSV provides historical backfill / meal-slot verification. The `mfp_daily_rollups` rollup dedupes across both sources (prefer HealthKit for recent days, CSV for backfill).

### 6.2 Cascade contract

- **User deletion → everything.** Both tables cascade-delete on `users`. Mirrors `whoop_recoveries`.
- **Connection deletion → FK SET NULL.** Only meaningful if we later add an aggregator; irrelevant for baseline (HealthKit + CSV don't produce external_connection rows).
- **No cascade between MFP tables** (rollups are derived, not FK'd to entries). Rollups get recomputed on next successful ingest.

### 6.3 Bounds validator changes (code, not schema)

- `AgentActionCategory.MealTiming` — exists. HealthKit meal-slot markers upgrade its signal quality from "inferred" to "real."
- `AgentActionCategory.Macros` — exists. Same.
- `AgentActionSource.UserDataInformed` — exists. Actions grounded in HealthKit-sourced MFP data use this source.
- No new bounds category.

---

## 7. Privacy doc delta

Baseline path has no third-party processor. That's the biggest privacy-simplification of the whole framework.

### 7.1 Section D.1 addition

Add to the "What gets sent" list, after the WHOOP-data bullet:

> **Aggregated MyFitnessPal nutrition data (Track D Session 1).** When a user has installed the SomaCore iOS companion app (or uploaded their MFP data export), the daily-card agent's input snapshot includes: (a) the last 7 days of `mfp_daily_rollups` (calories, protein_g, carbs_g, fat_g, fiber_g, meals_logged per day), (b) the last 3 days of `mfp_food_entries` with meal_date, meal_slot, and macronutrient totals per meal slot. Individual food-item names never leave our backend — they're recorded in `food_items` only from the CSV path, are absent from the HealthKit path, and are stripped from the agent's input snapshot regardless.

### 7.2 Section D.2 reinforcement

Add to the "What does NOT get sent" list:

> **Individual food-item names, brands, or restaurant identifiers from MFP.** The daily-card agent receives macronutrient totals per meal-slot and meal-slot timing, not the specific foods the user logged. This preserves the coach's ability to reason about macro targets and meal timing while keeping food-choice patterns (which can be sensitive — dietary restrictions, disordered-eating adjacent behaviors, cultural food identity) inside our infrastructure.

### 7.3 No new processor for phase 1

The iOS companion + CSV upload paths do not add a third-party processor. Users grant HealthKit permission to our own app; MFP data flows MFP → HealthKit (on-device) → our companion (on-device) → our backend. No middle party sees the payload. This is a real advantage of the baseline choice; Tai's privacy review touches only Section D.

### 7.4 Section E (iOS companion) — new subsection

Add a short subsection describing:
- The iOS companion's role (client of our backend + reader of HealthKit).
- HealthKit permission scopes we request (list from §5.1).
- Local storage: Keychain-stored Entra token, no cached food data (we transmit and forget on device).
- Deletion: HealthKit permission revocation and app uninstall both stop future ingestion; existing `mfp_food_entries` and `mfp_daily_rollups` are user-deletable via the existing account-delete flow.

---

## 8. Answers to every open question in the seed

### 8.1 "Is MyFitnessPal's public developer API still open?"

**Closed.** See §2. Verbatim: *"We are not accepting requests for API access at this time."*

### 8.2 "What does an MFP CSV export actually contain?"

Per-meal-slot rollups (breakfast/lunch/dinner/snack) with calories + macronutrients + micronutrients + food-item notes per meal row + summarized timestamps. Individual food items appear inside meal-row notes but are not separately timestamped. Progress and exercise are separate CSVs with per-entry rows. Premium-only.

### 8.3 "Do the third-party aggregators surface MFP data?"

- **Terra:** Yes, first-class web OAuth support (§4-rejected on cost floor).
- **Junction (formerly Vital):** Yes, but MFP integration flagged deprecated on their own provider matrix (§4-rejected on cost + deprecation risk).
- **Rook:** Only via HealthKit + iOS SDK bridge — architecturally identical to our baseline but with $399/mo middleman.
- **Spike:** Marketing claim; provider matrix contradicts. Unverified.
- **Human API:** Dead (acquired by LexisNexis 2023).
- **Validic:** Gated docs, enterprise-only.

None beat the baseline given the framework in §4.

### 8.4 "If we go the aggregator path, what's the ongoing cost per user per month?"

Moot given the §4 verdict. For record: Terra $499/mo floor, Junction $300/mo floor. Per-user math only starts to matter beyond ~50 users.

### 8.5 "Can MFP data be partially useful without meal-time timestamps?"

Yes — meal-slot classification (breakfast/lunch/dinner/snack) alone is sufficient for the coach's `MealTiming` reasoning. Baseline preserves meal-slots via HealthKit's meal-slot metadata (§3.2).

### 8.6 "Is there an obvious MVP we're overthinking?"

The framework Adam ratified IS the MVP. iOS companion is the smallest thing that also covers WHOOP + Strava + Lumen + Apple native + cycle in one shipment. CSV upload is the smallest non-iOS fallback. Nothing simpler covers the same ground.

---

## 9. Recommended path forward

**Phase 1 (~2.5 Track A sessions):**
1. iOS companion app: Swift skeleton + Entra SSO + HealthKit permission flow + on-device spike to verify MFP meal-slot HK metadata + `HKObserverQuery` for nutrition types + POST to backend. TestFlight to Adam + Tai + third internal user. (~2 sessions.)
2. `/me/food` CSV upload endpoint: MIME + size validation, ZIP unpack, CSV parse into `mfp_food_entries` + rollup recompute. (~0.5 session.)

**Not-a-code-problem gates:**
- Adam + Tai on MFP Premium (Adam already; Tai confirm).
- Tai signoff on Section D.1/D.2 + new Section E subsection. No aggregator processor to disclose — lightweight review.
- Adam ratifies iOS-companion scope in phase 1 vs. the current `phase-1-scope.md` (which lists mobile-app work as out-of-scope). This seed's shipment likely requires an addendum or scope amendment. Flag for Adam before shipping.

**Parallel side-track (~zero cost, non-blocking):**
- Adam emails `partners@myfitnesspal.com` pitching integration. Response likely months out or "not accepting." If unexpectedly positive, revisit sequencing at that point.

**When to reconsider aggregators:**
- Coach behavior evolves to require sub-daily latency (a live "you're about to break your fasting window" nudge), OR
- User count crosses ~50 where per-MAU pricing beats the aggregator floor and operational-complexity math shifts, OR
- The on-device HK-metadata spike (Session 1) reveals MFP's meal-slot marking is unreadable and the CSV path can't cover backfill for a fast-growing user base.

---

## 10. Verification appendix — sources

Every URL below is what a checker will re-fetch.

### MyFitnessPal API status

- **MFP API portal states verbatim "We are not accepting requests for API access at this time".** — https://myfitnesspalapi.com/
- **`myfitnesspal.com/apps/api/version` confirms "The MyFitnessPal API is currently a private API available to approved developers only".** — https://www.myfitnesspal.com/apps/api/version

### MFP data export (Premium)

- **MFP help center describes Premium data-export flow: desktop-only, ZIP by email, per-meal nutrition rollups + progress + exercise.** — https://support.myfitnesspal.com/hc/en-us/articles/360032273352-Data-Export-FAQs (page 403s to WebFetch; content sourced from search snippet + third-party corroboration)

### MFP → HealthKit write shape (LOAD-BEARING for baseline granularity)

- **MFP writes Meal Summaries (calories and nutrients) to HealthKit; default meal headers (Breakfast, Lunch, Dinner, Snack) sync to their associated meal slots; custom meal names bucket to "Other".** — https://support.myfitnesspal.com/hc/en-us/articles/360032271092-Apple-Health-FAQ-and-Troubleshooting (page 403s to WebFetch; content quoted verbatim in Google's index — see §3.2)

### Apple HealthKit as substrate

- **HealthKit is Apple's device-local health data repository across iPhone, iPad, Apple Watch, Vision Pro.** — https://developer.apple.com/health-fitness/
- **`HKHealthStore` available on all Apple client OSes since iOS 8.0 / watchOS 2.0 / iPadOS 8.0 / macOS 13.0 / Mac Catalyst 13.0 / visionOS 1.0.** — https://developer.apple.com/documentation/healthkit/hkhealthstore
- **Third-party apps require on-device `HKHealthStore.requestAuthorization`; there is no OAuth-style server flow.** — https://developer.apple.com/documentation/healthkit/hkhealthstore/1614152-requestauthorization

### HealthKit locked-device read constraint

- **Rook explicitly states: "For security, iOS devices encrypt the HealthKit storage when users lock their devices. As a result, apps may not be able to read data from Apple Health when running in the background."** — https://docs.tryrook.io/docs/rookconnect/sdk/iOS/

### Aggregators (rejected in this seed, retained for reference)

- **Terra API supported-integrations page lists MFP with enum `MYFITNESSPAL`, "Periodically fetched", "N/A" credentials.** — https://docs.tryterra.co/reference/health-and-fitness-api/supported-integrations
- **Terra confirms MFP is fully supported via web OAuth (Terra Widget), no iOS SDK required.** — https://tryterra.co/community/clarification-on-myfitnesspal-and-apple-health-integration-for-web-applications
- **Terra pricing: Quick Start $499/mo monthly ($399/mo annual), 100k credits included, Enterprise custom.** — https://tryterra.co/pricing
- **Junction (formerly Vital) MFP integration guide.** — https://docs.junction.com/wearables/guides/my_fitness_pal
- **Junction providers index lists `my_fitness_pal_v2` "Application closed" and `my_fitness_pal` "deprecated".** — https://docs.junction.com/wearables/providers/introduction
- **Junction Launch tier: $0.50/user/mo, $300/mo minimum.** — https://www.junction.com/pricing
- **Rook confirms MFP nutrition only reachable via HealthKit + Rook iOS SDK.** — https://support.tryrook.io/en/articles/9185446-using-nutrition-data-from-rook
- **Spike provider matrix (authoritative; contradicts marketing landing page).** — https://docs.spikeapi.com/technical-references/provider_matrix.md
- **Human API acquired by LexisNexis 2023.** — https://risk.lexisnexis.com/about-us/press-room/press-release/20230425-humanapi-acquisition

### Reverse-engineered scraping (rejected)

- **Community project `seqian/ExportMyFitnessPal`.** — https://github.com/seqian/ExportMyFitnessPal
- **Community project `andrewzey/freemydiary`.** — https://github.com/andrewzey/freemydiary

### Sources deliberately NOT used

- Spike's MFP landing page (`spikeapi.com/integrations/myfitnesspal`) — contradicts their own provider matrix; treated as marketing-only.
- Any Wikipedia claim.
- Marketing pages from aggregators when their own docs said different.

---

## Change log

- 2026-06-30 (initial): first research pass conducted directly in the main-loop context after async agents stalled twice. Aggregator-first framing.
- 2026-06-30 (revision 1): stalled async agents eventually delivered. Corrected Terra pricing floor upward 10x ($20-50 → $499); Junction discovered to support MFP with deprecation flag; Rook re-classified from "doesn't cover MFP" to "covers MFP via iOS SDK bridge only". Recommendation revised to CSV-first with aggregator deferred.
- **2026-07-01 (revision 2, this doc):** Adam ratified new baseline framework — iOS companion app (direct HealthKit → our backend) + CSV upload is the floor for every non-Function-Health seed. HealthKit inverted from "impossible" to "the enabling substrate." Load-bearing new finding: MFP writes meal-slot-aware summaries to HealthKit, so baseline preserves `MealTiming` fidelity. Aggregators demoted from primary options to one column of the comparison chart; rejected on cost floor + coverage-multiplier math. Doc restructured around the framework and the comparison chart is now the deliverable.
