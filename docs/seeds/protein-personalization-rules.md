# Seed: Protein-target personalization (Track B rules-engine input)

**Priority.** Tai flagged this as a **rules-engine concern**, not a today-fix. She was clear that the current coach output is right for what the coach can see; the personalization she wants requires inputs the coach doesn't have yet. This seed captures the shape of the input Track B will need.

**Status.** Research pass complete (2026-07-01) — input contract landed at [`protein-personalization-rules-research.md`](protein-personalization-rules-research.md). Not promoted to a session brief because Track B hasn't started; the research doc is Track B Session 1's design input. Blocks nothing until Track B kicks off.

---

## Why now

Tai's exact wording after seeing the live card 2026-06-28:

> One note for the next iteration: the protein target (0.7–1g per lb bodyweight) is a population-level range. As we add more user context — specific body composition goal, training history, cycle phase — I want that to resolve to a specific personalized target rather than a general range. Flag for the rules engine.

Today the coach produces the range because that's what its system prompt + WHOOP data justify honestly. It has no idea what her body-composition goal is (recomp? cut? gain?), no visibility into training history depth (was last week a peak or a taper?), and no cycle-phase awareness. Without that, "0.8g/lb" and "1.0g/lb" are equally defensible and the coach has to give the range.

For the coach to resolve to a single number, the rules engine has to encode:

1. **What goal the user is currently pursuing** — recomposition, cut, maintenance, gain — and where in that pursuit they are (week 3 of a 12-week cut is different from week 11)
2. **Training history depth** — trailing 8–12 week volume + intensity, so the target can respond to accumulated load
3. **Cycle phase awareness** — luteal vs. follicular, or menstrual/perimenopause/menopause status for users with cycles

The daily card would then get an already-personalized number in its input snapshot rather than deriving it from a range. The coach's job is to communicate the number, not to compute it.

## Deliverables the research pass has to produce

1. **Rules-engine input contract for protein target.** What signals does the engine need in order to compute a specific g/lb value? Draft the input schema. This is a specification exercise, not a science one — we're not deciding the equation, we're deciding what the equation needs to see.
2. **User onboarding surface for goal + cycle.** Body composition goal and cycle status don't come from WHOOP. What does the `/me` (or `/me/profile`) surface look like where a user declares "I'm in week 4 of a cut targeting -1lb/week" and "I'm in the luteal phase since 2026-06-25"? What's the minimum viable input?
3. **Training-history summary.** WHOOP workouts + Strava activities (see [strava-integration.md](strava-integration.md)) already flow in. The rules engine wants a rolled-up view — weekly volume trend, intensity distribution over 8 weeks, taper detection. Draft the summary shape the engine reads.
4. **How the daily card consumes the result.** If the engine computes "1.05g/lb for Tai today," how does that get into the coach's input snapshot? A single field, or the whole reasoning trail so the coach can cite it?
5. **What happens when the engine can't compute confidently?** No goal declared, no cycle data — does the coach fall back to the current range, or refuse to give a target at all? Draft the fallback policy.

## Known context

- ADR 0012 sequenced the LLM card BEFORE the rules engine deliberately: get real reactions first, encode rules against real reactions. Tai's protein-target feedback IS the kind of reaction that shapes rules. This seed is on-plan.
- Coach's persona doc + bounds doc are Tai-authored and locked. The rules-engine output feeds INTO the input snapshot; it does not change what the coach says or how it says it. It changes what the coach has to work with.
- The agent's system prompt is loaded at container start from `docs/agent-voice-and-persona.md`. Any prompt changes flow through that file, not the code.
- We currently have no user profile beyond WHOOP + Entra identity. Body-composition goal + cycle status are net-new user surface.
- Function Health integration ([function-health-integration.md](function-health-integration.md)) is likely a dependency for protein-target personalization in the long run — lab-based markers (fasting insulin, testosterone, thyroid) inform the target. But this seed doesn't need Function Health to ship a first cut; body-composition goal + cycle phase get us most of the way.

## Open questions the research pass needs to answer

- **What's the smallest surface** that captures goal + cycle without turning `/me` into a health-app onboarding funnel? Tai will have strong opinions here — she designed the voice to be action-first, not survey-first.
- **How opinionated should the engine be?** Options span:
  - Encode a specific published formula (e.g., ISSN's evidence-based recommendation for cutting phase = 1.2–1.6 g/lb) and pick the midpoint
  - Encode a range with tighter bounds than the current population range, still leave the coach to hedge
  - Have Tai (or a domain SME) author the actual g/lb table by phase × goal × cycle
- **Cycle-phase input model** — user-declared vs. read from an app (Apple Health has cycle tracking; so does Whoop's women's-health features). Cheapest first cut?
- **Fallback boundary** — when the engine falls back to a range, does the coach's voice change? Or does it just say "protein target is a range this week — I don't have enough context to narrow it"?
- **What other targets have the same "population range → personalized number" shape?** Carbs, hydration volume, caffeine cutoff. This seed's design for protein should probably generalize.

## What this seed is NOT asking for

- Nutrition-science recommendations. The research pass produces input contracts, not g/lb formulas.
- The rules engine itself. That's Track B; this seed feeds Track B.
- Any change to the current coach behavior. Live agent stays as-is until Track B produces an input the coach can use.
