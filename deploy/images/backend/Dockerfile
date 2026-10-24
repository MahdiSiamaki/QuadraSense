# syntax=docker/dockerfile:1
#
# The three .NET processes of QuadraSense, from one build:
#
#   docker build -f deploy/images/backend/Dockerfile --target api      -t quadrasense/api:<tag>      .
#   docker build -f deploy/images/backend/Dockerfile --target worker   -t quadrasense/worker:<tag>   .
#   docker build -f deploy/images/backend/Dockerfile --target migrator -t quadrasense/migrator:<tag> .
#
# The context is the repository root, not backend/: the worker embeds db/analytics/jobs/
# refresh_marts.sql, the application layer embeds the frontend's widget contract, and the migrator
# carries db/*/migrations.
#
# Runs on OpenShift's restricted-v2 SCC unchanged: a non-root user, no file the process needs to
# write inside the image, and nothing that depends on which UID it is given. OpenShift starts the
# container as an arbitrary UID in group 0, so the application files are owned by root and only
# READ by the process - which is also what makes a read-only root filesystem possible. See
# docs/operations/deployment/01-container-images.md.

# The SDK is pinned to global.json's version exactly. A different feature band ships different
# analyzers, and with TreatWarningsAsErrors a different verdict (CLAUDE.md, "Cloud sessions").
#
# Empty means the default after ':-' below - so compose can pass a value from .env, or nothing, and
# the default lives in this file only. A mirror (the department's Nexus) is a value here.
ARG SDK_IMAGE
ARG RUNTIME_IMAGE

# ------------------------------------------------------------------------------------- build
FROM ${SDK_IMAGE:-mcr.microsoft.com/dotnet/sdk:10.0.400} AS build

# In MCI's network NuGet comes through the Nexus proxy, not nuget.org (FR-NSM-204/00 2-1).
# Empty means the SDK's default feed.
ARG NUGET_SOURCE=

WORKDIR /src
COPY global.json ./
COPY backend/Directory.Build.props backend/Sqm.slnx backend/
COPY backend/src/Sqm.Api/Sqm.Api.csproj                       backend/src/Sqm.Api/
COPY backend/src/Sqm.Application/Sqm.Application.csproj       backend/src/Sqm.Application/
COPY backend/src/Sqm.Contracts/Sqm.Contracts.csproj           backend/src/Sqm.Contracts/
COPY backend/src/Sqm.Domain/Sqm.Domain.csproj                 backend/src/Sqm.Domain/
COPY backend/src/Sqm.Infrastructure/Sqm.Infrastructure.csproj backend/src/Sqm.Infrastructure/
COPY backend/src/Sqm.Ingestion/Sqm.Ingestion.csproj           backend/src/Sqm.Ingestion/
COPY backend/src/Sqm.Migrator/Sqm.Migrator.csproj             backend/src/Sqm.Migrator/

# Restore on its own layer: it changes when a package does, not when a line of code does.
RUN set -eu; \
    for p in Sqm.Api Sqm.Ingestion Sqm.Migrator; do \
      dotnet restore "backend/src/$p/$p.csproj" ${NUGET_SOURCE:+--source "$NUGET_SOURCE"}; \
    done

COPY backend/ backend/
COPY db/ db/
# The two files the .NET projects embed from outside backend/: the mart refresh (Sqm.Ingestion,
# under db/ above) and the dashboard widget contract, one file shared with the frontend (ADR-016).
COPY frontend/src/features/dashboard/registry/widget-contract.json frontend/src/features/dashboard/registry/

RUN set -eu; \
    dotnet publish backend/src/Sqm.Api/Sqm.Api.csproj             -c Release --no-restore -o /out/api      -p:UseAppHost=false; \
    dotnet publish backend/src/Sqm.Ingestion/Sqm.Ingestion.csproj -c Release --no-restore -o /out/worker   -p:UseAppHost=false; \
    dotnet publish backend/src/Sqm.Migrator/Sqm.Migrator.csproj   -c Release --no-restore -o /out/migrator -p:UseAppHost=false; \
    mkdir -p /out/migrator/db/operational /out/migrator/db/analytics; \
    cp -r db/operational/migrations /out/migrator/db/operational/; \
    cp -r db/analytics/migrations   /out/migrator/db/analytics/

# ------------------------------------------------------------------------------------ runtime
FROM ${RUNTIME_IMAGE:-mcr.microsoft.com/dotnet/aspnet:10.0.11} AS runtime

# Any non-zero UID. OpenShift replaces it with one from the namespace's range; compose sets one
# explicitly (deploy/compose/compose.yaml) to prove nothing depends on it.
ARG APP_UID=10001

ENV ASPNETCORE_HTTP_PORTS=8080 \
    DOTNET_NOLOGO=1 \
    DOTNET_CLI_TELEMETRY_OPTOUT=1 \
    # InvariantGlobalization is set in Directory.Build.props; this says the same thing to the
    # runtime, so an image without ICU libraries cannot fail at startup looking for them.
    DOTNET_SYSTEM_GLOBALIZATION_INVARIANT=1

WORKDIR /app

# The one directory the process writes: uploaded originals (ImportStorage:RootPath). Group 0 owns
# it and may write it, because OpenShift's arbitrary UID always has group 0 - and because a new
# named volume mounted here copies these permissions. setgid keeps new files in group 0.
USER 0
RUN mkdir -p /var/lib/quadrasense/uploads \
 && chgrp -R 0 /var/lib/quadrasense \
 && chmod -R 2770 /var/lib/quadrasense
ENV ImportStorage__RootPath=/var/lib/quadrasense/uploads

USER ${APP_UID}:0

FROM runtime AS api
COPY --from=build --chown=0:0 /out/api/ /app/
EXPOSE 8080
ENTRYPOINT ["dotnet", "Sqm.Api.dll"]

FROM runtime AS worker
COPY --from=build --chown=0:0 /out/worker/ /app/
ENTRYPOINT ["dotnet", "Sqm.Ingestion.dll"]

FROM runtime AS migrator
COPY --from=build --chown=0:0 /out/migrator/ /app/
ENTRYPOINT ["dotnet", "Sqm.Migrator.dll"]
