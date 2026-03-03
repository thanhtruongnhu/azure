using Azure.Messaging.ServiceBus;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Logging;
using ReactorTelemetry.AuditLogger.Services;
using ReactorTelemetry.Shared.Models;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace ReactorTelemetry.AuditLogger.Functions;

// EDUCATIONAL: The audit-logger subscription has NO SQL filter — it receives ALL events,
// including Normal readings. This is intentional: a regulatory audit trail must be complete.
// The safety-processor subscription only gets Warning/Critical/Emergency events.
//
// This function demonstrates the "fan-out" pattern: one published message (in the topic)
// is independently consumed by TWO subscribers (safety-processor + audit-logger),
// each doing different work. Neither subscriber affects the other.

public class LogAuditEventFunction
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() }
    };

    private readonly ILogger<LogAuditEventFunction> _logger;
    private readonly AuditTableService _auditTableService;

    public LogAuditEventFunction(
        ILogger<LogAuditEventFunction> logger,
        AuditTableService auditTableService)
    {
        _logger = logger;
        _auditTableService = auditTableService;
    }

    [Function(nameof(LogAuditEventFunction))]
    public async Task Run(
        [ServiceBusTrigger(
            topicName: "%ServiceBusTopic%",
            subscriptionName: "%ServiceBusSubscription%",
            Connection = "ServiceBusConnection",
            IsSessionsEnabled = false)]
        ServiceBusReceivedMessage message,
        ServiceBusMessageActions messageActions,
        CancellationToken cancellationToken)
    {
        using var scope = _logger.BeginScope(new Dictionary<string, object>
        {
            ["MessageId"]     = message.MessageId,
            ["CorrelationId"] = message.CorrelationId ?? "",
            ["SafetyLevel"]   = message.ApplicationProperties.GetValueOrDefault("SafetyLevel", "unknown")
        });

        _logger.LogInformation("Audit logging event {MessageId}", message.MessageId);

        TelemetryEvent? telemetryEvent;
        try
        {
            telemetryEvent = JsonSerializer.Deserialize<TelemetryEvent>(
                message.Body.ToString(), JsonOptions);
        }
        catch (JsonException ex)
        {
            // Non-retriable: dead-letter malformed audit records rather than losing them silently
            _logger.LogError(ex, "Cannot deserialize audit message {MessageId}", message.MessageId);
            await messageActions.DeadLetterMessageAsync(
                message, "DeserializationFailed", ex.Message, cancellationToken);
            return;
        }

        if (telemetryEvent is null)
        {
            await messageActions.DeadLetterMessageAsync(
                message, "NullPayload", "Deserialized to null.", cancellationToken);
            return;
        }

        try
        {
            await _auditTableService.WriteAuditRecordAsync(telemetryEvent, cancellationToken);
            await messageActions.CompleteMessageAsync(message, cancellationToken);

            _logger.LogInformation(
                "Audit record persisted for reactor {ReactorId}, event {EventId}",
                telemetryEvent.ReactorId, telemetryEvent.CorrelationId);
        }
        catch (Exception ex)
        {
            // Retriable failure (e.g., Table Storage transient error) — throw for retry
            _logger.LogError(ex, "Failed to persist audit record for {MessageId}", message.MessageId);
            throw;
        }
    }
}
