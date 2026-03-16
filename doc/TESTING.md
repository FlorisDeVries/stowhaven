# Testing Guide

This document describes the test organization and how to run tests in the Backup API project.

## Test Categories

Tests are categorized using xUnit traits to enable selective test execution:

### Unit Tests
- Use mocked dependencies
- No external system interaction (network, file system, databases)
- Fast execution
- Examples: `GlobalExceptionFilterTests`, `ManifestManagerConcurrencyTests`

**Attribute:**
```csharp
[Fact]
[Trait("Category", "Unit")]
public void MyUnitTest() { }
```

### Integration Tests
- Interact with real dependencies
- Use actual file system, network, or external services
- Slower execution but verify end-to-end behavior
- Examples: `FileSystemServiceTests`

**Attribute:**
```csharp
[Fact]
[Trait("Category", "Integration")]
public async Task MyIntegrationTest() { }
```

## Running Tests

### Run All Tests
```bash
dotnet test FlorisDeV.BackupApi.sln
```

### Run Only Unit Tests
```bash
dotnet test FlorisDeV.BackupApi.sln --filter "Category=Unit"
```

### Run Only Integration Tests
```bash
dotnet test FlorisDeV.BackupApi.sln --filter "Category=Integration"
```

### Run Tests in Specific Project
```bash
# Client tests
dotnet test FlorisDeV.BackupApi.sln tests/services/client

# API tests
dotnet test FlorisDeV.BackupApi.sln tests/services/api
```

### Run Tests with Additional Filters
```bash
# Run specific test class
dotnet test FlorisDeV.BackupApi.sln --filter "FullyQualifiedName~FileSystemServiceTests"

# Combine filters
dotnet test FlorisDeV.BackupApi.sln --filter "Category=Unit&FullyQualifiedName~ManifestManager"
```