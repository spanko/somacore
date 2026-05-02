# Runbook

Operational procedures for deploying, debugging, and maintaining SomaCore. This document is a stub during phase 1 and fills in as procedures are validated.

## Environments

| Environment | Resource group | Subscription | Status |
|---|---|---|---|
| Dev | `somacore-dev-rg` | `tento100.com` tenant subscription | active |
| Prod | `somacore-prod-rg` | same | not created yet (phase 2) |

Region: `westus3` (subject to confirm).

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
