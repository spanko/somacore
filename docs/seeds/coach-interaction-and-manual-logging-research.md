# Coach interaction + manual logging — research pass

**Status.** Research pass, 2026-07-02. Answers every open question in [`coach-interaction-and-manual-logging.md`](coach-interaction-and-manual-logging.md).

**Shape.** Internal-design seed (like protein-personalization) — no external-vendor research required. Grounded in the existing agent code (`LiveDailyAgentService`, `AgentResponseValidator`, `agent_invocations`) and the Track D table contracts.

**Author.** Claude. **Date.** 2026-07-02.

---

## 1. Executive summary (60-second read)

**Recommend two phases, in this order:**

**Phase 1 — Quick-log (manual data entry), no chat.** A "Log something" affordance on `/me`: the user types one line of free text ("lunch: chicken bowl, maybe 50g protein" / "45 hard minutes on the bike this morning"), the model extracts a structured entry, the user sees exactly what was extracted and confirms, and the entry lands in the **same tables the Track D integrations will fill** (`mfp_food_entries` with `source='manual'`, `healthkit_workouts`-shaped rows with `source_bundle_id='manual'`). Tomorrow's card reads it like any other data. This is most of the user value ("the coach can finally see my nutrition **today**, months before MFP ships"), reuses the Function Health confirm-before-persist pattern, adds one narrow free-text-to-Anthropic surface, and costs one invocation per log.

**Phase 2 — Card conversation.** A bounded follow-up thread attached to today's card: ask why, report context ("knee pain", "traveling this week"), get an adjusted read. Capped per day, expires with the card, every turn logged to `agent_invocations`, bounds enforced by requiring the model to answer through a structured tool (same mechanical-guard philosophy as the card). Context worth keeping ("knee pain") is offered back to the user as an explicit, visible, deletable note — never silently remembered.

**Why this split.** Quick-log is small, cheap, bounded, and its privacy delta is one sentence of free text per entry. Conversation is the bigger lift (voice addendum from Tai, thread model, caps, a meaningfully larger privacy surface) and the bigger review burden. Splitting lets Phase 1 ship on engineering time while Tai works through the Phase-2 questions — and Phase 1 quietly builds the extraction machinery Phase 2's "log it from chat" moment reuses.

**Both phases keep ADR 0012's three commitments:** no autonomous action (user confirms every write), full logging (every invocation in `agent_invocations`), disclosure on the surface.

**Sequencing vs. Track D:** Phase 1 has zero external dependencies and no vendor gate — it can start immediately, even before Function Health, and it front-runs the MFP integration's value. Recommended order: **Quick-log Phase 1 → Function Health → MFP → Strava → conversation Phase 2**, with Phase 2's Tai-gates (voice addendum, privacy part) worked in parallel during the Track D builds.

---

## 2. Phase 1 — Quick-log

### 2.1 Surface

- A single input on `/me` under the daily card: *"Tell the coach something it can't see — a meal, a workout, how you're feeling."* One text box, one button.
- Submit → model extracts → a confirm card renders: "Here's what I understood" with the structured entry (type, fields, timestamp) and **Confirm / Edit / Discard** buttons. Nothing persists without Confirm. Same safeguard shape as Function Health's confirm-before-coach-reads, for the same reason: extraction can be wrong.
- Confirmed entries appear in a "Logged by you" list on `/me` with per-entry delete.

### 2.2 Extraction contract

One new invocation kind: `quick_log_extraction`. Model gets the user's line + current local time + a tool schema with three entry types:

| Entry type | Lands in | Source value | Fields extracted |
|---|---|---|---|
| **Meal** | `mfp_food_entries` | `source='manual'` | meal_slot (inferred from time if unstated), calories?, protein_g?, carbs_g?, fat_g? — all nullable; user said "maybe 50g protein" → protein only |
| **Workout** | `healthkit_workouts` | `source_bundle_id='manual'` | workout_type, duration, intensity descriptor → stored in `hk_metadata`, started_at |
| **Note** | `user_notes` (new, §2.4) | `source='quick_log'` | free text + optional category (symptom / schedule / context) |

- **Same tables as the integrations** — this is the load-bearing design decision. When MFP ships, manual meal entries and HealthKit meal entries coexist in `mfp_food_entries`, distinguished by `source`, deduped by the existing `(user_id, meal_date, meal_slot, source)` uniqueness. The coach input-window builder reads one shape forever. (The `mfp_` prefix becomes a slight misnomer once manual entries land there; renaming to `food_entries` in the MFP session's migration is a cheap cleanup worth doing then, not now.)
- If the model can't classify confidently, the confirm card says so and offers the three types as buttons. Never guess-and-persist.
- Partial data is fine and expected. A meal row with only `protein_g` populated still improves the coach's protein math over nothing.

### 2.3 What the coach does with it

`AgentInputSnapshotBuilder` gains the same `latest_food_entries` / `daily_macro_rollups` / `latest_workouts` sections the Track D briefs already specify — fed today by manual rows, later by integration rows. One addition: entries carry their `source` into the snapshot so the coach can hedge honestly ("based on what you logged" vs. "based on your WHOOP data").

### 2.4 `user_notes` — the third entry type, kept deliberately small

```sql
CREATE TABLE user_notes (
    id           uuid PRIMARY KEY,
    user_id      uuid NOT NULL REFERENCES users(id) ON DELETE CASCADE,
    source       text NOT NULL,            -- 'quick_log' (phase 1) / 'conversation' (phase 2)
    category     text,                     -- 'symptom' / 'schedule' / 'context' / null
    note         text NOT NULL,            -- the user's words, as confirmed by them
    active_until date,                     -- nullable; "traveling until Friday" gets an expiry
    created_at   timestamptz NOT NULL DEFAULT now(),
    trace_id     text
);
```

- Notes go into the input snapshot (active ones only: no `active_until` or `active_until >= today`, capped at 10 most recent).
- **This is user-visible memory with a delete button, not silent model memory.** The "Logged by you" list shows active notes; delete removes them from all future snapshots. This answers the seed's memory question: yes, context persists — explicitly, visibly, deletably.

### 2.5 Cost + caps

- One extraction invocation per log. Capped at **20 logs/user/day** (generous; three users). Extraction calls are small — pennies/day at alpha scale.
- All extraction invocations logged to `agent_invocations` with `kind='quick_log_extraction'` — visible in `/admin/agent` like everything else.

### 2.6 Privacy delta (Part 4 of the draft pack when promoted)

- **New Section D.1 item:** the user's typed log line goes to Anthropic for extraction, and confirmed entries/notes appear in future card snapshots. We cannot pre-filter what a user types; the input box carries an inline notice: *"What you type here is processed by our AI provider — same rules as your card data: never used for training, no identifiers attached."*
- **What does NOT change:** no identifiers attached (same anonymous reference), Commercial Terms posture identical, user free text is the only new category.
- **Tai gate:** yes — one new part in the draft pack, small.

### 2.7 Lift

~1 build session: `POST /me/log` endpoint + extraction service (reuses `AnthropicMessagesClient` + a new tool schema) + confirm flow + `user_notes` migration + `mfp_food_entries`/`healthkit_workouts` migrations pulled forward from the Track D briefs + snapshot-builder sections + tests (extraction golden-set with ~10 canned utterances, idempotent confirm, cap enforcement).

Pulling the two Track D tables forward is a feature, not scope creep: the MFP/Strava sessions then start with their tables already live and populated.

---

## 3. Phase 2 — Card conversation

### 3.1 Interaction model

- **Thread-per-card.** Each daily card gets an optional follow-up thread. The thread dies when the card is superseded (next morning). No persistent chat history surface — the coach's continuity lives in structured data (entries, notes, invocation log), not transcript replay.
- Why thread-per-card and not persistent chat: retention story is trivial (thread content follows `agent_invocations` retention), cost is bounded, the product stays "a coach with a daily plan you can question," not "a chatbot" — which is also the persona posture Tai authored.
- **Caps: 10 user turns per card per day** and a per-turn input length cap. At the cap: *"That's it for today — tomorrow's card picks this up."* Both numbers are config, tuned in alpha.

### 3.2 Bounds enforcement in conversation

Same mechanical philosophy as the card — the model cannot free-text its way out of bounds because it must respond through a tool:

- Response tool schema: `{ reply: string, refusal: bool, proposed_entries: [...]?, proposed_note: {...}? }`.
- Server-side validator (extended `AgentResponseValidator`) checks the reply against the OUT OF BOUNDS list the same way card actions are checked; a `refusal=true` response renders Tai's authored refusal language (voice addendum, §3.4).
- `proposed_entries` / `proposed_note` is the chat-to-log bridge: "I also did 30 min of yoga" mid-conversation → the same confirm card as Phase 1. **Confirmation is never conversational** ("yes" in chat doesn't persist data) — it's the same explicit button, so the no-autonomous-action commitment stays mechanical, not interpretive.

### 3.3 What the model sees per turn

Today's card + its snapshot (cached system prompt) + the thread so far + the new user turn. No re-query of the database mid-thread — the thread reasons about the morning's data plus what the user says. Keeps cost linear and behavior reviewable.

### 3.4 Tai gates (why Phase 2 waits)

1. **Conversational voice addendum** to `agent-voice-and-persona.md` — how the coach handles disagreement, repeated out-of-bounds pushes, emotional disclosures, and the refusal wording. User-facing behavior = Tai authors it.
2. **Privacy part** — bigger than Phase 1's: sustained free-form user text to Anthropic, thread retention (recommendation: thread content lives in `agent_invocations` rows, inheriting whatever retention Tai picks there), and the disclosure when *typing to* the model (input-adjacent notice, not just card-footer).
3. **Cost sign-off** — worst case ~10 turns × 3 users × 30 days at card-scale token counts ≈ low tens of dollars/month. Small, but it's the first unbounded-by-design surface; the cost-telemetry follow-up (`/admin/health` spend rollup) should ship with or before it.

### 3.5 Lift

~2 build sessions: thread model + endpoints + validator extension + refusal path + chat-to-log bridge + `/admin/agent` thread view + cap handling + tests. Plus Tai's authoring time, which is the real critical path.

---

## 4. Answers to every open question in the seed

- **Chat scoped to today's card or persistent?** Today's card (§3.1). Continuity lives in structured data, not transcripts.
- **Does chat context persist into future cards?** Yes, via `user_notes` — explicit, user-visible, deletable, optionally expiring (§2.4). Never silent.
- **Voice addendum needed?** Yes, for Phase 2. Phase 1 needs none — extraction isn't user-facing voice; the confirm card is UI copy, not persona.
- **Disclosure when typing?** Inline notice at the input (§2.6, §3.4), distinct from the card-footer disclosure.
- **Cap by messages or tokens?** Messages user-facing (10 turns), token ceiling server-side as backstop. Cap message: warm, references tomorrow's card.

---

## 5. Recommended path forward

1. **Promote Phase 1 (quick-log) to a session brief now.** No vendor dependency, one Tai gate (small privacy part — add as Part 4 to `privacy-draft-track-d.md` so it rides the same review), ~1 session of build. It makes tomorrow's coach visibly smarter and gives Tai a richer alpha to react to — which is the ADR 0012 flywheel.
2. **Hold Phase 2 as a designed-but-not-scheduled follow-up** until (a) Tai's voice addendum exists, (b) cost telemetry ships, (c) quick-log usage shows people actually want to talk back (they will, but let the alpha say so).
3. Sequence: **Quick-log → Function Health → MFP → Strava → Conversation.**

**Gates:** Tai — privacy Part 4 (quick-log free text), later the voice addendum + Part 5 (conversation). Adam — none beyond the build itself.

---

## Change log

- 2026-07-02: initial research pass. Internal-design seed; grounded in existing agent infrastructure and Track D table contracts. Recommendation: split into quick-log (ship first, ~1 session) and card-conversation (designed, gated on Tai's voice addendum + cost telemetry).
