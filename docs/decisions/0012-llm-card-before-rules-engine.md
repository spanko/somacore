# ADR 0012 — Ship the LLM-powered daily card before the deterministic rules engine

**Status:** Accepted
**Date:** June 2026
**Context:** Phase 2 / pre-Track-B

---

## Context

The original architecture (see `docs/architecture.md` and the phase-1 brief in `CLAUDE.md`) calls for a deterministic rules engine grounded in physiological science, with an LLM layer that *explains* the rules' output but does not make the call. The intent was to sequence Track B (rules engine) before any LLM-front surface, so the rules drove every recommendation and the LLM was constrained to narration.

Track A (WHOOP ingestion) is now code-complete and shipped to dev. Tai and Adam are both reconnected with the full scope set. The next gate was supposed to be: Tai specs the rules → we build the engine → we then wrap an LLM on top.

That sequence has a chicken-and-egg problem the original plan didn't surface: Tai cannot authentically spec the rules until she has reacted to something. A spec written from first principles in the abstract will encode what she *thinks* matters, not what actually moves her in practice when she sees a recommendation against her morning recovery. The signal we need to design good rules is the signal we don't yet have.

Anthropic released Fable 5 around the same time Track A landed. Fable 5 is materially better than prior model families at voice consistency, persona discipline, and producing output that reads like a specific coach rather than a generic assistant. With prompt caching on the system prompt + persona + action-library, the per-card cost stays well under a cent.

## Decision

**Ship an LLM-front daily-action card on `/me`, powered by Fable 5, *before* building the deterministic rules engine.** The rules engine still ships in Track B — but informed by what Tai actually responds to in the alpha, not by guessed-at first-principles rules.

### What this changes

- **Order:** LLM card (this work) → observation (~2-4 weeks of real use) → rules engine (Track B) → LLM narrates the rules (the original sequence, now informed by what the alpha taught us).
- **Surface:** A new card above the recoveries table on `/me`. Renders a one-paragraph "today's read" + 3 ranked actions. No autonomous action. Fully logged.
- **Scope of what the LLM sees:** The user's last 7 days of recovery/sleep/workout data + time-of-day. No medical history, no nutrition data (we don't ingest it yet), no biometrics outside WHOOP.
- **Scope of what the LLM can output:** Defined by the in-bounds / out-of-bounds list Tai writes. Encoded as a hard refusal guard, not as polite suggestions to the model.

### What this preserves

- **No mutation, no autonomous action.** The card is read-only output. The user reacts; nothing in the world changes because of it.
- **Every invocation is logged** in `agent_invocations` — input snapshot, output, model id, token counts, cost, duration, trace id. Reviewable in `/admin/agent` (TBD) and analyzable for what patterns matter when we build the rules.
- **Privacy review is a hard gate.** Tai signs off on `docs/privacy-data-handling.md` covering what we send to Anthropic before the first real model call. The scaffolding ships before that signoff; the network call to Anthropic does not.
- **Reversibility.** If the card produces output we don't want, the fix is a prompt change, not a code change. If we want to revert to "rules first," the LLM card stays as the narration layer that was always planned for Track C.

### What this defers

- The deterministic rules engine. Now Track B's first deliverable, informed by what the alpha surfaces.
- Apple Health / Oura / nutrition / biomarker ingestion. Still Track C / phase 3.
- The mobile (Flutter) app. Still phase 3+.

## Why Fable 5 specifically

The card stands or falls on whether it sounds like *the agent we want to build*, not like a chatbot. Fable 5 is the first model family where the voice-consistency and persona-discipline benchmarks make a single-paragraph daily card legibly distinct from a generic LLM completion. The qualitative read on real outputs against test prompts is unambiguously better for this use case than the Claude 4.X family at any tier.

We pin to `claude-fable-5` for the daily card. The rest of the codebase (any future "explain this rule" surfaces, admin tooling, batch summarization) stays on the Claude 4.X family.

## Consequences

**Positive:**
- Tai gets something to react to in days, not weeks. The signal we need to design good rules starts flowing immediately.
- The architecture's LLM-narrates-rules pattern stays valid; we're just sequencing the rules engine *after* the alpha rather than before.
- We ship something demo-able to ourselves and to anyone we'd want to show the product to in the next month.

**Negative / acknowledged trade-offs:**
- The alpha card has no rules-engine grounding. The model's recommendations are reasonable-sounding but not derived from anything formal. We document this on the `/me` disclosure so the user knows what they're reading.
- Every invocation costs money. With prompt caching this stays small (~$0.01-0.05 per card render at current pricing assumptions), but it's a real per-user marginal cost the deterministic-rules version wouldn't have.
- A bad output blames the agent in the user's perception. Mitigated by: clear disclosure, full logging, easy prompt iteration.
- We're slightly more locked into Anthropic for the alpha. The interface (`IDailyAgentService`) abstracts the provider; the prompt is portable; the lock-in is shallow.

## Implementation outline

1. Domain: `AgentInvocation` entity + `AgentAction` record. ✓ (commit `30fcb5a`)
2. Migration: `0005_agent_invocations.sql` — one new table, `ON DELETE CASCADE` on `users` per the existing pattern. ✓ (commit `30fcb5a`)
3. Infrastructure: `IDailyAgentService.GenerateAsync(userId, ct)` returning `Result<DailyAgentResponse>`. Stub implementation that returns a hardcoded sample. ✓ (commit `30fcb5a`)
4. `/me`: a "Daily plan" card rendered from the stub, with the privacy disclosure stub. ✓ (commit `30fcb5a`)
5. **Gate — pre-Fable signoffs from Tai:**
   - ✅ Persona + voice spec — delivered 2026-06-22, captured at [`docs/agent-voice-and-persona.md`](../agent-voice-and-persona.md). This is what gets compiled into the system prompt verbatim.
   - ✅ In/out-of-bounds — delivered 2026-06-22, captured at [`docs/agent-bounds.md`](../agent-bounds.md). This encodes mechanically as a response validator, not as a polite suggestion to the model.
   - ⬜ Privacy review of `docs/privacy-data-handling.md` — outstanding. Gates the actual network call to Anthropic.

## Pre-implementation reconciliation owed

Two small cleanups before the Fable wire-up that should land in the same PR as the network call:

1. **`AgentActionCategory` constants in [`AgentAction.cs`](../../src/SomaCore.Domain/Agent/AgentAction.cs) need to match Tai's IN BOUNDS list.** Today's placeholder set (`Hydration`, `CaffeineTiming`, `WorkoutIntensity`, `SleepTiming`, `Stress`, `MealTiming`) is partial. Tai's list adds: `workout_structure`, `macros`, `recovery`, `symptom_adjustment`, `supplements_from_labs`, and renames `WorkoutIntensity` → `training_intensity` (the semantic shifts from "intensity" to "type and intensity"). Reconcile against [`docs/agent-bounds.md`](../agent-bounds.md).
2. **Add a `Source` field on `AgentAction`** to carry provenance per Tai's voice doc — `protocol-based`, `user data-informed`, or a reference to a specific user-uploaded lab document. The lab-document ingestion surface doesn't exist yet; the field can land as `string?` for now and the model can populate it for the first two values.

## References

- `docs/architecture.md` — original LLM-narrates-rules architecture (still valid, just sequenced after the alpha now).
- `CLAUDE.md` — phase-1 scope brief.
- [`docs/agent-voice-and-persona.md`](../agent-voice-and-persona.md) — Tai's voice spec; the system prompt.
- [`docs/agent-bounds.md`](../agent-bounds.md) — Tai's bounds; the refusal guard.
- `docs/privacy-data-handling.md` — the doc Tai reviews before the network call to Anthropic.
- ADR 0011 — ingestion trace contract (the alpha card opens its own trace root distinct from the ingestion roots; same `IngestionTracing` helpers, different source).
