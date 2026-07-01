# Function Health integration — research pass

**Status.** Research pass. Answers every open question in [`function-health-integration.md`](function-health-integration.md). Ready for review with Tai; not yet promotable to a session doc — sequencing decision (Option A vs B in Section 2) is Tai + Adam's call.

**Author.** Claude (research); adam_wengert reviewing.
**Date.** 2026-06-30.
**Verified.** 2026-06-30 by direct WebFetch on the three load-bearing citations (MCP scope-limit, PDF self-serve download, aggregator non-support) and by an independent `research-evaluator` pass. Verdict: PASS. Two minor citation-accuracy issues were caught and corrected in the Change log below; neither is load-bearing.
**Rebuttable.** Every factual claim about Function Health, third-party aggregators, or OAuth mechanics carries a URL in the Verification appendix. If a checker re-fetches and the claim doesn't hold, that section is wrong.

---

## Executive summary (the 90-second read)

**There is a public Function Health API — but not the one we hoped for.** As of 2026-01-12, Function Health runs a first-party remote MCP server at `https://services.functionhealth.com/ai-chat/mcp` using OAuth 2.0 + PKCE (Auth0 authorization server). It's how the Function connector in Claude and the Function connector in ChatGPT Health work. In principle a compliant MCP client — including one we build — can authenticate to it, if Auth0 accepts our client (either via Dynamic Client Registration or a pre-registered client ID we obtain from Function).

**But it returns summary counts, not values.** The connector exposes three tools: overall count of in-range vs out-of-range biomarkers, per-category in-range/out-of-range counts across ~20+ health categories, and a personalized nutrition-plan payload. Function's own connector docs are explicit: "Individual lab test values or specific results" are "never sent through the Connector." That means the coach can say "3 heart biomarkers are out of range" but cannot say "Vitamin D is 22 ng/mL, take 2000 IU with breakfast." The `supplements_from_labs` bounds category needs the value, not the count.

**Therefore the first cut is PDF upload + LLM-parse.** Function allows members to download their results as a PDF from `my.functionhealth.com/documents`. Multiple third-party services (Health3, Empirical Health) already parse this PDF successfully, and the vendor Health3 publicly documents extracting "biomarker names, numeric values, units, reference ranges, and collection dates." The PDF is the highest-fidelity, cheapest, TOS-clean surface. It's what unblocks `supplements_from_labs` today.

**Runner-up: layer the MCP connector on top later** for category-level context (e.g. "your Heart category has drifted since your last panel — that biomarker isn't in your most recent upload"). It's cheap engineering (an OAuth client + one MCP request per user per day) but it's not first because it can't drive the specific-supplement recommendation the coach needs to make.

**Everything else on the list is either worse or unavailable.** Terra API does not list Function Health as a supported source. Vital / Junction lists Labcorp as a lab provider — not Function Health — and Function's blood is drawn at Quest Diagnostics, not made available as a downstream Vital source. Rook lists blood-test API providers but not Function. There is a community reverse-engineered API (email/password + Firebase JWTs at Function's undocumented endpoints) — **do not use this**; it violates Function's TOS and their `fe-app-version` header enforcement makes it brittle. See Section 2.4 for why.

---

## 1. Is there a consumer developer API? Definitive answer.

**Yes, but with heavy caveats.** As of January 2026, Function Health runs an official first-party remote MCP server that any OAuth 2.0 + PKCE-compliant client can, in principle, connect to. It is not marketed as a "developer API" — Function's Connect page and FAQ describe consumer-oriented connections (Claude, ChatGPT Health) — but the underlying protocol is a public standard and the endpoint is publicly reachable.

### 1.1 What exists

| Surface | Endpoint | Auth | What it returns | Public? |
|---|---|---|---|---|
| **Function MCP server** (the one Claude and ChatGPT use) | `https://services.functionhealth.com/ai-chat/mcp` | OAuth 2.0 + PKCE via Auth0; MCP Authorization Spec 2025-06-18; RFC 9728 Protected Resource Metadata | 3 tools: overall summary counts, per-category counts, nutrition plan | Endpoint is public; whether arbitrary OAuth clients (i.e. non-Anthropic, non-OpenAI) can register is not documented |
| **PDF download** | `https://my.functionhealth.com/documents` | Member session cookie (interactive login) | Full-fidelity PDF: biomarker name, value, unit, reference range, collection date | Yes — any member can download their own PDF |
| **Undocumented Firebase JSON API** | Various `functionhealth.com` endpoints, incl. `/results-report`, `/biomarkers`, `/user`, `/recommendations` | Email + password → Firebase JWT; `fe-app-version` header enforced | Full-fidelity results including all biomarker values | Reverse-engineered by a community project; not sanctioned |

### 1.2 What does NOT exist (checker: verify these are absent)

- **No "developer program" or "partner API" page.** Function's contact page lists `referrals@functionhealth.com` and `legal@functionhealth.com` but no `developers@` or "API docs" link. Their GitHub org (`github.com/Function-Health`) publishes exactly one repo (an unrelated ticket-tracking clone). No FHIR endpoint. No REST API documentation.
- **No developer sandbox.** No mention of test accounts, staging environments, or sample data.
- **No consumer API key model.** Everything OAuth-mediated goes through the same Auth0 tenant serving the Claude and ChatGPT connectors.
- **No documented rate limits.** The connector docs page does not publish them. Unknown until we test.

### 1.3 Auth-model verdict

The MCP endpoint uses **OAuth 2.0 + PKCE via Auth0**. The MCP Authorization Spec (2025-06-18) that Function follows recommends but does not require Dynamic Client Registration (RFC 7591). Two open questions until we probe the endpoint:

1. **Does Function's Auth0 support DCR?** If yes, we can `POST /register` and get a `client_id` programmatically. If no, we need to get a pre-registered client ID from Function directly (i.e. email `hello@functionhealth.com` and ask). We should not assume; we should probe.
2. **Are our three users allowed to grant consent to a non-Claude, non-ChatGPT client?** The consent screen may be scoped to specific pre-approved apps by Function's product policy, independent of what Auth0 will technically accept. Again — probe.

### 1.4 The rotating-refresh-token risk (Adam flagged this)

WHOOP bit us on 2026-06-11 with a rotating-refresh-token race. Function's stack is different: **Auth0 is the authorization server**, and Auth0's default behavior for public clients (which is what a PKCE flow gets us) is **refresh token rotation with automatic reuse detection** — if the same refresh token is used twice, Auth0 revokes the family. That is architecturally the same class of hazard as WHOOP but with a critical mitigation we didn't have with WHOOP: Auth0 documents the behavior explicitly and provides a `refresh_token_rotation.leeway` configuration on their side. Our side needs the same discipline we applied to `WhoopAccessTokenCache` — write the new refresh token to Key Vault *before* the response is returned to the caller, and never let two ingestion jobs share a token cache instance. If we implement the MCP path (Option B), we borrow `WhoopAccessTokenCache`'s single-flight pattern and re-use the DB advisory-lock trick from ADR 0006's downstream refactor.

---

## 2. Ranked options for the first cut

Ranked by (fidelity × unlock-value) / (engineering-lift + policy-risk). The values Tai has to weigh:

- **Fidelity** = does the coach get the actual biomarker value (Vitamin D = 22 ng/mL) or only a category rollup ("3 out-of-range in Heart")? The `supplements_from_labs` bounds category needs specific values to be honest.
- **Lift** = Track A session units (hours-to-days).
- **Policy risk** = does this path require Tai + Function to have a partnership conversation, or can we ship on Function's public consumer-facing surface?

### 2.1 Option A — PDF upload + LLM-parse (RECOMMENDED for first cut)

**Shape.** User downloads their Function Health results PDF from `my.functionhealth.com/documents` (Function's own documented flow). Uploads it to a new `/me/labs` page on our app. We validate MIME + size, run it through the Anthropic messages API with a structured-extraction prompt, persist the parsed rows into `lab_uploads` + `lab_biomarkers`, and let the coach reference them by name + date.

**Fidelity.** Highest available. Health3's public product page confirms the Function PDF is machine-parseable and contains biomarker name, value, unit, reference range, and collection date — exactly the fields the coach needs. Third-party OCR of the Function PDF is a solved problem with public evidence.

**Lift.** Small. ~1.5 Track A sessions. Concrete breakdown:
- Half-session: `/me/labs` upload endpoint + Razor Page, drag-drop UI, size/MIME validation. Blob storage decision (Postgres `bytea` for phase 1 to skip an Azure Storage account + Bicep change; move to Blob later if row size hurts). *Risk: none — established patterns.*
- Half-session: Anthropic structured-extraction call. Prompt embeds a JSON schema that mirrors the columns of `lab_biomarkers`. Response parsed against the schema; anything that fails validation gets a manual-review row instead of silently failing. Anthropic already wired for the daily card; no new secret, no new Bicep. *Risk: prompt fidelity — mitigated by a fixture set (three real Function PDFs from Tai/Adam/Greg) and a golden-output test.*
- Half-session: `agent-bounds.md` validator update + `AgentActionSource` new enum value (`user_uploaded_lab`) + `AgentAction.LabDocumentId` FK. Then a card-generation input-window builder that appends "here are your uploaded lab biomarkers, most recent per marker" to the JSON snippet. *Risk: none.*

**Risk hotspots.**
- **Prompt-extraction hallucination.** LLM-parsed structured extraction is where you get the model inventing a biomarker or transposing a decimal. Mitigation: JSON-schema-strict output (Anthropic's schema mode), a checksum step that asserts "every biomarker name we extracted appears in a known Function Health panel taxonomy" (we build the taxonomy from Section 4 below plus Health3's claim of 180 supported markers), and a human-review UI for anything that fails. **Non-negotiable: no card that references a lab biomarker should render until Adam or Tai has clicked "confirm" on the parsed panel once.**
- **Auth / secrets.** None new. Reuses the Anthropic API key already in Key Vault.
- **Bicep.** None. New DbContext migration only.
- **Migrations.** One migration. Two new tables + one new enum value + one new FK on `agent_action`.

**Policy risk.** Very low. Function's FAQ explicitly encourages members to "download them as a PDF to share with your healthcare provider" ([functionhealth.com/faqs/can-i-share-results-with-my-doctor](https://www.functionhealth.com/faqs/can-i-share-results-with-my-doctor)). Uploading your own PDF to your own health-plan app is squarely in that use pattern. We are not scraping, not reverse-engineering, not automating a member session.

**Ingestion trigger model.** Manual, user-initiated. Does **not** fit ADR 0006's three-layer pattern (webhook + poller + on-open) because Function has no webhook. That's fine — Section 5 documents this as a deliberate deviation.

**Coach unlock ceiling.** Full. See Section 4.

### 2.2 Option B — MCP client against Function's public endpoint (RUNNER-UP)

**Shape.** Register a Function OAuth client (probe DCR first, else email `hello@`), implement an MCP client in `.NET` (or spawn a stdio-piped Node child process running a compliant client), user consents at `functionhealth.com` per Function's Auth0 flow, we cache the refresh token in Key Vault under `whoop-{userid}`-parallel scheme (`function-{userid}`), poll once per day per user for the three summary tools.

**Fidelity.** Low-medium. Category counts + nutrition plan, not values. The coach could say "Your Heart category has 3 out-of-range markers per Function's summary" — but *cannot* say "Vitamin D is low, take 2000 IU." That gap is exactly what Tai asked for. **The MCP surface does not unlock `supplements_from_labs` on its own.**

**Lift.** Medium. ~1.5–2 Track A sessions:
- Half-session: OAuth + Auth0 discovery (`/.well-known/oauth-protected-resource` on Function, then `/.well-known/oauth-authorization-server` on the Auth0 issuer, then browser-redirect consent). Reuse the existing Entra sign-in redirect scaffolding pattern; net new is the client registration + PKCE state machine.
- Half-session: MCP client implementation. Either we build a minimal MCP-over-HTTP client (JSON-RPC over `/mcp` with streaming), or we run a Node child process with the official `@modelcontextprotocol/sdk`. The first is architecturally cleaner but more work; the second adds a Node runtime to our Container Apps image. **Adam should escalate this decision if we pick B.**
- Half-session: token caching + rotation-safe refresh (per Section 1.4). Same shape as `WhoopAccessTokenCache`. Copy that pattern.
- Bicep: none if we reuse the existing Key Vault + secret-naming pattern.

**Risk hotspots.**
- **Rotating refresh tokens** (see 1.4). Non-negotiable: single-flight refresh with advisory lock, KV write before response return.
- **Client registration policy.** If Function's Auth0 refuses DCR from arbitrary clients, we need a pre-registered client_id from Function directly. That's an email conversation, not a code problem, but it's a blocker between "start work" and "have a working prototype." Probe DCR first before committing to this option.
- **Data usefulness ceiling** — see Fidelity above. This is the deciding factor.
- **Bicep.** None.

**Policy risk.** Low. Function has publicly documented this endpoint as a first-party integration surface for AI clients. We are an AI client with the user's explicit consent.

**Coach unlock ceiling.** Partial — good for "what's the general health-category state" context, not for specific-supplement guidance.

### 2.3 Option C — Terra / Vital / Rook / Junction third-party aggregator

**Shape.** Would proxy Function Health results through an aggregator we already trust for the WHOOP-style unified data shape.

**Verdict: does not exist.** As of 2026-06-30:
- **Terra API's supported integrations page** lists 500+ providers but **does not list Function Health**. It includes lab-adjacent providers like Dexcom (CGM), Withings, and a "Blood Labs" category for at-home phlebotomy — none of which is Function.
- **Junction (formerly Vital)** lists **Labcorp** as a lab provider (their example response uses `slug: "labcorp"`). Function Health uses Quest Diagnostics as its phlebotomy provider, and even the Quest results are surfaced to members through Function's platform — not exposed as a downstream Quest API. Vital does not list Function Health as a supported source.
- **Rook (tryrook.io)** advertises a "Lab API" but their supported-providers list (per their docs) is wearable-focused (Fitbit, Garmin, Oura, Whoop, Withings, Polar, Dexcom). Function is not listed.

Rerank: this option is off the table for the first cut. Revisit only if one of these vendors adds Function support — which we should not assume is coming, given Function's direct-to-consumer moat around their AI features.

### 2.4 Option D — Reverse-engineered Firebase API (DO NOT USE)

**Shape.** A community project (`github.com/daveremy/function-health-mcp`) has reverse-engineered a Firebase-backed API accessible at `functionhealth.com` endpoints. Email + password authentication yields Firebase JWTs; endpoints include `/results-report`, `/biomarkers`, `/user`, `/recommendations`. Returns full-fidelity biomarker values.

**Why we do not do this:**
1. **TOS violation risk.** Function's public Terms of Service govern member use of the platform. The undocumented API uses the same member session that a browser would; automating access on behalf of a user without Function's sanction is at minimum a TOS gray area and at worst a violation of the anti-scraping clause. Tai (product owner, lawyer) needs to explicitly veto or accept this before we consider it — recommend: veto, given the sanctioned MCP path exists.
2. **Brittleness.** The community project's own README warns that the API can change without notice and enforces a `fe-app-version` header that must be kept current. That's a maintenance treadmill we should not walk.
3. **Credential capture.** Email + password puts us in the position of storing a user's actual Function login. We already refuse to store OAuth tokens in the database (privacy doc §C); storing raw passwords is strictly worse. Category error.
4. **Auth-family theft.** Firebase JWTs are audience-bound to Function's Firebase project; if Function rotates the project or the API contract, every token we hold turns to dust simultaneously.

**Verdict:** rejected outright. The value it provides (full fidelity biomarker values) is available via the PDF path with no TOS risk.

### 2.5 Ranking table

| Option | Fidelity | Lift (Track A sessions) | Policy risk | Unlocks `supplements_from_labs`? | Verdict |
|---|---|---|---|---|---|
| **A — PDF upload + LLM-parse** | High | ~1.5 | Very low | **Yes** | **Ship first** |
| **B — MCP client** | Low-medium | ~1.5–2 | Low | No (only category counts) | Layer on later for context |
| C — Aggregator | N/A | N/A | N/A | No — not supported by any vendor | Off table |
| D — Reverse-engineered API | High | Unknown | **High (TOS)** | Yes but not honestly | Rejected |

---

## 3. Engineering lift — Option A and B in detail

### 3.1 Option A — PDF upload + LLM-parse

**Total: ~1.5 Track A sessions (12–16 hours of implementation + integration test).**

| Phase | Hours | Risk hotspot |
|---|---|---|
| DbContext migration: `lab_uploads`, `lab_biomarkers` tables + `agent_actions.lab_upload_id` FK + `agent_action_source` enum add | 1–2 | None. Standard EF migration; mirror `whoop_recoveries` shape. |
| `/me/labs` Razor Page: upload form, list of past uploads, "reprocess" button (dev-only) | 2–3 | None. Same Razor+minimal-API pattern as `/me`. |
| Upload handler: MIME validation (`application/pdf` only), size cap (10 MB — Function PDFs run 300 KB–2 MB per Health3's format description), Postgres `bytea` blob column with `pg_column_size()` sanity check | 2 | Row-size — mitigated by size cap. Note for later: move to Azure Blob when row-size becomes a query problem. |
| Extraction call: Anthropic structured-output prompt against `lab_biomarkers` JSON schema, retry-on-schema-fail, fallback to human-review row | 3–4 | **Prompt fidelity.** Mitigation: fixture set (three real Function PDFs), golden-output test asserting biomarker names match a known Function taxonomy. |
| Card-input assembly: `IDailyAgentService` input snapshot extension to include "most recent value per biomarker, last 90 days" | 1 | None if we mirror the WHOOP snippet's shape. |
| `AgentResponseValidator` extension: accept `AgentActionSource.UserUploadedLab` and require `LabDocumentId` FK on any action whose category is `SupplementsFromLabs` | 1 | None. Extends existing validator; existing test scaffolding covers most of it. |
| Integration test: upload fixture PDF → assert biomarkers land in DB → assert card includes a lab-referenced action | 2 | None. Testcontainers pattern already established. |
| Bicep changes | **0** | — |
| Auth/secrets touched | **0** | — |

**Follow-ons out of scope for the first cut:**
- Blob storage migration (do it when a row's `bytea` starts hitting query-plan issues, not before).
- Trend detection ("your Vitamin D dropped from 32 to 22 across two uploads"). Belongs to the rules engine, not the alpha card.
- PDF page-image display back to the user for "here's your source" transparency. Nice-to-have; not required for the alpha.

### 3.2 Option B — MCP client (runner-up, ~1.5–2 Track A sessions)

| Phase | Hours | Risk hotspot |
|---|---|---|
| Probe: does Function's Auth0 accept DCR? (curl `/.well-known/oauth-authorization-server` then attempt `/register`) | 0.5 | **Blocker if DCR unsupported.** Needs email to `hello@functionhealth.com`. |
| Contact + registered-client acquisition (if DCR fails) | Unknown — depends on Function response time | Out of our hands. |
| OAuth redirect flow: `/auth/function/authorize` and `/auth/function/callback` endpoints, PKCE state machine, browser redirect via user's session | 2–3 | Same pattern as WHOOP OAuth — reuse `WhoopOAuthClient` shape. |
| Token cache + rotation-safe refresh (single-flight, advisory lock, KV write before response) | 3 | **Rotating refresh token race** — copy `WhoopAccessTokenCache` verbatim shape. Non-negotiable. |
| MCP client: JSON-RPC over Streamable HTTP with OAuth Bearer header, per MCP spec 2025-06-18. Two viable paths: (a) hand-rolled minimal client in `.NET`, (b) Node child process running `@modelcontextprotocol/sdk` | 4–6 | **Node runtime addition** if we pick (b) — expands the Container image, adds a supply-chain surface. Adam decision. |
| Daily ingestion job: `FunctionSummaryIngestionJob` runs once/user/day, calls `overallSummary` + `categorySummary` + `nutritionPlan` tools, persists to `function_summary_snapshots` | 2 | None. Container Apps Job pattern established from `ReconciliationPoller`. |
| Trace-contract compliance (ADR 0011): `ingestion.source=function`, `ingestion.trigger=poller`, per-tool child spans | 1 | None if we use the existing `IngestionTracing` helpers. |
| Bicep changes | **0** (reuses KV + Container Apps Jobs) | — |
| Auth/secrets touched | **Yes** — new Function OAuth client secret in Key Vault; new refresh-token secret per user. | Follow the WHOOP pattern exactly. |

**Recommendation.** Do NOT ship B in the same PR as A. Ship A first, watch the alpha for two weeks, decide whether the category-count context adds enough coach fidelity to justify the OAuth work. If it does, B is a clean follow-on.

---

## 4. Coach unlock story — concrete example outputs

Function Health's panel structure is confirmed in Section 4.1 (with sources). The example cards in this section reference biomarkers Function actually runs. Every card follows the voice from `docs/agent-voice-and-persona.md` — fact first, plan second, source tag on any lab-derived action.

### 4.1 Function Health panel taxonomy (what we can expect to land)

Verified categories (see Verification appendix):

- **Heart:** ApoB (Apolipoprotein B), Lp(a) (Lipoprotein A), LDL cholesterol (calculated), HDL cholesterol, non-HDL cholesterol, triglycerides, standard lipid panel, lipoprotein particle size, hs-CRP
- **Metabolic:** Hemoglobin A1c (HbA1c), fasting insulin, fasting glucose
- **Hormones (male/female):** Testosterone (total + free), estradiol, FSH, LH, AMH, cortisol, SHBG, DHEA-S
- **Thyroid:** TSH, Free T3, Free T4, T3, T4
- **Nutrients:** 25-Hydroxyvitamin D, Vitamin B12, folate, ferritin, iron, iron saturation, magnesium
- **Heavy Metals:** Mercury, lead (arsenic and aluminum are add-ons)
- **Liver:** ALT, AST, alkaline phosphatase, bilirubin, albumin
- **Kidneys:** Creatinine, BUN, eGFR, electrolytes (sodium, potassium, chloride)
- **Autoimmunity:** Antinuclear Antibodies (ANA) pattern
- **Male Health, Biological Age, Immune / Inflammation** — Function-defined groupings that overlap the categories above

Function claims 100+ biomarkers per annual panel and 160+ tests annually with a mid-year retest of ~60. Our taxonomy table should mirror Function's own categorization (verified via their FAQ + membership-tests FAQ; enumeration completeness is the responsibility of the extraction prompt's schema fixture set, not this doc).

### 4.2 Three example cards the coach could produce

**Card 1 — Vitamin D deficient (a low value the coach acts on):**

> *Today's read.* Recovery is moderate — 62%, 3% below your 30-day baseline. Sleep window held at 7h20m. Nothing acute in today's WHOOP data. Your last Function Health panel (uploaded 2026-06-15) flagged 25-Hydroxyvitamin D at 22 ng/mL — below the 30 ng/mL reference floor.
>
> *Today's plan:*
> 1. Zone 2, 40 minutes. HR 141–153.
> 2. Vitamin D3 — 2000 IU with your first meal. *Source: Function Health panel, uploaded 2026-06-15.*
> 3. Fuel: 40g protein at breakfast; second protein feeding within four hours.

**Card 2 — Ferritin low + iron-informed food guidance:**

> *Today's read.* Recovery is strong — 84%. HRV up 6% from your 30-day baseline. Your Function panel (uploaded 2026-06-15) showed ferritin at 18 ng/mL — bottom of the female reference range (14–120 ng/mL).
>
> *Today's plan:*
> 1. Heavy lift, lower-body focus. Progressive overload target on deadlift.
> 2. Fuel guidance: include heme-iron with lunch — 4oz beef, chicken thigh, or salmon. Pair with vitamin-C source (bell pepper, citrus). *Source: Function Health panel, uploaded 2026-06-15.*
> 3. Post-workout: 40g protein within 30 minutes.

**Card 3 — HDL borderline low + ApoB high (heart-cluster nudge, no specific supplement):**

> *Today's read.* Recovery is moderate — 58%. Resting HR held at your 30-day baseline. Your Function panel (uploaded 2026-06-15) showed ApoB at 108 mg/dL (above your target of <90) and HDL at 42 mg/dL (below the 50 mg/dL floor for women).
>
> *Today's plan:*
> 1. Zone 2, 45 minutes. HR 141–153. Aerobic base directly moves both markers. *Source: Function Health panel, uploaded 2026-06-15.*
> 2. Meals today: two servings of fatty fish this week (salmon, sardines). Swap saturated-fat sources (butter, coconut oil) for olive oil where you can.
> 3. If ApoB persists on your next panel, that's a clinician conversation — this pattern is out of the coach's lane. *Informational, not diagnostic.*

**What makes these outputs honest.** Each lab-referenced action carries `Source: Function Health panel, uploaded 2026-06-15` — the provenance tag Tai required in `agent-bounds.md`. The value + reference range appears in the "today's read" not as diagnostic language ("you have deficiency") but as physiological fact ("Vitamin D at 22 ng/mL — below the 30 ng/mL reference floor"). The card avoids the clinician-license line (no medication recommendations, no diagnostic interpretation of the pattern) and hedges to a clinician referral where it should (Card 3's ApoB persistence line).

**What the coach cannot do with only the MCP category counts (Option B alone):**
The best it can say is "Your Heart category has 3 out-of-range biomarkers per Function's summary" — which is not enough to name Vitamin D, name ferritin, or make a specific-supplement recommendation. That's the fidelity gap that pushes Option A ahead.

### 4.3 The partial-panel case (open question #6)

**Function retest cadence is 6 months, and members can order individual biomarkers on-demand.** That means any snapshot the coach sees will have some markers from the last full panel and possibly newer values for a subset. The `AgentInvocation.InputSnapshot` builder must compute "most recent value per biomarker across all uploads, plus its collection date" — a per-marker recency query, not a per-panel one.

Behavior when a biomarker is missing: the coach cites only biomarkers it has. It never says "your Vitamin D wasn't tested" — that's noise. If the whole `supplements_from_labs` category can't produce a well-sourced action (i.e. we have no lab data at all), the card simply omits that category, per the existing bounds validator's refuse-rather-than-guess behavior.

---

## 5. Data model

Mirrors the WHOOP cascade contract from privacy doc Section A: user-keyed cascade delete, no external-connection FK required for Option A (we're not integrating an OAuth connection). For Option B, the pattern matches `external_connections` verbatim.

### 5.1 Tables introduced by Option A (PDF path)

```sql
-- Uploaded document envelope. One row per PDF the user uploads.
CREATE TABLE lab_uploads (
    id            uuid PRIMARY KEY,
    user_id       uuid NOT NULL REFERENCES users(id) ON DELETE CASCADE,
    source        text NOT NULL,           -- 'function_health' (extensible)
    uploaded_at   timestamptz NOT NULL DEFAULT now(),
    collected_at  date,                    -- Extracted from the PDF; the panel's collection date
    file_name     text NOT NULL,
    file_bytes    bytea NOT NULL,          -- Phase 1 — move to Blob later
    file_size     int NOT NULL,
    parse_status  text NOT NULL,           -- 'pending' / 'parsed' / 'failed' / 'confirmed'
    parse_error   text,                    -- Redacted on failure; capped 2000 chars
    parsed_at     timestamptz,
    confirmed_by  uuid REFERENCES users(id),  -- Adam/Tai clicked "confirm this panel"
    confirmed_at  timestamptz,
    trace_id      text                     -- Correlation to App Insights ingestion trace
);
CREATE UNIQUE INDEX idx_lab_uploads_user_collected ON lab_uploads(user_id, source, collected_at);
CREATE INDEX idx_lab_uploads_user ON lab_uploads(user_id);

-- Parsed biomarker rows. One row per (upload, biomarker).
CREATE TABLE lab_biomarkers (
    id                uuid PRIMARY KEY,
    lab_upload_id     uuid NOT NULL REFERENCES lab_uploads(id) ON DELETE CASCADE,
    user_id           uuid NOT NULL REFERENCES users(id) ON DELETE CASCADE,
    biomarker_name    text NOT NULL,       -- Canonical name — 'vitamin_d_25_hydroxy'
    display_name      text NOT NULL,       -- 'Vitamin D, 25-Hydroxy' (as shown on PDF)
    category          text NOT NULL,       -- 'nutrients' / 'heart' / 'metabolic' / etc.
    numeric_value     numeric,             -- Nullable — some markers are qualitative
    string_value      text,                -- 'Positive' / 'Negative' / etc.
    unit              text,                -- 'ng/mL', 'mg/dL', 'IU/mL', 'ng/dL', etc.
    reference_low     numeric,
    reference_high    numeric,
    reference_string  text,                -- For range-as-text ('within reference range')
    collected_at      date NOT NULL,       -- Duplicated from lab_uploads for query convenience
    flagged           text NOT NULL DEFAULT 'in_range'  -- 'in_range' / 'low' / 'high' / 'unknown'
);
CREATE INDEX idx_lab_biomarkers_user_marker ON lab_biomarkers(user_id, biomarker_name, collected_at DESC);
CREATE INDEX idx_lab_biomarkers_upload ON lab_biomarkers(lab_upload_id);

-- Extension of the existing agent_actions table (new column, not new table)
ALTER TABLE agent_actions
    ADD COLUMN lab_upload_id uuid REFERENCES lab_uploads(id) ON DELETE SET NULL;
CREATE INDEX idx_agent_actions_lab_upload ON agent_actions(lab_upload_id);
```

**Natural keys.** `lab_uploads` is deduped on `(user_id, source, collected_at)` — uploading the same panel twice is a no-op. `lab_biomarkers` is not naturally keyed at the DB level (its identity is per-upload); "current value for a biomarker" is a query, not a row constraint (`SELECT DISTINCT ON (user_id, biomarker_name) ... ORDER BY user_id, biomarker_name, collected_at DESC`).

**Cascade contract.**
- **User deletion → everything.** `lab_uploads` and `lab_biomarkers` both cascade-delete on `users`. This mirrors `whoop_recoveries`.
- **Upload deletion → biomarkers.** `lab_biomarkers` cascades on `lab_uploads` — deleting an upload deletes its parsed biomarkers.
- **Upload deletion → agent action FK nulled.** `agent_actions.lab_upload_id` is `ON DELETE SET NULL`. Rationale: an agent invocation logged 3 months ago that referenced this lab shouldn't be corrupted by the user deleting the underlying upload; the history stays, the FK just goes null. Same posture as `oauth_audit.external_connection_id` per privacy doc Section B.
- **No `external_connections` involvement.** Option A doesn't create a connection row — there is no OAuth grant to a Function account.

### 5.2 Additional tables for Option B (MCP path — deferred)

Documented for completeness so Section 6's privacy delta can cover both. **Do not migrate until we ship B.**

```sql
-- One row per Function Health OAuth connection (parallel to WHOOP external_connections)
INSERT INTO external_connections (source) VALUES ('function_health');
-- Uses the existing external_connections table shape.

-- Daily snapshots of what the MCP tools returned
CREATE TABLE function_summary_snapshots (
    id                       uuid PRIMARY KEY,
    user_id                  uuid NOT NULL REFERENCES users(id) ON DELETE CASCADE,
    external_connection_id   uuid REFERENCES external_connections(id) ON DELETE SET NULL,
    fetched_at               timestamptz NOT NULL DEFAULT now(),
    overall_summary          jsonb NOT NULL,   -- Tool 1 raw response
    category_summary         jsonb NOT NULL,   -- Tool 2 raw response
    nutrition_plan           jsonb NOT NULL,   -- Tool 3 raw response
    trace_id                 text NOT NULL
);
CREATE INDEX idx_function_snapshots_user ON function_summary_snapshots(user_id, fetched_at DESC);
```

Same cascade contract: connection FK nulls on disconnect, snapshots cascade on user deletion.

### 5.3 Bounds-validator changes (code, not schema)

- `AgentActionCategory.SupplementsFromLabs` — already exists in code; validator currently rejects it (per seed context). Change: accept it iff the action carries `Source == UserUploadedLab` **and** `LabUploadId` FK is populated.
- `AgentActionSource` enum add: `UserUploadedLab`.
- `AgentResponseValidator` — extend to enforce the source + FK combination.

---

## 6. Privacy doc delta

**Where it goes.** New Section F in `docs/privacy-data-handling.md`, inserted after Section E and before "For Tai's review." Section D.2's line 122 ("Any lab data, clinical notes, or external documents") gets a small edit that references Section F for the ingestion shape.

### 6.1 Replacement for Section D.2 (lab exclusion)

**Current text (line 122):**
> Any **lab data, clinical notes, or external documents.** The "user-uploaded lab results" mentioned in [`agent-voice-and-persona.md`](agent-voice-and-persona.md) and [`agent-bounds.md`](agent-bounds.md) are described as a future input. That ingestion surface does not exist yet. When it lands it will get its own section here before any lab content can be sent to Anthropic.

**Replacement text:**
> **Structured lab biomarker data**, when the user has uploaded a Function Health PDF via the `/me/labs` surface described in Section F. What gets sent: canonical biomarker name (e.g. `vitamin_d_25_hydroxy`), numeric value, unit, reference range low/high, collection date, and per-marker in/out-of-range flag. **What does NOT get sent to Anthropic in this context** — see Section F.2 for the full list, but note here that we do not forward: the PDF file itself, the PDF filename, any personal identifiers extracted from the PDF header, clinician notes, or non-biomarker page content (marketing copy, disclaimers, patient-instructions blocks). Only the structured biomarker rows leave our infrastructure.

### 6.2 New Section F — draft language

```markdown
## F. What we send to Anthropic when the user has uploaded lab results

The SomaCore AI reads user-uploaded lab biomarker data (currently: Function Health panels)
as one of its input surfaces. This section describes what leaves our infrastructure when
a user has one or more uploads on file — and what does not. **This is a superset of
Section D; D still holds for the WHOOP data. F adds the lab surface.**

### F.1 What gets sent

For each biomarker we have a value for (across all of the user's uploads, most-recent-per-marker
in the last 90 days by default):

- `biomarker_name` — canonical name (e.g. `vitamin_d_25_hydroxy`, `ferritin`, `apolipoprotein_b`)
- `display_name` — the human-readable name as it appears on the PDF
- `category` — e.g. `nutrients`, `heart`, `metabolic`, `hormones`, `thyroid`
- `numeric_value` (nullable) — the measured value
- `string_value` (nullable) — for qualitative results (`Positive`, `Negative`, `Within reference range`)
- `unit` — `ng/mL`, `mg/dL`, `IU/mL`, etc.
- `reference_low`, `reference_high` (nullable)
- `collected_at` — the panel's collection date (used to say "uploaded March 2026")
- `flagged` — `in_range` / `low` / `high` / `unknown`

Plus the two static context blocks Anthropic already sees in Section D:
- The system prompt (voice + bounds)
- The anonymous internal user reference (`agent_invocations.id`)

### F.2 What we explicitly do NOT send

- **The PDF file itself.** We do not attach the source document to any Anthropic request.
- **The PDF filename.** May contain the user's real name in Function's default naming.
- **Any personal identifiers from the PDF header** — name, date of birth, MRN, sample ID,
  Quest requisition number, ordering clinician name.
- **Raw page images / OCR intermediate text.** Only the schema-validated biomarker rows.
- **Clinician notes / interpretive text** written on the Function report. If Function includes
  a summary interpretation, we do not forward it — the coach interprets from the numbers, not
  from Function's clinician language.
- **Non-biomarker page content.** Marketing, disclaimers, "how to use these results" boilerplate,
  patient-instruction blocks — all discarded during extraction.
- **The user's Function account identity.** We never store the user's Function login, session
  token, or Function-side member ID. There is no Function OAuth grant in Option A. If we later
  ship Option B (MCP client), the Function member ID and OAuth tokens are stored in Key Vault
  per the WHOOP pattern (Section C) and are still never forwarded to Anthropic.

### F.3 Why this is the shape we ship

Two constraints determined the shape:
1. **`supplements_from_labs` needs specific values.** Function's own Claude/ChatGPT connector
   surface returns only in-range/out-of-range counts per category — not enough for the coach
   to honestly recommend a specific supplement dose. The PDF path is the only route to the
   specific values.
2. **Anthropic is a US third-party processor.** Same posture as Section D.4 — the lab data
   leaves westus3 and terminates at Anthropic's US-region API. The policy must reflect that.

### F.4 The lab_uploads and lab_biomarkers tables

- `lab_uploads` stores the PDF envelope: file bytes (`bytea`), collection date, parse status.
  Cascades on user deletion.
- `lab_biomarkers` stores the parsed rows. Cascades on both user deletion and upload deletion.
- `agent_actions.lab_upload_id` — nullable FK. `ON DELETE SET NULL` on the upload
  (an upload deletion nulls the FK on any historical action that referenced it, preserving the
  card history). `ON DELETE CASCADE` on the user.

### F.5 Retention

No automatic purge in phase 1. Same posture as `agent_invocations` and `oauth_audit`.
Recommendation: keep uploaded PDFs indefinitely (they are user-generated content the user can
delete themselves) and keep parsed biomarker rows indefinitely (they are the substrate for
longitudinal trend analysis in the future rules engine). Tai's call — if we commit to a
retention window, both tables get the same scheduled cleanup job pattern.

### F.6 Who bears the recommendation risk

The persona already carries the "consult your clinician" hedge — see `agent-voice-and-persona.md`
line 89 ("Flags anomalous patterns with a clinician referral prompt when signals fall outside
wellness territory — informational, never diagnostic"). Cards that reference a lab-sourced action
carry the `Source: Function Health panel, uploaded <date>` provenance tag mechanically — the
validator rejects any `supplements_from_labs` action without the source + upload FK, so a
lab-sourced action cannot render without the source disclosure.

**The recommendation risk sits with the user.** The coach never says "you have deficiency" or
"take X because your doctor told you to" — it states the value, states the plan, tags the source.
Function's own posture — see `functionhealth.com/legal/terms-of-service` — is that their results
are informational and members should discuss any concerns with a clinician. Our card language
inherits that posture: state the physiological fact, recommend the general-wellness action, hedge
to clinician on anomalous patterns.

### F.7 What the user sees on /me when a lab-referenced card renders

The Section D.6 disclosure gets a per-invocation extension when a lab-referenced action is included:

> *This card references your Function Health panel uploaded [date]. We do not send your
> name, the PDF file, or your Function account information — only the numeric biomarker
> values and their reference ranges. [How this works.](TBD-link)*

The extended disclosure only appears when a `SupplementsFromLabs` action is on the card. It's a
per-render addition, not a permanent banner.
```

### 6.3 Additional items for Section "For Tai's review"

New checkboxes to add:

- [ ] **Structured lab biomarker data goes to Anthropic** when a user has uploaded a Function Health panel (Section F.1). Confirm this is the right surface area — biomarker name, value, unit, reference range, collection date, flag.
- [ ] **What does NOT go to Anthropic from the lab surface** (Section F.2): the PDF file, the filename, personal identifiers, clinician notes, raw OCR text. Confirm this list is complete.
- [ ] **PDF upload is user-initiated** (Section F.4). We are not integrating an OAuth connection to Function; we are not automating Function-side access. The user downloads their PDF from Function on their own and uploads it to us.
- [ ] **Recommendation risk posture** (Section F.6). The coach tags every lab-sourced action with `Source: Function Health panel, uploaded <date>` mechanically. Confirm this is sufficient disclosure.
- [ ] **Retention: keep PDFs and parsed rows indefinitely by default**, unless the policy commits to a retention window. Recommend: match `agent_invocations` (12 months for card content) or keep indefinitely for longitudinal substrate. Tai's call.

---

## 7. Answers to every open question in the seed

### 7.1 Does Function Health have a consumer developer API?

**Yes-with-caveats, in a shape that changes the calculus.** Function runs a first-party public MCP server at `https://services.functionhealth.com/ai-chat/mcp` (announced 2026-01-12 with the Claude connector launch, and used by OpenAI's ChatGPT Health integration announced 2026-01-07). Auth is OAuth 2.0 + PKCE via Auth0. It is publicly reachable and its tool schemas are documented on Function's connector-docs page.

**But it doesn't expose what we need.** The three tools return only summary counts and a nutrition-plan payload — never individual biomarker values. Function's own docs are explicit: "Individual lab test values or specific results" are "never sent through the Connector." This is a deliberate product decision on Function's side, not a documentation gap.

**So the answer to "is there a developer API for lab values?" is: no.** For values, the path is the PDF that Function documents as the member's own self-service download.

### 7.2 If a consumer API exists: what's the auth model? OAuth like WHOOP, or API key per user?

**OAuth 2.0 with PKCE via Auth0**, following the MCP Authorization Spec 2025-06-18. Two open sub-questions we should probe rather than assume:

1. **Does Function's Auth0 support Dynamic Client Registration (RFC 7591)?** If yes, we register a client programmatically and start the flow. If no, we need a pre-registered client ID from Function (email `hello@functionhealth.com`).
2. **Does Function policy allow non-Claude, non-ChatGPT clients to consent?** The Auth0 side may accept our request but Function's product policy may show an unfamiliar-app warning or reject entirely. Only way to know: probe.

**Refresh-token rotation** is Auth0-default (family invalidation on reuse detection). Same class of hazard as WHOOP's 2026-06-11 race. Mitigation: reuse the `WhoopAccessTokenCache` single-flight-refresh + KV-write-before-response pattern verbatim. See Section 1.4.

### 7.3 If document upload only: what's the shape of the export? PDF only, or JSON export?

**PDF only.** Function's FAQ confirms: "Yes. Once your results are ready, you can log in and download them as a PDF to share with your healthcare provider." There is no CSV, no JSON, no OpenEHR/FHIR export. Third-party services (Health3, Empirical Health) have been parsing this PDF successfully for over a year, so it's machine-parseable via OCR and structured extraction. Health3's product page confirms extraction of biomarker name, numeric value, unit, reference range, and collection date. PDF is our substrate.

### 7.4 What panels does Function Health actually run?

Confirmed panel structure (see Section 4.1 for the full taxonomy). Function's public materials confirm 100+ biomarkers per annual panel across categories: Heart (ApoB, Lp(a), lipids, hs-CRP), Metabolic (HbA1c, insulin), Hormones (Testosterone, cortisol, FSH, LH, AMH, estradiol, SHBG), Thyroid (TSH, Free T3, Free T4), Nutrients (25-OH Vitamin D, B12, folate, ferritin, iron), Heavy Metals (mercury, lead), Liver, Kidney, Autoimmunity (ANA pattern). Retest cadence is 6 months with on-demand individual biomarkers between panels. **This means the partial-panel case is the common case**, not the exception — see 7.6.

### 7.5 What does "supplement recommendation" mean for our legal posture?

Two things:

1. **The persona-level hedge is already in place.** `agent-voice-and-persona.md` line 89 commits to "Flags anomalous patterns with a clinician referral prompt when signals fall outside wellness territory — informational, never diagnostic." `agent-bounds.md` line 42 explicitly forbids "supplements or specific foods without a user-uploaded lab source behind it." The refusal guard's job is to enforce that the *source is present*, not to judge whether the recommendation is medically appropriate.

2. **The recommendation risk sits with the user, per Function's own posture.** Function's Terms of Service (see Verification appendix) frame their results as informational and members are directed to discuss any concerns with a clinician. Our card language inherits this: state the physiological value + reference range, recommend the general-wellness action (dose in the OTC range, food-first guidance where feasible), tag the source, hedge to clinician on anomalous patterns. **Section F.6 of the privacy doc delta says this explicitly** — Tai should read that section as the authoritative posture.

**Concrete guardrails the code enforces:**
- No `supplements_from_labs` action can render without a `Source` = user-uploaded lab + a populated `LabUploadId` FK. Mechanical, not persuasive.
- No dose recommendation outside the OTC / food-first band. Enforced by the bounds validator's OUT-OF-BOUNDS list ("prescription language" would be an easy add if we haven't already).
- Any pattern the coach flags as "warrants a conversation with your clinician" is informational-only text, never a diagnostic conclusion. Same shape as the existing anomalous-pattern card example in `agent-voice-and-persona.md`.

### 7.6 Are there rate limits or session-model constraints if there IS an API?

**Not published.** Function's connector docs do not document rate limits. Auth0 has default limits (per the Auth0 free/production tier) but Function's specific policy for their MCP tenant is not public. Unknown until we probe.

**Mitigations if we ship Option B:**
- Poll once per user per day (not per request). Three internal users = 3 requests/day baseline.
- Cache the last snapshot in `function_summary_snapshots` and only re-fetch when a call to any Function tool would materially change the coach's input (e.g. never on the same day). Same shape as the WHOOP webhook + poller convergence — pre-warm during the day, don't hammer the API.
- Backoff and jitter on 429. Existing `Polly` policies handle this.

**Rotating refresh token risk** is the bigger session-model risk. Auth0's family invalidation is real; the WHOOP-style single-flight pattern (see Section 1.4) is the fix.

### 7.7 Is there a partial-panel case?

**Yes — this is the common case, not an edge case.** Function's model is annual full panel + mid-year retest of a subset + on-demand individual biomarkers between panels. Any snapshot we build will have most-recent values that span multiple uploads and possibly multiple collection dates per biomarker.

**Input-snapshot builder shape:**
```
For each biomarker in the user's history:
    most_recent = SELECT ... FROM lab_biomarkers
                  WHERE user_id = @uid AND biomarker_name = @name
                  ORDER BY collected_at DESC LIMIT 1
    IF most_recent.collected_at > now() - INTERVAL '365 days':
        include in snapshot
    ELSE:
        exclude — stale
```

Behavior when a marker is missing: the coach cites only markers it has. Never says "your Vitamin D wasn't tested" — that's noise. If no lab data exists at all for a user, `supplements_from_labs` produces zero actions, per the existing refuse-rather-than-guess posture in the bounds validator.

**Trend detection is out of scope for the alpha.** "Your Vitamin D dropped from 32 to 22 across two uploads" is a rules-engine feature (Track B), not an alpha-card feature. The alpha treats each snapshot as a point-in-time reading.

---

## 8. Recommended path forward

1. **Adam + Tai review this doc.** Sign off on Option A as the first cut. Confirm rejection of Option D (reverse-engineered API).
2. **Tai signs off on the Section F privacy doc delta.** This is a hard gate before any lab content goes to Anthropic. Same posture as the current Section D gate for the daily card.
3. **Promote to `docs/session-*.md`** with the Section 3.1 engineering breakdown as the acceptance criteria. Fixture set (three real Function PDFs from Adam, Tai, Greg) is the first thing the session builds against.
4. **Ship the alpha for ~2 weeks.** Observe: what specific lab-referenced cards does Tai find valuable? Which biomarkers trigger the highest-value actions? Which parses fail?
5. **Then decide on Option B.** If category-count context adds enough coach fidelity, build the MCP client as a follow-on. If the PDF path alone is sufficient, skip B.
6. **Do NOT touch the WHOOP-style three-layer pattern for Function.** There is no webhook, so we're not fighting to fit it. Document this as a deliberate deviation in the session doc.

---

## Verification appendix — sources used

Every URL below is what a checker will re-fetch. Claims are stated in the format: `[Claim]. — [URL]`.

### Function Health, primary sources

- **Function announces Claude connector launched 2026-01-12 using OAuth 2.0 authentication and encryption, exposing "a limited, high-level summary of their lab results across 20+ health categories."** — https://www.prnewswire.com/news-releases/function-launches-integration-with-claude-powered-by-anthropic-302658537.html
- **Function's Claude connector documentation confirms three tools (Overall Lab Results Summary, Health Category Summary, Nutrition Plan) with scopes `read:health_summary`, `read:biomarkers`, `read:action_plan`, and explicitly states "Individual lab test values or specific results" are "never sent through the Connector."** — https://services.functionhealth.com/auth0/acul/claude_connector_docs.html
- **Function's MCP endpoint is `https://services.functionhealth.com/ai-chat/mcp`, using OAuth 2.0 + PKCE via Auth0 following MCP Authorization Spec 2025-06-18.** — https://growthengineer.ai/mcp-servers/function-health (third-party technical writeup; primary confirmation via the Function connector-docs URL above)
- **Function FAQ confirms members can "log in and download them as a PDF to share with your healthcare provider" — the only user-accessible export.** — https://www.functionhealth.com/faqs/can-i-share-results-with-my-doctor
- **Function integrates external app data via "Connected Apps" — one-way into Function, not out — with no developer API mentioned.** — https://www.functionhealth.com/faqs/does-function-integrate-with-oura-fitbit-apple-health-or-other-health-services
- **Function membership includes 160+ tests annually; specific biomarkers named include ApoB, Lp(a), Lipoprotein Particle Size, FSH, LH, AMH, Testosterone, Cortisol, Estradiol, Free T3, Free T4, TSH, T3, T4, HbA1c, Insulin, hs-CRP, Mercury, Lead.** — https://www.functionhealth.com/faqs/which-tests-are-included-with-a-function-membership
- **Function's downloadable PDFs are at `https://my.functionhealth.com/documents`.** — https://www.functionhealth.com/faqs/can-i-share-results-with-my-doctor
- **Function Terms of Service (contact + legal-review reference for Section F.6).** — https://www.functionhealth.com/legal/terms-of-service
- **Function GitHub organization has one repo (unrelated ticket-tracking clone) — no public API code.** — https://github.com/Function-Health

### OpenAI / ChatGPT Health, corroborating primary source

- **OpenAI announces ChatGPT Health includes Function as a connected app for biomarkers and nutrition guidance, alongside Apple Health and MyFitnessPal (2026-01-07).** — https://help.openai.com/en/articles/20001036-what-is-chatgpt-health

### Anthropic, corroborating primary source

- **Anthropic's healthcare initiative launched with HealthEx and Function among first connector partners (2026-01-11).** — https://fortune.com/2026/01/11/anthropic-unveils-claude-for-healthcare-and-expands-life-science-features-partners-with-healthex-to-let-users-connect-medical-records/

### MCP authorization spec, primary source

- **MCP Authorization Spec 2025-06-18 defines OAuth 2.1 + PKCE flow, RFC 9728 Protected Resource Metadata, and RFC 7591 Dynamic Client Registration as SHOULD-support.** — https://modelcontextprotocol.io/specification/2025-06-18/basic/authorization

### Third-party aggregators, primary sources

- **Terra API supported-integrations list — 500+ providers enumerated, Function Health NOT listed.** — https://docs.tryterra.co/reference/health-and-fitness-api/supported-integrations
- **Junction (Vital) lab-testing API — example response shows `labcorp` as sole enumerated provider; Function Health NOT listed.** — https://docs.junction.com/api-reference/lab-testing/labs
- **Vital rebranded to Junction; the legacy tryvital.com URL now 308-redirects to junction.com. Junction's lab-testing API docs enumerate Labcorp (and per the marketing site, Quest); Function Health NOT listed at either the marketing or docs surface.** — https://junction.com/ (redirect target of former https://www.tryvital.com/)
- **Rook Labs API page describes the surface generically ("every lab, every format," "PDFs, images, scans, or direct lab feeds") and names no specific lab providers; Function Health NOT mentioned in any capacity.** — https://www.tryrook.io/labs-api

### PDF parseability, third-party evidence

- **Health3 documents extracting biomarker name, numeric value, unit, reference range, and collection date from Function Health PDFs across "180 supported biomarkers" and multi-page PDFs up to 100 MB.** — https://www.health3.app/import/function-health/
- **Empirical Health documents an upload-and-import flow for Function PDFs into their metrics tab.** — https://help.empirical.health/article/18-how-to-export-function-health-results

### Reverse-engineered API (rejected option; sourcing for the rejection)

- **Community reverse-engineered project confirms undocumented Firebase-backed API at Function endpoints using email + password auth, with `fe-app-version` header enforcement and "reverse-engineered, undocumented API. It can change at any time without notice" warning.** — https://github.com/daveremy/function-health-mcp

### Function Health company facts, corroborating

- **Function Health company facts (Series B $298M at $2.5B valuation Nov 2025, acquisitions of Ezra, Getlabs, SuppCo, partnerships with Anthropic and OpenAI).** — https://en.wikipedia.org/wiki/Function_Health

### Sources flagged as stale / lower-confidence

- User review by Dann Berg (Function panel walkthrough, dated 2025). Corroborating detail on category structure — checker should treat as secondary. — https://dannb.org/blog/2025/function-health/
- Referral-promo landing page (`functionhealthpromo.com`) — marketing content, not used for factual claims.

### Sources deliberately NOT used

- No Wikipedia claim is load-bearing (Function Health Wikipedia page is used only for corroborating company facts).
- No health blog claims about biomarker interpretation — the coach's biomarker interpretations in Section 4.2 are illustrative examples of shape, not medical claims.
- Function's actual biomarker enumeration should be treated as approximate — the fixture-set exercise in Section 3.1 (three real Function PDFs) is what confirms the exact panel structure for our extraction prompt. This doc's Section 4.1 is a starting taxonomy, not a specification.

---

## Change log

- 2026-06-30: initial research pass.
- 2026-06-30: verification pass (direct WebFetch on the three load-bearing citations + independent `research-evaluator` agent). Verdict PASS. Two minor citation-accuracy issues corrected in this same commit: (a) the Rook Labs API description previously listed wearables that were on a different Rook page — corrected to describe what the cited Labs API page actually says; (b) `tryvital.com` marker replaced with `junction.com`, since Vital rebranded to Junction and the legacy URL now 308-redirects. The Section 2 recommendation (Option A: PDF upload + LLM-parse) is unchanged by both corrections.
