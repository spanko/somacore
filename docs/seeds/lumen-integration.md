# Seed: Lumen integration

**Priority.** Fourth of Tai's four data-source seeds (2026-06-28). Sequenced last because Lumen historically has the most closed data model of the four sources; this seed's first job is to confirm whether integration is even possible before we invest in it.

**Status.** Research pass complete — verdict DEFER (2026-07-01), not promoted. See [`lumen-integration-research.md`](lumen-integration-research.md): no public API, no export path, HealthKit write-direction unconfirmed. A ~10-min on-device spike rides along with the MFP iOS-companion session; watchlist triggers in the research doc §5.

---

## Why now

Lumen measures metabolic fuel usage (fat vs. carb) via breath analysis several times per day. It's the only source in Tai's list that reads *substrate utilization* directly — everything else (WHOOP, Strava, MFP) reads energy expenditure or intake, not what the body is actually burning at the moment. If we can pull it in, the coach gains:

- Fasted vs. fed metabolic state — "you woke up burning fat; here's how to keep that window open"
- Post-meal metabolic recovery — "your last meal spiked carb-burning for 3 hours; time the next meal to protect the fasting window"
- Ketosis / low-carb adherence signal for users pursuing metabolic goals

Tai listed Lumen because she uses it herself; it slots into the coach's `meal_timing` and `caffeine_timing` categories in a way the other sources can't.

## Deliverables the research pass has to produce

1. **Is Lumen integration possible at all?** Their historical posture has been closed — no public developer API for consumer users. Confirm current state. If closed, the seed ends there with a clean "not now, revisit if Lumen opens a partner program."
2. **If open:** the same four-part answer as the other seeds — sequencing, engineering lift, coach unlock story, data model. Same structure as [strava-integration.md](strava-integration.md).
3. **If closed but exportable:** what does a Lumen data export look like? Are reads timestamped? Can the user pull a CSV/JSON from their app? Manual upload + parse is a valid fallback.
4. **Coach unlock story.** Concrete example outputs that use metabolic-state data. "You logged a fat-burn read this morning; extend your fasting window another 90 minutes to consolidate before your first meal" — show the difference vs. today's inferred fueling guidance.
5. **Privacy delta.** Metabolic reads are behavioral/health-adjacent. Privacy doc section D commitments need to be extended if this ships. Draft.

## Known context

- Lumen sells a device + subscription; historically no public API. Enterprise/research partnerships exist.
- ADR 0011 trace contract: new `ingestion.source=lumen` root if it ships.
- ADR 0006 (three-layer ingestion) may not apply — probably no webhook path from a consumer app.
- Coach category `MealTiming` covers fueling; a Lumen read informs that category directly.
- No existing precedent in our codebase for closed-API integrations. This is the seed most likely to hit "not viable, defer."

## Open questions the research pass needs to answer

- **Is there a Lumen developer program now?** Their product has matured; they may have opened partner integrations. Check current state, not old blog posts.
- **HealthKit bridge exists?** Lumen writes some data to Apple Health for iOS users. Same caveats as MyFitnessPal — we have no iOS client, so a HealthKit-only answer routes to the same aggregator conversation.
- **Third-party aggregators** — do Terra, Vital, Rook, or Human API surface Lumen reads? If yes, this seed collapses to "same integration you'd do for MFP."
- **What does the raw signal look like?** Lumen's app shows a fuel-usage percentage per read. Is that the API-level payload, or do they publish the underlying RQ / VO2 values?
- **Frequency** — how often does a user typically log a read? Once, twice, four times per day? The input snapshot window for the coach depends on this.

## What this seed is NOT asking for

- Speculative "if Lumen opened its API we would..." plans. Answer needs to be based on today's state.
- Any change to the coach's existing metabolic-adjacent reasoning. Today's card infers fueling from workout + recovery data; Lumen supplements, doesn't replace.
- The clinical validity discussion of breath-based metabolic measurement. That's a science conversation Tai will have separately. Ingestion assumes Lumen's read is what the user acts on.

## Note on deprioritization

If this seed's research pass concludes "not viable," that's a fine outcome. Tai listed Lumen as an item on a wish list, not as a blocking dependency. Better to close it out cleanly than to hold the seed open indefinitely.
