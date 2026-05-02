# 0002. .NET 9 minimal API on Azure Container Apps

Date: 2026-05-02
Status: Accepted

## Context

We need a backend stack for the SomaCore API. Constraints:

- Adam ships production systems on .NET + Azure today.
- All target integrations (WHOOP, Oura, Strava) are OAuth 2.0 + REST. No language-specific advantage.
- Azure AI Foundry + Semantic Kernel give us a mature Azure-native AI synthesis path for phase 2.
- We want fast iteration in phase 1, low operational complexity.

## Decision

- **Language/framework:** C# 13, .NET 9, ASP.NET Core minimal APIs.
- **Compute:** Azure Container Apps for the HTTP API. Container Apps Jobs for scheduled work (poller, token refresh sweeper).
- **Containerization:** Multi-stage Dockerfile per service, pushed to Azure Container Registry.

## Consequences

- Adam moves at maximum speed.
- Container Apps gives us scale-to-zero for dev and per-replica scaling for prod, without the operational weight of AKS.
- Container Apps Jobs is purpose-built for our scheduled poller workload.
- We're betting on .NET 9 LTS (Nov 2024 GA, supported through Nov 2026). Migrating to .NET 10 LTS is a routine upgrade.
- If we ever need heavy scientific Python (physiological time-series modeling, ML training), it carves out as a dedicated service rather than reshaping the platform.

## Alternatives considered

- **Node + TypeScript.** Adam can ship in it, but the production stack is .NET; mixing introduces operational complexity for no payoff.
- **Python + FastAPI.** Better for ML-adjacent code, but we don't have ML in phase 1 and the WHOOP integration is plain REST.
- **Azure Functions instead of Container Apps.** Functions has cold-start and configuration limitations that bite at the webhook layer. Container Apps gives us more control with comparable operational cost.
- **AKS (Kubernetes).** Massive overhead for a single-service phase-1 deployment. Revisit when the service count justifies it.
