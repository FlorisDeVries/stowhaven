# Use the official .NET 9 runtime as base image
FROM mcr.microsoft.com/dotnet/aspnet:9.0 AS base
WORKDIR /app
EXPOSE 8080

# Use the SDK image to build the application
FROM mcr.microsoft.com/dotnet/sdk:9.0 AS build
WORKDIR /src

# Copy the project file and restore dependencies
COPY src/BackupApi.csproj ./
RUN dotnet restore

# Copy the source code and build the application
COPY src/ ./
RUN dotnet build -c Release -o /app/build

# Publish the application
FROM build AS publish
RUN dotnet publish -c Release -o /app/publish /p:UseAppHost=false

# Final stage - runtime image
FROM base AS final
WORKDIR /app

# Create a non-root user to run the application
RUN adduser --disabled-password --gecos '' --shell /bin/sh --uid 1000 appuser

# Copy the published application
COPY --from=publish /app/publish .

# Change ownership of the app directory to the appuser
RUN chown -R appuser:appuser /app

# Switch to the non-root user
USER appuser

# Set the ASP.NET Core environment
ENV ASPNETCORE_ENVIRONMENT=Production
ENV ASPNETCORE_URLS=http://+:8080

# Health check
HEALTHCHECK --interval=30s --timeout=10s --start-period=5s --retries=3 \
    CMD curl -f http://localhost:8080/health || exit 1

ENTRYPOINT ["dotnet", "BackupApi.dll"]
