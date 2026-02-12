# Monitoring and Observability

This project uses OpenTelemetry for distributed tracing and observability, with multiple backend options.

## Available Monitoring Services

### 1. Zipkin (Distributed Tracing)
- **URL**: http://localhost:9411
- **Purpose**: Visualize distributed traces across services
- **Protocol**: Zipkin HTTP API

### 2. Aspire Dashboard (OTLP)
- **Dashboard URL**: http://localhost:18888
- **OTLP Endpoint**: http://localhost:4317
- **Purpose**: Modern .NET observability dashboard with traces, metrics, and logs
- **Protocol**: OpenTelemetry Protocol (OTLP)

### 3. Azure Monitor (Optional)
- **Purpose**: Production monitoring in Azure
- **Protocol**: Azure Monitor exporter
- **Configuration**: Set `OTEL_EXPORTER_AZURE_MONITOR_CONNECTION` with your connection string

## Starting Monitoring Services

### Start all services:
```bash
docker compose up -d
```

This will start:
- `zipkin` - Always runs
- `aspire-dashboard` - Always runs
- `azurite` - Azure Storage emulator
- `backup-api` - Your main API service

### View traces:

**Aspire Dashboard** (Recommended):
1. Open http://localhost:18888
2. Navigate to "Traces" section
3. View real-time traces from all services

**Zipkin**:
1. Open http://localhost:9411
2. Click "Run Query" to see recent traces
3. Click on a trace to see the full span details

## Configuration

### For Services in Docker Compose

Services inside Docker use the service hostnames. The `backup-api` already has logging configured via GELF to send logs to the monitoring stack.

### For Console Apps (like backup-client)

The backup client runs **outside Docker** and connects via localhost:

**appsettings.json**:
```json
{
  "OTEL_SERVICE_NAME": "backup-client",
  "OTEL_EXPORTER_ZIPKIN_ENDPOINT": "http://localhost:9411/api/v2/spans",
  "OTEL_EXPORTER_OTLP_ENDPOINT": "http://localhost:4317"
}
```

The console logging library will automatically:
- Create structured logs with Serilog
- Generate distributed traces with OpenTelemetry
- Export to all configured backends

### Disabling Exporters

To disable an exporter, set its configuration value to an empty string:

```json
{
  "OTEL_EXPORTER_ZIPKIN_ENDPOINT": "",
  "OTEL_EXPORTER_OTLP_ENDPOINT": ""
}
```

## Example: Viewing a Backup Operation

1. Start monitoring services:
   ```bash
   docker compose up -d zipkin aspire-dashboard
   ```

2. Run the backup client:
   ```bash
   cd src/services/client
   dotnet run
   ```

3. View traces in Aspire Dashboard:
   - Open http://localhost:18888
   - Look for service "backup-client"
   - See the full trace: `BackupClient.Run` → `BackupService.Backup`

4. Or view in Zipkin:
   - Open http://localhost:9411
   - Click "Run Query"
   - Click on the backup-client trace

## Trace Context

Traces automatically include:
- **Service name**: Identifies which service created the span
- **Operation name**: The activity/method being traced
- **Tags**: Custom attributes (e.g., `backup.success`, `app.version`)
- **Timing**: Duration of each operation
- **Parent/child relationships**: How operations nest

## Best Practices

1. **Always use ActivitySource for custom spans**:
   ```csharp
   using var activity = activitySource.StartActivity("MyOperation");
   activity?.SetTag("custom.tag", "value");
   ```

2. **Add tags for important business data**:
   ```csharp
   activity?.SetTag("backup.size", fileSize);
   activity?.SetTag("backup.success", true);
   ```

3. **Record exceptions in traces**:
   ```csharp
   catch (Exception ex)
   {
       activity?.SetStatus(ActivityStatusCode.Error, ex.Message);
       activity?.RecordException(ex);
       throw;
   }
   ```

4. **Use structured logging**:
   ```csharp
   logger.LogInformation("Backup completed for {FilePath} with size {Size}", 
       filePath, fileSize);
   ```

## Production Configuration

For production, use Azure Monitor:

```json
{
  "OTEL_EXPORTER_AZURE_MONITOR_CONNECTION": "InstrumentationKey=xxx;IngestionEndpoint=https://xxx.in.applicationinsights.azure.com/",
  "OTEL_EXPORTER_ZIPKIN_ENDPOINT": "",
  "OTEL_EXPORTER_OTLP_ENDPOINT": ""
}
```

Or configure multiple exporters for hybrid scenarios (e.g., Zipkin for local dev + Azure Monitor for staging).
