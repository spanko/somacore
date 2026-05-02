# 0008. Bicep for infrastructure as code

Date: 2026-05-02
Status: Accepted

## Context

Phase 1 deploys: a resource group, Container Apps environment, Container App, Container Apps Jobs, Postgres Flexible Server, Key Vault, App Insights, Log Analytics workspace, Container Registry. We want this codified, reviewable, and reproducible — not clicked through the portal.

Options:

1. **Bicep** — Microsoft's first-party Azure DSL.
2. **Terraform** — multi-cloud, mature, larger ecosystem.
3. **Pulumi** — IaC in real programming languages.
4. **ARM templates directly** — JSON; mostly historical.

## Decision

**Bicep.** Templates live in `infra/` at the repo root. Parameter files per environment (`parameters.dev.json`, future `parameters.prod.json`). Deployments are manual via `az deployment group create` from a trusted machine in phase 1.

## Consequences

- First-party support; new Azure features land in Bicep faster than Terraform or Pulumi.
- No cross-cloud baggage — we're Azure-only and that's stable.
- Tooling is already on Adam's machine (Azure CLI ships with the Bicep transpiler).
- Trade-off: Bicep modules are less mature than Terraform modules. We'll accept the occasional copy-paste rather than over-modularize.

## Alternatives considered

- **Terraform.** Excellent tool, but multi-cloud abstractions are friction we don't need; Azure provider sometimes lags first-party Microsoft tools by weeks/months.
- **Pulumi.** Real programming language is appealing, but adds a runtime + state-store dependency. Probably the right answer if our infra graph gets complex; not yet.
- **Click-ops.** No.
