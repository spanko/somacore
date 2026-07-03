# Seed: Coach interaction + manual data logging on /me

**Priority.** Adam, 2026-07-02: *"let's create the ability to interact with the LLM on the /me page - I want us to be able to upload data that aligns with the systems we're going to integrate - like nutrition and exercise."*

**Status.** Phase 1 → promoted to [`docs/session-quick-log.md`](../session-quick-log.md) (2026-07-02, Adam: "proceed as recommended"). Phase 2 (card conversation) designed in [`coach-interaction-and-manual-logging-research.md`](coach-interaction-and-manual-logging-research.md) §3, deliberately unscheduled — gated on Tai's conversational voice addendum + cost telemetry.

---

## Why now

Two capabilities in one ask, and they reinforce each other:

1. **Interaction.** The daily card is one-way — the coach speaks, the user reads. There's no way to ask "why zone 2 today?", say "my knee hurts, adjust", or tell the coach something it can't see. Tai's feedback loop (the whole point of ADR 0012's LLM-before-rules sequencing) is currently throttled to what she remembers to relay through Adam.

2. **Manual data entry ahead of the integrations.** The Track D builds (MFP, Strava, Function) will land nutrition and workout data over the coming weeks-to-months. But the coach could use that data *today* if users could simply tell it: "lunch was ~50g protein", "did an hour of hard intervals this morning." Manual entry is also the permanent fallback for anything an integration misses — and the data contract already exists: the Track D session briefs define `mfp_food_entries`, `healthkit_workouts`, etc. with a `source` column. Manual entry is just another source writing to the same tables, so when the integrations arrive, the coach's view doesn't change shape — it gets denser.

## Deliverables the research pass has to produce

1. **Interaction model.** Open chat attached to the daily card? Structured quick-replies? Both? What's the smallest surface that makes the coach feel interactive without building a general-purpose chatbot? Session/thread model, message caps, and what happens to conversation history.
2. **Manual-entry model.** How does "log a meal / log a workout" work — dedicated forms, chat-extracted ("ate a burrito" → structured entry the user confirms), or both? Which tables do entries land in, with what `source` value, so Track D integrations slot in without a schema change?
3. **Bounds enforcement in conversation.** The current validator checks structured card actions. Free-form replies need an equivalent mechanical guard — what's the response schema for chat, and how do OUT OF BOUNDS topics get refused in a way that matches Tai's voice doc?
4. **Privacy delta.** User free text goes to Anthropic — we cannot control what a user types (names, medications, third parties). This is a categorically different input surface than our curated snapshots and needs its own privacy-doc part + Tai gate.
5. **Cost + abuse model.** The card is one invocation/day/user. Chat is unbounded. Caps, per-user daily budget, and what the cost telemetry needs to show.
6. **Engineering lift + sequencing** relative to the Track D queue — does this jump ahead of Function Health (it has no external-vendor dependency at all), or slot behind it?

## Known context

- `LiveDailyAgentService` + `AgentInputSnapshotBuilder` + `AgentResponseValidator` + `agent_invocations` logging already exist — the invocation plumbing is reusable.
- Persona + bounds (`agent-voice-and-persona.md`, `agent-bounds.md`) are Tai-authored, card-shaped. Conversational voice is adjacent but not specified.
- ADR 0012 committed to: no autonomous action, full logging, disclosure on the surface. Interaction must keep all three.
- Track D session briefs define the target tables (`mfp_food_entries` etc.) with `source` columns — manual entry should write there, not to parallel tables.
- Function Health established the confirm-before-persist pattern for LLM-extracted data; chat-extracted log entries look like the same problem.
- Privacy doc Section D is built on "we control exactly what gets sent." Free-text chat breaks that premise; needs explicit treatment, not a footnote.

## Open questions the research pass needs to answer

- Is chat scoped to *today's card* (a follow-up thread that expires) or persistent (a running relationship)? Retention and cost follow from this.
- Does chat-provided context ("my knee hurts") persist into future cards, and if so where — a `user_notes` surface? That's memory, and memory needs a deletion story.
- Voice: does Tai need to author a conversational addendum to the persona doc before this ships (likely yes — it's user-facing behavior)?
- What's the disclosure when a user is typing to the model vs. reading a generated card?
- Do we cap by message count, token budget, or both? What does the user see at the cap?

## What this seed is NOT asking for

- A general-purpose chatbot. The coach converses about the user's plan and data. Off-plan conversation gets the bounds treatment.
- Autonomous actions. The coach still never mutates anything without user confirmation — chat-extracted log entries included.
- Replacing the Track D integrations. Manual entry is the fallback and the head start, not the plan.
