namespace ReactorTelemetry.Shared.Models;

// EDUCATIONAL: This enum integer value is NOT persisted anywhere — we use the
// string name in Service Bus ApplicationProperties (for SQL filter compatibility).
// Using [JsonConverter(typeof(JsonStringEnumConverter))] in Program.cs ensures
// JSON serialization uses "Warning" not 1.

/// <summary>
/// Safety classification of a reactor telemetry reading.
/// Used as a Service Bus message property for subscription SQL filtering.
/// </summary>
public enum SafetyLevel
{
    /// <summary>All readings within normal operating parameters.</summary>
    Normal = 0,

    /// <summary>One or more readings approaching operational limits. Log and monitor.</summary>
    Warning = 1,

    /// <summary>Readings outside operational limits. Requires immediate operator attention.</summary>
    Critical = 2,

    /// <summary>Readings indicate potential safety system activation. Automated response may trigger.</summary>
    Emergency = 3
}
