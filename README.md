# somacore

Backend API and infrastructure for **SomaCore** (working name; consumer brand "10 to 100" under evaluation) — a personal health and wellness decision engine.

This repo contains the .NET 9 backend, EF Core data layer, Bicep infrastructure, and operational documentation. The mobile client lives in a separate repo (`mobile`, not yet created — phase 2).

## Phase 1 status

We are building **phase 1**: WHOOP recovery ingestion through three reliable layers, with a minimal web view, for three internal users. See [`docs/phase-1-scope.md`](./docs/phase-1-scope.md) for the committed scope and exit criteria.

## Quick links

- [`CLAUDE.md`](./CLAUDE.md) — standing brief for Claude Code sessions
- [`docs/architecture.md`](./docs/architecture.md) — full architecture
- [`docs/phase-1-scope.md`](./docs/phase-1-scope.md) — what's in, what's out
- [`docs/conventions.md`](./docs/conventions.md) — code conventions
- [`docs/runbook.md`](./docs/runbook.md) — deploy, rotate, debug
- [`docs/decisions/`](./docs/decisions/) — ADRs

## Prerequisites

| Tool | Version | Purpose |
|---|---|---|
| .NET SDK | 9.0+ | Backend |
| PostgreSQL | 16+ | Local dev database (or use the Azure dev DB) |
| Azure CLI | 2.60+ | Deployment |
| Bicep CLI | latest | Infra |
| Docker Desktop | latest | Local container builds |

You will also need:

- An Entra ID account in the `tento100.com` tenant
- Read access to the dev Key Vault (Adam grants this)
- A WHOOP account with a strap, registered against the dev app

## First-time setup

1. **Clone and restore.**

   ```bash
   git clone https://github.com/spanko/somacore.git
   cd somacore
   dotnet restore       # once src/ exists
   ```

2. **Local secrets.** The API uses dotnet user-secrets for local development. Initialize and populate:

   ```bash
   cd src/SomaCore.Api
   dotnet user-secrets init
   dotnet user-secrets set "ConnectionStrings:Postgres" "<your local pg connstring>"
   dotnet user-secrets set "AzureAd:TenantId" "<tenant id>"
   dotnet user-secrets set "AzureAd:ClientId" "<api app reg client id>"
   dotnet user-secrets set "Whoop:ClientId" "<whoop dev client id>"
   dotnet user-secrets set "Whoop:ClientSecret" "<whoop dev secret>"
   ```

   Get the values from the dev Key Vault (`az keyvault secret show ...`) — do not copy from anywhere else.

3. **Database.** Run the latest migration against your local Postgres:

   ```bash
   cd src/SomaCore.Api
   dotnet ef database update
   ```

4. **Run.**

   ```bash
   dotnet run --project src/SomaCore.Api
   ```

   The API listens on `https://localhost:5001` by default. Sign in at `/me` to test.

5. **Run tests.**

   ```bash
   dotnet test
   ```

## Deploying

See [`docs/runbook.md`](./docs/runbook.md) for the full deploy procedure. The short version:

```bash
cd infra
az deployment group create \
  --resource-group somacore-dev-rg \
  --template-file main.bicep \
  --parameters @parameters.dev.json
```

CI/CD is intentionally minimal in phase 1 (manual deploys from a trusted machine). GitHub Actions will be added in phase 2.

## Working with Claude Code in this repo

This repo is set up to be productive with [Claude Code](https://www.claude.com/product/claude-code). On session start, Claude Code reads `CLAUDE.md` automatically.

Recommended MCP servers — see [`.mcp/README.md`](./.mcp/README.md) for config:

- **GitHub** — for issues, PRs, cross-branch search
- **PostgreSQL (read-only)** — for schema introspection and ad-hoc queries

## License

All rights reserved. This is a private repository; do not redistribute.
