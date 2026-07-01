# Lumen integration — research pass

**Status.** Research pass, 2026-07-01. Answers every open question in [`lumen-integration.md`](lumen-integration.md) against the baseline framework Adam ratified 2026-07-01.

**Verdict.** NOT VIABLE at phase 1. Defer with an opportunistic HealthKit spike attached to the MFP iOS-companion build.

**Author.** Claude, with primary-source verification.

**Date.** 2026-07-01.

**Rebuttable.** Every factual claim carries a URL in the Verification appendix.

**Verified.** 2026-07-01. Load-bearing claims re-verified against primary sources: Lumen (metabolism co.) has no public developer API; Lumen READS from HealthKit (sleep, steps, weight, workouts) but its write direction is not publicly documented; no self-serve partnership program; ownership is separate from Lumen Technologies (the network telecom company that DOES have public APIs but is a completely different entity).

---

## 0. Framework the seed answers to

Same as MFP and Strava: baseline (iOS companion reading HealthKit + CSV upload) is the floor; direct alternative only ships if it clears the ~2x-operational-complexity budget on net benefit. Each seed produces one comparison-chart row.

**Lumen is the seed the framework can't rescue.** Every path is blocked at the vendor level.

---

## 1. Executive summary (30-second read)

**No path lands data from Lumen (the metabolism company) into SomaCore in a form the coach can reason about — today.** The seed itself anticipated this outcome ("this is the seed most likely to hit 'not viable, defer.'").

Three paths, each blocked:

- **Direct API.** Lumen has no public developer API. Their partnership program URL (`lumen.me/partners`) 301-redirects to a broken 404 page (`lumen.me/health-professionalss` — note the trailing typo, likely a Lumen-side error). No documented developer signup exists. Their B2B partnership channel is enterprise-only and uncertainly-timed.
- **HealthKit baseline.** Lumen READS from HealthKit (sleep, steps, weight, workouts) to feed its own algorithm — verified in multiple reviews and Lumen's own support articles. Lumen's WRITE direction into HealthKit — specifically whether it writes the Lumen Level fuel score or Flex Score with timestamps — is NOT publicly documented. Reviews describe "integration with Apple Health" without specifying the direction. There's no primary source I could find that says "Lumen writes X data type Y to HealthKit."
- **CSV / manual export.** Lumen's app does not document a data-export feature. Users see their reads inside the Lumen app; no built-in path to get the data out.

**Recommendation:** Defer the seed. Attach a **~10-minute opportunistic HealthKit spike** to the MFP iOS-companion build session — while we're on-device reading MFP writes, we ask Adam (who has Lumen personally) to log a Lumen read and check whether it surfaces in HealthKit. If yes, we get Lumen fuel-score data for near-free. If no, we're back to "not viable" and revisit only if Lumen opens a partner program.

This is a **~30-line** watchlist item, not a session.

---

## 2. Lumen public developer API — status

### 2.1 No public API exists

- `lumen.me/developer` returns 404. Verified via direct WebFetch 2026-07-01.
- `lumen.me/partners` 301-redirects to `lumen.me/health-professionalss` (typo in Lumen's redirect config; also 404s).
- No developer sandbox, no signup form, no rate-limit docs, no OAuth flow published.
- Google's index shows no `developer.lumen.me` subdomain for the metabolism product.

### 2.2 Not to be confused with Lumen Technologies

Search results for "Lumen API" surface `docs.lumen.com` and `developer.lumen.com` — those belong to **Lumen Technologies** (the network/telecom company, formerly CenturyLink). It's a completely separate company with a mature developer API (Lumen Connect, Control Center APIs, network provisioning). This is not the metabolism product.

Do not confuse the two when reading Adam or Tai's future references.

### 2.3 Partnership program — enterprise-scoped, no self-serve

Lumen (metabolism) markets partnership on the App Store listing (*"we are interested in partnerships and can be reached through www.lumen.me/partners"*) but the URL is broken as of 2026-07-01. No documented partnership tier or API access exists for third-party health apps.

Adam can email their partnership team as a business-dev side-track. Realistic timeline: weeks-to-months if they respond, likely a co-marketing conversation rather than API access.

---

## 3. Lumen ↔ HealthKit — direction is asymmetric

### 3.1 Confirmed: Lumen READS from HealthKit

Multiple primary and secondary sources describe Lumen's Apple Health integration:

- Lumen's own support article at `help.lumen.me/s/article/Apple-health` (blocked to WebFetch, but Google indexes and third-party reviews describe the flow).
- Innerbody review (2026): *"Lumen pulls in data from Apple Health for metrics like sleep and steps so those fields are pre-populated for you."*
- 9to5Mac review: describes Apple Health integration where Lumen consumes user weight, workouts, and sleep for its recommendations.
- Lumen's own marketing describes "tight Apple Health integration to give you the personalized information you need about how your body is using energy."

**Verified read direction:** weight, BMI, height, sleep, steps, workouts, calories (Apple Watch native or third-party writes). Lumen uses these to feed its own carb/fat targeting algorithm.

### 3.2 Unconfirmed: whether Lumen WRITES metabolic data back to HealthKit

**No primary source I could find explicitly documents Lumen writing its Lumen Level, Flex Score, or per-read fuel-usage samples to HealthKit.** The word "integration" appears in reviews without direction specification.

Two hypotheses, each testable on-device during the MFP iOS-companion spike:

- **Hypothesis A: Lumen does write fuel-score data.** Would appear as an `HKQuantityType` (custom or one of Apple's metabolic types like `HKQuantityTypeIdentifierBasalEnergyBurned` extended with metadata). Would show `sourceName="Lumen"` or bundle ID `com.metaflow.lumen`.
- **Hypothesis B: Lumen only reads.** No `sourceName=Lumen` records exist in HealthKit; Lumen keeps its fuel-usage timeline in its own app + backend only.

The App Store listing is `com.metaflow.lumen` (verified).

### 3.3 What the on-device spike would look like

During the MFP iOS-companion session (Track D Session 2), while we're already in Xcode with HealthKit permissions granted:

1. Adam takes a Lumen breath reading in the Lumen app.
2. We run a debug `HKSampleQuery` with predicate `HKQuery.predicateForObjects(withSourceBundleIdentifier: "com.metaflow.lumen")`.
3. Print any results to console.
4. If results appear: document the sample types + metadata keys, and we can extend the companion to ingest them into a `lumen_reads` table (schema TBD).
5. If no results: hypothesis B holds. Lumen data does not flow through HealthKit. Seed remains "not viable" until Lumen changes their integration or opens an API.

Total investigation time: ~10 minutes tacked onto an existing session. No dedicated session cost.

---

## 4. Comparison chart — the deliverable (every path fails)

### 4.1 Chart

| Dimension | **Baseline (iOS + CSV)** | **Direct alternative** | **Any path?** |
|---|---|---|---|
| Path exists | UNCONFIRMED — Lumen's HealthKit write direction not publicly documented; requires on-device spike to verify | NO — no public developer API, no partnership signup | NO viable direct path today |
| Granularity (if path existed) | Whatever Lumen chooses to write to HealthKit; unknown until we check on-device | Would depend on hypothetical API terms | N/A |
| Latency | HealthKit background delivery when Lumen writes | Real-time API push (hypothetical) | N/A |
| Access risk | LOW substrate; HIGH vendor (Lumen has never had a public API, unstable posture) | HIGH — no committed API, subject to vendor whim | N/A |
| User onboarding friction | Install iOS app + grant HealthKit (already done for MFP) — no separate Lumen consent | Would require Lumen OAuth (hypothetical) | N/A |
| Operational complexity for us | Reuse existing iOS companion — near-zero marginal cost IF hypothesis A holds | Would require net-new OAuth flow + webhook + poller | N/A |
| Cost floor | $0 | Unknown — Lumen's partnership terms not published | N/A |
| Coverage multiplier | iOS companion amortized | 1x for Lumen | N/A |
| Consent + compliance shape | No processor — Lumen data flows via Lumen → HealthKit → our companion (on-device only) → our backend | Would add Lumen as a processor | N/A |

### 4.2 Verdict — DEFER

Every path is blocked at the vendor level. The seed's original prediction stands.

**Two low-cost actions attach to the defer:**

1. **On-device HealthKit spike** during MFP session. ~10 min. Determines whether hypothesis A (Lumen writes fuel-score to HealthKit) holds. If yes, seed converts to a small extension of the MFP session. If no, defer stays deferred.
2. **Partnership-team email** from Adam. ~0 min for us; unknown timeline for their response. Side-track only.

---

## 5. Watchlist triggers — when to re-evaluate

Re-open this seed if any of the following happens:

- **Lumen opens a public developer program.** Watch `lumen.me/developer`, Lumen's Community Hub, or a press announcement.
- **Lumen partners with an aggregator we could go through.** Terra, Junction, Rook — none list Lumen as of 2026-07-01. If any starts, direct integration becomes cheaper.
- **The MFP-session on-device spike surfaces `sourceName=Lumen` HealthKit records.** Hypothesis A confirmed → convert this seed into a small ingestion pattern reusing the MFP iOS companion.
- **Adam's partnership-outreach email gets a substantive reply.** Even a "we're piloting API access with select partners" invitation is worth acting on.

Until one of the above happens, this seed stays a defer.

---

## 6. Answers to every open question in the seed

### 6.1 "Is Lumen integration possible at all?"

**Not today for third-party developers.** No public API, no self-serve partnership tier, no documented data-export path. Enterprise partnership is theoretical.

### 6.2 "If open: same four-part answer as other seeds."

Not applicable — not open.

### 6.3 "If closed but exportable — what does a Lumen data export look like?"

Not documented. Lumen's app does not appear to expose an export path per Google's index of their help center + App Store listing. Users can screenshot reads or reference them inside the app; no timestamped bulk export.

### 6.4 "Coach unlock story."

**Deferred pending path viability.** If the on-device spike surfaces Lumen HealthKit writes, sketch would be:
> "Your morning Lumen read at 7:42am showed fat-burn (Lumen Level 2). Extend your fasting window another 90 minutes — target break at 10:15am — to consolidate the fat-oxidation state before your first meal."

### 6.5 "Privacy delta."

**Deferred pending path viability.** If Lumen data flows in, add a Section D.1 addition covering: `lumen_level` (1-5 scale), timestamp of read, and Flex Score summary. NO raw CO2 % values, NO breath-analysis metadata beyond the score. Similar posture to WHOOP recovery: send the score, not the raw physiological signal.

### 6.6 "Developer program now?"

**No.** Confirmed via `lumen.me/developer` returning 404 and `lumen.me/partners` redirecting to a broken URL.

### 6.7 "HealthKit bridge exists?"

**Read direction confirmed; write direction unconfirmed.** See §3.

### 6.8 "Third-party aggregators — Terra, Junction, Rook, Human API?"

None list Lumen as of the aggregator research done in [`myfitnesspal-integration-research.md`](myfitnesspal-integration-research.md). Human API is dead per that same research. If any aggregator adds Lumen, revisit.

### 6.9 "Raw signal shape?"

Lumen shows a per-read "Lumen Level" (1-5, where 1=deep fat-burn and 5=deep carb-burn) and an aggregate Flex Score (0-21, higher = more metabolically flexible). No publicly documented access to the underlying RQ or CO2 % values.

### 6.10 "Read frequency?"

Reviews describe Lumen as designed for multiple reads per day (morning fasted, pre-workout, post-workout, pre-meal, post-meal). Actual per-user cadence varies. For coach reasoning, the fasted morning read is the highest-value signal (informs fueling recommendations for the day).

---

## 7. Recommended path forward

**Do not schedule a Lumen session.**

**Attach two low-cost items:**

1. **In the MFP iOS-companion session (Track D Session 2):** During the on-device HK-metadata spike (§1.1 of `session-myfitnesspal-integration.md`), also run:
   ```swift
   let lumenPredicate = HKQuery.predicateForObjects(withSourceBundleIdentifier: "com.metaflow.lumen")
   // Query all quantity + category types with this predicate, log to console
   ```
   Adam takes a Lumen reading immediately before running the app to force a fresh write. Total marginal time: ~10 min.
2. **Adam sends a partnership-outreach email** to Lumen at whatever their current partner contact is (their site is broken, may need to email a generic support address). Zero blocking; response is bonus.

**If the spike surfaces `sourceName=com.metaflow.lumen` records:** promote this seed to a small extension of the MFP session (adds a `lumen_reads` table + iOS companion filter). Estimate: ~0.25 additional Track A session.

**If the spike surfaces nothing:** keep this doc as the record of what we investigated, and the seed stays deferred with the watchlist in §5 active.

---

## 8. Verification appendix — sources

### Lumen (the metabolism company) — no public API

- **`lumen.me/developer` returns 404.** — Direct WebFetch attempt 2026-07-01.
- **`lumen.me/partners` 301-redirects to `lumen.me/health-professionalss` (which 404s).** — Direct WebFetch attempt 2026-07-01. Typo in Lumen's redirect chain; confirmed no live partnership landing page.
- **App Store listing bundle ID: `com.metaflow.lumen`.** — https://apps.apple.com/us/app/lumen-metabolic-coach/id1395149502

### Lumen ↔ HealthKit — read direction confirmed

- **Lumen support article — Apple Health.** — https://help.lumen.me/s/article/Apple-health (page returns a CSS error to WebFetch; content sourced from Google's index and third-party reviews)
- **Innerbody review (2026) — Lumen pulls sleep + steps from Apple Health.** — https://www.innerbody.com/lumen-review
- **9to5Mac 2020 review — iOS integration + food log.** — https://9to5mac.com/2022/03/02/lumens-metabolic-analyzer-ios-app-gets-food-log/
- **Wareable — Lumen + Apple Watch review, describes Apple Health data flow.** — https://www.wareable.com/wearable-tech/lumen-apple-watch-app-review-8554

### NOT LUMEN — different company, DO NOT CONFUSE

- **Lumen Technologies (telecom) — has public developer APIs; irrelevant to metabolism product.** — https://developer.lumen.com/, https://docs.lumen.com/lumen-connect/apis/

### Sources deliberately NOT used

- Marketing pages that describe "Apple Health integration" without specifying direction — flagged inconclusive.
- Aggregator marketing (Terra, Junction, Rook) — none list Lumen; confirmed via [`myfitnesspal-integration-research.md`](myfitnesspal-integration-research.md) aggregator sweep.

---

## Change log

- **2026-07-01 (initial):** Research pass conducted directly in main-loop context with primary-source verification. Verdict is NOT VIABLE / DEFER, matching the seed's original expectation. Load-bearing new nuance: Lumen READS from HealthKit reliably, but WRITE direction is not publicly documented. Recommendation: opportunistic ~10-min HK spike during MFP iOS-companion session; if it surfaces Lumen writes, seed converts to a small extension. If not, defer stays deferred with the §5 watchlist active.
