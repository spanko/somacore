# syntax=docker/dockerfile:1.7
# Multi-stage Dockerfile for SomaCore.IngestionJobs. Build context = repo root:
#   az acr build --file src/SomaCore.IngestionJobs/Dockerfile <context>

ARG DOTNET_VERSION=9.0

# ---- Stage 1: build ---------------------------------------------------------
FROM mcr.microsoft.com/dotnet/sdk:${DOTNET_VERSION} AS build
WORKDIR /src

COPY Directory.Build.props Directory.Packages.props ./
COPY src/SomaCore.sln ./src/

COPY src/SomaCore.Domain/SomaCore.Domain.csproj                 src/SomaCore.Domain/
COPY src/SomaCore.Infrastructure/SomaCore.Infrastructure.csproj src/SomaCore.Infrastructure/
COPY src/SomaCore.IngestionJobs/SomaCore.IngestionJobs.csproj   src/SomaCore.IngestionJobs/

RUN dotnet restore src/SomaCore.IngestionJobs/SomaCore.IngestionJobs.csproj

COPY src/ ./src/

RUN dotnet publish src/SomaCore.IngestionJobs/SomaCore.IngestionJobs.csproj \
    --configuration Release \
    --no-restore \
    --output /app/publish

# ---- Stage 2: runtime -------------------------------------------------------
# Console app — no aspnet runtime needed.
FROM mcr.microsoft.com/dotnet/runtime:${DOTNET_VERSION} AS runtime
WORKDIR /app

ENV DOTNET_RUNNING_IN_CONTAINER=true \
    DOTNET_PRINT_TELEMETRY_MESSAGE=false

# The aspnet base image has an `app` user; the runtime image does too.
USER app

COPY --from=build /app/publish ./

ENTRYPOINT ["dotnet", "SomaCore.IngestionJobs.dll"]
