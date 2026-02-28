
using System.ComponentModel.DataAnnotations;
using FluentAssertions;
using FlorisDeV.BackupApi.Models.Api.Requests;

namespace FlorisDeV.BackupApi.Tests;

/// <summary>
/// Tests for API request model validation attributes.
/// Note: [Required] attribute on value types (like Guid) only prevents null,
/// not Guid.Empty. Empty GUID validation must be handled at the business logic level.
/// </summary>
public class ApiRequestValidationTests
{
    #region StartBackupRunRequest Tests

    [Fact]
    [Trait("Category", "Unit")]
    public void StartBackupRunRequest_WithValidDeviceId_PassesValidation()
    {
        // Arrange
        var request = new StartBackupRunRequest
        {
            DeviceId = Guid.NewGuid()
        };

        // Act
        var validationResults = ValidateModel(request);

        // Assert
        validationResults.Should().BeEmpty();
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void StartBackupRunRequest_WithEmptyGuid_PassesValidation()
    {
        // Arrange
        // Note: [Required] doesn't validate Guid.Empty since Guid is a value type
        var request = new StartBackupRunRequest
        {
            DeviceId = Guid.Empty
        };

        // Act
        var validationResults = ValidateModel(request);

        // Assert
        validationResults.Should().BeEmpty();
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void StartBackupRunRequest_CanBeConstructedWithDefaultValues()
    {
        // Arrange & Act
        var request = new StartBackupRunRequest();

        // Assert - DeviceId defaults to Guid.Empty (no validation error from [Required])
        request.DeviceId.Should().Be(Guid.Empty);
        var validationResults = ValidateModel(request);
        validationResults.Should().BeEmpty();
    }

    #endregion

    #region CommitBackupRunRequest Tests

    [Fact]
    [Trait("Category", "Unit")]
    public void CommitBackupRunRequest_WithValidGuids_PassesValidation()
    {
        // Arrange
        var request = new CommitBackupRunRequest
        {
            DeviceId = Guid.NewGuid(),
            RunId = Guid.NewGuid()
        };

        // Act
        var validationResults = ValidateModel(request);

        // Assert
        validationResults.Should().BeEmpty();
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void CommitBackupRunRequest_WithEmptyGuids_PassesValidation()
    {
        // Arrange
        // Note: [Required] doesn't validate Guid.Empty since Guid is a value type
        var request = new CommitBackupRunRequest
        {
            DeviceId = Guid.Empty,
            RunId = Guid.Empty
        };

        // Act
        var validationResults = ValidateModel(request);

        // Assert
        validationResults.Should().BeEmpty();
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void CommitBackupRunRequest_CanBeConstructedWithDefaultValues()
    {
        // Arrange & Act
        var request = new CommitBackupRunRequest();

        // Assert - Both GUIDs default to Empty (no validation errors from [Required])
        request.DeviceId.Should().Be(Guid.Empty);
        request.RunId.Should().Be(Guid.Empty);
        var validationResults = ValidateModel(request);
        validationResults.Should().BeEmpty();
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void CommitBackupRunRequest_WithSameDeviceIdAndRunId_PassesValidation()
    {
        // Arrange
        var guid = Guid.NewGuid();
        var request = new CommitBackupRunRequest
        {
            DeviceId = guid,
            RunId = guid
        };

        // Act
        var validationResults = ValidateModel(request);

        // Assert - While unusual, same GUID is technically valid at the model level
        validationResults.Should().BeEmpty();
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void CommitBackupRunRequest_WithDifferentValidGuids_PassesValidation()
    {
        // Arrange
        var request = new CommitBackupRunRequest
        {
            DeviceId = Guid.NewGuid(),
            RunId = Guid.NewGuid()
        };

        // Act
        var validationResults = ValidateModel(request);

        // Assert
        validationResults.Should().BeEmpty();
    }

    #endregion

    #region Helper Methods

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

    #endregion
}
