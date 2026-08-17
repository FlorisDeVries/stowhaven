# Monitoring and observability

The API, worker, and client use structured logging and OpenTelemetry. Azure deployments also create Log Analytics and Application Insights resources.

## What is wired today

| Runtime | Logs | Traces and metrics |
| --- | --- | --- |
| Local API/worker | Console plus OTLP | OTLP to the Aspire dashboard |
| Local client | Console/file plus optional OTLP | OTLP when its endpoint is configured |
| Azure API/worker | Container Apps platform logs and Application Insights SDK | `APPLICATIONINSIGHTS_CONNECTION_STRING` enables the SDK and the Container Apps environment also receives it for Dapr; the explicit Azure Monitor OpenTelemetry exporter is currently disabled |
| Gateway | Default ASP.NET Core/container logs | No custom OpenTelemetry registration |

The shared logging library registers OTLP and Azure Monitor exporters. It does not register a Zipkin exporter. Docker Compose still starts a Zipkin container, but current application traces are not sent there.

## Local stack

Start the services:

```bash
docker compose up -d
```

Useful endpoints:

| Service | URL |
| --- | --- |
| Gateway and combined Swagger UI | `http://localhost:8200` |
| Direct API | `http://localhost:8210` |
| Direct worker | `http://localhost:8220` |
| Aspire dashboard | `http://localhost:18888` |
| Zipkin container (not wired) | `http://localhost:9411` |
| RedisInsight | `http://localhost:5540` |

API and worker containers send OTLP to `http://dev-dashboard:18889`. The client normally runs on the host and its Development configuration uses `http://localhost:4317`, the Aspire dashboard's host OTLP/gRPC port.

Open `http://localhost:18888` after running a backup to inspect application logs, traces, and metrics. Do not expect the run to appear in Zipkin unless a Zipkin exporter is added to the code.

## Exporter configuration

The active settings are:

```json
{
  "OTEL_SERVICE_NAME": "backup-client",
  "OTEL_EXPORTER_OTLP_ENDPOINT": "http://localhost:4317",
  "OTEL_EXPORTER_AZURE_MONITOR_CONNECTION": ""
}
```

- Set `OTEL_EXPORTER_OTLP_ENDPOINT` to enable OTLP traces, metrics, and logs.
- Set `OTEL_EXPORTER_AZURE_MONITOR_CONNECTION` to enable the explicit Azure Monitor exporters.
- Leave an exporter setting empty to disable it.

`APPLICATIONINSIGHTS_CONNECTION_STRING` is injected into the production API and worker and is consumed by the Application Insights SDK registered by both services. The shared OpenTelemetry setup separately reads `OTEL_EXPORTER_AZURE_MONITOR_CONNECTION`. In the current Bicep deployment the latter is deliberately empty, so OpenTelemetry instruments are not also exported through the Azure Monitor OpenTelemetry exporter.

## Instruments

The client activity source and meter are `florisdev.backup.client`.

| Client instrument | Type | Unit |
| --- | --- | --- |
| `florisdev.backup.files.count` | Counter | files |
| `florisdev.backup.failures` | Counter | failures |
| `florisdev.backup.duration` | Histogram | ms |
| `florisdev.backup.size` | Histogram | bytes |

The API and worker use their own `TelemetryProvider.SourceName` values and the shared `florisdev.backup.*` instruments around runs, SAS generation, event processing, state operations, failures, and duration. HTTP server/client auto-instrumentation is enabled for API and worker only in Development.

## Health endpoints

API and worker expose:

- `GET /health/liveness` — self check only
- `GET /health/readiness` — self plus configured Dapr and Blob Storage checks
- `GET /healthz` — compatibility endpoint with a detailed response

The API additionally exposes `GET /api/health`, `GET /api/health/alive`, and `GET /api/health/ready`; the client uses `/api/health/alive` for its authenticated scaled-to-zero wake-up probe.

The public Gateway exposes `GET /healthz`. It proxies API routes under `/api`, so API health can also be reached through the Gateway with paths such as `/api/health/liveness` when authentication permits it.

The Dapr health check remains part of readiness because the API and worker use Dapr bindings. The production configuration disables the obsolete pub/sub-specific portion of that probe.

## Production queries

Container stdout/stderr is available in the Log Analytics workspace connected to the Container Apps environment. Application Insights receives telemetry from the application SDK and Dapr configuration. When the OpenTelemetry instruments must also use the Azure Monitor exporter, set `OTEL_EXPORTER_AZURE_MONITOR_CONNECTION` to the Application Insights connection string through a secure deployment setting and check for duplicate auto-instrumentation.

Use stable identifiers such as `device.id`, `backup.run_id`, and commit IDs to correlate operations. Avoid adding SAS URLs, recovery phrases, access tokens, or full local file paths to telemetry.

## Related documentation

- [Technical design](TECHNICAL_DESIGN.md#13-observability-and-health)
- [Advanced client configuration](ADVANCED_CONFIGURATION.md#telemetry)
- [GitHub Actions deployment](GITHUB_ACTIONS_DEPLOYMENT.md)
