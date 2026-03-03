using FluentAssertions;
using ReactorTelemetry.Ingestor.Validators;
using ReactorTelemetry.Shared.Models;
using Xunit;

namespace ReactorTelemetry.Tests.Unit;

// EDUCATIONAL: xUnit test structure
// - [Fact]   = single test case, no parameters
// - [Theory] = parameterized test; takes [InlineData], [MemberData], or [ClassData]
// - Arrange / Act / Assert pattern keeps tests readable
// - FluentAssertions: .Should().BeTrue() reads like natural language
//   vs Assert.True() — better failure messages and easier to chain

public class TelemetryEventValidatorTests
{
    private readonly TelemetryEventValidator _sut = new();

    // ── Valid events ──────────────────────────────────────────────────────────

    [Fact]
    public void Valid_normal_event_passes_validation()
    {
        var evt = BuildValidEvent();
        var result = _sut.Validate(evt);
        result.IsValid.Should().BeTrue();
    }

    [Theory]
    [InlineData(SafetyLevel.Normal)]
    [InlineData(SafetyLevel.Warning)]
    [InlineData(SafetyLevel.Critical)]
    [InlineData(SafetyLevel.Emergency)]
    public void All_safety_levels_are_accepted(SafetyLevel level)
    {
        var evt = BuildValidEvent() with { SafetyLevel = level };
        _sut.Validate(evt).IsValid.Should().BeTrue();
    }

    // ── Invalid events ────────────────────────────────────────────────────────

    [Fact]
    public void Empty_reactorId_fails_validation()
    {
        var evt = BuildValidEvent() with { ReactorId = Guid.Empty };
        var result = _sut.Validate(evt);

        result.IsValid.Should().BeFalse();
        result.Errors.Should().ContainSingle(e =>
            e.PropertyName == "ReactorId");
    }

    [Fact]
    public void Stale_timestamp_fails_validation()
    {
        var evt = BuildValidEvent() with
        {
            Timestamp = DateTimeOffset.UtcNow.AddHours(-2)
        };
        var result = _sut.Validate(evt);

        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e => e.PropertyName == "Timestamp");
    }

    [Fact]
    public void Future_timestamp_fails_validation()
    {
        var evt = BuildValidEvent() with
        {
            Timestamp = DateTimeOffset.UtcNow.AddMinutes(10)
        };
        var result = _sut.Validate(evt);

        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e => e.PropertyName == "Timestamp");
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(3501)]
    public void Out_of_range_core_temperature_fails(double temp)
    {
        var evt = BuildValidEvent() with
        {
            Readings = new ReactorReadings
            {
                CoreTemperatureCelsius = temp,
                CoolantPressureBar = 155
            }
        };
        var result = _sut.Validate(evt);

        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e =>
            e.PropertyName.Contains("CoreTemperatureCelsius"));
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(201)]
    public void Out_of_range_coolant_pressure_fails(double pressure)
    {
        var evt = BuildValidEvent() with
        {
            Readings = new ReactorReadings
            {
                CoreTemperatureCelsius = 285,
                CoolantPressureBar = pressure
            }
        };
        var result = _sut.Validate(evt);

        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e =>
            e.PropertyName.Contains("CoolantPressureBar"));
    }

    [Fact]
    public void Negative_neutron_flux_fails_validation()
    {
        var evt = BuildValidEvent() with
        {
            Readings = new ReactorReadings
            {
                CoreTemperatureCelsius = 285,
                CoolantPressureBar = 155,
                NeutronFluxPerCm2s = -1
            }
        };
        var result = _sut.Validate(evt);

        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e =>
            e.PropertyName.Contains("NeutronFluxPerCm2s"));
    }

    [Fact]
    public void Critical_event_with_low_temp_fails_sanity_check()
    {
        var evt = BuildValidEvent() with
        {
            SafetyLevel = SafetyLevel.Critical,
            Readings = new ReactorReadings
            {
                CoreTemperatureCelsius = 50,  // Implausibly low for a Critical event
                CoolantPressureBar = 155
            }
        };
        var result = _sut.Validate(evt);

        result.IsValid.Should().BeFalse();
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static TelemetryEvent BuildValidEvent() => new()
    {
        ReactorId  = Guid.NewGuid(),
        Timestamp  = DateTimeOffset.UtcNow,
        SafetyLevel = SafetyLevel.Normal,
        Readings   = new ReactorReadings
        {
            CoreTemperatureCelsius = 285.4,
            CoolantPressureBar     = 155.0,
            NeutronFluxPerCm2s     = 3.2e13
        }
    };
}
