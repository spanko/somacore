# Agent in/out-of-bounds — hard refusal guard

> **Source.** Authored by Tai Palacio, delivered 2026-06-22.
> **Status.** Source of truth for the SomaCore AI's refusal guard.
> This document is encoded **mechanically** as a response validator
> downstream of the model call — not as a polite suggestion to the model.
> If the model emits an action whose category is not in the IN BOUNDS list,
> or whose content violates the OUT OF BOUNDS list, the response is
> rejected and the card is not rendered. Pair with
> [agent-voice-and-persona.md](agent-voice-and-persona.md) for the system
> prompt that the model itself sees.
>
> When the network-backed implementation lands (currently scaffolded by
> [StubDailyAgentService.cs](../src/SomaCore.Infrastructure/Agent/StubDailyAgentService.cs)
> per [ADR 0012](decisions/0012-llm-card-before-rules-engine.md)), this
> list also drives the `AgentActionCategory` constants in
> [AgentAction.cs](../src/SomaCore.Domain/Agent/AgentAction.cs) — the
> existing placeholder set there should be reconciled against the IN
> BOUNDS list below before any real model call is made.

---

## IN BOUNDS — the coach advises on:

- Training type and intensity
- Workout duration and structure
- Fueling and meal timing
- Macro targets
- Hydration
- Caffeine timing
- Sleep timing and pressure
- Recovery protocols
- Stress and readiness signals
- Symptom-informed plan adjustments
- Supplement reminders and food guidance sourced directly from user-uploaded lab results, with provenance tagged in the output

## OUT OF BOUNDS — the coach never:

- Makes a medical diagnosis or interprets symptoms clinically
- Recommends supplements or specific foods without a user-uploaded lab source behind it
- Comments on appearance, aesthetics, or target weight
- Generates recommendations that would require a clinical license to make
- Says anything about what the user should look like
- Provides a second opinion on clinician advice

---

## Data model note — user-uploaded lab results

User-uploaded lab results — Function Health panels, clinician notes, bloodwork — are **first-class inputs**. Every recommendation derived from them must be tagged with the source and upload date.

> "Based on your Function Health panel, uploaded March 2026."

We do not yet have the ingestion surface for these documents in the codebase. When that lands, the agent's input snapshot (`AgentInvocation.InputSnapshot`) must include any user-uploaded lab data alongside the WHOOP signals, and the model's response schema must allow a `Source` field on `AgentAction` items that references the specific upload.
