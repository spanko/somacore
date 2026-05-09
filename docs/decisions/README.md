# Architectural Decision Records

This directory holds short, dated records of significant architectural decisions. The format is deliberately lightweight — long ADRs don't get read.

## Why ADRs?

When you (or a future contributor, or a Claude Code session) encounter "why is it this way?", the answer should be a 2-minute read, not an archaeological expedition through git history.

## When to write one

Write an ADR when you make a decision that:

- A future contributor might second-guess
- Constrains future choices in a meaningful way
- Was a real choice (i.e., there were defensible alternatives)

Don't write one for:

- Mechanical or obviously-correct decisions
- Coding style preferences (those go in `conventions.md`)
- Decisions that are easily reversible

## Format

Use the template below. Keep it under one screen of markdown. Number them sequentially.

```markdown
# NNNN. Title in present tense

Date: YYYY-MM-DD
Status: Accepted | Proposed | Superseded by NNNN

## Context

What's the situation we're deciding in? What constraints apply?

## Decision

What did we decide? State it plainly.

## Consequences

What does this make easier? Harder? What did we close off?

## Alternatives considered

What else did we look at, and why didn't we pick it? Brief.
```

## Index

- [0001 — Record architectural decisions](./0001-record-architectural-decisions.md)
- [0002 — .NET 9 minimal API on Azure Container Apps](./0002-dotnet-9-minimal-api.md)
- [0003 — Azure Database for PostgreSQL Flexible Server, plain tables](./0003-postgres-flexible-server.md)
- [0004 — EF Core for migrations and ORM](./0004-ef-core-migrations.md)
- [0005 — Microsoft Entra ID for phase-1 auth](./0005-entra-id-for-phase-1-auth.md)
- [0006 — Three-layer WHOOP ingestion (webhook + poller + on-open)](./0006-three-layer-whoop-ingestion.md)
- [0007 — Azure Key Vault for OAuth tokens (not the database)](./0007-key-vault-for-oauth-tokens.md)
- [0008 — Bicep for infrastructure as code](./0008-bicep-for-iac.md)
- [0009 — Postgres-backed work queue for phase 1](./0009-postgres-backed-work-queue.md)
- [0010 — Razor Pages for the /me surface](./0010-razor-pages-for-me.md)
