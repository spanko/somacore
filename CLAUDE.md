# CLAUDE.md

This file is the standing brief for Claude Code working in this repository. It is read at the start of every session. Keep it short, accurate, and current.

When patterns change, update this file. When a new architectural decision is made, add an ADR under `docs/decisions/` and link it here. Stale instructions are worse than no instructions.

---

## Product in three sentences

**SomaCore** (working name; consumer-facing brand "10 to 100" under evaluation) is a personal health and wellness super-agent that aggregates data from wearables, nutrition platforms, and biomarker systems to generate a daily, prescriptive action plan. The product is a **decision engine, not a dashboard** — every output is an action the user can take today, not a chart for them to interpret. Differentiation comes from the orchestration layer: deterministic rules-engine decisions grounded in physiological science, with an LLM layer that explains the "why" but does not make the call.

**See `docs/architecture.md` for the full architectural picture, `docs/phase-1-scope.md` for what we are building right now.**

---

## Phase 1 scope (what we are building right now)

**One sentence:** WHOOP recovery data flowing end-to-end into our system on a rock-solid three-layer ingestion pipeline, with a minimal web view authenticated by Microsoft Entra ID, for three internal users.

**In scope:**
- ASP.NET Core minimal API on Azure Container Apps
- Postgres Flexible Server, EF Core migrations
- Azure Key Vault for OAuth tokens and secrets
- Microsoft Entra ID for sign-in (using the `tento100.com` tenant)
- WHOOP OAuth flow + token refresh
- Three ingestion paths for **recovery data only**: webhook handler with HMAC validation, reconciliation poller (Container Apps Job), on-open synchronous pull
- A server-rendered `/me` page showing recovery score, 7-day trend, and ingestion provenance
- Application Insights tracing
- Bicep IaC for everything above

**Out of scope (do not build, do not stub, do not "while we're here"):**
- Flutter mobile app
- Sleep, workout, cycle data ingestion (the patterns will be extended in phase 2 — same handler, different event types)
- Apple Health, Oura, Strava, MyFitnessPal integrations
- Rules engine
- AI synthesis layer
- Adherence tracking
- Multi-tenant signup flow (the three users are managed in Entra)
- Service Bus (Postgres-backed work queue is sufficient at this scale)
- Production WHOOP app submission
- Staging environment

If a request lands that drifts into out-of-scope territory, **stop and ask Adam** before implementing. Do not invent parallel patterns to accommodate scope creep.

---

## Architecture summary

| Concern | Choice | ADR |
|---|---|---|
| Backend language/framework | .NET 9, ASP.NET Core minimal APIs | [0002](./docs/decisions/0002-dotnet-9-minimal-api.md) |
| Database | Azure Database for PostgreSQL Flexible Server | [0003](./docs/decisions/0003-postgres-flexible-server.md) |
| Migrations | EF Core | [0004](./docs/decisions/0004-ef-core-migrations.md) |
| Time-series storage | Plain Postgres tables (no Timescale yet) | [0003](./docs/decisions/0003-postgres-flexible-server.md) |
| Identity provider | Microsoft Entra ID, `tento100.com` tenant | [0005](./docs/decisions/0005-entra-id-for-phase-1-auth.md) |
| Secret storage | Azure Key Vault (OAuth tokens, client secrets) | [0007](./docs/decisions/0007-key-vault-for-oauth-tokens.md) |
| WHOOP ingestion | Three-layer: webhook + poller + on-open | [0006](./docs/decisions/0006-three-layer-whoop-ingestion.md) |
| Compute | Azure Container Apps (API) + Container Apps Jobs (poller) | [0002](./docs/decisions/0002-dotnet-9-minimal-api.md) |
| IaC | Bicep | [0008](./docs/decisions/0008-bicep-for-iac.md) |
| Work queue (phase 1) | Postgres-backed `webhook_events` table | [0009](./docs/decisions/0009-postgres-backed-work-queue.md) |
| `/me` rendering | Razor Pages (not Blazor) | [0010](./docs/decisions/0010-razor-pages-for-me.md) |

**Three-layer ingestion in one paragraph:** WHOOP webhooks pre-warm the data store in real time. A reconciliation poller catches missed webhooks (WHOOP's docs are explicit that webhook delivery is not guaranteed). An on-open synchronous pull is the last-resort fallback. All three paths feed the same `IngestRecovery` handler — same idempotency, same logs, same code path. See `docs/architecture.md` for the full breakdown.

---

## Repo layout

```
somacore/
├── CLAUDE.md                    ← you are here
├── README.md                    ← human-facing setup + run instructions
├── Directory.Build.props        ← repo-wide MSBuild defaults (nullable, warnings-as-errors)
├── Directory.Packages.props     ← Central Package Management (all NuGet versions pinned here)
├── .config/dotnet-tools.json    ← dotnet-ef pinned as a local tool
├── docs/
│   ├── architecture.md          ← full architecture picture
│   ├── conventions.md           ← code style + patterns
│   ├── phase-1-scope.md         ← committed scope, exit criteria
│   ├── runbook.md               ← ops: deploy, rotate, debug
│   ├── schema/                  ← phase-1 SQL schema spec + design notes
│   └── decisions/               ← ADRs
├── src/
│   ├── SomaCore.sln
│   ├── SomaCore.Domain/         ← entities + enum-like constants, no I/O deps
│   ├── SomaCore.Infrastructure/ ← EF Core DbContext, configurations, migrations, Postgres provider
│   ├── SomaCore.Api/            ← ASP.NET Core minimal API (entry point)
│   └── SomaCore.IngestionJobs/  ← Container Apps Jobs entry point (stub in phase 1)
├── tests/
│   ├── SomaCore.UnitTests/      ← xUnit + FluentAssertions + NSubstitute
│   └── SomaCore.IntegrationTests/  ← Testcontainers Postgres
├── infra/                       ← Bicep templates for somacore-dev-rg
│   ├── main.bicep
│   ├── parameters.dev.json
│   └── modules/                 ← per-resource Bicep modules
└── .mcp/                        ← MCP server config for Claude Code
```

### Working with the EF tooling

`dotnet-ef` is pinned as a local tool. After `dotnet tool restore`, invoke as:

```powershell
dotnet dotnet-ef migrations add <Name> -p src/SomaCore.Infrastructure -s src/SomaCore.Api
dotnet dotnet-ef migrations script -p src/SomaCore.Infrastructure -s src/SomaCore.Api
dotnet dotnet-ef database update -p src/SomaCore.Infrastructure -s src/SomaCore.Api
```

A design-time `IDesignTimeDbContextFactory<SomaCoreDbContext>` lives in `SomaCore.Infrastructure` so EF tooling does not depend on user-secrets being populated. Override the design-time connection string via `SOMACORE_DESIGN_TIME_PG` if needed.

### Naming conventions in the data layer

EF Core entity types use PascalCase; columns, tables, indexes, and constraints in the database use snake_case via the `EFCore.NamingConventions` package. The schema spec at `docs/schema/0001_initial_schema.sql` is the source of truth — when EF generates SQL that diverges from it, fix the entity configuration first; only hand-edit the migration as a last resort with a comment explaining why. The current migration carries one such hand-edit (`idx_users_email` wraps `lower(email)` because EF Core has no fluent API for expression-based index columns).

---

## Code conventions (top of mind)

Full conventions in `docs/conventions.md`. The non-negotiables:

1. **Async all the way.** Every I/O-bound method is `async Task<T>`. No `.Result`, no `.Wait()`, no `GetAwaiter().GetResult()`. If you find yourself reaching for one of these, stop and rethink.
2. **Structured logging.** Use Serilog with structured properties: `_logger.LogInformation("Webhook received {EventType} for {WhoopUserId} with trace {TraceId}", ...)`. Never string-interpolate into log messages.
3. **No secrets in code, ever.** Config via `Microsoft.Extensions.Configuration`, secrets via Key Vault references. The string `"client_secret"` should appear in zero `.cs` files.
4. **Idempotency by default.** Webhook handlers, ingestion jobs, and any retry-prone code path must dedupe by `trace_id` or equivalent before doing work. Assume every message will arrive at least twice.
5. **EF Core for the schema, raw SQL when warranted.** Use EF Core for migrations and CRUD. Drop to Dapper or raw `NpgsqlCommand` for query-heavy paths where EF's generated SQL is wrong or slow. Don't fight EF — escape it.
6. **Result types for expected failure.** Use a `Result<T>` pattern (or `OneOf<>`) for operations that can fail in known ways (token refresh failed, WHOOP returned 4xx). Throw exceptions only for unexpected failures.
7. **Tests for the ingestion path are non-optional.** The webhook signature validator, the recovery ingestion handler, and the token refresh flow each need unit tests with at least the happy path and one failure mode covered before they merge.

---

## Working agreements with Claude Code

Things to **always** do in this repo:

- **Read `docs/phase-1-scope.md` before adding a feature.** If it's not in scope, surface that before writing code.
- **Run tests after non-trivial changes.** `dotnet test` from repo root.
- **Update `CLAUDE.md`** when introducing a new pattern, library, or convention. The future-you reading this file should not be surprised by what they find in the codebase.
- **Add an ADR** when making a decision that future-you might second-guess. Follow the template in `docs/decisions/README.md`. Keep them short.
- **Run `dotnet format`** before committing. CI will reject unformatted code.
- **Use feature branches.** Branch name: `<type>/<short-desc>` — `feat/whoop-oauth`, `fix/webhook-dedup`, `chore/ef-migration-bump`.

Things to **never** do in this repo:

- **Never commit secrets**, even placeholder ones. If you need a placeholder, use `__REPLACE_ME__` literally.
- **Never add a new dependency without checking it in to a decision file.** A new NuGet package is a decision; record why.
- **Never widen scope unilaterally.** The phase-1 fence is real. If something feels like it "obviously" belongs, write the ADR for the change, not the code.
- **Never invent a parallel pattern.** If three classes do auth two different ways, pick one and refactor — don't add a third. Surface the ambiguity in CLAUDE.md if you can't pick.
- **Never skip the HMAC validation on the webhook handler.** It is the difference between an authenticated endpoint and a DOS-by-design endpoint.
- **Never store an OAuth token in the database.** Tokens go in Key Vault; the database stores secret names and refresh metadata only.
- **Never log a token, secret, or full webhook payload.** Trace IDs and event IDs are fine. Bodies are not.

---

## When in doubt

Escalate to Adam — do not invent. Specifically, ask before:

- Touching anything in the auth flow (Entra or WHOOP)
- Changing how secrets are stored or read
- Adjusting data retention windows
- Modifying the webhook handler's contract with WHOOP
- Adding a new external dependency or service
- Restructuring the repo layout

Adam is the implementer of record. Tai is the product owner and final say on user-facing behavior; she is also a lawyer, so anything touching consent, retention, or disclosures gets her review before merging.

---

## Out-of-band references

- **Privacy policy** lives at `https://spanko.github.io/privacy/privacy/` (will move to `legal.tento100.com` later). Source: `github.com/spanko/privacy`.
- **WHOOP developer dashboard:** `developer-dashboard.whoop.com`. We have a dev app registered; production app submission is a phase-2 action.
- **Azure subscription:** the one associated with the `tento100.com` Entra tenant.
- **Resource group naming:** `somacore-dev-rg` and `somacore-prod-rg` (prod does not exist yet).
- **Region:** `westus3` (confirm with Adam before deploying — single-region for phase 1).
