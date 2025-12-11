# Some optimization were applied to the standard docker template based on these sources:
#  - https://thorsten-hans.com/how-to-build-smaller-and-secure-docker-images-for-net5
#  - https://github.com/dotnet/dotnet-docker/blob/main/samples/dotnetapp/Dockerfile
#  - https://faithlife.codes/blog/2022/05/using-cbl-mariner-for-asp-net-core-apps/
#  - https://learn.microsoft.com/en-us/dotnet/core/deploying/ready-to-run

# ---------------
# Build arguments
# ---------------

# The build/app version usually coming from CI/CD pipeline
ARG BUILD_VERSION=1.0.0

# The dotnet base container version/tag.
# Use https://mcr.microsoft.com/v2/dotnet/sdk/tags/list to list all available tags
ARG SDK_VERSION=10.0-noble
ARG RUNTIME_VERSION=10.0-noble-chiseled-extra

# Targets specific runtime environments (native compilation).
# This parameter is connected to the runtime version specified above.
# For alpine distributions use linux-musl-x64 and linux-x64 for the rest.
# Check https://learn.microsoft.com/en-us/dotnet/core/rid-catalog for RIDs.
ARG RUNTIME_RID=linux-x64

# For increased security, in a production environment, do not run with 'root' user.
# Use 'root' only for development and testing and 'app' for production builds.
ARG CONTAINER_USER=app

# -----------------------
# Build and publish stage
# -----------------------
FROM mcr.microsoft.com/dotnet/sdk:${SDK_VERSION} AS publish

ARG DEBIAN_FRONTEND=noninteractive
ARG BUILD_VERSION
ARG RUNTIME_RID

WORKDIR /src

RUN \
    # install additional packages \
    ( \
        # alpine
        (apk update && apk --no-cache add tzdata zlib-dev) || \
        # debian/ubuntu
        (apt-get update && apt-get install -y --no-install-recommends tzdata && rm -rf /var/lib/apt/lists/*) || \
        # cbl-mariner
        (tdnf install -y tzdata && tdnf clean all) \
    )

COPY ["src/services/api/FlorisDeV.BackupApi.csproj", "src/services/api/"]
COPY ["src/common/featureflags/FlorisDeV.FeatureFlags.csproj", "src/common/featureflags/"]
COPY ["src/common/healthchecks/FlorisDeV.HealthChecks.csproj", "src/common/healthchecks/"]
COPY ["src/common/logging/FlorisDeV.Logging.csproj", "src/common/logging/"]

# restore all dependencies
RUN --mount=type=cache,id=nuget_manager,target=/root/.nuget/packages \
    dotnet restore "src/services/api/" \
    -r ${RUNTIME_RID} \
    -p:PublishReadyToRun=true

# copy all project files
COPY src/. src/.
COPY .editorconfig .

# publish (build)
RUN --mount=type=cache,id=nuget_manager,target=/root/.nuget/packages \
    dotnet publish "src/services/api/" \
    -r ${RUNTIME_RID} \
    --no-self-contained \
    -p:PublishReadyToRun=true \
    -p:InformationalVersion=${BUILD_VERSION} \
    -c Release \
    -o /app/publish \
    --no-restore

# -----------
# Final stage
# -----------
FROM mcr.microsoft.com/dotnet/aspnet:${RUNTIME_VERSION} AS base-release

ARG BUILD_VERSION
ARG CONTAINER_USER

LABEL build.version=${BUILD_VERSION}

# switch user and working folder
WORKDIR /app

# copy application binaries from previous (publish) stage
COPY --from=publish /app/publish /app/
COPY --from=publish /usr/share/zoneinfo /usr/share/zoneinfo

# environment variables
ENV ASPNETCORE_URLS=http://+:8080 \
    TZ=UTC
    # DOTNET_RUNNING_IN_CONTAINER=true \
    # DOTNET_SYSTEM_GLOBALIZATION_INVARIANT=true \
    # DOTNET_EnableDiagnostics=0

# use port higher than 1023 due to running as non-root
EXPOSE 8080

# run as non-root
USER ${CONTAINER_USER:-app}

# enable health checks (requires curl installed)
HEALTHCHECK --interval=5s --timeout=10s --retries=3 CMD curl --fail http://localhost:${WEBSITES_PORT:-8080}/health/liveness || exit 1

ENTRYPOINT ["./FlorisDeV.BackupApi"]