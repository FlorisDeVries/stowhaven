using System.ComponentModel.DataAnnotations;
using FlorisDeV.BackupApi.Models;
using Xunit;

namespace FlorisDeV.BackupApi.Tests;

public class SasRequestTests
{
    [Fact]
    public void SasRequest_ValidPath_PassesValidation()
    {
        // Arrange
        var request = new SasRequest
        {
            Path = "test/file.txt",
            TtlMinutes = 60
        };

        // Act
        var validationResults = ValidateModel(request);

        // Assert
        Assert.Empty(validationResults);
    }

    [Fact]
    public void SasRequest_EmptyPath_FailsValidation()
    {
        // Arrange
        var request = new SasRequest
        {
            Path = "",
            TtlMinutes = 60
        };

        // Act
        var validationResults = ValidateModel(request);

        // Assert
        Assert.Single(validationResults);
        Assert.Contains("Path is required", validationResults.First().ErrorMessage);
    }

    [Fact]
    public void SasRequest_TtlTooHigh_FailsValidation()
    {
        // Arrange
        var request = new SasRequest
        {
            Path = "test/file.txt",
            TtlMinutes = 300 // Over 240 limit
        };

        // Act
        var validationResults = ValidateModel(request);

        // Assert
        Assert.Single(validationResults);
        Assert.Contains("TTL must be between 1 and 240 minutes", validationResults.First().ErrorMessage);
    }

    private static List<ValidationResult> ValidateModel(object model)
    {
        var validationResults = new List<ValidationResult>();
        var validationContext = new ValidationContext(model);
        Validator.TryValidateObject(model, validationContext, validationResults, true);
        return validationResults;
    }
}
