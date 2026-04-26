
using System.ComponentModel.DataAnnotations;
using FluentAssertions;
using FlorisDeV.BackupContracts.Api.Requests;

namespace FlorisDeV.BackupApi.Tests;

/// <summary>
/// Tests for API request model validation attributes.
/// Route values are authoritative for device identity; request models only validate body fields.
/// </summary>
public class ApiRequestValidationTests
{
    [Fact]
    [Trait("Category", "Unit")]
    public void StartBackupRunRequest_CanBeConstructedWithDefaultValues()
    {
        var request = new StartBackupRunRequest();

        var validationResults = ValidateModel(request);

        validationResults.Should().BeEmpty();
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void CommitBackupRunRequest_WithValidRunId_PassesValidation()
    {
        var request = new CommitBackupRunRequest
        {
            RunId = Guid.NewGuid()
        };

        var validationResults = ValidateModel(request);

        validationResults.Should().BeEmpty();
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void CommitBackupRunRequest_WithEmptyRunId_PassesValidation()
    {
        // Note: [Required] doesn't validate Guid.Empty since Guid is a value type.
        var request = new CommitBackupRunRequest
        {
            RunId = Guid.Empty
        };

        var validationResults = ValidateModel(request);

        validationResults.Should().BeEmpty();
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void RegisterDeviceRequest_WithoutDeviceId_PassesValidation()
    {
        var request = new RegisterDeviceRequest
        {
            DisplayName = "test-device"
        };

        var validationResults = ValidateModel(request);

        validationResults.Should().BeEmpty();
    }

    private static List<ValidationResult> ValidateModel(object model)
    {
        var validationResults = new List<ValidationResult>();
        var validationContext = new ValidationContext(model);
        System.ComponentModel.DataAnnotations.Validator.TryValidateObject(
            model,
            validationContext,
            validationResults,
            validateAllProperties: true);
        return validationResults;
    }
}
