# Session brief — Quick-log (manual data entry on /me)

**Status.** Session prompt. Promoted from [`docs/seeds/coach-interaction-and-manual-logging-research.md`](seeds/coach-interaction-and-manual-logging-research.md) Phase 1 on 2026-07-02, on Adam's "proceed as recommended." Phase 2 (card conversation) is designed in the same research doc and deliberately NOT scheduled — gated on Tai's conversational voice addendum + cost telemetry.

**Sequencing.** Ships FIRST in the current queue — before Function Health — because it has zero external-vendor dependencies and it pre-builds tables the MFP/Strava sessions reuse. Sequence: **Quick-log → Function Health → MFP → Strava → Conversation (Phase 2).**

---

## Goal

A "tell the coach something" input on `/me`. One line of free text → the model extracts a structured entry → the user confirms exactly what was understood → the entry lands in the same tables the Track D integrations will fill → tomorrow's card reasons over it.

Three entry types:
- **Meal** → `mfp_food_entries`, `source='manual'` — meal slot + whatever macros the user stated (all nullable)
- **Workout** → `healthkit_workouts`, `source_bundle_id='manual'` — type, duration, start time, intensity descriptor
- **Note** → `user_notes` (new) — symptom / schedule / context, optionally expiring ("traveling until Friday")

## Non-negotiables (carried from ADR 0012 + Function Health)

1. **Confirm-before-persist.** Nothing writes without the user clicking Confirm on the extraction. Extraction can be wrong; the user is the check.
2. **Every extraction invocation logged** to `agent_invocations` (`kind='quick_log_extraction'`), visible in `/admin/agent`.
3. **Feature-flagged OFF by default** (`QuickLog:Enabled`). The extraction call sends user free text to Anthropic — a new privacy surface not covered by Tai's existing Section D signoff. The flag flips only after Tai signs privacy draft **Part 4**. Scaffolding ships before signoff; the network call does not (ADR 0012 precedent).
4. **Notes are visible, deletable memory.** The "Logged by you" list on `/me` shows entries and active notes with per-entry delete. Nothing is silently remembered.

## Build checklist

1. **Migrations:** `mfp_food_entries` (per MFP session brief §1.4 schema), `healthkit_workouts` (per Strava session brief §1.7 schema), `user_notes` (per research §2.4). Daily rollups are computed in-memory at snapshot-build time — the `mfp_daily_rollups` table is deferred to the MFP session, which can materialize it if query cost warrants.
2. **Domain entities + EF configurations** matching the existing WHOOP entity pattern; cascade on user delete for all three tables.
3. **Extraction service:** `IQuickLogExtractionService` on `AnthropicMessagesClient`, one tool schema with the three entry types; unclassifiable input returns a "couldn't classify" result offering the three types; partial fields are expected and fine.
4. **`/me` surface:** input box + POST extract → confirm card (Confirm / Edit / Discard) → POST confirm persists → "Logged by you" list with delete. Cap: 20 extractions/user/day, enforced server-side.
5. **Snapshot builder:** add `latest_food_entries` (last 3 days), `daily_macro_rollups` (last 7 days, computed), `user_notes` (active, max 10); manual workouts join the existing workouts section with a `source` marker. Sections are omitted (not empty) when a user has no data.
6. **Inline disclosure** at the input: *"What you type here is processed by our AI provider — same rules as your card data: never used for training, no identifiers attached."*
7. **Tests:** extraction response validation against canned Anthropic responses (≥10 utterance fixtures), confirm-flow validation, cap enforcement, cascade contract, snapshot sections with mixed sources. `dotnet format` + full suite green.

## Exit criteria

- [ ] Build + tests green, format clean, CI green
- [ ] With `QuickLog:Enabled=true` in dev: Adam logs a real meal, confirms, and next card render references it
- [ ] Unconfirmed extraction persists nothing (verified by test + manual check)
- [ ] Note with `active_until` in the past is absent from the snapshot
- [ ] `/admin/agent` shows extraction invocations with kind badge
- [ ] Privacy draft Part 4 added to `privacy-draft-track-d.md` for Tai (gates the flag flip, not the merge)

## NOT in scope

- Phase 2 conversation (thread, caps, voice addendum) — designed in the research doc, waits on Tai.
- `mfp_daily_rollups` table — deferred to MFP session.
- Renaming `mfp_food_entries` → `food_entries` — cheap cleanup for the MFP session's migration, noted there.
- Photo/voice input, food-database lookups, calorie estimation beyond what the user states.
