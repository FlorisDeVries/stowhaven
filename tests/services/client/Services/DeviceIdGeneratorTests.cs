using FluentAssertions;
using FlorisDeV.BackupClient.Services;

namespace FlorisDeV.BackupClient.Tests.Services;

/// <summary>
/// Tests for DeviceIdGenerator functionality.
/// Note: These tests verify behavior and stability rather than exact values,
/// since actual device IDs depend on hardware characteristics.
/// </summary>
public class DeviceIdGeneratorTests
{
    [Fact]
    [Trait("Category", "Unit")]
    public void GenerateDeviceId_ReturnsValidGuid()
    {
        // Act
        var deviceId = DeviceIdGenerator.GenerateDeviceId();

        // Assert
        deviceId.Should().NotBe(Guid.Empty);
        deviceId.Should().NotBe(default(Guid));
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void GenerateDeviceId_CalledMultipleTimes_ReturnsSameId()
    {
        // Act
        var deviceId1 = DeviceIdGenerator.GenerateDeviceId();
        var deviceId2 = DeviceIdGenerator.GenerateDeviceId();
        var deviceId3 = DeviceIdGenerator.GenerateDeviceId();

        // Assert
        // The device ID should be deterministic based on hardware - same across calls
        deviceId1.Should().Be(deviceId2);
        deviceId2.Should().Be(deviceId3);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void GenerateDeviceId_IsStable_AcrossMultipleCalls()
    {
        // Arrange
        const int iterations = 100;
        var deviceIds = new HashSet<Guid>();

        // Act
        for (int i = 0; i < iterations; i++)
        {
            deviceIds.Add(DeviceIdGenerator.GenerateDeviceId());
        }

        // Assert
        // Should only generate a single unique ID across all calls (deterministic)
        deviceIds.Should().HaveCount(1, "device ID should be stable and deterministic");
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void GenerateDeviceId_DoesNotThrowException()
    {
        // Act
        var act = () => DeviceIdGenerator.GenerateDeviceId();

        // Assert
        act.Should().NotThrow("the method should handle all exceptions gracefully");
    }

    [Fact]
    [Trait("Category", "Integration")]
    public void GenerateDeviceId_ProducesConsistentFormat()
    {
        // Act
        var deviceId = DeviceIdGenerator.GenerateDeviceId();
        var deviceIdString = deviceId.ToString();

        // Assert
        deviceIdString.Should().MatchRegex(@"^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$",
            "device ID should be a valid GUID format");
    }

    [Fact]
    [Trait("Category", "Integration")]
    public void GenerateDeviceId_CanBeParsedBackToGuid()
    {
        // Act
        var deviceId = DeviceIdGenerator.GenerateDeviceId();
        var deviceIdString = deviceId.ToString();
        var parsed = Guid.Parse(deviceIdString);

        // Assert
        parsed.Should().Be(deviceId);
    }

    [Fact]
    [Trait("Category", "Integration")]
    public void GenerateDeviceId_IsDifferentFromNewGuid()
    {
        // Arrange
        var randomGuids = Enumerable.Range(0, 10)
            .Select(_ => Guid.NewGuid())
            .ToList();

        // Act
        var deviceId = DeviceIdGenerator.GenerateDeviceId();

        // Assert
        // Device ID should ideally not match random GUIDs (deterministic vs random)
        // This might fail in extremely rare cases, but indicates proper hardware-based generation
        randomGuids.Should().NotContain(deviceId, 
            "device ID should be hardware-based and not randomly generated");
    }

    [Theory]
    [Trait("Category", "Unit")]
    [InlineData(5)]
    [InlineData(10)]
    [InlineData(50)]
    public void GenerateDeviceId_ConcurrentCalls_ProduceSameId(int concurrentCalls)
    {
        // Arrange
        var deviceIds = new System.Collections.Concurrent.ConcurrentBag<Guid>();

        // Act
        Parallel.For(0, concurrentCalls, _ =>
        {
            deviceIds.Add(DeviceIdGenerator.GenerateDeviceId());
        });

        // Assert
        deviceIds.Distinct().Should().HaveCount(1, 
            "all concurrent calls should produce the same deterministic device ID");
    }

    [Fact]
    [Trait("Category", "Integration")]
    public void GenerateDeviceId_IncorporatesMachineName()
    {
        // Arrange
        var machineName = Environment.MachineName;

        // Act
        var deviceId = DeviceIdGenerator.GenerateDeviceId();

        // Assert
        // We can't directly test if machine name is incorporated (it's hashed),
        // but we can verify it doesn't throw and produces valid output
        deviceId.Should().NotBe(Guid.Empty);
        machineName.Should().NotBeNullOrEmpty("machine name should be available");
    }

    [Fact]
    [Trait("Category", "Performance")]
    public void GenerateDeviceId_Performance_CompletesQuickly()
    {
        // Arrange
        var sw = System.Diagnostics.Stopwatch.StartNew();

        // Act
        var deviceId = DeviceIdGenerator.GenerateDeviceId();

        // Assert
        sw.Stop();
        sw.ElapsedMilliseconds.Should().BeLessThan(1000, 
            "device ID generation should complete within 1 second");
        deviceId.Should().NotBe(Guid.Empty);
    }

    [Fact]
    [Trait("Category", "Performance")]
    public void GenerateDeviceId_MultipleCalls_AreEfficient()
    {
        // Arrange
        const int callCount = 1000;
        var sw = System.Diagnostics.Stopwatch.StartNew();

        // Act
        for (int i = 0; i < callCount; i++)
        {
            var _ = DeviceIdGenerator.GenerateDeviceId();
        }

        // Assert
        sw.Stop();
        var averageMs = (double)sw.ElapsedMilliseconds / callCount;
        averageMs.Should().BeLessThan(10, 
            "average call should take less than 10ms");
    }

    [Fact]
    [Trait("Category", "Integration")]
    public void GenerateDeviceId_ResultCanBeStoredAndRetrieved()
    {
        // Arrange
        var deviceId = DeviceIdGenerator.GenerateDeviceId();
        var tempFile = Path.Combine(Path.GetTempPath(), $"device-id-test-{Guid.NewGuid()}.txt");

        try
        {
            // Act
            File.WriteAllText(tempFile, deviceId.ToString());
            var retrieved = Guid.Parse(File.ReadAllText(tempFile));

            // Assert
            retrieved.Should().Be(deviceId);
        }
        finally
        {
            // Cleanup
            if (File.Exists(tempFile))
            {
                File.Delete(tempFile);
            }
        }
    }

    [Fact]
    [Trait("Category", "Integration")]
    public void GenerateDeviceId_IsValidForUseAsIdentifier()
    {
        // Act
        var deviceId = DeviceIdGenerator.GenerateDeviceId();

        // Assert
        // Verify it can be used in common scenarios
        deviceId.ToString().Should().NotBeNullOrWhiteSpace();
        deviceId.ToString("N").Should().HaveLength(32); // Hex format without hyphens
        deviceId.ToString("D").Should().HaveLength(36); // Standard format with hyphens
        deviceId.ToByteArray().Should().HaveCount(16);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void GenerateDeviceId_NeverReturnsEmptyGuid()
    {
        // Arrange
        const int iterations = 100;

        // Act & Assert
        for (int i = 0; i < iterations; i++)
        {
            var deviceId = DeviceIdGenerator.GenerateDeviceId();
            deviceId.Should().NotBe(Guid.Empty, 
                $"iteration {i} should not return empty GUID");
        }
    }

    [Fact]
    [Trait("Category", "Integration")]
    public void GenerateDeviceId_HashingProducesValidBytes()
    {
        // Act
        var deviceId = DeviceIdGenerator.GenerateDeviceId();
        var bytes = deviceId.ToByteArray();

        // Assert
        bytes.Should().HaveCount(16, "GUID should be 16 bytes");
        bytes.Should().Contain(b => b != 0, "should not be all zeros");
    }
}
