# Privacy data-handling reference (phase 1)

**Purpose.** This document describes what we actually store, where, and what happens when a user disconnects WHOOP or deletes their account. It is the engineering-side reference for Tai when she reviews the privacy policy. It is **not** the policy text — it is the ground truth the policy must accurately describe.

**Status.** Reflects code as of 2026-05-15, phase 1 (WHOOP recovery only, three internal users). When any of the behaviors below change, update this doc *before* changing the policy.

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

If anything above doesn't match what you understood from the policy draft, flag it and we'll either fix the policy or fix the code (whichever is more honest).
