# Phase 1 scope

This document is the contract for what we are building right now. If a request, idea, or "while we're here" thought drifts outside this scope, **stop and surface it before implementing.**

## Goal

Build a rock-solid WHOOP **recovery** ingestion pipeline that we can trust as a foundation for the prescriptive layer that comes next. Validate it against three real users (Adam, Tai, Greg) for at least a week of real data.

## Why this scope, in one paragraph

WHOOP recovery is the trigger event for the morning plan in our long-term architecture. Until we trust that one ingestion path under real conditions — including dropped webhooks, slow scoring, and the strap coming off — building a rules engine on top of it produces unreliable plans. Validating recovery alone, end-to-end, with three users, surfaces every failure mode we'd otherwise hit later when scope is wider and changes are more expensive.

## In scope

### Backend
- ASP.NET Core minimal API (.NET 9) deployed to Azure Container Apps
- Postgres Flexible Server with EF Core migrations
- Azure Key Vault for OAuth tokens and secrets
- Application Insights tracing
- Bicep infrastructure for everything above

### Identity
- Microsoft Entra ID sign-in via the `tento100.com` tenant
- Two app registrations: API (resource) and Web (client)
- JIT user provisioning on first sign-in

### WHOOP integration
- OAuth 2.0 flow (authorize → callback → token exchange → Key Vault)
- Token refresh (proactive, ~50-min cadence with jitter)
- **Recovery data only.** Other event types (`sleep.updated`, `workout.updated`, `cycle.updated`) are acknowledged with 200 and dropped — we do not store them in phase 1.
- Webhook handler with HMAC signature validation, 2XX in <1 sec, idempotent enqueue
- Reconciliation poller as a Container Apps Job (cron, fixed wake window 4–11am MT)
- On-open synchronous pull endpoint
- All three paths feed the same `IRecoveryIngestionHandler`

### User-facing
- A server-rendered `/me` page (Razor or minimal Blazor — pick one in the first build session and add an ADR)
- Mobile-friendly layout (CSS, not native)
- Shows: today's recovery score, score_state badge, HRV (ms), RHR (bpm), 7-day mini-trend, ingestion provenance debug panel
- Microsoft sign-in protected
- WHOOP "Connect your account" flow for first-time users

### Operational
- `/admin/health` endpoint showing last webhook received, last poller run, last token refresh, error counts
- Structured logging via Serilog → App Insights
- Manual deploys from a trusted machine via `az deployment group create`

## Out of scope

Do not build, do not stub, do not "while we're here" any of the following. Each is real product work, but is sequenced behind phase 1.

- Flutter mobile app
- Sleep, workout, cycle ingestion (deferred to phase 2 — extend the handler pattern)
- Apple Health, Oura, Strava, MyFitnessPal adapters
- Rules engine
- AI synthesis layer (Azure AI Foundry, Semantic Kernel)
- Adherence tracking
- User signup flow beyond Entra-managed accounts
- Per-user adaptive polling schedule (fixed window in phase 1)
- Service Bus (Postgres-backed work queue is sufficient)
- Production WHOOP app submission
- Production Azure environment
- Staging environment
- Full CI/CD pipeline (manual deploys are fine)
- Multi-region, geo-redundancy
- Per-source data normalization (we have one source)

If something feels like it "obviously" belongs in phase 1 but isn't on the in-scope list above, write the ADR for the change and surface it with Adam — don't add the code first.

## Exit criteria

Phase 1 is complete when **all** of the following are true:

### Engineering exit (Adam validates)

1. WHOOP OAuth flow completes end-to-end for all three users; tokens persist across server restarts
2. Token refresh runs unattended for **7 consecutive days** without manual intervention or refresh failures
3. Webhook arrives → signature validates → recovery stored within 30 sec, with `trace_id` traceable end-to-end in App Insights
4. **Reconciliation poller catches a recovery the webhook missed**, validated by deliberately blackholing one webhook delivery and observing the poller fill the gap within its scheduled window
5. **On-open synchronous pull works** when neither webhook nor poller has run yet (validated in a manufactured cold-start scenario)
6. **All three `score_state` values observed in storage** with correct differentiation:
   - `SCORED` — normal night with strap worn (most days)
   - `PENDING_SCORE` — observed by checking `/me` early enough on a real morning that scoring is still in progress
   - `UNSCORABLE` — observed by deliberately not wearing the strap one night
7. The `/me` page renders correctly on a phone for all three users, showing their own data (and nothing else)
8. `/admin/health` shows green for at least 7 consecutive days

### Product exit (Tai validates)

1. Tai opens `/me` on her phone unprompted, sees her recovery for the day, and the experience is good enough that she would actually use it daily
2. The debug panel surfaces enough information that she can tell whether the data is fresh and where it came from
3. Privacy policy on `legal.tento100.com` (or current URL) is reviewed and accurate as of the day data starts flowing
4. No surprises: nothing in her data, on the page, or in her sign-in experience that we hadn't told her to expect

### Cumulative exit

If both engineering and product exit are met, phase 1 is done and we plan phase 2.

If engineering exit is met but product exit reveals a UX problem, we treat that as a phase-1 fix, not a phase-2 deferral.

If engineering exit reveals a reliability gap (e.g., the reconciliation pattern doesn't actually work the way we think), we treat that as the most important thing happening and do not move to phase 2 until it's understood and resolved.

## Timeline (planning estimate)

- **Week 1:** backend skeleton, Entra auth, WHOOP OAuth flow, deployed to dev Azure
- **Week 2:** all three ingestion layers wired, observability, idempotency tests
- **Week 3:** `/me` page, three users onboarded, observation period, fault injection (forced UNSCORABLE, blackholed webhook)

Three weeks is the upper bound from the planning conversation; faster is fine, but week 3 is **mostly observation**, not building. Resist the temptation to fill it with new features.
