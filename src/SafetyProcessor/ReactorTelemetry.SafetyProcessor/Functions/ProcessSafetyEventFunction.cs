using Azure.Messaging.ServiceBus;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Logging;
using ReactorTelemetry.SafetyProcessor.Services;
using ReactorTelemetry.Shared.Models;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace ReactorTelemetry.SafetyProcessor.Functions;

// EDUCATIONAL: Service Bus retry / dead-letter flow
//
//   Message arrives → Function runs
//   ├── Success     → messageActions.CompleteMessageAsync()  → message deleted from subscription
//   ├── Throw       → lock expires → message redelivered (up to maxDeliveryCount times)
//   └── DLQ         → messageActions.DeadLetterMessageAsync() → moves to $deadletterqueue
//
// maxDeliveryCount = 3 (set in servicebus.bicep for this subscription).
// After 3 failures, Service Bus automatically moves the message to the DLQ.
// The ReprocessDeadLetterFunction handles DLQ drain every 5 minutes.
//
// Key design rule:
//   Dead-letter IMMEDIATELY for non-retriable errors (bad JSON, null payload).
//   THROW for retriable errors (transient DB failure, downstream service unavailable).
//   Never dead-letter a message you could successfully retry — DLQ is for permanent failures.

public class ProcessSafetyEventFunction
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() }
    };

    private readonly ILogger<ProcessSafetyEventFunction> _logger;
    private readonly SafetyEvaluationService _evaluationService;

    public ProcessSafetyEventFunction(
        ILogger<ProcessSafetyEventFunction> logger,
        SafetyEvaluationService evaluationService)
    {
        _logger = logger;
        _evaluationService = evaluationService;
    }

    [Function(nameof(ProcessSafetyEventFunction))]
    public async Task Run(
        // EDUCATIONAL: Connection = "ServiceBusConnection" maps to the app setting prefix
        // ServiceBusConnection__fullyQualifiedNamespace (managed identity auth pattern).
        // The double-underscore (__) is how .NET configuration flattens nested keys.
        // %ServiceBusTopic% and %ServiceBusSubscription% are resolved from app settings at runtime.
        [ServiceBusTrigger(
            topicName: "%ServiceBusTopic%",
            subscriptionName: "%ServiceBusSubscription%",
            Connection = "ServiceBusConnection",
            IsSessionsEnabled = false)]
        ServiceBusReceivedMessage message,
        ServiceBusMessageActions messageActions,   // Exposes Complete / DeadLetter / Abandon
        CancellationToken cancellationToken)
    {
        using var scope = _logger.BeginScope(new Dictionary<string, object>
        {
            ["MessageId"]     = message.MessageId,
            ["CorrelationId"] = message.CorrelationId ?? "",
            ["DeliveryCount"] = message.DeliveryCount
        });

        _logger.LogInformation(
            "Processing safety event. MessageId={MessageId}, DeliveryCount={DeliveryCount}",
            message.MessageId, message.DeliveryCount);

        // Extra diagnostics on later delivery attempts — helps understand why it's retrying
        if (message.DeliveryCount > 1)
            _logger.LogWarning(
                "Message {MessageId} is on attempt {Attempt}/{Max}. EnqueuedAt={EnqueuedAt}",
                message.MessageId, message.DeliveryCount, 3, message.EnqueuedTime);

        // ── Deserialize ───────────────────────────────────────────────────────
        TelemetryEvent? telemetryEvent;
        try
        {
            telemetryEvent = JsonSerializer.Deserialize<TelemetryEvent>(
                message.Body.ToString(), JsonOptions);
        }
        catch (JsonException ex)
        {
            // EDUCATIONAL: Deserialization failure is NOT retriable — the message body
            // won't spontaneously fix itself between retries. Dead-letter immediately
            // to avoid burning all 3 delivery attempts on a fundamentally bad message.
            _logger.LogError(ex, "Cannot deserialize message {MessageId} — dead-lettering", message.MessageId);
            await messageActions.DeadLetterMessageAsync(
                message,
                deadLetterReason: "DeserializationFailed",
                deadLetterErrorDescription: ex.Message,
                cancellationToken);
            return;
        }

        if (telemetryEvent is null)
        {
            _logger.LogError("Deserialized null for message {MessageId} — dead-lettering", message.MessageId);
            await messageActions.DeadLetterMessageAsync(
                message, "NullPayload", "Message deserialized to null.",
                cancellationToken);
            return;
        }

        // ── Evaluate ──────────────────────────────────────────────────────────
        try
        {
            await _evaluationService.EvaluateSafetyEventAsync(telemetryEvent, cancellationToken);

            // EDUCATIONAL: We call CompleteMessageAsync explicitly because the trigger
            // binding is configured with autoComplete: false in host.json.
            // Explicit completion gives us control: we only complete AFTER successful processing.
            // If we complete before and then crash, the event is lost. If we don't complete
            // and crash, the lock expires and the message is redelivered — safe retry.
            await messageActions.CompleteMessageAsync(message, cancellationToken);

            _logger.LogInformation(
                "Completed safety event {EventId} for reactor {ReactorId}",
                telemetryEvent.CorrelationId, telemetryEvent.ReactorId);
        }
        catch (OperationCanceledException)
        {
            // Cancellation during shutdown — don't dead-letter, let the lock expire for redelivery
            _logger.LogWarning("Processing cancelled for message {MessageId} — will be redelivered", message.MessageId);
            throw;
        }
        catch (Exception ex)
        {
            // EDUCATIONAL: For retriable errors, throw rather than dead-lettering.
            // Service Bus will redeliver up to maxDeliveryCount times.
            // After that, it auto-dead-letters with reason "MaxDeliveryCountExceeded".
            _logger.LogError(ex,
                "Failed to evaluate message {MessageId} (attempt {Attempt}/3) — will retry",
                message.MessageId, message.DeliveryCount);
            throw;
        }
    }
}
