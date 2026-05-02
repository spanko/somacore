# 0007. Azure Key Vault for OAuth tokens (not the database)

Date: 2026-05-02
Status: Accepted

## Context

We integrate with WHOOP (and later Oura, Strava, etc.) via OAuth 2.0. Each user has a refresh token that is functionally a long-lived credential to their WHOOP data. Compromising one such token compromises one user's WHOOP data; compromising the entire token store compromises every connected user.

The tokens have to be readable by the API (for WHOOP requests, refresh) and the ingestion jobs (for poller fetches).

## Decision

- **OAuth tokens are stored in Azure Key Vault**, one secret per user, named `whoop-refresh-{user_id}` (or equivalent for other sources).
- **The database stores the secret name** (`whoop_token_secret_name` column on `users`) and refresh metadata (last refresh time, scopes, expiry hint), **never the token value**.
- The API and ingestion jobs use **managed identity** to read tokens from Key Vault. No client secret for Key Vault auth.
- Tokens are **cached in process memory** (5-min TTL) to avoid Key Vault throttling on hot paths.
- Rotation: a new refresh token replaces the old in the same secret name on every refresh (Key Vault retains version history automatically).

## Consequences

- Database compromise does not equal token compromise. The blast radius of a SQL injection or accidental backup leak is materially smaller.
- Key Vault gives us audit logging, soft-delete, and version history out of the box.
- Key Vault read latency (~50–100 ms) is hidden behind in-process cache.
- Slight operational complexity: every new user creates a Key Vault secret; account deletion must delete the corresponding secret.

## Alternatives considered

- **Tokens in the database, encrypted with a column-level key.** Conceptually OK, but moves the encryption-key problem to a different store (still need somewhere safe for *that*) and reinvents what Key Vault gives us for free.
- **Tokens in App Configuration.** App Configuration is for config, not secrets. Reject.
- **Tokens encrypted in the database with Always Encrypted (Azure SQL).** We're on Postgres, and even with pgcrypto this is more moving parts than Key Vault.
- **Per-tenant HSM.** Massive overkill for phase 1. Revisit if we hit a regulatory bar (HIPAA covered entity, SOC 2 Type II) that demands it.
