using FluentValidation;
using ReactorTelemetry.Shared.Models;

namespace ReactorTelemetry.Ingestor.Validators;

// EDUCATIONAL: FluentValidation separates validation rules from the model itself.
// Benefits over DataAnnotations:
// - Richer conditions (When, Unless, DependentRules)
// - Testable in isolation (just instantiate and call Validate())
// - No pollution of the domain model with UI/validation attributes
// - Async validation support (e.g., database uniqueness checks)

public class TelemetryEventValidator : AbstractValidator<TelemetryEvent>
{
    // Normal operating ranges — in a real system these would come from configuration
    private const double MinCoreTemp = 0;
    private const double MaxCoreTemp = 3500;
    private const double MinCoolantPressure = 0;
    private const double MaxCoolantPressure = 200;

    // Reject events with timestamps too far in the past or future
    // (prevents replay attacks and clock-skew bugs)
    private static readonly TimeSpan MaxTimestampAge = TimeSpan.FromHours(1);
    private static readonly TimeSpan MaxTimestampFuture = TimeSpan.FromMinutes(5);

    public TelemetryEventValidator()
    {
        RuleFor(x => x.ReactorId)
            .NotEmpty()
            .WithMessage("ReactorId is required and must be a valid UUID.");

        RuleFor(x => x.Timestamp)
            .NotEmpty()
            .Must(ts => ts >= DateTimeOffset.UtcNow - MaxTimestampAge)
            .WithMessage($"Timestamp is too old (max age: {MaxTimestampAge.TotalHours}h).")
            .Must(ts => ts <= DateTimeOffset.UtcNow + MaxTimestampFuture)
            .WithMessage($"Timestamp is too far in the future (max: {MaxTimestampFuture.TotalMinutes}min).");

        RuleFor(x => x.SafetyLevel)
            .IsInEnum()
            .WithMessage("SafetyLevel must be one of: Normal, Warning, Critical, Emergency.");

        RuleFor(x => x.Readings)
            .NotNull()
            .WithMessage("Readings object is required.");

        When(x => x.Readings != null, () =>
        {
            RuleFor(x => x.Readings.CoreTemperatureCelsius)
                .InclusiveBetween(MinCoreTemp, MaxCoreTemp)
                .WithMessage($"CoreTemperatureCelsius must be between {MinCoreTemp} and {MaxCoreTemp}°C.");

            RuleFor(x => x.Readings.CoolantPressureBar)
                .InclusiveBetween(MinCoolantPressure, MaxCoolantPressure)
                .WithMessage($"CoolantPressureBar must be between {MinCoolantPressure} and {MaxCoolantPressure} bar.");

            // Neutron flux is optional, but if provided it must be non-negative
            When(x => x.Readings.NeutronFluxPerCm2s.HasValue, () =>
            {
                RuleFor(x => x.Readings.NeutronFluxPerCm2s!.Value)
                    .GreaterThanOrEqualTo(0)
                    .WithMessage("NeutronFluxPerCm2s must be non-negative.");
            });

            // EDUCATIONAL: Cross-field validation — Critical/Emergency readings
            // should have physically plausible values (extra safety check)
            RuleFor(x => x)
                .Must(e => e.SafetyLevel < SafetyLevel.Critical ||
                           e.Readings.CoreTemperatureCelsius > 100)
                .WithMessage("Critical/Emergency events must have a core temperature above 100°C (sanity check).");
        });
    }
}
