# Claude Code kickoff — Session 1

**Paste this into Claude Code from the root of the `somacore` repo. Read everything before pasting; the pre-flight section has steps that need to happen first.**

---

## Pre-flight (do these before pasting the prompt)

These three things need to be true before the session starts. Each is a one-liner.

### 1. The schema files need to be in the repo

The schema lives in `docs/schema/` and the kickoff prompt references it. From the repo root:

```powershell
mkdir docs\schema
copy <path-to-downloads>\0001_initial_schema.sql docs\schema\
copy <path-to-downloads>\SCHEMA-NOTES.md docs\schema\

git add docs/schema
git commit -m "Add phase-1 schema and design notes"
git push
```

### 2. Local Postgres needs to be running

The session creates a migration and applies it. Easiest path is Docker:

```powershell
docker run --name somacore-pg `
  -e POSTGRES_USER=somacore `
  -e POSTGRES_PASSWORD=devonly `
  -e POSTGRES_DB=somacore `
  -p 5432:5432 `
  -d postgres:16
```

If something's already on 5432, pick another port and adjust the connection string in the prompt accordingly.

### 3. .NET 9 SDK needs to be installed

```powershell
dotnet --version
```

Should print `9.0.x` or higher. If not: `winget install Microsoft.DotNet.SDK.9`.

---

## The prompt

Everything below this line goes into Claude Code as a single message.

---

I'm starting the first build session for SomaCore. Read `CLAUDE.md` first — it's the standing brief with project context, scope boundaries, and working agreements.

## Your task

Generate a .NET 9 solution skeleton for the SomaCore API with EF Core entities that match the phase-1 schema, plus the first migration. The solution must build cleanly, the migration must apply cleanly to a local Postgres, and `dotnet ef migrations script` output must match `docs/schema/0001_initial_schema.sql` semantically (same tables, columns, types, constraints, indexes — naming may differ where EF Core has its own conventions).

This is **session 1 of phase 1**. It is deliberately bounded. **Do not** add WHOOP integration, OAuth handlers, web pages, controllers, or any other logic. The exit criteria below are the contract.

## Required reading before you write code

1. `CLAUDE.md` — project brief, conventions, working agreements
2. `docs/phase-1-scope.md` — what's in and out of scope
3. `docs/conventions.md` — code style, async, logging, errors, testing
4. `docs/architecture.md` — high-level architecture (sections on identity model and three-layer ingestion are most relevant)
5. `docs/schema/0001_initial_schema.sql` — **the schema is the source of truth.** Entities mirror this exactly.
6. `docs/schema/SCHEMA-NOTES.md` — design decisions and EF Core mapping guidance (read carefully — it has specific instructions for UUID v7, jsonb, enum-like text columns, and timestamp handling)
7. `docs/decisions/0002-dotnet-9-minimal-api.md`, `0003-postgres-flexible-server.md`, `0004-ef-core-migrations.md`, `0007-key-vault-for-oauth-tokens.md`

## Solution structure

Create `src/SomaCore.sln` with these projects:

```
src/
├── SomaCore.sln
├── SomaCore.Api/                # ASP.NET Core minimal API (entry point — empty for now, just Program.cs that builds + runs)
├── SomaCore.Domain/             # Domain types, value objects, no I/O dependencies
├── SomaCore.Infrastructure/     # EF Core DbContext, entities, migrations, Postgres provider
└── SomaCore.IngestionJobs/      # Container Apps Jobs entry point (empty Program.cs for now)
tests/
├── SomaCore.UnitTests/
└── SomaCore.IntegrationTests/   # Testcontainers Postgres (skeleton only — one passing smoke test)
```

Project dependency rules from `docs/conventions.md` apply: `Domain` depends on nothing; `Infrastructure` depends on `Domain`; `Api` and `IngestionJobs` depend on both. Tests reference what they're testing.

## Specific requirements

### Solution-wide

- Use **Central Package Management**: a `Directory.Packages.props` at `src/` root pinning all package versions. Individual `.csproj` files use `<PackageReference>` without versions.
- Pin packages at the latest stable versions as of your knowledge cutoff. If unsure of a version, prefer the latest GA over a preview.
- Enable nullable reference types repo-wide via `Directory.Build.props` at `src/` root: `<Nullable>enable</Nullable>`, `<ImplicitUsings>enable</ImplicitUsings>`, `<TreatWarningsAsErrors>true</TreatWarningsAsErrors>`, `<LangVersion>latest</LangVersion>`.
- File-scoped namespaces everywhere (per `.editorconfig`).

### SomaCore.Domain

- One folder per aggregate concept: `Users/`, `ExternalConnections/`, `WhoopRecoveries/`, `WebhookEvents/`, `OAuthAudit/`.
- Constants for the enum-like text columns (per SCHEMA-NOTES.md):
  ```csharp
  public static class ScoreState
  {
      public const string Scored = "SCORED";
      public const string PendingScore = "PENDING_SCORE";
      public const string Unscorable = "UNSCORABLE";
  }
  ```
  Same pattern for `ConnectionStatus`, `ConnectionSource`, `WebhookEventStatus`, `OAuthAuditAction`.
- Entity classes are plain C# with public properties. No EF Core attributes — all mapping happens in `Infrastructure` via `IEntityTypeConfiguration<T>`.
- Use `Guid` for all UUID columns. The domain doesn't care that they're v7 — generation happens in Infrastructure.

### SomaCore.Infrastructure

- `SomaCoreDbContext : DbContext` with `DbSet<T>` for each aggregate.
- One `IEntityTypeConfiguration<T>` per entity, in a `Configurations/` folder. These are the source of truth for EF→SQL mapping.
- Configure `jsonb` columns (`raw_payload`, `raw_body`, `connection_metadata`, `context`) using Npgsql's JSON support. Use `JsonDocument` for raw payloads (not typed models — we don't need typed access in phase 1).
- Configure CHECK constraints to match the SQL schema using `ToTable(t => t.HasCheckConstraint(...))`. EF Core 9 supports this directly.
- Configure all unique indexes and partial unique indexes to match the SQL. The partial unique indexes (`status = 'active'` for `external_connections`, etc.) are critical — they enforce business invariants.
- A small `Guid7Generator` helper class that wraps `Guid.CreateVersion7()` — even though it's just one line, having a named helper makes the intent obvious at call sites and gives us a single place to swap to native `uuidv7()` when we're on PG18.
- A `SaveChangesInterceptor` that sets `created_at` on insert and `updated_at` on insert+update. Per SCHEMA-NOTES.md we manage timestamps in the application layer, not via triggers.

### SomaCore.Api

- `Program.cs` that builds and runs an empty minimal API. Two endpoints only:
  - `GET /` — returns `{"service": "somacore-api", "version": "0.1.0"}`
  - `GET /admin/health` — returns `{"status": "ok"}` (no DB ping yet — we keep this simple)
- Wire up Serilog with structured JSON output to console (App Insights sink can come later — overkill for local dev).
- Wire up `SomaCoreDbContext` via `AddDbContext` reading the connection string from `ConnectionStrings:Postgres`.
- Wire up the `SaveChangesInterceptor`.
- Use `dotnet user-secrets` for the local connection string. Initialize and document the command in `README.md`.
- **No** auth wiring yet. **No** controllers. **No** WHOOP-related code. **No** webhook handler.

### SomaCore.IngestionJobs

- `Program.cs` that builds and runs an empty console app. It should:
  - Take a single command-line arg (e.g., `--job=token-refresh`)
  - Print "would run job: {name}" and exit 0
- This is a stub. Real jobs come in a later session.

### Tests

- `SomaCore.UnitTests`: xUnit + FluentAssertions + NSubstitute. One smoke test per Domain folder verifying entity construction works and constants are defined. Five trivial tests, all green.
- `SomaCore.IntegrationTests`: xUnit + Testcontainers.PostgreSql. **One** integration test that:
  1. Spins up a Postgres container
  2. Applies migrations via `dbContext.Database.MigrateAsync()`
  3. Asserts the migration ran (e.g., `users` table exists, expected indexes present via a `pg_indexes` query)

  This single test is the proof that the migration is real and applicable. Make it bulletproof.

### Migration

- Generate the initial migration via `dotnet ef migrations add InitialCreate -p SomaCore.Infrastructure -s SomaCore.Api`.
- The generated SQL (visible via `dotnet ef migrations script`) should match `docs/schema/0001_initial_schema.sql` semantically.
- If you find a divergence between what EF Core generates and what the SQL spec requires, **prefer adjusting the entity configuration** to produce the right SQL. Only edit the generated migration file as a last resort, and if you do, leave a comment explaining why.

### CI / Dockerfile / Bicep

**None of those.** Do not create them. Out of scope for this session.

## Working agreements for this session

- Read CLAUDE.md and the docs listed above before writing code. Don't assume.
- When you make a meaningful technical decision (a package choice, a project dependency, a non-obvious EF Core configuration), call it out in your final summary so I can review.
- If something in the schema is ambiguous to translate to EF Core, **stop and ask** before guessing. The schema is the source of truth.
- Run `dotnet build` and `dotnet test` before declaring done. Both must pass with zero warnings.
- Update `docs/runbook.md` with the local-dev startup commands you actually used (the `docker run` line, user-secrets setup, the `dotnet ef database update` command). Keep it terse.
- Update `CLAUDE.md`'s "Repo layout" section to reflect what now exists under `src/` and `tests/`.
- If you need to add an ADR (e.g., for a notable package choice), use the template in `docs/decisions/README.md`.

## Out of scope for this session

Do not build, do not stub, do not "while we're here" any of these:

- WHOOP OAuth start/callback endpoints
- Webhook handler
- Reconciliation poller logic (just the empty Program.cs)
- Token refresh logic
- Key Vault integration code (we're not deploying yet)
- Microsoft.Identity.Web / Entra sign-in (next session)
- Razor Pages / Blazor / any UI
- Bicep templates
- Dockerfile
- GitHub Actions / CI

Each of those gets its own bounded session.

## Exit criteria

You are done when **all** of these are true. Verify each one explicitly before declaring done:

1. `dotnet build src/SomaCore.sln` — succeeds with **zero warnings**.
2. `dotnet test src/SomaCore.sln` — all tests pass. The integration test actually spun up a container and applied the migration (don't fake it).
3. `dotnet ef migrations script -p src/SomaCore.Infrastructure -s src/SomaCore.Api` — emits SQL that creates the same five tables, with the same columns, types, NOT NULL constraints, CHECK constraints, primary keys, foreign keys, unique indexes, partial unique indexes, and regular indexes as `docs/schema/0001_initial_schema.sql`.
4. `dotnet run --project src/SomaCore.Api` — starts cleanly. `GET http://localhost:5000/` returns the service info JSON. `GET http://localhost:5000/admin/health` returns `{"status": "ok"}`.
5. `dotnet run --project src/SomaCore.IngestionJobs -- --job=token-refresh` — prints `would run job: token-refresh` and exits 0.
6. `git status` — only the changes you made to add new files and the `runbook.md` / `CLAUDE.md` updates. No leftover artifacts, no `bin/` or `obj/` committed (`.gitignore` already excludes them).
7. Final summary message lists: every package added with the version pinned, every notable design choice you made, anywhere you deviated from the schema spec (with reasoning), and anything you wanted to do but flagged as out of scope.

## When you finish

Don't auto-commit or push. Hand back a single summary message describing what you built, what tradeoffs you made, and any questions for me. I'll review before committing.

---

## Post-session checklist (run after Claude Code finishes)

After Claude Code declares done and hands back its summary, you do these checks before committing:

```powershell
# 1. Confirm clean build
dotnet build src\SomaCore.sln
# Expect: zero warnings

# 2. Confirm tests pass
dotnet test src\SomaCore.sln
# Expect: all green, including the integration test

# 3. Spot-check the migration SQL against the spec
dotnet ef migrations script -p src\SomaCore.Infrastructure -s src\SomaCore.Api > generated-migration.sql
# Compare against docs\schema\0001_initial_schema.sql:
# - Same tables (5 of them)?
# - Same columns and types?
# - Same CHECK constraints?
# - Same partial unique indexes (the score_state, external_connections active ones)?
# Don't expect identical text — EF will name things differently. Compare semantically.

# 4. Run the API locally and curl the health endpoint
dotnet run --project src\SomaCore.Api
# In another terminal:
curl http://localhost:5000/admin/health

# 5. Read Claude Code's summary message critically
# - Any "I made this decision because..." that surprises you? Push back.
# - Any "I deviated from the schema by..." that wasn't in the SCHEMA-NOTES guidance? Push back.
# - Anything flagged out of scope that you actually want? Note for next session.

# 6. Once satisfied:
git add -A
git status                                                      # last sanity check
git commit -m "Session 1: solution skeleton + EF entities + initial migration"
git push
```

If any of steps 1–4 fail, the session isn't done — push back to Claude Code with the specific failure rather than fixing it yourself. The whole point of bounded sessions is that exit criteria are non-negotiable.

## Heads-up on time

This session is probably 30–60 min of Claude Code working, depending on how chatty you want it. If it goes much longer than 90 min, something has gone sideways — check in.
