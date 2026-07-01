# Seed: Strava integration

**Priority.** Third of Tai's four data-source seeds (2026-06-28). Likely the easiest of the four given Strava's mature developer API.

**Status.** → promoted to [`docs/session-strava-integration.md`](../session-strava-integration.md) (2026-07-01). Research pass: [`strava-integration-research.md`](strava-integration-research.md). Verdict was HYBRID — direct API primary + iOS-companion HealthKit fallback.

---

## Why now

WHOOP workouts are the current source of truth for the coach's training input. But WHOOP's workout capture is auto-detected from strap wear + optional manual logging — it misses activities the user runs their watch/phone for, and doesn't carry route, cadence, power, or split data. Strava is where users of a certain profile actually record their sessions and pace themselves. Bringing Strava in gives the coach:

- Higher-fidelity workout signal (splits, HR zones over duration, effort vs pace, terrain from route)
- Coverage of activities WHOOP missed (bike rides where the strap sat on the desk, races where the user wore something else)
- Behavioral patterns Strava reveals that WHOOP doesn't (weekly volume trend, kudos-driven training bumps)

## Deliverables the research pass has to produce

1. **Sequencing.** Strava has webhooks + a REST API. Confirm the current shape and produce a plan that mirrors ADR 0006's three-layer WHOOP ingestion (webhook for real-time, poller for reconciliation, on-open pull for fresh data on `/me`).
2. **Engineering lift.** Should be lighter than WHOOP was because we already have the ingestion patterns, the trace contract (ADR 0011), the OAuth token cache with race-rescue (`WhoopAccessTokenCache`), and the disconnect+reconnect UI. Strava is "same shape, different vendor." Estimate assuming ~70% code reuse.
3. **Coach unlock story.** Concrete example outputs that use Strava data the coach doesn't currently see. "Yesterday's tempo run held 158bpm avg over 42 minutes — that's the third session this week in your zone-3 band. Today's plan pulls back to zone 2 to consolidate." Show the difference vs WHOOP-only inference.
4. **Data model.** New tables (`strava_activities`?), what's the natural key (Strava activity id), cascade rules matching WHOOP. Reconcile with the existing `whoop_workouts` table — do they merge into a canonical `workouts` view for the coach, or does the coach see them as separate sources?
5. **Overlap handling.** If a run is captured by BOTH WHOOP and Strava, we have two rows for the same real-world activity. Coach input snapshot needs a dedup strategy (prefer Strava for GPS-having activities, prefer WHOOP for strain, merge scores). Draft the dedup rule.
6. **Privacy delta.** Strava activity data can include location traces. Privacy doc section D needs a specific commitment about whether location leaves our infrastructure (send lat/lng to Anthropic — probably no; strip before sending). Draft the new sentences.

## Known context

- Strava's API is mature, well-documented, and widely used. This is the seed with the most confident public information.
- OAuth 2 with refresh tokens; rate-limited per app + per user. The rate-limit story is worth documenting explicitly given we got burned by WHOOP's on 2026-06-11.
- Webhooks are subscription-based per-app (single URL, all subscribers) — matches how WHOOP works, so the webhook drainer pattern applies.
- Our token cache (`WhoopAccessTokenCache`) has a documented race-rescue for rotating refresh tokens. Strava doesn't rotate on refresh, so the rescue may not be needed — but confirm.
- ADR 0006 (three-layer ingestion) is the template. This should feel like Track A with the labels changed.
- `AgentActionCategory.TrainingIntensity` and `AgentActionCategory.WorkoutStructure` already exist. Validator doesn't need changes.

## Open questions the research pass needs to answer

- **Rate limit specifics** — Strava's current per-app cap (was 15-minute-window and daily). Do they publish an SLA? Does our three-user scale fit inside a single unpaid-app tier?
- **Activity types coverage** — running, cycling, swimming, weight-training, "workout" as a catchall. Which of these have score data (average HR, elevation, splits) vs. just timestamps?
- **Webhook reliability** — Strava's webhook docs describe retry behavior. How does it compare to WHOOP's (which we now know is not guaranteed)?
- **Should we accept manual Strava-only auth flow** (Strava-only user, no WHOOP), or force both? Persona today assumes WHOOP as the recovery signal source. A Strava-only user has no recovery score.
- **De-authorization behavior** — when a user disconnects Strava (from our app OR from Strava's side), what does the webhook flow look like?

## What this seed is NOT asking for

- Route visualization, GPX rendering, social features. The coach reads training signal from Strava; the user experiences Strava at Strava. We ingest, we don't rebuild.
- Third-party aggregator middleware. Strava's API is good enough that a direct integration wins here.
- Any change to WHOOP ingestion. This is additive.
