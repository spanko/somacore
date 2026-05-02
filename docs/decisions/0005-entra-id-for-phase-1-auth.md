# 0005. Microsoft Entra ID for phase-1 auth

Date: 2026-05-02
Status: Accepted

## Context

Phase 1 has three users (Adam, Tai, Greg) — all internal. We need authentication for the `/me` page, and a clean identity story for OAuth-connected sources (WHOOP first, more later).

We have an active Entra ID tenant on the `tento100.com` domain (M365). Adam is tenant global admin.

Options:

1. Build our own auth (magic-link by email, etc.) — ~3 hours of work plus a transactional email dependency.
2. Use the Entra tenant we already have.
3. Use a third-party (Auth0, Clerk, WorkOS) — overkill for three users.

## Decision

- **Phase 1 auth = Microsoft Entra ID** via the `tento100.com` tenant.
- Use **Microsoft.Identity.Web** in ASP.NET Core for OIDC sign-in.
- Two app registrations:
  - **`SomaCore API`** — the resource. Exposes `api.access` scope.
  - **`SomaCore Web`** — the client (for the `/me` page).
- A user record in Postgres (`users` table) is keyed by Entra `oid` (object ID) and JIT-created on first sign-in.
- WHOOP connection is a *property* of the SomaCore user, not the spine of identity.

## Consequences

- Zero auth code to write. MFA, conditional access, sign-in logs, password reset — all free.
- Identity model is clean: one stable, immutable `oid` per user, separate from any source they connect.
- **Trade-off acknowledged:** as tenant global admin, Adam has visibility into identity-layer activity (sign-ins, devices, conditional access) for users in this tenant. Tai and Greg were briefed on this. Acceptable for an internal-co-founder phase; will be revisited before external users exist.
- Phase 2 (external users) will introduce a separate identity model (likely Entra External ID or an alternative), and the `tento100.com` tenant becomes our staff workspace.

## Alternatives considered

- **Build our own auth (magic-link).** Real cost in time and ongoing maintenance for marginal value. Reject.
- **Use WHOOP OAuth as primary identity.** Couples our identity model to a single integration; falls apart as soon as we add Apple Health or Oura. Reject.
- **Auth0 / Clerk / WorkOS.** Overkill for three users in a tenant we already own.
- **Entra External ID for phase 1.** Higher setup ceremony than Entra ID for internal users, and we'd want to move to it for external users anyway. Defer to phase 2.
