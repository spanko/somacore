# 0001. Record architectural decisions

Date: 2026-05-02
Status: Accepted

## Context

We're starting a new project. Decisions that feel obvious today will feel arbitrary in three months without context. Git history captures *what* changed but not *why*.

## Decision

We will record significant architectural decisions as ADRs in `docs/decisions/`. The format is deliberately lightweight — see `README.md` in this directory for the template.

ADRs are added in the same PR as the decision they describe. CLAUDE.md links to them by number.

## Consequences

- Future contributors (human or AI) can quickly answer "why is it this way?"
- Decisions become first-class artifacts, easier to revisit and supersede explicitly.
- A small amount of overhead per decision. Acceptable.

## Alternatives considered

- **No ADRs, rely on git history and inline comments.** Tried this in past projects; loses information on every refactor.
- **Wiki / external doc system.** Drift between code and docs is the failure mode; keeping ADRs in the repo keeps them honest.
