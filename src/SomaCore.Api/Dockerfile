# syntax=docker/dockerfile:1.7
# Multi-stage Dockerfile for SomaCore.Api.
# Build context is expected to be the repo root: `docker build -f src/SomaCore.Api/Dockerfile .`
# (or `az acr build --file src/SomaCore.Api/Dockerfile <repo-root>`).

ARG DOTNET_VERSION=9.0

# ---- Stage 1: build ---------------------------------------------------------
FROM mcr.microsoft.com/dotnet/sdk:${DOTNET_VERSION} AS build
WORKDIR /src

# Copy solution-wide MSBuild files first so package restore caches well.
COPY Directory.Build.props Directory.Packages.props ./
COPY src/SomaCore.sln ./src/

# Copy the project files we need to restore (avoids invalidating cache on src changes).
COPY src/SomaCore.Domain/SomaCore.Domain.csproj           src/SomaCore.Domain/
COPY src/SomaCore.Infrastructure/SomaCore.Infrastructure.csproj src/SomaCore.Infrastructure/
COPY src/SomaCore.Api/SomaCore.Api.csproj                 src/SomaCore.Api/

RUN dotnet restore src/SomaCore.Api/SomaCore.Api.csproj

# Now the rest of the source.
COPY src/ ./src/

RUN dotnet publish src/SomaCore.Api/SomaCore.Api.csproj \
    --configuration Release \
    --no-restore \
    --output /app/publish

# ---- Stage 2: runtime -------------------------------------------------------
FROM mcr.microsoft.com/dotnet/aspnet:${DOTNET_VERSION} AS runtime
WORKDIR /app

# .NET 9 ASP.NET Core defaults to listening on 8080 inside containers; pin it
# explicitly so the Container App's ingress.targetPort=8080 is unambiguous.
ENV ASPNETCORE_URLS=http://+:8080 \
    DOTNET_RUNNING_IN_CONTAINER=true \
    DOTNET_PRINT_TELEMETRY_MESSAGE=false

EXPOSE 8080

# Run as non-root for defense-in-depth; the aspnet base image ships an `app` user.
USER app

COPY --from=build /app/publish ./

ENTRYPOINT ["dotnet", "SomaCore.Api.dll"]
