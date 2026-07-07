ARG BUILD_VERSION=1.0.0
ARG SDK_VERSION=10.0-noble
ARG RUNTIME_VERSION=10.0-noble-chiseled-extra
ARG RUNTIME_RID=linux-x64
ARG CONTAINER_USER=app

FROM mcr.microsoft.com/dotnet/sdk:${SDK_VERSION} AS publish

ARG BUILD_VERSION
ARG RUNTIME_RID

WORKDIR /src

COPY ["Directory.Build.props", "."]
COPY ["src/services/gateway/Gateway.csproj", "src/services/gateway/"]

RUN --mount=type=cache,id=nuget_manager,target=/root/.nuget/packages \
    dotnet restore "src/services/gateway/" \
    -r ${RUNTIME_RID} \
    -p:PublishReadyToRun=true

COPY src/services/gateway/. src/services/gateway/.

RUN --mount=type=cache,id=nuget_manager,target=/root/.nuget/packages \
    dotnet publish "src/services/gateway/" \
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

ENV ASPNETCORE_URLS=http://+:8080 \
    TZ=UTC

EXPOSE 8080

USER ${CONTAINER_USER:-app}

ENTRYPOINT ["./Gateway"]