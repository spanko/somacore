# Seed: MyFitnessPal integration (via Apple Health bridge)

**Priority.** Second of Tai's four data-source seeds (2026-06-28). Delivers nutrition tracking to the coach, which unlocks the `meal_timing` and `macros` bounds categories with real signal instead of the coach guessing at defaults.

**Status.** → promoted to [`docs/session-myfitnesspal-integration.md`](../session-myfitnesspal-integration.md) (2026-07-01). Research pass: [`myfitnesspal-integration-research.md`](myfitnesspal-integration-research.md). Note: the "via Apple Health bridge" framing below was researched and inverted — the bridge requires our own iOS app, which Adam greenlit 2026-07-01 (two native apps; Flutter off the table).

**Constraint that reshapes everything.** Tai specifically said "MyFitnessPal food journal **via Apple Health bridge**." Apple Health is iOS-only, and we currently have no mobile client — dev is web + Container Apps. This seed's first job is to say whether the Apple Health path is actually viable on our current architecture, and if not, what the alternatives are.

---

## Why now

The coach's current output includes prescriptive fueling instructions ("Pre-workout: 45g protein, 60g carbs — 90 minutes before your session"). Today those numbers are protocol-based defaults inferred from WHOOP recovery + workout data. They are not tied to what Adam or Tai actually ate. MyFitnessPal is the industry-standard food journal Tai already uses; sourcing her intake makes the coach's macro guidance real instead of aspirational.

## Deliverables the research pass has to produce

1. **Whether the Apple Health bridge is actually viable for us right now.** Apple Health is a HealthKit surface — iOS-only, requires user grant on an iPhone. The bridge exports MyFitnessPal food entries into HealthKit; from HealthKit outward we'd need a receiving surface. Options include:
   - Build an iOS companion app that reads HealthKit and posts to our API. Big lift.
   - Use a third-party aggregator that already reads HealthKit and offers a server-side API (Terra, Vital, Rook, Human API). Moderate lift, adds a vendor.
   - Skip Apple Health entirely and use MyFitnessPal's direct API if one exists.
   - Skip both and use CSV/JSON export from MyFitnessPal's web export.
   Rank by lift, fidelity, and vendor exposure.
2. **Engineering lift** for the top ranked option and one runner-up. Ballpark in Track A session units.
3. **Coach unlock story.** Concrete example outputs that use meal-timing data: "You logged 32g protein at your last meal; you're at 68g total against a 140g daily target, so today's post-workout window closes at 8pm." Show what the card looks like different from today.
4. **Data model.** How do food entries land? Per-entry granularity (each meal), per-day rollups, or both? What's the natural key? What retention window? Nutrition data reveals eating patterns — Tai's lawyer hat will want a specific answer on retention and deletion cascades.
5. **Privacy delta.** Food journal data is not clinical but IS behavioral. Privacy doc section D covers Anthropic data flow — what parts of the intake do we send to the model, and what parts stay server-side only? A macro rollup is very different from raw meal logs.

## Known context

- ADR 0011 trace contract: new `ingestion.source=myfitnesspal` root if this ships.
- ADR 0006 (three-layer ingestion) is the reference shape but doesn't necessarily apply — food entries may only be pull-based, no webhook path.
- `AgentActionCategory.MealTiming` and `AgentActionCategory.Macros` already exist. Validator doesn't need changes.
- No iOS client. Anyone who says "just use HealthKit" needs to answer where the HealthKit-reading code runs.
- We are three-user internal for phase 1. Building an iOS app for three people to get their MFP data is a bad ratio; a manual CSV upload is not.

## Open questions the research pass needs to answer

- **Is MyFitnessPal's public developer API still open?** It has been closed to new partners for years. If so, the CSV export or a third-party aggregator becomes the answer by elimination.
- What does an MFP CSV export actually contain — per-entry rows, or day rollups? What fields are populated?
- Do the third-party aggregators (Terra, Vital, Rook, Human API) actually surface MFP data, or only workout + wearable data? Their marketing is optimistic; the actual coverage per-vendor per-source varies.
- If we go the aggregator path, what's the ongoing cost per user per month?
- Can MFP data be **partially useful without meal-time timestamps**? Some exports strip time-of-day. Coach output depends heavily on timing.
- Is there **an obvious "just have Tai email me her weekly export" MVP** we're overthinking around? For three users, a manual upload endpoint may beat a full integration.

## What this seed is NOT asking for

- iOS app scoping. If the research pass concludes iOS is required, the answer is "not now, revisit when we have a mobile roadmap."
- Nutrition-science decisions about macro targets — that's the rules engine's job.
- Integration with any specific meal-planning system — this is intake tracking, not prescription.
