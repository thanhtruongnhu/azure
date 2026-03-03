using FluentAssertions;
using Microsoft.ApplicationInsights;
using Microsoft.ApplicationInsights.Extensibility;
using Microsoft.Extensions.Logging.Abstractions;
using ReactorTelemetry.SafetyProcessor.Services;
using ReactorTelemetry.Shared.Models;
using Xunit;

namespace ReactorTelemetry.Tests.Unit;

// EDUCATIONAL: Testing a service that has side-effects (App Insights telemetry)
//
// Strategy: Use TelemetryConfiguration.CreateDefault() with a no-op channel
// so TelemetryClient doesn't throw when there's no connection string in tests.
// We verify behavior through logging (NullLogger in unit tests) and by checking
// that no exceptions are thrown.
//
// For production-grade App Insights testing, use ITelemetryChannel with a
// List<ITelemetry> backing store (spy pattern) to assert on emitted telemetry.

public class SafetyEvaluationServiceTests
{
    private readonly SafetyEvaluationService _sut;

    public SafetyEvaluationServiceTests()
    {
        // Minimal TelemetryClient that doesn't require a real App Insights endpoint
        var config = TelemetryConfiguration.CreateDefault();
        config.DisableTelemetry = true;
        var telemetryClient = new TelemetryClient(config);

        _sut = new SafetyEvaluationService(
            NullLogger<SafetyEvaluationService>.Instance,
            telemetryClient);
    }

    [Theory]
    [InlineData(SafetyLevel.Warning)]
    [InlineData(SafetyLevel.Critical)]
    [InlineData(SafetyLevel.Emergency)]
    public async Task EvaluateSafetyEventAsync_does_not_throw_for_any_safety_level(SafetyLevel level)
    {
        var evt = BuildEvent(level);
        var act = () => _sut.EvaluateSafetyEventAsync(evt, CancellationToken.None);
        await act.Should().NotThrowAsync();
    }

    [Fact]
    public async Task EvaluateSafetyEventAsync_completes_for_normal_level()
    {
        // Normal events are filtered at the Service Bus level and should never
        // reach the SafetyProcessor, but the service should handle them gracefully.
        var evt = BuildEvent(SafetyLevel.Normal);
        var act = () => _sut.EvaluateSafetyEventAsync(evt, CancellationToken.None);
        await act.Should().NotThrowAsync();
    }

    [Fact]
    public async Task EvaluateSafetyEventAsync_handles_null_neutron_flux()
    {
        var evt = BuildEvent(SafetyLevel.Warning) with
        {
            Readings = new ReactorReadings
            {
                CoreTemperatureCelsius = 310,
                CoolantPressureBar     = 158,
                NeutronFluxPerCm2s     = null
            }
        };
        var act = () => _sut.EvaluateSafetyEventAsync(evt, CancellationToken.None);
        await act.Should().NotThrowAsync();
    }

    private static TelemetryEvent BuildEvent(SafetyLevel level) => new()
    {
        ReactorId   = Guid.NewGuid(),
        Timestamp   = DateTimeOffset.UtcNow,
        SafetyLevel = level,
        Readings    = new ReactorReadings
        {
            CoreTemperatureCelsius = level >= SafetyLevel.Critical ? 340 : 310,
            CoolantPressureBar     = 155,
            NeutronFluxPerCm2s     = 3.2e13
        },
        AuthenticatedSubject = "test-regulator"
    };
}
