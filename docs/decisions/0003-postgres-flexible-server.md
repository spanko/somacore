# 0003. Azure Database for PostgreSQL Flexible Server, plain tables

Date: 2026-05-02
Status: Accepted

## Context

We need a primary data store for SomaCore. Phase-1 data shape is small and relational: users, recovery events, webhook events, audit logs. Phase-2 expands into more time-series-ish data (sleep, workout, plan outputs, adherence).

Key decisions:
1. Which database product?
2. Plain tables vs. specialized time-series storage (TimescaleDB)?

## Decision

- **Product:** Azure Database for PostgreSQL Flexible Server. B-series burstable tier (B1ms or B2s) for dev; Standard tier sized to load for prod.
- **Schema strategy:** Plain Postgres tables with appropriate indexes. **No TimescaleDB extension in phase 1.**

## Consequences

- Familiar tooling (psql, pgAdmin, EF Core).
- Azure-managed: backups, point-in-time restore, HA optional.
- Plain tables are simpler to query, simpler to migrate, simpler to test against (Testcontainers Postgres).
- We will revisit Timescale before crossing ~10K users or before adding query patterns that hit Postgres performance walls (continuous aggregates, retention policies, downsampling). Not before.
- Azure for PostgreSQL Flexible Server supports the Timescale extension via the `timescaledb` extension, so the migration path is local — same DB, enable an extension, port specific tables.

## Alternatives considered

- **Cosmos DB.** Document model is wrong for our relational shape; would need to invent denormalization patterns to no benefit.
- **SQL Server / Azure SQL.** Fine, but Postgres is more interoperable, EF Core support is excellent, and Timescale is Postgres-native.
- **TimescaleDB now.** Premature optimization. We have one user in phase 1. Adopting Timescale early adds operational and mental overhead for a problem we don't have.
- **Single-tenant SQLite for dev.** Tempting for fast bootstrap but creates dev/prod parity problems (different SQL dialects, different transaction semantics). Use Postgres locally via Docker or Testcontainers.
