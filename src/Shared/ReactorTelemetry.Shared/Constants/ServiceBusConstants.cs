namespace ReactorTelemetry.Shared.Constants;

// EDUCATIONAL: Centralizing these names prevents typos that cause silent failures.
// A misspelled subscription name won't throw — the trigger simply never fires.

public static class ServiceBusConstants
{
    public const string TopicName = "reactor-events";
    public const string SafetyProcessorSubscription = "safety-processor";
    public const string AuditLoggerSubscription = "audit-logger";

    // Service Bus message ApplicationProperty keys
    // These must match the SQL filter expression in servicebus.bicep exactly.
    public const string SafetyLevelProperty = "SafetyLevel";
    public const string ReactorIdProperty = "ReactorId";
    public const string CorrelationIdProperty = "CorrelationId";
}
