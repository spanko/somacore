# Session brief — MyFitnessPal integration

**Status.** Session prompt. Promoted from [`docs/seeds/myfitnesspal-integration-research.md`](seeds/myfitnesspal-integration-research.md) after the 2026-07-01 rework against the new baseline framework Adam ratified. This document is the ship-worthy plan; the research doc stays as the reference for why we made these choices.

**Track / phase.** Phase 2. Not part of Track A (WHOOP). Suggest calling this **"Track D — external data sources, Session 2"** (Session 1 was Function Health). Naming is Adam's call.

**Framework decision made 2026-07-01.** Baseline = iOS companion app (direct HealthKit → our backend, no aggregator SDK) + CSV upload for non-iOS users. Aggregators (Terra, Junction) rejected on the comparison chart — see research doc §4. **This session's iOS-companion work also unlocks Strava + Lumen + Apple native + cycle-phase in future sessions**, per the coverage-multiplier finding. That amortization is a load-bearing part of the ROI story.

**Scope-conflict flag.** [`docs/phase-1-scope.md`](phase-1-scope.md) currently lists "Flutter mobile app" as out-of-scope. A minimal Swift iOS companion is a different shape from a Flutter port, but this session ships mobile client code for the first time. Before this session starts: (a) Adam explicitly amends `phase-1-scope.md` OR (b) confirms this session is Phase 2, so the phase-1 fence doesn't apply. Either is fine; the ambiguity isn't.

---

## Goal

Land two coach categories — `MealTiming` and `Macros` — as first-class signals in the daily card, powered by real MFP nutrition data, via a two-path baseline:

- **Path A — iOS companion app.** Users install a minimal Swift TestFlight build (Adam, Tai, third internal user), grant HealthKit read permission once. From that point on, whenever MFP writes to HealthKit on their phone, our companion reads it and posts to our backend. **Invisible after install** — no user action per meal.
- **Path B — `/me/food` CSV upload.** Non-iOS fallback (and iOS parallel channel for historical backfill). User exports their MFP data ZIP from `myfitnesspal.com` on desktop (Premium required), uploads to `/me/food`. Backend parses the meal-nutrition CSV into the same schema Path A writes to.

Both paths write to the same `mfp_food_entries` + `mfp_daily_rollups` tables. The coach reads from those tables regardless of source.

**Coach unlock example:** *"You're at 68g protein against a 140g target with dinner left — target 45g at your next meal. Your first meal today landed at 10:45am, so if you want to extend your fasting window tomorrow, aim to break fast at 11:15am."* The macro number comes from `mfp_daily_rollups.protein_g`; the meal-timing observation comes from `mfp_food_entries.meal_slot` and `logged_at`.

---

## Approach — why baseline wins (and why we skip aggregators)

The research doc's §4 comparison chart is the load-bearing artifact. Verdict: baseline wins on 7 of 9 dimensions (data freshness past-daily and per-item food-name granularity are the only aggregator wins; both are moot for our use case — daily card reads once per day, and our privacy posture strips food names before Anthropic sees the input snapshot).

Concretely, four points to internalize before implementing:

1. **HealthKit preserves meal-slot fidelity.** MFP's Apple Health FAQ documents that MFP writes meal summaries to HealthKit with default meal headers (Breakfast, Lunch, Dinner, Snack) preserved; custom meal names bucket to "Other." This is verified from MFP's own docs — see research §3.2. The coach's `MealTiming` category gets first-class signal from Path A without any aggregator.
2. **Cost floor is $0 for baseline vs. $300-$499/mo for aggregators.** At three-user scale that's $3.6k-$6k/year saved. At any scale below ~50 users, the aggregator floor dominates per-MAU math.
3. **Coverage multiplier.** The iOS companion is not a one-source shipment. This session's Swift skeleton + Entra SSO + HealthKit permission pattern is directly reused by Strava, Lumen, cycle-phase, and Apple Watch native seeds. The ~2-session cost amortizes across 4+ sources.
4. **No third-party processor.** Baseline has zero aggregator in the compliance surface. Tai's privacy review touches only Section D.1/D.2 + new Section E — lightweight. The alternative (Terra or Junction) requires a Section G processor disclosure and, for Junction specifically, a credential-share consent posture that's weaker than delegated OAuth.

Aggregators come back on the table if (a) coach behavior evolves to require sub-daily latency (a live "you're about to break your fasting window" nudge), OR (b) user count crosses ~50 so per-MAU pricing beats the floor. Neither is close to today's state.

---

## Phase 1 — what ships this session

### 1.1 iOS companion app (Swift, TestFlight)

**Deliverable:** a minimal Swift iOS app distributed via TestFlight to Adam, Tai, and the third internal user, that:

- Signs the user in with Microsoft Entra ID using the same SSO app registration the web app uses.
- Stores the Entra access token in iOS Keychain.
- Requests HealthKit read permission for these `HKQuantityTypeIdentifier` values (nutrition scope only in this session — workout, sleep, cycle types added in later Track D sessions):
  - `dietaryEnergyConsumed` (calories)
  - `dietaryProtein`
  - `dietaryCarbohydrates`
  - `dietaryFatTotal`
  - `dietaryFiber`
  - `dietarySugar`
  - `dietarySodium`
- Registers one `HKObserverQuery` per type with background delivery.
- On callback, uses `HKAnchoredObjectQuery` to fetch samples since last device-side anchor, batches them, and posts to `POST /api/ingest/healthkit` with the user's Entra bearer token.
- Handles background delivery via `Info.plist` Background Modes (background fetch, background processing) and a registered background task identifier (`com.tento100.somacore.healthkit-sync`).

**Session-1 spike (non-negotiable):** the exact `HKMetadata` key MFP uses for meal-slot marking is not publicly documented. Before wiring the ingest pipeline, run an on-device spike where Adam or Tai open MFP, log a meal, and we `print(sample.metadata)` to Xcode console. Confirm whether MFP writes:
- Apple's standard `HKMetadataKeyMealType` (preferred — well-documented enum)
- A private key like `MFPMealType` or `MyFitnessPal-MealName` (workable but requires string-key handling)
- Nothing — in which case meal-slot must be reconstructed from timestamp proximity (fragile; Path B becomes the meal-slot fidelity source and Path A degrades to per-day totals).

The spike's outcome determines whether we key on a standard enum, a private string, or fall back to CSV-first for meal-slot. It's a ~30 minute exercise and it de-risks the whole schema.

### 1.2 Backend ingest endpoint

**Deliverable:** `POST /api/ingest/healthkit` in `SomaCore.Api`:

- `[Authorize]` behind the same Entra policy as `/me`.
- Accepts a JSON envelope: `{ device_id, samples: [{ type, value, unit, start_date, end_date, source_name, source_bundle_id, metadata: {...} }], anchor_token }`.
- Rejects any post whose `source_bundle_id` is not on an allowlist configured in `appsettings` (starting allowlist: `com.myfitnesspal.mfp`, `com.mfp.MyFitnessPal`, whatever Adam confirms during the spike).
- Idempotency: dedupe by `(user_id, sample.uuid)` where `sample.uuid` comes from `HKObject.uuid` on the client.
- Groups nutrition samples by `(source_name, meal_slot, meal_date)` and upserts `mfp_food_entries` rows. Recomputes affected `mfp_daily_rollups` rows.
- Emits an ingestion trace matching the ADR 0011 shape (`ingestion.source=healthkit_ios`, per-user, per-batch).
- Returns the anchor token the client should store for the next `HKAnchoredObjectQuery`.

### 1.3 `/me/food` CSV upload surface

**Deliverable:** Razor Page at `/me/food` behind the existing `[Authorize]` policy:

- Drag-drop or file-picker; single-file per submit; MIME validation `application/zip` only.
- Size cap: 50 MB (MFP export ZIPs are typically 1-10 MB but include historical data).
- Server unpacks the ZIP, identifies the meal-nutrition CSV (name pattern per MFP export convention — verify against Adam + Tai's actual exports in the fixture set §1.6).
- Parses CSV row-by-row into `mfp_food_entries` with `source='csv_upload'` and `ingested_via='csv_upload'`. Recomputes `mfp_daily_rollups`.
- Response redirects to `/me/food/{upload_id}` — a review surface showing per-day rollups and per-meal-slot rows for the last 14 days from the upload. **No "confirm" gate here** — CSV is a first-party MFP export, not an LLM-parsed artifact; we trust the values as-shipped. (This is different from Function Health's confirm-before-coach-reads gate — that gate exists because LLM extraction can hallucinate. CSV parsing can't.)
- Ingestion trace: `ingestion.source=csv_upload`.

### 1.4 Domain + persistence

Add two tables. From research §6.1:

```sql
CREATE TABLE mfp_food_entries (
    id                     uuid PRIMARY KEY,
    user_id                uuid NOT NULL REFERENCES users(id) ON DELETE CASCADE,
    external_connection_id uuid REFERENCES external_connections(id) ON DELETE SET NULL,
    source                 text NOT NULL,           -- 'healthkit_ios' / 'csv_upload'
    meal_date              date NOT NULL,
    meal_slot              text NOT NULL,           -- 'breakfast' / 'lunch' / 'dinner' / 'snack' / 'other'
    logged_at              timestamptz,
    calories               numeric,
    protein_g              numeric,
    carbs_g                numeric,
    fat_g                  numeric,
    fiber_g                numeric,
    sugar_g                numeric,
    sodium_mg              numeric,
    food_items             jsonb NOT NULL DEFAULT '[]'::jsonb,  -- Populated from CSV path only; empty from HealthKit
    raw_payload            jsonb,                   -- HealthKit sample batch OR CSV row source
    ingested_via           text NOT NULL,           -- 'ios_observer' / 'ios_reconciliation' / 'csv_upload'
    ingested_at            timestamptz NOT NULL DEFAULT now(),
    trace_id               text
);
CREATE UNIQUE INDEX idx_mfp_food_entries_user_date_slot_source
    ON mfp_food_entries(user_id, meal_date, meal_slot, source);
CREATE INDEX idx_mfp_food_entries_user_date
    ON mfp_food_entries(user_id, meal_date DESC);

CREATE TABLE mfp_daily_rollups (
    id                uuid PRIMARY KEY,
    user_id           uuid NOT NULL REFERENCES users(id) ON DELETE CASCADE,
    rollup_date       date NOT NULL,
    calories          numeric,
    protein_g         numeric,
    carbs_g           numeric,
    fat_g             numeric,
    fiber_g           numeric,
    meals_logged      int NOT NULL DEFAULT 0,
    updated_at        timestamptz NOT NULL DEFAULT now()
);
CREATE UNIQUE INDEX idx_mfp_daily_rollups_user_date
    ON mfp_daily_rollups(user_id, rollup_date);
```

**Uniqueness key note.** The `mfp_food_entries` unique index includes `source` — HealthKit and CSV paths CAN both write a row for the same `(user, meal_date, meal_slot)`. That's a feature: HealthKit gives freshness, CSV gives historical backfill / meal-slot verification. `mfp_daily_rollups` rollup dedupes across both sources (prefer HealthKit for recent days, CSV for backfill).

**Cascade contract** per research §6.2: user delete cascades everything; no cross-table cascade (rollups are derived and recomputed on each ingest).

### 1.5 Coach input-window extension

- `LiveDailyAgentService`: append two sections to the input snapshot JSON:
  - `latest_food_entries`: last 3 days of `mfp_food_entries` — `{meal_date, meal_slot, calories, protein_g, carbs_g, fat_g, fiber_g}`. **No `food_items` field** — food names never leave our backend.
  - `daily_macro_rollups`: last 7 days of `mfp_daily_rollups` — same field shape as the entries but per-day.
- Cap: last 21 meal-slot rows total (7 days × 3 slots typical). No paging.
- If no MFP data exists for a user, both sections are omitted from the snapshot (not sent as empty arrays — the coach shouldn't reason about "the user has zero meals" when the truth is "the user hasn't onboarded MFP yet").

### 1.6 Bounds validator changes (code, not schema)

- `AgentActionCategory.MealTiming` — already exists in bounds. No validator change; it just gets more real signal.
- `AgentActionCategory.Macros` — already exists. Same.
- `AgentActionSource.UserDataInformed` — already exists. Actions grounded in MFP data use this source.
- **No new bounds category, no new source enum.** MFP integration doesn't create new coach categories; it feeds two existing ones with real data instead of protocol-based inference.

### 1.7 Fixture set for testing

Mirrors the Function Health three-PDF fixture pattern:

- **Two MFP data-export ZIPs** — one from Adam, one from Tai (the third internal user's export TBD by that user; not blocking).
- **Two HealthKit sample batches** — recorded during the §1.1 on-device spike, one covering a full day of Adam's MFP logging, one covering a full day of Tai's.
- **Golden-output JSON** for each fixture: what the resulting `mfp_food_entries` and `mfp_daily_rollups` rows should look like, hand-verified against what the fixture actually contains.
- **Integration tests:**
  - CSV parser against both ZIP fixtures — asserts rows match golden output within tolerance (numeric values exact, timestamps to the minute).
  - HealthKit ingest endpoint against both sample-batch fixtures — asserts idempotency (re-posting the same batch is a no-op), asserts meal-slot metadata extraction, asserts rollup recompute correctness.
  - Coach input-window builder — asserts the last-3-days + last-7-days shape with a mixed CSV + HealthKit dataset.
- **This test suite is the acceptance gate for Phase 1.** Without it, we can't trust that the ingest pipeline is preserving meal-slot fidelity end-to-end.

### 1.8 Privacy doc updates

From research §7. Apply verbatim; Tai's signoff is a hard gate for shipping.

- **Section D.1 addition** (wording in research §7.1): add the MyFitnessPal-data bullet describing exactly what gets sent to Anthropic (rollups + macronutrient totals + meal_slot, no food-item names).
- **Section D.2 reinforcement** (wording in research §7.2): add the explicit "food item names, brands, restaurant identifiers do NOT get sent" bullet.
- **New Section E** — subsection covering the iOS companion:
  - The companion's role (client of our backend + reader of HealthKit).
  - HealthKit permission scopes we request.
  - Local storage on device: Keychain-stored Entra token, no cached food data.
  - Deletion posture: HealthKit permission revocation and app uninstall both stop future ingestion; existing `mfp_food_entries` / `mfp_daily_rollups` are user-deletable via the existing account-delete flow.

### 1.9 Exit criteria for Phase 1

- [ ] `dotnet build`, `dotnet test` green
- [ ] Migration applies cleanly to dev DB
- [ ] Session-1 spike completed: exact `HKMetadata` key MFP uses for meal-slot marking documented in a comment on `HealthKitIngestHandler` OR fallback plan (timestamp-proximity meal-slot inference, CSV-first for meal fidelity) documented in an ADR
- [ ] iOS companion in Adam's TestFlight, HealthKit permission granted, MFP writes flowing through to `mfp_food_entries` for a fresh meal Adam logs
- [ ] `/me/food` accepts a real MFP export ZIP and parses it into `mfp_food_entries` matching the golden fixture
- [ ] Two-user fixture set runs green in integration tests
- [ ] Coach card generated for a user with MFP data references the correct macro totals or meal-slot timing from the input snapshot
- [ ] Bounds validator behavior spot-checked: MealTiming action citing MFP data has `Source=UserDataInformed`
- [ ] Privacy doc Section D.1/D.2/E updates in the repo AND Tai has signed off in writing
- [ ] `/admin/agent` surfaces the `latest_food_entries` and `daily_macro_rollups` sections of the input snapshot for any invocation that referenced MFP data — makes it possible to review outputs for signal-fidelity issues
- [ ] `phase-1-scope.md` amended OR this session confirmed as Phase 2 (see Scope-conflict flag at top)

## Phase 1 is **NOT** in scope of the following

- Aggregator integrations (Terra, Junction). Explicitly rejected per research §4 comparison chart.
- Strava, Lumen, cycle-phase, Apple Watch native ingestion. These use the same iOS companion foundation and land in later Track D sessions.
- Android via Health Connect. Same architecture, different SDK. All three internal users are on iOS per Adam.
- App Store review / production release. TestFlight only for this session.
- Historical trend visualization on `/me/food`. Review surface just shows the last 14 days from the most recent CSV upload; trend charts deferred.
- Any user-facing "you're on track / behind" language on `/me/food` — that's the coach's job.
- Coach language changes / persona edits. The bounds category + source enum already exist; the coach voice doc doesn't change.
- Any change to WHOOP ingestion, even though WHOOP also writes to HealthKit. We continue to use the WHOOP direct API as the authoritative source; HealthKit-provided WHOOP data (if we start reading it later) becomes a cross-check, not a replacement.
- Container Apps Job for reconciliation of missed HealthKit ingest. The `HKAnchoredObjectQuery` anchor pattern on the iOS side handles gap-filling on the client. Server-side reconciliation deferred until we see a real miss.

---

## Phase 2 — aggregator path (contingent, may never ship)

Documented for continuity so Phase 1's schema doesn't paint us into a corner. **Do not implement in this session.**

### 2.1 Trigger conditions

Aggregator path ships only if one of these becomes true:
- Coach behavior evolves to require sub-daily latency (a real-time "you're about to break your fasting window" nudge) that HealthKit background delivery + daily-card cadence can't cover.
- User count crosses ~50, at which point Terra or Junction per-MAU pricing beats the floor and operational-complexity math shifts.
- Session-1 spike reveals MFP's HK meal-slot marking is unreadable AND the CSV path can't cover backfill for a fast-growing user base.

### 2.2 Approach when triggered

- Between Terra ($499/mo floor, stable) and Junction ($300/mo floor, MFP integration flagged deprecated): direct-vendor conversation with Junction confirming MFP integration status before commitment. If Junction confirms stability, cheaper choice wins. If not, Terra.
- Schema: reuse `mfp_food_entries` with `source='terra'` or `source='junction'`. One new row in `external_connections`. Two new secrets in Key Vault (`{vendor}-dev-api-key`, `{vendor}-signing-secret`). One Bicep change to bind them.
- Privacy: Section G addition covering the aggregator as a processor. Junction requires an additional credential-share disclosure.
- Coach input-window: no changes — aggregator data lands in the same tables the coach already reads.

### 2.3 Not-a-code-problem gate

Cannot start Phase 2 without Adam explicitly approving the $300-$500/mo line item. Aggregator vendor conversation happens before the session, not during.

---

## Reference material

- **[`docs/seeds/myfitnesspal-integration-research.md`](seeds/myfitnesspal-integration-research.md).** The research pass this session brief was promoted from. Contains the full comparison chart, aggregator disqualification rationale, all source citations.
- **[`docs/seeds/myfitnesspal-integration.md`](seeds/myfitnesspal-integration.md).** The original seed from Tai's 2026-06-28 feedback. Kept for provenance.
- **[`docs/agent-voice-and-persona.md`](agent-voice-and-persona.md) + [`docs/agent-bounds.md`](agent-bounds.md).** Voice / bounds. The `MealTiming` and `Macros` categories in bounds are what this session unblocks with real signal.
- **[`docs/decisions/0006-three-layer-whoop-ingestion.md`](decisions/0006-three-layer-whoop-ingestion.md).** Ingestion pattern reference. This session's `HKObserverQuery` + `HKAnchoredObjectQuery` pair is the HealthKit equivalent of webhook + reconciliation-poller (on-device rather than server-side).
- **[`docs/decisions/0011-ingestion-trace-contract.md`](decisions/0011-ingestion-trace-contract.md).** Trace contract. The `/api/ingest/healthkit` endpoint MUST emit traces matching this shape.
- **[`docs/decisions/0012-llm-card-before-rules-engine.md`](decisions/0012-llm-card-before-rules-engine.md).** The overall LLM-first architecture that made `MealTiming` and `Macros` bounds categories worth grounding in real signal.
- **[`docs/privacy-data-handling.md`](privacy-data-handling.md).** The doc §1.8 revises. Tai signs off before ship.
- **[`docs/session-function-health-integration.md`](session-function-health-integration.md).** The prior Track D session brief. This session mirrors its structure; the fixture-set + exit-criteria patterns are directly borrowed.
- **[`docs/phase-1-scope.md`](phase-1-scope.md).** The scope doc that currently excludes mobile-app work. See the scope-conflict flag at top of this doc.
