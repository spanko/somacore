# Privacy data-handling reference (phase 1)

**Purpose.** This document describes what we actually store, where, and what happens when a user disconnects WHOOP or deletes their account. It is the engineering-side reference for Tai when she reviews the privacy policy. It is **not** the policy text — it is the ground truth the policy must accurately describe.

**Status.** Reflects code as of 2026-06-22. Covers WHOOP ingestion (recoveries + sleeps + workouts as of Track A close-out) and the SomaCore AI scaffolding (ADR 0012) currently rendering a stub on `/me`. Sections D and E cover the Anthropic-backed surface that hasn't gone live yet but ships behind a single feature-flag flip once Tai signs off on this doc. When any of the behaviors below change, update this doc *before* changing the policy.

**Audience.** Tai (legal/product), Adam (engineering).

---

## A. WHOOP email is stored in `connection_metadata`

When a user completes the WHOOP OAuth flow, we call `GET https://api.prod.whoop.com/developer/v2/user/profile/basic` to confirm whose tokens we just received. The response includes `user_id`, `email`, `first_name`, `last_name`. We persist these to `external_connections.connection_metadata` (Postgres `jsonb`).

**What's stored, concretely.** A row in `external_connections` carries something like:

```json
{
  "whoop_user_id": 12345,
  "whoop_email": "user@example.com",
  "whoop_first_name": "Alex",
  "whoop_last_name": "Doe"
}
```

`first_name` and `last_name` are only stored when WHOOP returns them.

**Track A expanded the WHOOP data we store** beyond just recoveries: we now also persist `whoop_sleeps` (sleep window, score, stage summary, raw payload) and `whoop_workouts` (start/end, sport name, heart-rate fields, raw payload). Both tables follow the same cascade contract as `whoop_recoveries` — keyed on `user_id`, FK to `external_connections` set to NULL on disconnect, full cascade on user deletion. The privacy posture is identical: disconnect severs the integration, the data stays with the user; full account deletion removes it.

**Why we store the email.** Two reasons:
1. Verification — if a user later reports "my WHOOP data isn't coming through," we can confirm the connection we have on file matches the WHOOP account they're looking at, without needing to make a live API call.
2. Diagnostic display on `/me` — the user sees which WHOOP account is currently linked. This is the "yes, this is the one" affordance.

**Implications for the privacy policy.**
- The WHOOP email is **a separate value from the Microsoft Entra sign-in email**. A user could sign into SomaCore with `alex@tento100.com` and link a WHOOP account registered as `alex@gmail.com`. We have both.
- The WHOOP email is **not** used for marketing, login, or any user contact. It is reference data only.
- When a user disconnects WHOOP (see Section B), the `external_connections` row is hard-deleted, which deletes the WHOOP email along with it.

**Code references.**
- Stored at: [WhoopAuthEndpoints.cs:182-200](src/SomaCore.Api/Whoop/WhoopAuthEndpoints.cs#L182-L200)
- Read for display at: [Me.cshtml.cs](src/SomaCore.Api/Pages/Me.cshtml.cs)

---

## B. OAuth audit trail survives a disconnect

We maintain an `oauth_audit` table that records every OAuth-related action: authorize redirect, callback success/failure, token refresh success/failure, manual disconnect. Each row carries `user_id`, `external_connection_id`, `source`, `action`, `success`, `error_message`, `context` (jsonb), and `occurred_at`.

**The cascade contract.** When a user disconnects WHOOP, the application hard-deletes the corresponding `external_connections` row. Two things survive that delete:
- **`whoop_recoveries`** rows the user has accumulated. The FK from `whoop_recoveries.external_connection_id` to `external_connections.id` is `ON DELETE SET NULL`. The user's recovery history stays tied to them via `user_id`. Disconnect severs the *integration*, not the *data*.
- **`oauth_audit`** rows. The FK from `oauth_audit.external_connection_id` to `external_connections.id` is also `ON DELETE SET NULL`. The audit row remains, with the FK nulled out. We retain the user ID, the action, the timestamp, and the redacted context — not the connection identifier.

**The same cascade contract applies to `webhook_events`** for completeness: webhook rows survive a disconnect with the connection FK nulled.

**What happens on full account deletion (not phase 1, but for the policy).** When a `users` row is deleted:
- `external_connections` cascade-deletes (`ON DELETE CASCADE`)
- `whoop_recoveries` cascade-deletes (`ON DELETE CASCADE`)
- `oauth_audit` and `webhook_events` keep their rows, but with `user_id` set to NULL — the audit trail loses the *who* but retains the *what happened* anonymously

This is intentional: the audit trail is there to answer "did SomaCore do something with WHOOP at this timestamp?" even after a user has been removed. It cannot be used to retroactively identify the user.

**Retention.** No automatic purge is in place for `oauth_audit` in phase 1. If the policy commits to a retention window, we will need to add a scheduled cleanup job.

**Code references.**
- Cascade rules: [docs/schema/SCHEMA-NOTES.md](docs/schema/SCHEMA-NOTES.md) — "Cascade rules"
- Schema source of truth: [docs/schema/0001_initial_schema.sql](docs/schema/0001_initial_schema.sql)
- Disconnect handler: [WhoopAuthEndpoints.cs](src/SomaCore.Api/Whoop/WhoopAuthEndpoints.cs) — `DisconnectAsync`

---

## C. WHOOP refresh tokens rotate; old tokens are not actively invalidated

WHOOP issues a new refresh token on every successful refresh, and the old refresh token becomes unusable as soon as the new one is issued (this is RFC 6749 §6 behavior plus token rotation). SomaCore writes the new refresh token to Key Vault on every refresh, replacing the old version.

**What we do NOT do.** We do not separately call WHOOP to invalidate the prior refresh token — WHOOP handles that on their side automatically when they issue the rotation. The old token simply stops working at WHOOP.

**Key Vault secret versioning.** Azure Key Vault retains *every version* of a secret. When we `SetSecret`, the prior version is not deleted — it's superseded but still readable by anyone with access. Our access path always reads the latest version, so the prior refresh tokens are functionally inert, but they remain readable in Key Vault history until the vault's soft-delete retention window expires (90 days for the dev vault).

**Implications for the privacy policy.**
- The policy can accurately say "we rotate refresh tokens on every refresh." It should **not** claim "old tokens are immediately destroyed" — they're not, they're inert.
- On disconnect, we *do* `StartDeleteSecret` on the Key Vault secret, which soft-deletes the current secret (and all prior versions). Soft-delete retention is currently 90 days; after that, the versions are unrecoverable.
- A user requesting "delete my data" today gets a hard delete of the `external_connections` row, a soft-delete of the KV secret (recovery window 90 days), and preserved audit/recovery rows per Section B.

**Best-effort revoke at WHOOP.** On disconnect, we additionally call `DELETE https://api.prod.whoop.com/developer/v2/user/access` with the user's current access token to revoke our app's OAuth grant on WHOOP's side. This is best-effort — if WHOOP returns an error or is unreachable, we log it and proceed with local teardown anyway. The user's intent is to disconnect; a stale grant at WHOOP that we can no longer use is harmless.

**Code references.**
- Token rotation on refresh: [WhoopAccessTokenCache.cs](src/SomaCore.Infrastructure/Whoop/WhoopAccessTokenCache.cs)
- Key Vault delete on disconnect: [AzureKeyVaultSecretsClient.cs](src/SomaCore.Infrastructure/Secrets/AzureKeyVaultSecretsClient.cs) — `TryDeleteSecretAsync`
- WHOOP revoke call: [WhoopOAuthClient.cs](src/SomaCore.Infrastructure/Whoop/WhoopOAuthClient.cs) — `RevokeAccessAsync`

---

## D. What we send to Anthropic for the SomaCore AI

The daily card on `/me` (see ADR 0012) — the SomaCore AI — runs against Anthropic's API. This section describes what physiological data leaves our infrastructure to generate each card, and equally important — what does not.

**This section describes the live behavior we commit to when the wire-up ships.** Until Tai signs off on this section, the daily card is stubbed locally and makes no network calls to Anthropic. The first invocation against the real model happens only after the privacy gate clears.

### D.1 What gets sent

Each invocation of `IDailyAgentService.GenerateAsync` builds a request payload containing:

1. **The system prompt** — the contents of [`docs/agent-voice-and-persona.md`](agent-voice-and-persona.md) verbatim, plus the IN BOUNDS / OUT OF BOUNDS list from [`docs/agent-bounds.md`](agent-bounds.md). This is static content authored by Tai. It contains no user data and is identical across all invocations and all users. Cached via Anthropic's prompt caching so it pays its full cost once and then reads from cache.

2. **The user's physiological input window**, structured as a JSON snippet:
   - Last 7 days of `whoop_recoveries`: `cycle_start_at`, `score_state`, `recovery_score`, `hrv_rmssd_milli`, `resting_heart_rate`. No `raw_payload` blob.
   - Last 7 days of `whoop_sleeps`: `start_at`, `end_at`, `score_state`, `sleep_performance_percentage`, `sleep_efficiency_percentage`, `total_sleep_time_milli`. No `raw_payload` blob.
   - Last 14 days of `whoop_workouts`: `start_at`, `end_at`, `sport_name`, `score_state`, `strain`, `average_heart_rate`. No `raw_payload` blob.
   - The user's local timezone offset (computed from the most recent sleep's `timezone_offset` field) plus the current local time-of-day. Used so the model can frame "this morning" / "this afternoon" appropriately.

3. **An anonymous internal user reference** — a short opaque identifier (the `agent_invocations.id`, a Guid7) we use solely so an Anthropic-side request log can correlate to one of our invocation rows for support purposes. It is not the user's `user_id`, not the WHOOP `whoop_user_id`, and cannot be reversed back to a user without our database.

### D.2 What we explicitly do NOT send

- The user's **name** (Entra display name OR WHOOP-returned first/last name).
- The user's **email** (Entra sign-in address OR WHOOP-account email).
- The user's **Entra Object ID** (the Microsoft identity).
- The user's **SomaCore `user_id`** (the Postgres primary key).
- The user's **WHOOP `whoop_user_id`** (their WHOOP account integer).
- **Any OAuth token** — access token, refresh token, or otherwise. Tokens never leave the API process under any circumstance.
- The **raw payloads** WHOOP sends us. We persist `raw_payload` jsonb for recomputability but never forward it.
- Any **lab data, clinical notes, or external documents.** The "user-uploaded lab results" mentioned in [`agent-voice-and-persona.md`](agent-voice-and-persona.md) and [`agent-bounds.md`](agent-bounds.md) are described as a future input. That ingestion surface does not exist yet. When it lands it will get its own section here before any lab content can be sent to Anthropic.

### D.3 Anthropic's data-handling posture

We use the Anthropic API under the standard Anthropic Commercial Terms. The relevant commitments per their published policy at the time of writing:

- **No training on customer data.** Anthropic does not use API requests or responses to train their models for paying API customers.
- **Transient processing.** Request and response data is processed to generate the response and not retained as content for any other purpose.
- **Retention is limited to the operational window needed for abuse detection and service operation** — not for product development. Per Anthropic's published policy, this is on the order of days, not indefinite.

Tai's review should independently verify these against Anthropic's then-current terms before the policy ships externally. We commit in code to passing the `metadata.user_id` field with the anonymous internal user reference (per D.1 item 3) and no other identifiers, and to NOT enabling any opt-in to data-retention-for-improvement features Anthropic may offer.

### D.4 Where Anthropic processes the data

The Anthropic API is currently a US-hosted service. Requests leave our westus3 Azure region and terminate at Anthropic's US-region endpoints. The privacy policy should reflect that physiological data crosses to a US third-party processor.

### D.5 User opt-out from the agent

Phase 1: not implemented. All signed-in users get the card on `/me`. Tai's review should decide whether this is acceptable for the three-internal-user alpha, or whether we add a per-user opt-out (a column on `users` or a setting in `connection_metadata`) before the model goes live. Either answer is small code; the question is product.

### D.6 What the user sees on `/me` when the SomaCore AI is live

The card carries an inline disclosure block — same surface as the existing "scaffolding only" banner today but with the live copy:

> *The SomaCore AI generates this card from your last 7 days of WHOOP recovery, sleep, and workout data. We do not send your name, email, or any account identifiers. [How this works.](TBD-link)*

Tai authors the linked deeper-explanation page when ready. The card itself never renders without this disclosure when running against the real model.

**Code references.**
- Anonymous user reference: implementation lands in a network-backed `IDailyAgentService` (does not exist yet — replaces [StubDailyAgentService.cs](../src/SomaCore.Infrastructure/Agent/StubDailyAgentService.cs))
- Input window assembly: same file, replaces the stub's hardcoded `inputSnapshot`
- Output validation against [`agent-bounds.md`](agent-bounds.md): mechanical refusal guard, runs server-side before the card is rendered

---

## E. The agent_invocations table

ADR 0012 introduced one new table, `agent_invocations`, that logs every daily-card generation — input window, model output, model id, token counts, cost estimate, duration, trace id, error if any.

### E.1 What's stored

| Column | Purpose | Privacy posture |
|---|---|---|
| `id`, `user_id`, `created_at` | Standard row metadata | Same as all our user-keyed tables |
| `input_snapshot` (jsonb) | Exact payload we sent to the model | Same physiological fields as Section D.1 item 2. No identifiers per D.2. |
| `todays_read` (text) | The "today's read" paragraph the model wrote | Whatever the agent's voice produced |
| `actions_json` (jsonb) | Structured action list (title, why, category, rank) | Whatever the agent's voice produced |
| `model_id` (text) | The Anthropic model the call hit | Operational |
| `cached_input_tokens`, `input_tokens`, `output_tokens` (int) | Usage tally for cost/audit | Operational |
| `cost_estimate_usd` (numeric) | Per-invocation cost | Operational |
| `duration_ms` (int) | Latency | Operational |
| `error_message` (text, truncated to 2000 chars) | Failure surface | Operational, redacted on truncation |
| `trace_id` (text) | Correlation to App Insights | Operational |

### E.2 Cascade contract on disconnect and account deletion

- **Disconnect WHOOP:** `agent_invocations` rows are NOT touched. The agent's view of the user's data is keyed by `user_id`, not `external_connection_id`. A user who disconnects + reconnects keeps their card history.
- **Full account deletion:** `agent_invocations.user_id` has a foreign key to `users.id` with `ON DELETE CASCADE`. Removing a `users` row removes every card we ever rendered for them. Anthropic-side: nothing to delete because nothing was retained (per D.3); we make no follow-up call.

### E.3 Retention

Phase 1: no automatic purge. Same posture as `oauth_audit` (Section B). If the policy commits to a retention window — recommendation: **12 months for card content**, longer if you want the analysis substrate for the eventual rules engine but stripped of `input_snapshot` after 12 months and kept only as operational rollups — we will add a scheduled cleanup job. Tai's call.

**Code references.**
- Entity: [AgentInvocation.cs](../src/SomaCore.Domain/Agent/AgentInvocation.cs)
- Configuration / FK cascade: [AgentInvocationConfiguration.cs](../src/SomaCore.Infrastructure/Persistence/Configurations/AgentInvocationConfiguration.cs)
- Stub that populates the row today: [StubDailyAgentService.cs](../src/SomaCore.Infrastructure/Agent/StubDailyAgentService.cs)

---

## For Tai's review

Before the privacy policy goes live, please confirm we're comfortable with each of these. Each item is a thing the policy must accurately reflect (or that we should change before publishing):

- [ ] **WHOOP email** is stored in `external_connections.connection_metadata`, alongside `whoop_user_id` and (when WHOOP returns them) first/last name. Used for verification and on-screen display only. Deleted on disconnect.
- [ ] **WHOOP email is distinct from Entra sign-in email.** A user can have two different email addresses on file (their work/Entra address and their WHOOP account address). Both are stored; neither is used for outbound communication in phase 1.
- [ ] **Disconnect deletes the connection row** (`external_connections`, including the WHOOP email/name fields) and **soft-deletes the Key Vault secret** (90-day recovery window before final purge).
- [ ] **Recovery history survives a disconnect.** A user who disconnects WHOOP keeps their `whoop_recoveries` rows, anonymized to the deleted connection (FK SET NULL). Recoveries are deleted only on full account deletion.
- [ ] **OAuth audit rows survive a disconnect** with the connection FK nulled. On full account deletion they survive with both connection and user IDs nulled — "an OAuth event happened at this time" stays, "who" doesn't.
- [ ] **No retention window on `oauth_audit`** today. If the policy commits to one, we add a cleanup job. Recommendation: pick a number (e.g., 18 or 24 months) and we'll implement it.
- [ ] **Refresh tokens rotate but prior versions are not actively destroyed.** They're inert (WHOOP rejects them) but remain readable in Key Vault history for 90 days after disconnect.
- [ ] **Best-effort revoke at WHOOP** on disconnect — we ask WHOOP to invalidate our grant, but we proceed with local teardown regardless of whether WHOOP responds.
- [ ] **No account-deletion endpoint in phase 1.** A user who wants their account fully deleted needs an admin to remove their `users` row. We should decide whether to commit to a self-service deletion timeline in the policy, or describe today's process honestly.
- [ ] **Three internal users only in phase 1.** Anyone signing up beyond Adam, Tai, and the third internal user is gated by Entra membership. The policy should be clear that public signup is not yet open.

### New items for the daily-card agent (Sections D + E)

- [ ] **Anthropic is a third-party processor** for the daily-card agent. Physiological data leaves Azure westus3 and terminates at Anthropic's US-region API.
- [ ] **What gets sent (Section D.1).** Last 7 days of recovery/sleep/workout summary fields + local time-of-day + a static voice/bounds system prompt + an anonymous internal user reference. Confirm this is the right surface area.
- [ ] **What does NOT get sent (Section D.2).** No name, email, Entra OID, SomaCore `user_id`, WHOOP `whoop_user_id`, OAuth token, or `raw_payload`. Confirm this is sufficient.
- [ ] **Anthropic's data-handling stance (Section D.3).** Standard Commercial Terms — no training on customer data, transient processing, retention limited to abuse-detection/service-operation window. **Tai to independently verify the then-current Anthropic terms before publishing the user-facing policy.**
- [ ] **`agent_invocations` table (Section E).** Logs every card invocation including the input window we sent. Cascade-deletes on full account deletion. Disconnect leaves card history intact.
- [ ] **No retention window on `agent_invocations`** today. Recommendation: 12 months for full row content. Tai's call.
- [ ] **User-visible disclosure on `/me` (Section D.6)** carries a single-paragraph plain-English description of the data flow and links to a deeper explanation. The deeper explanation is Tai's to author.
- [ ] **No per-user opt-out from the agent in phase 1 (Section D.5).** All three internal users get the card. Confirm acceptable for alpha, or we add an opt-out before the SomaCore AI goes live.
- [ ] **Lab-result ingestion is described in the voice spec but does not exist yet.** When that surface lands, it gets its own section in this doc before any lab content goes to Anthropic.

If anything above doesn't match what you understood from the policy draft, flag it and we'll either fix the policy or fix the code (whichever is more honest).
