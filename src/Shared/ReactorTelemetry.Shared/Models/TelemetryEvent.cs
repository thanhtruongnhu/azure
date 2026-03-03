namespace ReactorTelemetry.Shared.Models;

// EDUCATIONAL: C# records (record + sealed) are ideal for domain model DTOs:
// - Immutable by default (init-only properties)
// - Value equality built in (two records with same fields are equal)
// - Positional syntax or named properties
// - 'with' expressions for non-destructive mutation
//
// This class is shared across all three Function projects via a project reference
// (not a NuGet package). For a real platform, publish this as a versioned NuGet
// package so each Function can independently version its contract dependency.

/// <summary>
/// Core domain model for a reactor telemetry reading.
/// Flows: HTTP request body → Service Bus message body → downstream processors.
/// </summary>
public sealed record TelemetryEvent
{
    /// <summary>Unique identifier for the reactor unit.</summary>
    public required Guid ReactorId { get; init; }

    /// <summary>UTC timestamp of the reading, provided by the regulator system.</summary>
    public required DateTimeOffset Timestamp { get; init; }

    /// <summary>Safety classification of this reading.</summary>
    public required SafetyLevel SafetyLevel { get; init; }

    /// <summary>Sensor measurements at the time of reading.</summary>
    public required ReactorReadings Readings { get; init; }

    /// <summary>
    /// Client-provided idempotency key. Used as Service Bus MessageId for
    /// duplicate detection within a 10-minute window. If the client does not
    /// provide one, the Ingestor Function generates a new Guid.
    /// </summary>
    public Guid CorrelationId { get; init; } = Guid.NewGuid();

    /// <summary>
    /// The 'sub' claim from the validated JWT, injected by APIM policy.
    /// Identifies which regulator system submitted this reading.
    /// Null when processing locally without APIM.
    /// </summary>
    public string? AuthenticatedSubject { get; init; }
}

/// <summary>Sensor readings from the reactor instrumentation.</summary>
public sealed record ReactorReadings
{
    /// <summary>Core temperature in Celsius. Normal operating range: 250–320°C.</summary>
    public required double CoreTemperatureCelsius { get; init; }

    /// <summary>Primary coolant loop pressure in bar. Normal range: 140–160 bar.</summary>
    public required double CoolantPressureBar { get; init; }

    /// <summary>Neutron flux (neutrons/cm²/s). Optional — not all reactors have this sensor.</summary>
    public double? NeutronFluxPerCm2s { get; init; }
}
