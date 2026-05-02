# 0004. EF Core for migrations and ORM

Date: 2026-05-02
Status: Accepted

## Context

We chose Postgres ([0003](./0003-postgres-flexible-server.md)) and .NET 9 ([0002](./0002-dotnet-9-minimal-api.md)). We need a migrations strategy and a default data-access pattern.

Options:

1. **EF Core** — full ORM, code-first migrations, very deep .NET integration.
2. **Dapper + manual SQL migrations** (Flyway, DbUp) — minimal, fast, you write the SQL.
3. **EF Core for migrations only, Dapper for queries** — hybrid.

## Decision

- **EF Core 9** for migrations and the default CRUD path.
- **Drop to Dapper or raw `NpgsqlCommand`** for query-heavy paths where EF's generated SQL is wrong, slow, or surprising. Don't fight EF — escape it.
- Migrations live in `SomaCore.Infrastructure/Migrations/`, generated via `dotnet ef migrations add <Name>`.
- Always use `.AsNoTracking()` for read-only queries.

## Consequences

- Velocity: Adam writes EF Core every day; the team is productive immediately.
- Schema is the source of truth in code (entity configurations + migrations), not in a hand-maintained SQL file.
- Risk: EF Core's generated SQL can be inefficient for complex queries. Mitigation: review SQL via `dbContext.Database.GetDbConnection().Query` logging or `EXPLAIN`, and escape to Dapper when needed. We already accept this.
- Pure DDD purists will dislike EF entities mixed with domain objects. We accept the pragmatism trade-off; if it bites us, refactor to a separate persistence model.

## Alternatives considered

- **Dapper-only with manual SQL migrations (Flyway/DbUp).** Cleaner SQL, more typing. Slower for the common case (CRUD against a moderate schema). Reject for phase 1.
- **Raw ADO.NET / Npgsql throughout.** Maximum control, maximum boilerplate. No.
- **NHibernate, LinqToDB, etc.** Smaller communities; we'd be on our own when something goes wrong.
