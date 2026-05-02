# Architecture

This document describes the SomaCore phase-1 architecture in enough detail to orient a new contributor (or a Claude Code session) without re-reading every ADR. For specific decisions and their reasoning, see [`decisions/`](./decisions/).

---

## System diagram (text form)

```
┌──────────────────┐
│  WHOOP backend   │
└──────┬───────────┘
       │ webhook (recovery.updated)
       │ ─────────────────────────────────────────┐
       │                                          │
       │ REST API (recovery, sleep, workout)      │
       └─────────────────┐                        │
                         │                        ▼
                         │              ┌─────────────────────────┐
                         │              │  Azure Container App    │
                         │              │  SomaCore.Api           │
                         │              │                         │
                         │              │  - /webhooks/whoop      │
                         │              │  - /auth/whoop/*        │
                         │              │  - /auth/me (Entra)     │
                         │              │  - /me  (Razor / minimal)
                         │              │  - /admin/health        │
                         │              └────┬─────────────┬──────┘
                         │                   │             │
                         │ token refresh     │ tokens      │ data
                         │ + REST fetch      ▼             ▼
                         │           ┌────────────┐  ┌──────────────────┐
                         │           │ Key Vault  │  │ Postgres Flexible│
                         │           │            │  │ Server           │
                         │           │ - WHOOP    │  │                  │
                         │           │   refresh  │  │ - users          │
                         │           │   tokens   │  │ - whoop_recoveries
                         │           │ - WHOOP    │  │ - webhook_events │
                         │           │   client   │  │ - oauth_audit    │
                         │           │   secret   │  └──────────────────┘
                         │           └────────────┘
                         │
                         ▼
                ┌──────────────────────┐
                │ Container Apps Job   │
                │ SomaCore.IngestionJobs│
                │                      │
                │ - reconciliation     │
                │   poller (cron)      │
                │ - token refresh      │
                │   sweeper (cron)     │
                └──────────┬───────────┘
                           │
                           └─→ same ingestion handler as webhook
                               (writes to whoop_recoveries)

         ┌──────────────────────┐
         │ Microsoft Entra ID   │
         │ tento100.com tenant  │
         └──────────────────────┘
                ▲
                │ OIDC sign-in for /me
                │
         ┌──────┴───────┐
         │  Browser     │
         │  (Tai, Greg, │
         │  Adam)       │
         └──────────────┘
```

---

## Components

### SomaCore.Api (Azure Container App)

Single ASP.NET Core minimal API. Hosts:

- **Entra-authenticated routes** (`/me`, `/auth/me`, `/admin/*`): require Microsoft sign-in via the `tento100.com` tenant. Used by the three internal users.
- **WHOOP OAuth flow** (`/auth/whoop/start`, `/auth/whoop/callback`): the user-facing OAuth journey to connect their WHOOP account to their SomaCore identity.
- **Webhook receiver** (`/webhooks/whoop`): unauthenticated except by HMAC signature. Receives WHOOP event notifications, validates signatures, returns 2XX in <1 sec, enqueues for async processing.
- **On-open synchronous pull** (`/api/recovery/refresh`): called by the `/me` page when no recent recovery exists. Triggers an in-line WHOOP fetch.
- **Health/admin** (`/admin/health`, `/admin/me`): operational visibility.

### SomaCore.IngestionJobs (Container Apps Jobs)

Two scheduled jobs, both running as one .NET console app with command-line dispatch:

- **Reconciliation poller** — every 30 minutes during a global "wake window" (4am–11am MT in phase 1; per-user adaptive scheduling deferred to phase 2). Reads `users` table, identifies users with no scored recovery for the current cycle, calls WHOOP API, feeds the same ingestion handler the webhook does.
- **Token refresh sweeper** — every 50 minutes with jitter. Reads token metadata from Postgres, refreshes any token within 10 min of expiry, writes new refresh token to Key Vault, updates metadata.

### Postgres Flexible Server

Single database, single schema. Phase-1 tables:

| Table | Purpose |
|---|---|
| `users` | One row per SomaCore user. Keyed by Entra `oid`. WHOOP connection state lives here. |
| `whoop_recoveries` | One row per WHOOP recovery event. Includes `score_state`, score, HRV, RHR, raw payload. |
| `webhook_events` | One row per webhook received. Used for idempotency dedupe and audit. |
| `oauth_audit` | One row per OAuth action (refresh, revoke, etc.). Used for debugging. |

**No tokens are stored in Postgres.** The `users` table holds the *name* of the Key Vault secret (`whoop_token_secret_name`) and refresh metadata (last refresh time, scopes, expiry hint), not the token value.

The schema will be defined and migrated via EF Core. See ADR [0004](./decisions/0004-ef-core-migrations.md).

### Azure Key Vault

Two categories of secrets:

- **App-level secrets**: WHOOP client ID + secret, Entra app reg client secret. Mounted into the Container App via Key Vault references in env vars.
- **Per-user OAuth tokens**: one secret per user named `whoop-refresh-{user_id}`. Read at runtime by the API and ingestion jobs using managed identity.

Cache aggressively in process memory (5-min TTL) to avoid Key Vault throttling on high-volume ingestion paths.

### Microsoft Entra ID

Tenant: `tento100.com`. Two app registrations in phase 1:

- **`SomaCore API`** — the resource. Exposes scope `api.access`. Tokens are issued for this audience.
- **`SomaCore Web`** — the client. Web sign-in for the `/me` page. Redirect URI: `https://app-dev.tento100.com/signin-oidc` (or `https://localhost:5001/signin-oidc` for local dev).

A third app reg for the future Flutter client will be added in phase 2.

---

## The three-layer ingestion pattern

This is the heart of phase 1. It comes directly from the WHOOP integration spec — see ADR [0006](./decisions/0006-three-layer-whoop-ingestion.md) for the rationale.

| Layer | Mechanism | Covers |
|---|---|---|
| **1. Webhook (primary)** | WHOOP POSTs `recovery.updated` to `/webhooks/whoop`. We validate HMAC, return 2XX in <1 sec, enqueue an event row, async worker fetches and stores. | Real-time pre-warm. |
| **2. Reconciliation poller** | Container Apps Job, cron every 30 min during the wake window. Pulls latest cycle for users with no fresh scored recovery. | Missed webhooks (WHOOP's docs are explicit that delivery is not guaranteed). |
| **3. On-open sync pull** | When the user opens the `/me` page and no fresh recovery is in store, the page triggers an in-line fetch. | Rare cases where webhook and poller both lag. |

**All three paths converge on `IRecoveryIngestionHandler`** — the same code, same idempotency check (dedupe by webhook event ID + WHOOP cycle ID), same logging, same downstream effects. This is critical: if the three paths diverge, we have three things to test, three places to introduce bugs, and three observability stories to maintain. They must converge.

---

## Score state handling

Every WHOOP recovery has a `score_state` of `SCORED`, `PENDING_SCORE`, or `UNSCORABLE`. Phase-1 behavior:

| score_state | Behavior |
|---|---|
| `SCORED` | Store the row with full score/HRV/RHR. Display on `/me`. |
| `PENDING_SCORE` | Store the row with nulls for score fields. Display "WHOOP is still scoring" on `/me`. Re-poll in 5 min. |
| `UNSCORABLE` | Store the row, flagged. Display "Recovery unavailable for today" on `/me`. Do not re-poll. |

Each state must be **observed in storage during phase-1 testing** before phase 1 is considered complete. Test plan in [`phase-1-scope.md`](./phase-1-scope.md#exit-criteria).

---

## Identity model

A SomaCore user is keyed by Entra `oid`. WHOOP `user_id` is associated to the SomaCore user after they complete the WHOOP OAuth flow.

Onboarding sequence for phase 1 users (Adam, Tai, Greg):

1. Adam adds the user to the `tento100.com` Entra tenant.
2. User opens `https://app-dev.tento100.com/me`, signs in via Microsoft.
3. First-time sign-in JIT-creates a row in `users` keyed by `entra_oid`.
4. `/me` shows "Connect your WHOOP" state.
5. User clicks the connect button, completes WHOOP OAuth, lands back on `/me`.
6. `users` row is updated with `whoop_user_id`, `whoop_token_secret_name`, scopes.
7. Webhooks for that user start arriving; poller picks them up; `/me` shows recovery.

---

## What's deferred to phase 2

So this document doesn't have to grow to cover everything later:

- Sleep, workout, cycle ingestion (extensions of the same handler pattern)
- Apple Health, Oura, Strava, MyFitnessPal adapters
- Rules engine producing daily plans
- AI synthesis layer (Azure AI Foundry / Semantic Kernel)
- Adherence tracking and feedback loop
- Flutter mobile client + dedicated mobile app reg in Entra
- Service Bus replacing the Postgres-backed work queue
- Per-user adaptive polling schedule
- Production environment (`somacore-prod-rg`)
- Full CI/CD pipeline

Each of these will get its own architecture doc section when we build it.
