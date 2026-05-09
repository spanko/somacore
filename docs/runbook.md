# Runbook

Operational procedures for deploying, debugging, and maintaining SomaCore. This document is a stub during phase 1 and fills in as procedures are validated.

## Local development setup

First-time setup on a fresh clone:

```powershell
# 1. Restore the dotnet-ef local tool
dotnet tool restore

# 2. Start a local Postgres for dev queries (the integration test uses its own Testcontainers instance)
docker run --name somacore-pg `
  -e POSTGRES_USER=somacore `
  -e POSTGRES_PASSWORD=devonly `
  -e POSTGRES_DB=somacore `
  -p 5432:5432 `
  -d postgres:16

# 3. Set the local API connection string via dotnet user-secrets
dotnet user-secrets --project src/SomaCore.Api set `
  "ConnectionStrings:Postgres" `
  "Host=localhost;Port=5432;Database=somacore;Username=somacore;Password=devonly"

# 4. Apply migrations to the local DB
dotnet dotnet-ef database update -p src/SomaCore.Infrastructure -s src/SomaCore.Api

# 5. Run the API
dotnet run --project src/SomaCore.Api
# Listens on http://localhost:5000 (HTTP) / https://localhost:5001 (HTTPS profile)

# 6. Run the ingestion-job stub
dotnet run --project src/SomaCore.IngestionJobs -- --job=token-refresh
```

If you don't set `ConnectionStrings:Postgres` via user-secrets, `SomaCore.Api` falls back to `Host=localhost;Port=5432;Database=somacore;Username=somacore;Password=devonly` so it still starts in a fresh local environment. Production deployments do not use this fallback (the env-var supplies the real value).

## Environments

| Environment | Resource group | Subscription | Status |
|---|---|---|---|
| Dev | `somacore-dev-rg` | `tento100.com` tenant subscription (`951c802d-200d-4380-9006-2999df2218d9`) | active |
| Prod | `somacore-prod-rg` | same | not created yet (phase 2) |

Region: `westus3`.

## Access model

Phase-1 access is gated by a single Entra ID security group. Engineers and product reviewers are members of that group; everyone else in the tenant gets nothing.

### Tenant + group

| | |
|---|---|
| Tenant ID | `41231c11-d3b4-48d4-81c5-dacf4245a6c1` (`tento100.com`) |
| Access group (display name) | `somacoredev` |
| Access group (object ID) | `1ceda703-73b5-43ba-b19c-f3b73b76a8d4` |
| Group type | Security, mail-disabled, assigned membership, `isAssignableToRole = false` |
| Phase-1 members | Adam Wengert, Tai Palacio, Greg Sheridan |

Membership changes are made by an owner of the group via `az ad group member add/remove --group <id> --member-id <userId>` (or via the Entra portal).

### Entra app registrations

| App | Application (client) ID | Role |
|---|---|---|
| `SomaCore API` | `9c9a7c4c-5643-44ab-a915-c18f3b50edaa` | Resource. Identifier URI `api://9c9a7c4c-5643-44ab-a915-c18f3b50edaa`. Exposes the `api.access` delegated scope (scope ID `d67c9f15-b191-4735-b9ab-3d6530ce282c`). |
| `SomaCore Web` | `3b053ca8-e91b-4c7e-86ac-bb1e8afff5a3` | Client. OIDC sign-in for the `/me` page. Pre-authorized on `SomaCore API` for `api.access`. Client secret stored in 1Password (`SomaCore Web - phase-1-dev secret`); will move to Key Vault in the Bicep session. |

The Web app is configured with **`appRoleAssignmentRequired = True`**. Only members of `somacoredev` can sign in; everyone else gets `AADSTS50105` at the Entra sign-in screen.

### Azure RBAC at the resource-group scope

| Principal | Scope | Role |
|---|---|---|
| `somacoredev` | `somacore-dev-rg` | **Reader** (view resources, App Insights logs, Postgres metrics — cannot modify) |
| Adam (subscription Owner) | subscription | **Owner** (Bicep deploys, role assignments, secret bootstrap) |

Two-tier model on purpose: the group sees enough to debug, Adam keeps the keys to make changes.

### Per-user data isolation (application layer)

Group access controls which humans can sign in. **Within the app, every WHOOP-data query must filter by `users.entra_oid = <signed-in user's OID>`** (or equivalent indirection via `users.id`). The schema is built for this — `whoop_recoveries`, `external_connections`, and `webhook_events` all carry `user_id` foreign keys — but the query layer must enforce it. Tai signing in must not see Greg's recovery score even though both are in `somacoredev`. This is enforced in C#, not in Postgres or Entra.

### Things still to wire up

- `Key Vault Secrets User` role for `somacoredev` on the dev Key Vault — added in the Bicep session, after the vault exists.
- Postgres Entra-authentication user/group mapping — set when the Postgres Flex Server is provisioned.
- Move the `SomaCore Web` client secret from 1Password into Key Vault — Bicep session.
- Conditional Access for `somacoredev` (MFA enforcement, device compliance) — deferred until external users exist.

## Initial deploy

Prerequisites:
- Azure CLI logged in to the right tenant (`az login --tenant <tento100-tenant-id>`)
- Bicep CLI installed (`az bicep upgrade`)
- Subscription set (`az account set --subscription <id>`)

Steps:

```bash
# Create resource group (one-time)
az group create --name somacore-dev-rg --location westus3

# Validate the Bicep
cd infra
az deployment group validate \
  --resource-group somacore-dev-rg \
  --template-file main.bicep \
  --parameters @parameters.dev.json

# Deploy
az deployment group create \
  --resource-group somacore-dev-rg \
  --template-file main.bicep \
  --parameters @parameters.dev.json
```

## App deploy (after infra exists)

```bash
# Build container image and push to ACR
az acr build \
  --registry somacoredevacr \
  --image somacore-api:$(git rev-parse --short HEAD) \
  --file src/SomaCore.Api/Dockerfile \
  .

# Update Container App with new image
az containerapp update \
  --name somacore-api \
  --resource-group somacore-dev-rg \
  --image somacoredevacr.azurecr.io/somacore-api:$(git rev-parse --short HEAD)
```

## Database migrations

Run migrations against the dev DB before deploying code that depends on the new schema:

```bash
# From repo root
dotnet ef database update \
  --project src/SomaCore.Infrastructure \
  --startup-project src/SomaCore.Api \
  --connection "Host=...;Database=somacore;Username=...;Password=...;SslMode=Require"
```

The connection string for the dev DB is in Key Vault (`somacore-dev-kv`, secret `postgres-admin-connection`). Pull it via `az keyvault secret show` — do not commit it.

## Secret rotation

### WHOOP client secret

If the WHOOP client secret needs rotation:

1. Generate a new secret in the WHOOP developer dashboard.
2. Update the Key Vault secret: `az keyvault secret set --vault-name somacore-dev-kv --name whoop-client-secret --value '...'`
3. Restart the Container App to pick up the new value (or wait for the Key Vault reference cache to expire — up to several minutes).

### User OAuth tokens

Rotated automatically by the token refresh sweeper (every ~50 min per user). Manual rotation is not normally needed.

### Entra app reg client secret

Rotate annually or after any suspected exposure. Procedure TBD — fill in after first rotation.

## Debugging webhook delivery

Symptom: a webhook came in but the recovery isn't showing up.

1. Check `webhook_events` table: was the event received? `SELECT * FROM webhook_events ORDER BY received_at DESC LIMIT 20;`
2. If yes, was it processed? Check the `status` and `processed_at` columns.
3. Check App Insights for the `trace_id` from the webhook event row.
4. If the event was received but not processed, check for exceptions in the async worker logs.
5. If the event was not received at all, check WHOOP's webhook delivery logs in their dashboard (if available) and check for HMAC validation rejections in `oauth_audit` (TBD: actually we'll log signature failures separately — fill in once that path exists).

## Debugging poller

Symptom: the poller didn't fill a gap in webhook coverage.

1. Check Container Apps Job execution history in the Azure portal.
2. Check the job's structured logs for the run that should have caught the gap.
3. Verify the user's `last_recovery_at` timestamp in `users` — the poller decides who to fetch based on this.

## Backups

Postgres Flexible Server has automated backups enabled (default 7-day retention). Restore procedure TBD — to be validated as part of phase-1 exit.

## On-call

There is no on-call rotation in phase 1. If something is on fire, Adam is the human in the loop. Add a contact email when external users exist.
