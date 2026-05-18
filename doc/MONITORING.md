# Monitoring and Observability

This project uses structured logging, OpenTelemetry instrumentation, health checks, and Azure monitoring resources to make the API, worker, and client observable in local and production environments.

## Observability model

| Environment | Primary tools | Configuration |
| --- | --- | --- |
| Local Docker Compose | Console logs, Zipkin, Aspire dashboard | `appsettings.Development.json` points OTLP/Zipkin exporters to local services. |
| Production Azure | Console logs, Log Analytics, Application Insights | Container Apps receive `APPLICATIONINSIGHTS_CONNECTION_STRING`; local OTLP/Zipkin endpoints stay empty. |
| Client development | Console logs, optional local OTLP/Zipkin | Client appsettings can point to localhost exporters when local tracing is wanted. |

Production should not use Docker Compose hostnames such as `dev-dashboard` or `zipkin`. Those endpoints are development-only.

## Local monitoring services

Docker Compose starts the development observability stack together with the API and worker:

- `zipkin` for distributed trace visualization.
- `dev-dashboard` for the .NET Aspire dashboard and OTLP ingestion.
- `backup-api` and `backup-worker` with Dapr sidecars.
- `azurite` and `redis` for local Dapr components.

Start the stack:

```bash
docker compose up -d
```

Useful local URLs:

| Service | URL |
| --- | --- |
| Backup Gateway | `http://localhost:8200` |
| Backup API | `http://localhost:8210` |
| Backup Worker | `http://localhost:8220` |
| Zipkin | `http://localhost:9411` |
| Aspire dashboard | `http://localhost:18888` |
| RedisInsight | `http://localhost:5540` |

## Local exporter configuration

API and worker development settings intentionally use Docker Compose service names because those processes run inside the Compose network:

```json
{
  "OTEL_EXPORTER_OTLP_ENDPOINT": "http://dev-dashboard:18889",
  "OTEL_EXPORTER_ZIPKIN_ENDPOINT": "http://zipkin:9411/api/v2/spans"
}
```

The backup client usually runs outside Docker, so localhost endpoints are appropriate when local tracing is enabled:

```json
{
  "OTEL_SERVICE_NAME": "backup-client",
  "OTEL_EXPORTER_ZIPKIN_ENDPOINT": "http://localhost:9411/api/v2/spans",
  "OTEL_EXPORTER_OTLP_ENDPOINT": "http://localhost:4317",
  "OTEL_EXPORTER_AZURE_MONITOR_CONNECTION": ""
}
```

To disable a local exporter, set its value to an empty string:

```json
{
  "OTEL_EXPORTER_ZIPKIN_ENDPOINT": "",
  "OTEL_EXPORTER_OTLP_ENDPOINT": ""
}
```

## Production monitoring

Production infrastructure creates:

- Log Analytics workspace.
- Application Insights resource.
- Container Apps environment connected to Log Analytics.
- `APPLICATIONINSIGHTS_CONNECTION_STRING` injected into the API and worker Container Apps.

The API and worker default production appsettings keep these local-development exporters empty:

```json
{
  "OTEL_EXPORTER_ZIPKIN_ENDPOINT": "",
  "OTEL_EXPORTER_OTLP_ENDPOINT": "",
  "OTEL_EXPORTER_AZURE_MONITOR_CONNECTION": ""
}
```

This avoids failed outbound dependencies to local-only services. Application telemetry is sent through Application Insights SDK configuration and Container Apps platform logging.

## Viewing a local backup trace

1. Start the local stack:

   ```bash
   docker compose up -d
   ```

2. Run the backup client:

   ```bash
   cd src/services/client
   dotnet run
   ```

3. Open the Aspire dashboard at `http://localhost:18888` and inspect traces, metrics, and logs.
4. Open Zipkin at `http://localhost:9411` and query recent traces if Zipkin export is enabled.

## Trace context

Traces and logs include:

- service name, such as `backup-api`, `backup-worker`, or `backup-client`;
- operation names for backup scans, upload operations, API calls, and commit processing;
- correlation IDs propagated through request logging middleware;
- tags such as backup target count, transferred bytes, success/failure markers, and exception details.

## Health checks

The API exposes:

- `GET /api/health`
- `GET /api/health/alive`
- `GET /api/health/ready`

The checks cover application readiness plus configured dependencies such as Dapr and Azure Blob Storage where applicable. Container Apps can use these endpoints for operational diagnostics.

## Best practices

- Keep local OTLP and Zipkin endpoints in development settings only.
- Use `APPLICATIONINSIGHTS_CONNECTION_STRING` for production API and worker telemetry.
- Do not set `OTEL_EXPORTER_OTLP_ENDPOINT` to Docker Compose service names in production.
- Use structured log templates and named properties rather than string interpolation.
- Add custom `Activity` spans around business operations that need end-to-end tracing.
- Tag traces with stable identifiers such as `device.id`, `run.id`, and `commit.id`, but avoid local file paths or secrets.
