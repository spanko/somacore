# 0010. Razor Pages for the /me surface

Date: 2026-05-09
Status: Accepted

## Context

Phase 1 has exactly one user-facing surface — the `/me` page — and we deferred the choice of rendering technology to the first build session that needs it (per [`docs/phase-1-scope.md`](../phase-1-scope.md) and [`docs/architecture.md`](../architecture.md)).

The page's job is small and specific:

- Microsoft sign-in (Entra OIDC) before access
- Show today's recovery score, score_state badge, HRV, RHR, 7-day mini-trend, ingestion provenance
- A "Connect WHOOP" call-to-action when the user hasn't linked a WHOOP account
- Mobile-friendly (CSS, not native — also already decided)

There is no client-side interactivity beyond two buttons (Connect WHOOP, sign out). Updates happen on page load; nothing live-polls. No SignalR.

The realistic options:

1. **Razor Pages** — server-rendered, request → response, minimal moving parts.
2. **Blazor Server** — server-rendered with a persistent SignalR connection for component interactivity.
3. **Blazor WebAssembly** — runs in the browser; talks to the API as a separate origin.
4. **Razor Components / SSR-only Blazor** (.NET 8+) — Razor-like SSR using Blazor component syntax.

## Decision

**Razor Pages**, hosted in `SomaCore.Api` alongside the minimal-API endpoints. Wire `Microsoft.Identity.Web.UI` for the OIDC sign-in/sign-out endpoints. The `/me` page is a single Razor Page with a `PageModel` that loads the signed-in user's row from `users` and any latest `whoop_recoveries` row.

## Consequences

- Zero SignalR, zero WebSocket, zero JavaScript framework — fewer things to break in production and fewer attack surfaces to reason about.
- Microsoft.Identity.Web's `AddMicrosoftIdentityWebApp` integrates cleanly: cookie-based auth with no token-management code in the page.
- The `/me` page can read `User` claims directly off `HttpContext.User` and pull data from `SomaCoreDbContext` synchronously-with-`await`. No "API call" pattern — same process.
- If we ever need richer client interactivity (live-updating widgets on `/me`, component reuse with a future Flutter analog), revisit. The migration cost from Razor Pages to Blazor Server (or to a separate Vite/React frontend) is bounded by the size of `/me`, which is ~one page.
- The Container App stays a single Linux container. No special hosting requirements (Blazor WebAssembly would push static-asset hosting decisions into the deploy story).

## Alternatives considered

- **Blazor Server.** Right answer if `/me` had live components or shared state across panels. It does not, and SignalR adds a persistent connection per user that we'd have to monitor and tune. Reject.
- **Blazor WebAssembly.** Right answer if we wanted to prototype a Flutter-like SPA shape. Adds CORS, separate auth flow (PKCE for SPAs), separate hosting decisions. Premature for one page. Reject.
- **Razor Components / SSR-only Blazor.** Closest peer to Razor Pages with newer syntax. Tooling and idioms are still settling vs. Razor Pages' decade of stability. Defer; revisit when phase 2 introduces more user-facing pages.
- **A separate `SomaCore.Web` project hosting Razor Pages, calling `SomaCore.Api`.** Cleaner separation, but doubles the deployment footprint (two Container Apps, two app registrations to wire) for one page. Phase 1 doesn't earn the split.
