# Worker image for asynchronous backup commit processing.
# Keep this aligned with src/services/api/Dockerfile.

ARG BUILD_VERSION=1.0.0
ARG SDK_VERSION=10.0-noble
ARG RUNTIME_VERSION=10.0-noble-chiseled-extra
ARG RUNTIME_RID=linux-x64
ARG CONTAINER_USER=app

FROM mcr.microsoft.com/dotnet/sdk:${SDK_VERSION} AS publish

ARG DEBIAN_FRONTEND=noninteractive
ARG BUILD_VERSION
ARG RUNTIME_RID

WORKDIR /src

RUN \
    ( \
        (apk update && apk --no-cache add tzdata zlib-dev) || \
        (apt-get update && apt-get install -y --no-install-recommends tzdata && rm -rf /var/lib/apt/lists/*) || \
        (tdnf install -y tzdata && tdnf clean all) \
    )

COPY ["src/services/worker/FlorisDeV.BackupWorker.csproj", "src/services/worker/"]
COPY ["src/common/core/FlorisDeV.BackupCore.csproj", "src/common/core/"]
COPY ["src/common/contracts/FlorisDeV.BackupContracts.csproj", "src/common/contracts/"]
COPY ["src/common/featureflags/FlorisDeV.FeatureFlags.csproj", "src/common/featureflags/"]
COPY ["src/common/healthchecks/FlorisDeV.HealthChecks.csproj", "src/common/healthchecks/"]
COPY ["src/common/logging/FlorisDeV.Logging.csproj", "src/common/logging/"]
COPY ["src/common/security/FlorisDeV.Security.csproj", "src/common/security/"]

RUN --mount=type=cache,id=nuget_manager,target=/root/.nuget/packages \
    dotnet restore "src/services/worker/" \
    -r ${RUNTIME_RID} \
    -p:PublishReadyToRun=true

COPY src/. src/.
COPY .editorconfig .
COPY .dockerignore .
COPY doc/README.md doc/README.md

RUN --mount=type=cache,id=nuget_manager,target=/root/.nuget/packages \
    dotnet publish "src/services/worker/" \
    -r ${RUNTIME_RID} \
    --no-self-contained \
    -p:PublishReadyToRun=true \
    -p:InformationalVersion=${BUILD_VERSION} \
    -c Release \
    -o /app/publish \
    --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:${RUNTIME_VERSION} AS base-release

ARG BUILD_VERSION
ARG CONTAINER_USER

LABEL build.version=${BUILD_VERSION}

WORKDIR /app

COPY --from=publish /app/publish /app/
COPY --from=publish /usr/share/zoneinfo /usr/share/zoneinfo

ENV ASPNETCORE_URLS=http://+:8080 \
    TZ=UTC

EXPOSE 8080

USER ${CONTAINER_USER:-app}

HEALTHCHECK --interval=5s --timeout=10s --retries=3 CMD curl --fail http://localhost:${WEBSITES_PORT:-8080}/health/liveness || exit 1

ENTRYPOINT ["./FlorisDeV.BackupWorker"]
