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

- ~~`Key Vault Secrets User` for `somacoredev`~~ — landed in the Bicep session as `Key Vault Secrets Officer` (read + write) so operators can seed and rotate. Same role for the UAMI so the runtime can rotate WHOOP refresh tokens. Tightening with a custom RBAC role is a phase-2 concern.
- Postgres Entra-authentication user/group mapping — set when the Postgres Flex Server is provisioned.
- Move the `SomaCore Web` client secret from 1Password into Key Vault — Bicep session.
- Conditional Access for `somacoredev` (MFA enforcement, device compliance) — deferred until external users exist.

## Deployed dev resources

After running `infra/main.bicep` against `somacore-dev-rg` in `westus3`:

| Resource | Name | Notes |
|---|---|---|
| Resource group | `somacore-dev-rg` | westus3 |
| Log Analytics workspace | `somacore-dev-law` | 30-day retention |
| Application Insights | `somacore-dev-ai` | workspace-based |
| User-assigned managed identity | `somacore-dev-uami` | client ID `20ba6439-c9a7-4a6e-af68-76696ce2bdde`, principal ID `cc5fbc4e-5561-4205-bf15-9c39ccee3f49` |
| Azure Container Registry | `somacoredevacr` | `somacoredevacr.azurecr.io`, Basic SKU, admin user disabled, UAMI has `AcrPull` |
| Key Vault | `somacore-dev-kv` | `https://somacore-dev-kv.vault.azure.net/`, RBAC mode. `somacoredev` group + UAMI both have `Key Vault Secrets Officer` (read + write). |
| Postgres Flex Server | `somacore-dev-pg` | `somacore-dev-pg.postgres.database.azure.com`, `Standard_B1ms` Burstable, 32 GB, password auth, `somacore` database, `AllowAllAzureServices` firewall rule |
| Container Apps Environment | `somacore-dev-cae` | Consumption workload profile |
| Container App | `somacore-api` | Auto-generated FQDN: `https://somacore-api.greenriver-03b3b72d.westus3.azurecontainerapps.io`. Custom hostname `app-dev.tento100.com` is the canonical user-facing URL once DNS is direct (see "Custom domain"). Listens on port 8080 (.NET 9 default in the runtime image). |
| Container Apps Job | `somacore-poller` | placeholder image, manual trigger |

## Initial deploy

Prerequisites:
- Azure CLI logged in to the right tenant (`az login --tenant <tento100-tenant-id>`)
- Bicep CLI installed (`az bicep upgrade`)
- Subscription set (`az account set --subscription <id>`)
- Resource group created (one-time): `az group create --name somacore-dev-rg --location westus3`
- The `somacoredev` security group exists (see [Access model](#access-model))

Run from the repo root:

```powershell
# Generate a strong Postgres admin password (passed at deploy time, never stored
# in source control). Bicep relays it to both Postgres and the Key Vault secret
# postgres-admin-password.
$pgPass = -join (
    1..32 | ForEach-Object {
        $chars = (65..90) + (97..122) + (48..57) + @(33,35,37,42,43,45,46,61,63,95)
        $b = [byte[]]::new(1)
        [System.Security.Cryptography.RandomNumberGenerator]::Create().GetBytes($b)
        [char]$chars[$b[0] % $chars.Count]
    }
)

# What-if (dry run)
az deployment group what-if `
    --resource-group somacore-dev-rg `
    --template-file infra/main.bicep `
    --parameters infra/parameters.dev.json `
    --parameters postgresAdminPassword=$pgPass

# Deploy
az deployment group create `
    --resource-group somacore-dev-rg `
    --template-file infra/main.bicep `
    --parameters infra/parameters.dev.json `
    --parameters postgresAdminPassword=$pgPass
```

Typical deploy time: 5–10 minutes (Postgres provisioning is the slow step).

## WHOOP recovery ingestion (phase 1)

The webhook + work-queue path is in place; reconciliation poller and on-open pull come in session 5b.

### Real-time path

1. WHOOP POSTs `recovery.updated` to `POST /webhooks/whoop`.
2. The endpoint reads the raw body (cap 64 KiB), validates the HMAC against `whoop-client-secret` (WHOOP signs webhooks with the OAuth client secret, not a separate webhook secret). Signature format: `base64(HMAC-SHA256(timestamp ‖ body, client_secret))`. Reject if header missing, signature wrong, or timestamp >5 min skewed.
3. Resolve the WHOOP `user_id` (numeric) → `external_connections` row via jsonb `@>` against `connection_metadata`.
4. Insert a `webhook_events` row with `status='received'` (or `'discarded'` for non-recovery event types). Idempotent on `(source, source_event_id, source_trace_id)` — duplicate deliveries return 200 silently.
5. Return 200 within ~50 ms.

### Background drainer

`WhoopWebhookDrainer` is a `BackgroundService` running in the API process (per ADR 0009). Every 2s it:

- `SELECT ... FROM webhook_events WHERE status = 'received' ... FOR UPDATE SKIP LOCKED LIMIT 5`
- Marks each row `'processing'`
- Calls `IRecoveryIngestionHandler.IngestAsync` for each — fetches the matching recovery from WHOOP, upserts a `whoop_recoveries` row keyed by `(external_connection_id, whoop_cycle_id)`
- Marks the event `'processed'` (or `'failed'` with truncated error on exception)

A token cache fronts WHOOP API calls: 5-min in-process TTL per `external_connection_id`, refreshes via `/oauth/oauth2/token` on miss, rotates the refresh token in KV and bumps `last_refresh_at` / `next_refresh_at` on the connection row.

### Verifying the path end-to-end

```sql
-- Did a webhook arrive?
SELECT id, source, event_type, status, received_at, processed_at, last_error
FROM webhook_events
ORDER BY received_at DESC LIMIT 10;

-- Did a recovery get persisted?
SELECT user_id, whoop_cycle_id, score_state, recovery_score, ingested_via, ingested_at
FROM whoop_recoveries
ORDER BY cycle_start_at DESC LIMIT 10;
```

If `webhook_events.status = 'failed'`, the `last_error` column carries the reason and there's a matching log line in App Insights tagged with `Whoop.Webhook` or `WhoopWebhookDrainer`. If `status = 'received'` for >30 sec, the drainer has stalled — check container logs for exceptions in `DrainOnceAsync`.

## WHOOP OAuth flow (phase 1)

The signed-in `/me` page exposes a "Connect WHOOP" button (`<a href="/auth/whoop/start">`). Both endpoints are `[Authorize]`-gated.

| Endpoint | Behavior |
|---|---|
| `GET /auth/whoop/start` | Looks up the SomaCore user row by Entra OID, mints an opaque state token (HttpOnly Secure SameSite=Lax cookie scoped to `/auth/whoop`, 10-min expiry), audits an `authorize` row, redirects to `https://api.prod.whoop.com/oauth/oauth2/auth?...&state=<encrypted>`. |
| `GET /auth/whoop/callback` | Pops the cookie, verifies the query `state` matches and decrypts, cross-checks the cookie's user vs the signed-in principal, exchanges code for tokens at `/oauth/oauth2/token`, fetches `/developer/v2/user/profile/basic`. On success: marks any existing `active` row revoked, inserts a fresh active `external_connections` row, writes the refresh token to `whoop-refresh-{somacoreUserId}` in KV, audits `callback_success`, redirects to `/me?whoop=connected`. |

Configuration (env vars on the Container App, set by Bicep):

| Var | Source |
|---|---|
| `Whoop__ClientId` | KV: `whoop-client-id` |
| `Whoop__ClientSecret` | KV: `whoop-client-secret` |
| `Whoop__RedirectUri` | parameter `whoopRedirectUri` |
| `Whoop__AuthorizeUri` / `TokenUri` / `ProfileUri` / `Scopes` | defaults in `WhoopOptions` (override via env if WHOOP changes endpoints or scopes change) |
| `KeyVault__VaultUri` | derived from the deployed vault |

Required WHOOP developer-dashboard configuration (one-time, manual):

- Redirect URIs include both `https://app-dev.tento100.com/auth/whoop/callback` and `https://localhost:5001/auth/whoop/callback`.
- Webhook URL is **not** set yet — that's session 5.

Debugging a failed connect:

1. `select * from oauth_audit where source = 'whoop' order by occurred_at desc limit 10;` — shows authorize / callback_success / callback_failed rows with the error message we recorded.
2. App Insights traces filtered by `Whoop.Auth.Start` / `Whoop.Auth.Callback` source contexts (see Serilog `SourceContext` field).
3. Container App console logs: `az containerapp logs show -g somacore-dev-rg -n somacore-api --type console --tail 50`.

## Custom domain (`app-dev.tento100.com`)

The Container App is fronted by `app-dev.tento100.com` so OIDC redirect URIs and the user-facing URL stay stable across redeploys.

DNS records to maintain at the `tento100.com` zone (Cloudflare in our case):

```
app-dev.tento100.com         CNAME   somacore-api.greenriver-03b3b72d.westus3.azurecontainerapps.io
asuid.app-dev.tento100.com   TXT     <customDomainVerificationId from `az containerapp show`>
```

**Important:** the CNAME must be DNS-only (gray-cloud at Cloudflare). Proxied (orange-cloud) routing breaks Container Apps' managed-certificate provisioning because the platform's HTTP-01 challenge can't reach the origin.

The hostname binding is now expressed in `infra/main.bicep` (parameters `customHostname` and `customHostnameCertificateId`), so subsequent `az deployment group create` runs preserve it. The first time, you have to issue the managed cert via the CLI (Bicep can't issue a cert that doesn't exist; once it does, Bicep just references its ID).

```powershell
# One-time first issuance (after DNS resolves directly):
$envId = az containerapp env show -g somacore-dev-rg -n somacore-dev-cae --query id -o tsv
az containerapp hostname add  -g somacore-dev-rg -n somacore-api --hostname app-dev.tento100.com
az containerapp hostname bind -g somacore-dev-rg -n somacore-api `
    --hostname app-dev.tento100.com --environment $envId --validation-method CNAME
# Cert provisioning takes ~5-15 min.

# Capture the cert resource ID for the parameters file:
az containerapp env certificate list -g somacore-dev-rg --name somacore-dev-cae `
    --managed-certificates-only `
    --query "[?properties.subjectName=='app-dev.tento100.com'].id | [0]" -o tsv
```

That ID goes into `infra/parameters.dev.json` under `customHostnameCertificateId`. From then on, `az deployment group create` is sufficient.

**ASP.NET Core gotcha:** Container Apps ingress terminates TLS and forwards plain HTTP, so the container sees `Request.Scheme = http`. Microsoft.Identity.Web composes redirect URIs from that, producing `http://app-dev...` which Entra rejects. `Program.cs` configures `ForwardedHeadersOptions` to honor `X-Forwarded-Proto` and registers `UseForwardedHeaders()` as the first middleware so OIDC redirect URIs come out as `https://app-dev.tento100.com/signin-oidc`. Don't move it.

## Seeding non-Postgres secrets into Key Vault

Run after the first deploy (Bicep does not handle these secrets — see ADR 0007). Pull values from the WHOOP developer dashboard and 1Password respectively.

```powershell
# WHOOP dev app credentials (from developer-dashboard.whoop.com)
az keyvault secret set --vault-name somacore-dev-kv --name whoop-client-id     --value "<whoop client id>"
az keyvault secret set --vault-name somacore-dev-kv --name whoop-client-secret --value "<whoop client secret>"

# SomaCore Web Entra app secret (currently in 1Password as "SomaCore Web - phase-1-dev secret")
az keyvault secret set --vault-name somacore-dev-kv --name web-client-secret --value "<web client secret>"
```

To retrieve later: `az keyvault secret show --vault-name somacore-dev-kv --name <name> --query value -o tsv`. Membership in `somacoredev` provides read access; nobody else in the tenant can.

## Routine deploys

There are two deploy paths. **Pick the one that matches what you changed.**

| Changed | Use | What it touches | Time |
|---|---|---|---|
| C# code, Razor pages, CSS, Dockerfile | [`scripts/deploy-app.ps1`](../scripts/deploy-app.ps1) | Container App image only | ~90 sec |
| `infra/**` (new resource, env var, role, secret binding, Postgres config) | [`scripts/deploy-infra.ps1`](../scripts/deploy-infra.ps1) | Whole template | 2.5–5 min |

### App rollout — fast path

```powershell
.\scripts\deploy-app.ps1
```

Runs `az acr build` against the local working tree, tags the image with the short git SHA + `:latest`, and `az containerapp update --image ...` to roll the running revision. **No Bicep, no Postgres, no Key Vault.** The Container App's revision history gives us the rollback path (`az containerapp revision activate --revision <previous>`).

The script ends by calling `/admin/health` against the live FQDN — quick smoke that the new revision came up.

### Infra rollout — full path

```powershell
.\scripts\deploy-infra.ps1            # actually deploy
.\scripts\deploy-infra.ps1 -WhatIf    # dry-run
```

Runs `az deployment group create` against `infra/main.bicep`, but **preserves**:

- The current Postgres admin password (read from KV, passed back in — no rotation).
- The currently-deployed Container App image tag (read from the running app, passed back as `apiImage` — no rollback to the placeholder).

If the deploy fails on `ServerIsBusy` from Postgres (which happens when the server is mid-operation — backup, RP reconciliation, etc.), the script waits 60 sec and retries once. After two failures, you investigate.

### Why the split

A whole-template ARM deployment re-evaluates every resource in `main.bicep`, even ones that haven't changed. Most are trivially idempotent. Postgres extension config (`azure.extensions=PGCRYPTO`) isn't — every PUT takes a server-config lock, which collides with anything else Postgres is doing. Routing app-only changes through the Container App resource directly skips that entire failure mode and shaves ~3 minutes off the loop.

We'll revisit this once we want blue-green deploys: Container Apps natively supports multi-revision traffic split, but the current setup uses `activeRevisionsMode: Single` (auto-cuts traffic to the latest) — adequate for phase-1 single-replica dev, not staged/live promotion.

### Land-mine: the empty-password failure mode

Bicep silently accepts an empty `postgresAdminPassword` parameter (because the parameter is `@secure()` typed, not validated) and:

- writes an empty string to the `postgres-admin-password` KV secret
- composes a `Password=;...` literal into the `postgres-connection-string` KV secret
- attempts to set the Postgres admin password to empty (Postgres Flex's RP appears to ignore this — the server keeps the previous password — but don't rely on that)

How you hit this: `az keyvault secret show ... --query value -o tsv` returns the value to stdout, but on auth failure it writes the error to stderr and `$pgPass` is empty. If the script doesn't halt, the deploy proceeds with a phantom-empty password, **corrupting both KV secrets**. The currently-running Container App keeps working because it captured its env vars at start; the next container that pulls fresh KV (a new Container App revision OR a Container Apps Job execution) fails to authenticate to Postgres.

`scripts/deploy-infra.ps1` now hard-throws if it can't read the password. If you ever see this corruption again:

```powershell
# Rotate the admin password to a known value and re-sync both KV secrets.
$pw = -join (1..32 | ForEach-Object { [char]@(65..90 + 97..122 + 48..57)[(Get-Random -Maximum 62)] })
az postgres flexible-server update -g somacore-dev-rg -n somacore-dev-pg --admin-password $pw
az keyvault secret set --vault-name somacore-dev-kv --name postgres-admin-password --value $pw --output none
$cs = "Host=somacore-dev-pg.postgres.database.azure.com;Port=5432;Database=somacore;Username=somacoreadmin;Password=$pw;SslMode=Require;Trust Server Certificate=true"
az keyvault secret set --vault-name somacore-dev-kv --name postgres-connection-string --value $cs --output none
# Restart API so it re-pulls KV.
az containerapp revision restart -g somacore-dev-rg -n somacore-api `
    --revision (az containerapp revision list -g somacore-dev-rg -n somacore-api --query "[?properties.active] | [0].name" -o tsv)
# Touch each Container Apps Job so it refreshes its cached KV secrets (jobs cache at
# the resource level, not per-execution — a benign env-var bump is enough).
foreach ($job in @('somacore-poller','somacore-refresh-sweeper')) {
    az containerapp job update -g somacore-dev-rg -n $job --set-env-vars "REFRESH_AT=$(Get-Date -Format o)" --output none
}
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

The Postgres admin password is in Key Vault (`somacore-dev-kv`, secret `postgres-admin-password`). Pull it via `az keyvault secret show` — do not commit it. The full connection string is:

```
Host=somacore-dev-pg.postgres.database.azure.com;Database=somacore;Username=somacoreadmin;Password=<from KV>;SslMode=Require;Trust Server Certificate=true
```

Migrating from a workstation requires a transient firewall rule for your IP (the deploy only allows Azure-internal traffic):

```powershell
$myIp = (Invoke-RestMethod https://api.ipify.org)
az postgres flexible-server firewall-rule create `
    --resource-group somacore-dev-rg --name somacore-dev-pg `
    --rule-name "adam-dev-$(Get-Date -Format yyyyMMdd)" `
    --start-ip-address $myIp --end-ip-address $myIp
# ... run dotnet ef database update ...
az postgres flexible-server firewall-rule delete `
    --resource-group somacore-dev-rg --name somacore-dev-pg `
    --rule-name "adam-dev-$(Get-Date -Format yyyyMMdd)" --yes
```

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
