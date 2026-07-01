# Track D — data-source seeds decision pack

**For.** Adam + Tai review, 2026-07-02 meeting.
**Date.** 2026-07-01.
**Author.** Claude, working from Tai's 2026-06-28 feedback + Adam's 2026-07-01 framework decisions.

---

## TL;DR

Five seeds researched, verified against primary sources, and landed as either **session briefs ready to ship** or **research passes with an explicit next-step**. Three sessions ready (Function Health, MFP, Strava). Two deferred with clear paths back (Lumen, protein-personalization).

**Total marginal cost across the three shippable sessions:** ~5.5 Track A sessions of engineering + $144/year (Strava developer subscription) + Tai's privacy signoff on three separate additions.

**The framework decision Adam ratified 2026-07-01** — baseline is a native iOS companion (HealthKit) + CSV upload for non-iOS users; per-source direct integrations only ship if they clear a ~2x operational-complexity budget on net benefit — reshapes every seed. Comparison chart per seed makes the trade-off legible.

---

## The framework in three sentences

**Baseline:** two native mobile companion apps (iOS Swift/HealthKit and Android Kotlin/Health Connect — Flutter is off the table as of 2026-07-01) + a `/me/*` CSV upload path for web-only users. This is the floor for every non-Function-Health data source. **Direct alternatives** (vendor OAuth, webhooks, aggregators) only ship if the per-source comparison chart shows measurable net benefit that clears roughly 2x the operational complexity of baseline-alone. The **coverage multiplier** — one iOS companion feeds MFP + Strava + Lumen + Apple native + cycle-phase in a single shipment — is the load-bearing dimension when weighing baseline against direct.

Function Health does not participate in this framework because labs aren't a HealthKit data category — its promoted decision (Option A + Option B rendezvous) stands unchanged from 2026-06-30.

---

## Master comparison table

| Seed | Verdict | Path shipped | Vendor cost | Ready to ship? | Load-bearing gate |
|---|---|---|---|---|---|
| **Function Health** | Ship A+B rendezvous | PDF upload + LLM parse (A) + Function MCP polling as drift trigger (B) | $0 | ✅ Session brief promoted (`f414b42`) | Tai signs privacy Section F; three real Function PDFs from Adam/Tai/Greg as fixture set |
| **MyFitnessPal** | Baseline wins | iOS HealthKit reads (meal-slot preserved) + `/me/food` CSV upload | $0 | ✅ Session brief promoted (`65db28c`) | Tai signs privacy Sections D.1/D.2/E; on-device spike confirms MFP's HKMetadata meal-slot key |
| **Strava** | Hybrid (direct + baseline) | Direct Strava API + webhook + poller (WHOOP-shape) + iOS companion for Apple Watch fallback | **$11.99/mo** ($144/yr) | ✅ Session brief promoted (`076f268`) | Adam creates Strava dev account + subscription; Tai signs privacy Section D.1 (no GPS, no polyline) |
| **Lumen** | DEFER — vendor path blocked | Opportunistic ~10-min HealthKit spike attached to MFP session | $0 | ❌ Research only (`aa3f641`) | Adam sends partnership email as side-track; watchlist active |
| **Protein-personalization** | Track B input contract landed | New `SomaCore.Domain.Rules` engine + `/me/profile` surface + HealthKit cycle-phase extension | $0 | ❌ Research only (`cef855e`) — waits on Track B | **Tai authors `docs/rules/protein-target.md`** — hard blocker; and reviews `/me/profile` question wording |

---

## Per-seed detail

### Function Health — ship A+B rendezvous

**Session brief:** [`docs/session-function-health-integration.md`](session-function-health-integration.md)

**Decided.** Phase 1 ships user-uploaded PDF at `/me/labs`, parsed via Anthropic Messages API structured extraction into `lab_biomarkers`, gated behind a "confirm before coach reads" review page. Coach references biomarkers by name + collection date with provenance tagged to `lab_upload_id`. Phase 2 (separate session) layers Function MCP polling to detect category-drift — banner on `/me` prompts user to upload a fresh PDF when their Function-side summary diverges from their last confirmed panel.

**Why A+B rendezvous (not A alone).** Function's MCP server returns category-summary counts, not values. So B alone can't drive `supplements_from_labs`; A alone leaves the coach reading stale values indefinitely. A gives values, B gives freshness triggers. They meet at the "please upload a fresh panel" nudge.

**Coach unlock:** `supplements_from_labs` bounds category — the coach can say *"Vitamin D is 22 ng/mL, low against the 30-100 ng/mL reference — take 2000 IU with your first meal."*

**Gates:** Tai signs privacy doc Section F (new); three real Function PDFs collected as fixture set with golden-output JSON for hallucination testing.

---

### MyFitnessPal — baseline wins

**Session brief:** [`docs/session-myfitnesspal-integration.md`](session-myfitnesspal-integration.md)

**Decided.** iOS companion reads HealthKit for MFP's meal-slot-preserving summaries (breakfast/lunch/dinner/snack markers survive; macros + micros preserved). `/me/food` accepts MFP data-export ZIP for non-iOS users and iOS backfill. Both paths feed the same `mfp_food_entries` + `mfp_daily_rollups` schema. Aggregators (Terra, Junction) rejected on cost floor ($3.6k-$6k/year) + coverage-multiplier math.

**Load-bearing verification** (from MFP's own Apple Health FAQ): *"MyFitnessPal will update Meal Summaries (calories and nutrients) to HealthKit. Additionally, only the default Breakfast, Lunch, Dinner and Snack headers will sync to their associated meal."* → meal-slot fidelity preserved through the HealthKit round-trip, matching what an aggregator would deliver.

**Coach unlock:** `MealTiming` + `Macros` bounds categories — coach references specific macro totals ("68g protein against 140g target — target 45g at next meal") and meal-slot timing ("first meal at 10:45am; extend fasting to 11:15am tomorrow").

**Risk item:** on-device spike (session 1 of iOS build) confirms MFP's HKMetadata meal-slot key. If not readable, meal-slot fidelity degrades to CSV-only.

**Gates:** Tai signs privacy Sections D.1, D.2, and new Section E (iOS companion). No third-party processor to disclose.

---

### Strava — hybrid (direct API + baseline)

**Session brief:** [`docs/session-strava-integration.md`](session-strava-integration.md)

**Decided.** Direct Strava API + webhook + reconciliation poller (mirrors ADR 0006's WHOOP three-layer pattern; ~70% code reuse) as primary source. iOS companion HealthKit reads as secondary source for Apple Watch native workouts and fallback. Dedup rule merges WHOOP + Strava + HealthKit workouts in the coach input-window builder.

**Why hybrid, not baseline-alone.** Strava's HealthKit round-trip is partial (elevation not always passed; only 30 days of Apple Watch data back-syncs; DC Rainmaker documented a 2022 abrupt-end incident). Coach's `TrainingIntensity` and `WorkoutStructure` unlock requires split-level HR + zone accumulation, which lives only in Strava's detailed activity endpoint — not in HealthKit. Aggregators (Terra) explicitly banned by Strava's Nov 2024 agreement update; Terra publicly confirmed being kicked out.

**Coach unlock:** *"Yesterday's tempo run held 158 bpm avg over 42 min — but your splits show you crossed threshold in miles 3-5. That's ~22 min of zone-3 across three sessions this week. Today's plan is 45-min zone-2 endurance: target 138-145 bpm."* — split-level reasoning WHOOP cannot produce.

**Costs and risks:**
- **$11.99/month** Strava developer subscription (Standard tier, effective June 2026).
- **Policy risk MEDIUM.** Strava's API terms restrict *display* to the user themselves; do not explicitly address LLM use for personal coaching. Direction of travel is restrictive. Watchlist item.
- Refresh-token rotation identical to WHOOP; `WhoopAccessTokenCache` race-rescue pattern applies verbatim.

**Gates:** Adam creates Strava developer account + $11.99/mo subscription; adds `strava-client-id` and `strava-client-secret` to Key Vault. Tai signs privacy Section D.1 with strict location commitment (**no GPS coordinates, no polyline route data, no gear ID, no kudos count** sent to Anthropic).

---

### Lumen — DEFER + opportunistic HealthKit spike

**Research doc:** [`docs/seeds/lumen-integration-research.md`](seeds/lumen-integration-research.md)

**Decided.** No path lands data from Lumen into SomaCore today. No public developer API. Partnership URL 301-redirects to a broken page. Lumen READS from HealthKit reliably (sleep, steps, weight, workouts feed its algorithm) but its WRITE direction into HealthKit is **not publicly documented** — reviews describe "Apple Health integration" without specifying direction.

**Attached to MFP session:** ~10-minute on-device spike where Adam takes a Lumen breath reading, then we query HealthKit with `HKQuery.predicateForObjects(withSourceBundleIdentifier: "com.metaflow.lumen")`. If records surface, seed converts to a ~0.25 additional session (small `lumen_reads` extension). If nothing, defer stays deferred with the watchlist active.

**Watchlist triggers for re-evaluation:** Lumen opens a public developer program; Lumen partners with Terra/Junction/Rook; the on-device spike surfaces Lumen HealthKit writes; Adam's partnership-outreach email gets a substantive reply.

**Cost of continuing to hold this seed:** near-zero (10-min spike opportunistic; email is zero-effort side-track).

---

### Protein-personalization — Track B input contract

**Research doc:** [`docs/seeds/protein-personalization-rules-research.md`](seeds/protein-personalization-rules-research.md)

**Decided.** Different in kind from the four data-source seeds. This is an internal-architecture spec — Track B's rules engine input contract. Doc lands the design so Track B Session 1 can pick up from a specified shape rather than re-designing from scratch.

**Design:**
- New `SomaCore.Domain.Rules` project with `IPersonalizedTargetsEngine.Compute(profile, training, forDate) → PersonalizedTargets`.
- `PersonalizedTargets` record with nullable per-target fields (protein, carbs, hydration, caffeine cutoff) and 4-level confidence ladder (High / Medium / Low / Unknown). Coach reads computed number when confidence is Medium+, falls back to population range otherwise.
- New `/me/profile` surface with 4 required fields + 1 optional block (~90 seconds to fill): bodyweight, goal, cycle status, cycle phase.
- HealthKit cycle-phase read preferred over user-declared when recent (< 30 days).
- Hybrid opinionation: Tai-authored `docs/rules/protein-target.md` (YAML) as primary; published-formula midpoint as fall-through.

**Prerequisites:** MFP session + Strava session shipped (both provide signals the engine reasons over). Engine handles nulls gracefully so it can start before both, but output degrades.

**Gates (hard blockers on Track B Session 1):**
- **Tai authors `docs/rules/protein-target.md`.** This is THE gate. Without it, engine falls through to published-formula midpoint.
- Tai reviews `/me/profile` question wording — cycle-related questions are cycle-user-facing language.
- Privacy doc addition for profile + cycle data sent to Anthropic.

---

## Not-a-code-problem gates rolled up

### What Adam owes

1. **Strava developer account creation** + $11.99/mo Standard-tier subscription + Key Vault secrets. Blocker for the Strava session start.
2. **Strava webhook subscription registration** (one-time API call once the webhook endpoint is deployed).
3. **Partnership-outreach email to Lumen.** ~0 cost; response likely months out or no-response.
4. **Partnership-outreach email to MyFitnessPal** (`partners@myfitnesspal.com`). ~0 cost side-track — if unexpectedly positive, could switch off baseline later.
5. **Confirm one internal-user Function Health PDF** for the fixture set. Adam already has Function; needs Tai's + Greg's consent for their PDFs.
6. **Apple Developer account** (already covered — 3 apps in TestFlight per Adam 2026-07-01).

### What Tai owes

1. **Privacy doc Section F (Function Health, new).** Full text drafted in Function Health research doc §6.2. Hard gate on the Function Health session shipping.
2. **Privacy doc Sections D.1, D.2, E (MFP).** Text drafted in MFP session brief §1.8. Hard gate on the MFP session shipping.
3. **Privacy doc Section D.1 (Strava, "no GPS / no polyline" language).** Text drafted in Strava session brief §1.10. Hard gate on the Strava session shipping.
4. **`/me/profile` question wording review** (protein-personalization).
5. **`docs/rules/protein-target.md` authorship** — the g/lb table by goal × cycle-phase. Hard blocker on Track B Session 1.
6. **Consent for using Tai's Function Health PDF** as fixture data. (Tai already opted in on the live agent; her Function PDF is a separate opt-in.)

### What both need to align on

1. **Track D naming convention.** Suggestion: Function Health = Session 1, MFP = Session 2, Strava = Session 3. Confirm or rename.
2. **Sequencing between MFP and Strava.** MFP session builds the iOS companion; Strava session extends it. So MFP ships first. Confirm MFP-first ordering.
3. **Function Health sequencing vs. MFP/Strava.** Function Health doesn't need the iOS companion; can run in parallel with or before MFP. Suggestion: Function Health first (it's the smallest lift and closes Tai's most-eager data-source ask).

---

## Recommended sequencing

**Suggested order:**

1. **Function Health Session 1 (Phase 1: PDF upload + LLM parse).** ~1.5 Track A sessions. Ships `supplements_from_labs`. Zero dependency on iOS companion. Tai's highest-priority ask from 2026-06-28.
2. **MyFitnessPal.** ~2.5 Track A sessions. Builds iOS companion foundation (Swift + HealthKit permission + backend ingest endpoint) that Strava reuses. Includes the Lumen opportunistic spike as a ~10-min tack-on.
3. **Strava.** ~2 Track A sessions marginal. Requires MFP's iOS companion existing. Requires Adam's Strava dev account.
4. **Function Health Session 2 (Phase 2: MCP polling drift trigger).** ~1.5 Track A sessions. Layer on when Adam wants the "please upload fresh panel" nudge to fire without user thinking about it. Not urgent.
5. **Protein-personalization / Track B Session 1.** After MFP + Strava; needs Tai's `protein-target.md` table.

**Total shippable work:** ~7.5 Track A sessions across 5 shipments. Plus $144/year (Strava) and Tai's five gate items.

**Non-blocking side-tracks (fire in parallel with any session):**
- Adam emails Lumen partnership team.
- Adam emails MFP partners@ contact.

---

## Coverage multiplier — why one iOS companion pays for itself

| Source | Reachable via iOS companion (HealthKit)? | Baseline coverage |
|---|---|---|
| **MyFitnessPal** | Yes — meal-slot summaries preserved | Primary path |
| **WHOOP** | Yes (redundant with our direct API path) | Cross-check for silent data-loss |
| **Strava** | Partial — activity envelope, missing splits/zones/elevation | Fallback + Apple Watch native workouts |
| **Lumen** | Unconfirmed pending on-device spike | Opportunistic |
| **Apple Watch native workouts** | Yes | Sole path (Strava/WHOOP don't cover unregistered activities) |
| **Cycle-tracking (any app or Apple native)** | Yes — menstrual flow + ovulation tests | Feeds protein-personalization engine |
| **Bodyweight** (Withings, Renpho, MFP, manual) | Yes | Feeds protein-personalization engine |
| **HRV, VO2max, sleep phases** (Apple Watch) | Yes | Cross-source signal |
| **Function Health** | No — labs aren't a HealthKit category | Doesn't apply |

**Interpretation:** the iOS companion is amortized across 4 seeds directly (MFP, Strava fallback, Lumen, protein-personalization) and provides bonus coverage on 3 more (WHOOP cross-check, Apple Watch, bodyweight). Per-source aggregators would deliver 1x each at $300-$500/mo floors. One-shipment coverage is the load-bearing math.

---

## What's NOT in this pack

**Deliberately deferred:**
- Android companion app (Kotlin + Health Connect). Same shape as iOS; ships when we onboard non-iOS users.
- Function Health direct-DCR client registration. Deferred to Function Health Session 2.
- Aggregator paths (Terra, Junction, Rook, Spike, Human API, Validic). Rejected across all four data sources on cost + coverage-multiplier + Strava's Nov 2024 intermediary ban.
- MFP direct partner API. Closed to new applicants; parallel side-track email only.
- Lumen direct API. Doesn't exist; watchlist item.
- Rules-engine implementation itself. This pack lands the input contract; Track B's actual engine ships in Track B Session 1.
- Carbs, hydration, caffeine-cutoff personalization. Same shape as protein; ships in follow-up Track B sessions.

**Deliberately rejected (and worth naming so we don't relitigate):**
- Reverse-engineered MFP scraping — TOS violation, credential capture.
- Reverse-engineered Lumen access — same reasoning.
- Route/GPS/polyline data flowing to Anthropic — privacy commitment.
- Individual food-item names flowing to Anthropic — privacy commitment.
- Terra aggregator for MFP — $499/mo floor; MFP direct data via baseline covers the coach's needs.
- Junction aggregator for MFP — $300/mo floor + MFP integration flagged deprecated on Junction's own provider matrix.

---

## Reference materials

**Session briefs (ready to ship):**
- [`docs/session-function-health-integration.md`](session-function-health-integration.md)
- [`docs/session-myfitnesspal-integration.md`](session-myfitnesspal-integration.md)
- [`docs/session-strava-integration.md`](session-strava-integration.md)

**Research passes (context and rationale):**
- [`docs/seeds/function-health-integration-research.md`](seeds/function-health-integration-research.md)
- [`docs/seeds/myfitnesspal-integration-research.md`](seeds/myfitnesspal-integration-research.md)
- [`docs/seeds/strava-integration-research.md`](seeds/strava-integration-research.md)
- [`docs/seeds/lumen-integration-research.md`](seeds/lumen-integration-research.md)
- [`docs/seeds/protein-personalization-rules-research.md`](seeds/protein-personalization-rules-research.md)

**Original seeds (Tai's 2026-06-28 feedback):**
- [`docs/seeds/function-health-integration.md`](seeds/function-health-integration.md)
- [`docs/seeds/myfitnesspal-integration.md`](seeds/myfitnesspal-integration.md)
- [`docs/seeds/strava-integration.md`](seeds/strava-integration.md)
- [`docs/seeds/lumen-integration.md`](seeds/lumen-integration.md)
- [`docs/seeds/protein-personalization-rules.md`](seeds/protein-personalization-rules.md)

**Framework + architecture:**
- [`docs/phase-1-scope.md`](phase-1-scope.md) — updated 2026-07-01 to note Flutter permanently off; two native apps direction.
- [`docs/agent-bounds.md`](agent-bounds.md) — Tai-authored bounds; unchanged.
- [`docs/agent-voice-and-persona.md`](agent-voice-and-persona.md) — Tai-authored persona; extended by the protein-personalization design when Track B ships.
- [`docs/decisions/0006-three-layer-whoop-ingestion.md`](decisions/0006-three-layer-whoop-ingestion.md) — mirrored by Strava session's shape.
- [`docs/decisions/0011-ingestion-trace-contract.md`](decisions/0011-ingestion-trace-contract.md) — applies to every new ingestion source.
- [`docs/decisions/0012-llm-card-before-rules-engine.md`](decisions/0012-llm-card-before-rules-engine.md) — the sequencing decision the protein-personalization seed is on-plan for.

---

## Commit history for this pack (chronological)

```
f414b42  session: Function Health integration — promoted from research pass
65db28c  session: MFP integration — promoted from research pass
076f268  session: Strava integration — promoted from research pass
aa3f641  research: Lumen integration — verdict DEFER, no session
cef855e  research: protein-personalization — Track B input contract landed
f9d35c8  docs: two native apps (iOS + Android), not Flutter — Phase 2 direction
```
