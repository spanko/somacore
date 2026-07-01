# Session brief — Function Health integration

**Status.** Session prompt. Promoted from [`docs/seeds/function-health-integration-research.md`](seeds/function-health-integration-research.md) after that research pass returned PASS on verification. This document is the ship-worthy plan; the research doc stays as the reference for why we made these choices.

**Track / phase.** Phase 2. Not part of Track A (WHOOP). Suggest calling this "Track D — external data sources, Session 1" when the next track index goes up; naming is Adam's call.

**Sequencing decision made 2026-06-30.** Option A (PDF upload + LLM-parse) ships as the value-bearing layer. Option B (MCP client against Function's Auth0/PKCE endpoint) is NOT dropped — it layers on top as a **notification/trigger** for A, not as a data-value alternative. See [Approach](#approach) below.

---

## Goal

Two-phase deliverable that lands `supplements_from_labs` on the daily card:

- **Phase 1 (this session).** A user uploads their Function Health results PDF at `/me/labs`, we parse the biomarker values, and the coach references them by name + collection date in its card output. Coach can say *"Vitamin D is 22 ng/mL, low against the 30–100 ng/mL reference — take 2000 IU with your first meal."* — with the source tagged to the specific upload.
- **Phase 2 (a follow-up session, spec below).** We register a first-party OAuth client with Function, users grant consent through Function's Auth0 flow, we poll Function's MCP server once per day per user for its three summary tools, detect category-drift ("your Heart category shows a fresh out-of-range marker since your last panel"), and **surface a banner on `/me` prompting the user to upload their latest PDF**. Phase 2's job is to fire the *"please upload a fresh panel"* nudge; Phase 1's job is to consume the PDF once uploaded. They meet at the notification.

Phase 1 unblocks `supplements_from_labs` today. Phase 2 makes the feature self-driving instead of relying on the user remembering to upload.

## Approach — why A and B rendezvous instead of competing

The research pass framed A and B as ranked alternatives because Option B alone cannot drive `supplements_from_labs` — Function's MCP server explicitly does not return individual biomarker values, only category-level summary counts. Adam's read (2026-06-30) was that this framing is wrong for the actual product: **A is the values layer, B is the "when to refresh" trigger.**

Concretely:

| Layer | Signal | Value it provides | Ships in |
|---|---|---|---|
| A (PDF parse) | User-uploaded PDF | Actual biomarker values → drives `supplements_from_labs` recommendations | Phase 1 |
| B (MCP client) | Daily poll of Function's summary tools | "Your Heart category has 3 out-of-range markers now, but your latest uploaded PDF only shows 2. Something changed." → drives a `/me` banner asking for a fresh upload | Phase 2 |

Without B, A works but the coach reads stale values indefinitely — the user has to remember to upload a new PDF every quarter. With B, the coach *knows* when the user's latest upload no longer matches what Function's servers say and can prompt for a refresh. The MCP summary is exactly the right shape for detecting drift: it's low-fidelity but always current.

---

## Phase 1 — what ships this session

Everything below is drawn from the research doc's Section 3 and 5. Refer there for the full engineering-lift analysis; this section is the checklist.

### 1.1 Domain + persistence

Add two tables and extend `agent_actions`. From research Section 5.1:

```sql
CREATE TABLE lab_uploads (
    id            uuid PRIMARY KEY,
    user_id       uuid NOT NULL REFERENCES users(id) ON DELETE CASCADE,
    source        text NOT NULL,           -- 'function_health' (extensible)
    uploaded_at   timestamptz NOT NULL DEFAULT now(),
    collected_at  date,                    -- Extracted from the PDF
    file_name     text NOT NULL,
    file_bytes    bytea NOT NULL,          -- Phase 1 — move to Blob later
    file_size     int NOT NULL,
    parse_status  text NOT NULL,           -- 'pending' / 'parsed' / 'failed' / 'confirmed'
    parse_error   text,                    -- Capped 2000 chars, redacted
    parsed_at     timestamptz,
    confirmed_by  uuid REFERENCES users(id),
    confirmed_at  timestamptz,
    trace_id      text
);
CREATE UNIQUE INDEX idx_lab_uploads_user_collected
    ON lab_uploads(user_id, source, collected_at);

CREATE TABLE lab_biomarkers (
    id                uuid PRIMARY KEY,
    lab_upload_id     uuid NOT NULL REFERENCES lab_uploads(id) ON DELETE CASCADE,
    user_id           uuid NOT NULL REFERENCES users(id) ON DELETE CASCADE,
    biomarker_name    text NOT NULL,       -- Canonical, e.g. 'vitamin_d_25_hydroxy'
    display_name      text NOT NULL,       -- As shown on PDF
    category          text NOT NULL,       -- 'nutrients' / 'heart' / 'metabolic' / etc.
    numeric_value     numeric,
    string_value      text,                -- For qualitative markers
    unit              text,
    reference_low     numeric,
    reference_high    numeric,
    reference_string  text,
    collected_at      date NOT NULL,       -- Duplicated for query convenience
    flagged           text NOT NULL DEFAULT 'in_range'  -- 'in_range' / 'low' / 'high' / 'unknown'
);
CREATE INDEX idx_lab_biomarkers_user_marker
    ON lab_biomarkers(user_id, biomarker_name, collected_at DESC);

ALTER TABLE agent_actions
    ADD COLUMN lab_upload_id uuid REFERENCES lab_uploads(id) ON DELETE SET NULL;
CREATE INDEX idx_agent_actions_lab_upload ON agent_actions(lab_upload_id);
```

Cascade contract per research 5.1: user delete cascades everything; upload delete cascades biomarkers; `agent_actions.lab_upload_id` sets null on upload delete so audit history survives.

### 1.2 Upload surface at `/me/labs`

- Razor Page at `/me/labs` behind the existing `[Authorize]` policy.
- Drag-drop or file-picker; single-file per submit; MIME validation `application/pdf` only.
- Size cap: 10 MB (Function's PDFs are typically 1-3 MB; 100 MB per Health3's docs is possible but 10 MB is more than adequate for us).
- Server writes `lab_uploads` row with `parse_status = 'pending'` and `file_bytes` populated, then invokes the parser (§1.3) synchronously for phase 1. Async worker deferred until parse latency is a real problem.
- Response redirects to `/me/labs/{upload_id}` — a review-and-confirm surface showing every parsed biomarker with a "confirm" button. **The coach does NOT reference the upload until the user clicks confirm.** This is the non-negotiable safeguard against a hallucinated biomarker landing on a card.

### 1.3 LLM-based structured extraction

- New service in `SomaCore.Infrastructure.Labs`: `IFunctionHealthPdfParser`. Concrete: `AnthropicFunctionHealthPdfParser`.
- Uses the Anthropic Messages API — reuses the existing `AnthropicMessagesClient`, no new secret or Bicep. Same API key already in Key Vault.
- Prompt embeds a JSON schema mirroring the `lab_biomarkers` columns. Requests structured output (Anthropic's tool-use for schema-strict responses).
- On success: writes `lab_biomarkers` rows, sets `lab_uploads.parse_status = 'parsed'`, sets `parsed_at`, returns to the review-and-confirm surface.
- On failure (schema violation, malformed JSON, PDF too corrupt to read): writes `parse_error` (capped 2000 chars, redacted), sets `parse_status = 'failed'`, response shows the raw error to the admin only, generic-friendly message to the user.
- **Biomarker taxonomy checksum.** After parsing, cross-reference every extracted `biomarker_name` against a known-Function-panel taxonomy. Anything unrecognized flags as `parse_status = 'failed'` for admin review — this is the second layer of defense against hallucination. The taxonomy comes from the fixture set (§1.5).

### 1.4 Coach input-window extension

- `LiveDailyAgentService`: append a `latest_biomarkers` section to the input snapshot JSON. Format: most recent value per biomarker across all `confirmed`-status uploads for the user.
- Schema per snapshot: `{ biomarker_name, display_name, category, numeric_value, unit, reference_low, reference_high, flagged, collected_at, lab_upload_id }`.
- Cap: 60 biomarkers max in the snapshot (Function's typical panel is 100+; the coach doesn't need all of them for a daily card, and cost/latency matters). Prioritize by `flagged != 'in_range'` first, then by clinical relevance (a heuristic list agreed with Tai; TBD in the session).

### 1.5 Bounds validator changes (code, not schema)

- Add `AgentActionSource.UserUploadedLab` enum value.
- `AgentActionCategory.SupplementsFromLabs` already exists; validator currently rejects. Change: accept iff `Source == UserUploadedLab` AND `LabUploadId` FK is populated.
- Extend `AgentResponseValidator` to enforce the source + FK combination. Reject any action tagged `SupplementsFromLabs` without a valid `LabUploadId`.

### 1.6 Fixture set for prompt-fidelity testing

- Three real Function Health PDFs from Adam, Tai, Greg (with their consent for testing use — they're the three internal users; this is not a broader collection exercise).
- Golden-output JSON for each PDF, hand-verified against what the PDF actually says.
- Integration test that runs the parser against each fixture and asserts the extracted `lab_biomarkers` match the golden output within tolerance (numeric values exact, reference ranges exact, names may be normalized differently).
- **This test suite is the acceptance gate for shipping Phase 1.** Without it, we have no defense against the extraction prompt drifting or the model changing behavior.

### 1.7 Privacy doc updates

From research Section 6. Apply verbatim; Tai's signoff on this language is a hard gate for shipping.

- **Section D.2 edit.** The current line "Any lab data, clinical notes, or external documents" gets replaced with wording that permits user-uploaded lab data via the new Section F ingestion shape, explicitly still excludes raw PDF images, external clinical notes, and any data the user has not personally uploaded.
- **New Section F.** Full text drafted in research doc §6.2. Covers: what gets sent to Anthropic per invocation (structured biomarker values + reference ranges, no PDF images, no report metadata), what's stored in Postgres, retention, deletion cascade, user visibility on the /me/labs upload log, and the "confirm-before-coach-reads" workflow.

### 1.8 Exit criteria for Phase 1

- [ ] `dotnet build`, `dotnet test` green
- [ ] Migration applies cleanly to dev DB
- [ ] `/me/labs` accepts a real Function PDF and parses it into `lab_biomarkers` rows matching the golden fixture
- [ ] The three-user fixture set runs green in integration tests
- [ ] Coach card generated for a user with a confirmed upload references at least one biomarker by name with the correct `lab_upload_id` in the persisted `agent_action` row
- [ ] Bounds validator rejects an `agent_action` tagged `SupplementsFromLabs` when `LabUploadId` is null (test case)
- [ ] Privacy doc Section F is in the repo AND Tai has signed off in writing
- [ ] `/admin/agent` surfaces `lab_upload_id` and the parsed biomarker snippet for any invocation that referenced a lab — makes it possible to review outputs for hallucination

## Phase 1 is **NOT** in scope of the following

- Function OAuth / MCP client (that's Phase 2)
- Any change to the daily-card generation flow beyond the input-snapshot appendix
- Azure Blob Storage for PDFs (`bytea` in Postgres is fine for phase 1; three users × maybe one PDF/quarter each = trivial)
- Automatic re-parsing of prior uploads if the extraction prompt improves (users can manually re-upload; automation deferred)
- Multi-source lab support (only Function Health for phase 1; the `source` column exists for extensibility but Quest, Labcorp, etc. are out of scope)
- Historical trend visualization on `/me/labs` (the review-and-confirm surface just shows the latest upload's rows; trend charts deferred)
- Any user-facing "why is this out of range" explanations on `/me/labs` itself — that's the coach's job

---

## Phase 2 — the MCP-driven trigger layer (separate session)

Documented here for continuity so Phase 1's schema doesn't paint us into a corner. **Do not implement in this session.** This block is the seed for the Phase 2 session brief.

### 2.1 Goal

Detect when a user's Function panel has changed since their latest confirmed upload, and prompt them on `/me` to upload a fresh PDF.

### 2.2 Approach

- **Register a Function OAuth client.** Probe DCR against Function's Auth0 first per research §1.4. If DCR is refused, email `hello@functionhealth.com` to request a pre-registered client_id. This is the not-a-code-problem step that determines the session's start date.
- **Extend `external_connections`** with `source = 'function_health'` (existing table, no schema change).
- **New table** `function_summary_snapshots` per research §5.2 — one row per (user, daily poll) storing the three MCP tools' raw responses as `jsonb`.
- **Daily poller** in `SomaCore.IngestionJobs.Jobs.FunctionSummaryPoller` — same Container Apps Job pattern as `ReconciliationPoller`. Rate-limit-safe cadence (once per user per day; Function has no published limits so we start conservative).
- **Drift detector** — pure function that compares the latest `function_summary_snapshots` row against the summary that would have been true for the user's most-recent confirmed `lab_uploads.collected_at`. If out-of-range category counts differ, the user is "drifted" and needs a fresh PDF.
- **Banner on `/me`.** New render condition parallel to `WhoopNeedsReconnect`. When drift is detected, `/me` shows a warning banner: *"Your Function panel has changed since your last upload. [Upload the latest PDF](/me/labs)."*

### 2.3 What Phase 2 buys

- User doesn't have to remember to upload. The system tells them.
- Coach output stays honest — it references confirmed biomarkers only, but the /me experience nudges the user to keep them current.
- Adam and Tai can see "drift detected but not yet uploaded" on `/admin/health` — a new field surfacing users whose Function panel is stale.

### 2.4 Not-a-code-problem gate

Cannot start Phase 2 until Function's Auth0 client-registration path is confirmed working for us (either DCR or a pre-registered client_id). Adam should send that email at Phase 1 signoff so the answer is back by the time Phase 1 lands.

---

## Reference material

- **[`docs/seeds/function-health-integration-research.md`](seeds/function-health-integration-research.md).** The research pass this session brief was promoted from. Contains the full engineering-lift analysis, three example coach output cards, the biomarker taxonomy discussion, all source citations.
- **[`docs/seeds/function-health-integration.md`](seeds/function-health-integration.md).** The original seed. Kept for provenance.
- **[`docs/agent-voice-and-persona.md`](agent-voice-and-persona.md) + [`docs/agent-bounds.md`](agent-bounds.md).** Voice / bounds. The `supplements_from_labs` category in bounds is what this session unblocks.
- **[`docs/decisions/0006-three-layer-whoop-ingestion.md`](decisions/0006-three-layer-whoop-ingestion.md).** Ingestion pattern reference. Phase 1 deliberately does not follow this (no webhook + poller + on-open) because Function has no webhook. Phase 2 mirrors the reference more directly (poller-only shape).
- **[`docs/decisions/0011-ingestion-trace-contract.md`](decisions/0011-ingestion-trace-contract.md).** Trace contract. Phase 1 doesn't emit ingestion traces (upload is user-initiated), but Phase 2's poller must.
- **[`docs/decisions/0012-llm-card-before-rules-engine.md`](decisions/0012-llm-card-before-rules-engine.md).** The overall LLM-first architecture that made `supplements_from_labs` a bounds category.
- **[`docs/privacy-data-handling.md`](privacy-data-handling.md).** The doc Phase 1 revises in §1.7. Tai signs off before ship.
