# Seed: Function Health integration

**Priority.** Highest of the four data-source seeds Tai raised 2026-06-28. She specifically named this the one she wants working next — it unlocks the `supplements_from_labs` category in `docs/agent-bounds.md` that is currently declared but has no ingestion surface behind it.

**Status.** → promoted to [`docs/session-function-health-integration.md`](../session-function-health-integration.md) (2026-06-30). Research pass: [`function-health-integration-research.md`](function-health-integration-research.md).

---

## Why now

Tai used the live daily card 2026-06-28 and her feedback was positive on voice/persona/reasoning. The one iteration ask on the coach itself was that the protein target (`0.7–1g / lb bodyweight`) is a population range and she wants it personalized — captured in [protein-personalization-rules.md](protein-personalization-rules.md). The bigger ask was data sources: MyFitnessPal, Function Health, Strava, Lumen. Function Health is the one she pushed hardest on:

> Specifically on Function Health — that's the one I'm most eager to move on because it unlocks supplement reminders and food guidance tied to my actual lab results, which is a feature we've already designed and I want to see it working.

The `supplements_from_labs` bounds category in [`docs/agent-bounds.md`](../agent-bounds.md) and the "user-uploaded lab result" example output in [`docs/agent-voice-and-persona.md`](../agent-voice-and-persona.md) both anticipate this. Right now the agent has no way to produce that output honestly because we have no lab data. The refusal guard in [`AgentResponseValidator`](../../src/SomaCore.Infrastructure/Agent/AgentResponseValidator.cs) rejects lab-referenced `source` values because the ingestion surface doesn't exist yet.

## Deliverables the research pass has to produce

1. **Sequencing.** What's the smallest first cut that puts a real Function Health lab panel in front of the agent? Options span:
   - Manual PDF upload with structured extraction (LLM-parsed at ingest)
   - Manual CSV/JSON export from the Function Health member portal, uploaded to `/admin/labs` or `/me/labs`
   - Direct API integration (if one exists for consumer-tier users)
   - Third-party bridge (Terra API, Vital, Rook, Human API — do any of them proxy Function Health?)
   Rank by lift and by fidelity.
2. **Engineering lift** for each option, ballparked in the same terms as prior Track A sessions: hours-to-days, risk hotspots, whether it touches auth/secrets, whether it needs Bicep changes.
3. **Coach unlock story.** What specifically does the agent's card look like once we have a lab in hand? Concrete example outputs against a hypothetical panel (vitamin D deficient, ferritin low, HDL borderline, etc.) — enough that Tai can react to shape.
4. **Data model.** What tables land in Postgres? What's the natural key? What cascades on user delete? Mirror the WHOOP-ingestion cascade contract per [docs/privacy-data-handling.md](../privacy-data-handling.md) Section A: user rows cascade-delete, external-connection FKs SET NULL.
5. **Privacy delta.** Function Health data is clinical. Section D of the privacy doc currently commits to "no lab data goes to Anthropic" — this seed's outcome revises that. Draft the specific sentences that replace those commitments, referencing the actual ingestion shape you settled on. Do NOT ship the code changes until Tai's re-signs off on the updated privacy doc.

## Known context

- ADR 0011 defines the ingestion trace contract; a Function Health path would be a new `ingestion.source=function_health` root — same helpers, different source.
- ADR 0012 already anticipates lab-uploaded documents as first-class inputs in the voice + bounds docs. Section D.2 of the privacy doc says the lab surface doesn't exist yet AND when it lands it gets its own section in that doc before any lab content is sent to Anthropic. This seed's spec has to include that new section.
- `AgentActionCategory.SupplementsFromLabs` and `AgentActionSource` (`protocol_based`, `user_data_informed`, and a future lab reference) exist in code. Validator will need extension to recognize the new lab-source format when this ships.
- The three-layer WHOOP ingestion (ADR 0006) is our reference pattern: webhook + poller + on-open pull, all feeding the same handler. Function Health may or may not fit that shape — spec should say.
- No mobile client. iOS HealthKit is not currently in-play. If Function Health only exports via HealthKit / Apple Health, that changes the sequencing dramatically.

## Open questions the research pass needs to answer

- Does Function Health have a **consumer developer API**? Enterprise partners have one; individual users may or may not. If not, the answer is document upload + parse. Get to a definitive answer, not "probably."
- If a consumer API exists: what's the auth model? OAuth like WHOOP, or API key per user?
- If document upload only: what's the shape of the export? PDF only, or is there a JSON export?
- What panels does Function Health actually run? The seed's example outputs above should target the actual biomarkers, not made-up ones. Look up their panel structure.
- What does "supplement recommendation" mean for our legal posture? Tai is a lawyer and vetted this at the persona-doc level; ingestion of an actual lab crosses a different line. The privacy doc revision needs to say specifically who bears the recommendation risk and cite the relevant "consult your clinician" hedge that the persona already carries.
- Are there **rate limits or session model constraints** if there IS an API? WHOOP's rotating-refresh-token race bit us hard on 2026-06-11; would want to flag any similar patterns.
- Is there a **partial-panel case** the coach has to handle gracefully? (e.g., cholesterol run this quarter but vitamin D not) — the input snapshot builder needs a shape that handles missing biomarkers.

## What this seed is NOT asking for

- A finished implementation plan. The research pass produces a spec; the spec gets promoted to `docs/session-*.md` and driven from there.
- A decision about MyFitnessPal / Strava / Lumen order. Those have their own seeds.
- Anything about the protein-personalization rule Tai raised — that's [protein-personalization-rules.md](protein-personalization-rules.md).
