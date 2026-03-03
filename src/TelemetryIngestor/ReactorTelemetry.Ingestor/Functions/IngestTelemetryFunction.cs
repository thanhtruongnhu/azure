using Azure.Messaging.ServiceBus;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Logging;
using ReactorTelemetry.Ingestor.Validators;
using ReactorTelemetry.Shared.Constants;
using ReactorTelemetry.Shared.Models;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace ReactorTelemetry.Ingestor.Functions;

// EDUCATIONAL: Trust boundary design
//
// APIM validates the JWT → extracts the 'sub' claim → injects it as x-authenticated-subject.
// The Function trusts this header because:
//   - The Function endpoint requires a secret x-functions-key header
//   - Only APIM holds that key (never exposed to the regulator client)
//   - So if x-authenticated-subject is present, it was injected by APIM after JWT validation
//
// This creates a clean separation:
//   APIM  → AuthN (who is this caller?)
//   Function → AuthZ + business logic (what can they do?)

public class IngestTelemetryFunction
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() }
    };

    private readonly ILogger<IngestTelemetryFunction> _logger;
    private readonly ServiceBusClient _serviceBusClient;
    private readonly TelemetryEventValidator _validator;

    public IngestTelemetryFunction(
        ILogger<IngestTelemetryFunction> logger,
        ServiceBusClient serviceBusClient,
        TelemetryEventValidator validator)
    {
        _logger = logger;
        _serviceBusClient = serviceBusClient;
        _validator = validator;
    }

    [Function(nameof(IngestTelemetryFunction))]
    public async Task<IActionResult> Run(
        [HttpTrigger(AuthorizationLevel.Function, "post", Route = "telemetry")] HttpRequest req,
        CancellationToken cancellationToken)
    {
        // Read APIM-injected headers
        var correlationId = req.Headers["x-correlation-id"].FirstOrDefault()
                            ?? Guid.NewGuid().ToString();
        var authenticatedSubject = req.Headers["x-authenticated-subject"].FirstOrDefault() ?? "unknown";
        var apimTimestamp = req.Headers["x-apim-timestamp"].FirstOrDefault();

        // EDUCATIONAL: BeginScope attaches these KV pairs to every log line emitted
        // within the using block. In Application Insights they become customDimensions,
        // making all traces for one request filterable by CorrelationId.
        using var scope = _logger.BeginScope(new Dictionary<string, object>
        {
            ["CorrelationId"] = correlationId,
            ["AuthenticatedSubject"] = authenticatedSubject
        });

        // ── Deserialize ───────────────────────────────────────────────────────
        TelemetryEvent? telemetryEvent;
        try
        {
            telemetryEvent = await JsonSerializer.DeserializeAsync<TelemetryEvent>(
                req.Body, JsonOptions, cancellationToken);
        }
        catch (JsonException ex)
        {
            _logger.LogWarning("Invalid JSON body: {Error}", ex.Message);
            return new BadRequestObjectResult(new
            {
                code = "INVALID_JSON",
                message = "Request body is not valid JSON.",
                traceId = correlationId
            });
        }

        if (telemetryEvent is null)
            return new BadRequestObjectResult(new
            {
                code = "EMPTY_BODY",
                message = "Request body is required.",
                traceId = correlationId
            });

        // ── Validate ──────────────────────────────────────────────────────────
        var validation = _validator.Validate(telemetryEvent);
        if (!validation.IsValid)
        {
            _logger.LogWarning("Validation failed for reactor {ReactorId}: {Errors}",
                telemetryEvent.ReactorId,
                string.Join("; ", validation.Errors.Select(e => e.ErrorMessage)));

            return new BadRequestObjectResult(new
            {
                code = "VALIDATION_FAILED",
                message = "One or more validation errors occurred.",
                errors = validation.Errors.Select(e => e.ErrorMessage),
                traceId = correlationId
            });
        }

        // Attach APIM-validated identity and ensure a CorrelationId exists
        telemetryEvent = telemetryEvent with
        {
            AuthenticatedSubject = authenticatedSubject,
            CorrelationId = telemetryEvent.CorrelationId == Guid.Empty
                ? Guid.Parse(correlationId)
                : telemetryEvent.CorrelationId
        };

        // ── Publish to Service Bus ────────────────────────────────────────────
        var topicName = Environment.GetEnvironmentVariable("ServiceBusTopic")
                        ?? ServiceBusConstants.TopicName;
        await using var sender = _serviceBusClient.CreateSender(topicName);

        var message = new ServiceBusMessage(JsonSerializer.SerializeToUtf8Bytes(telemetryEvent, JsonOptions))
        {
            // EDUCATIONAL: MessageId = CorrelationId enables duplicate detection.
            // Service Bus silently drops a second message with the same MessageId
            // within the 10-minute dedup window configured in servicebus.bicep.
            // This gives us at-most-once delivery without any application-level logic.
            MessageId = telemetryEvent.CorrelationId.ToString(),
            ContentType = "application/json",
            CorrelationId = correlationId,
            TimeToLive = TimeSpan.FromHours(1),

            // EDUCATIONAL: ApplicationProperties are evaluated by the Service Bus broker
            // BEFORE delivering a message to a subscription — the subscription SQL filter
            // runs here, not in application code. Setting SafetyLevel as a property
            // means the safety-processor subscription never even receives Normal events;
            // they are filtered out at the broker before any Function is invoked.
            ApplicationProperties =
            {
                [ServiceBusConstants.SafetyLevelProperty] = telemetryEvent.SafetyLevel.ToString(),
                [ServiceBusConstants.ReactorIdProperty]   = telemetryEvent.ReactorId.ToString(),
                [ServiceBusConstants.CorrelationIdProperty] = correlationId
            }
        };

        try
        {
            await sender.SendMessageAsync(message, cancellationToken);

            _logger.LogInformation(
                "Published event {EventId} for reactor {ReactorId}, SafetyLevel={SafetyLevel}",
                telemetryEvent.CorrelationId, telemetryEvent.ReactorId, telemetryEvent.SafetyLevel);

            // Log APIM → Function gateway overhead for SLA monitoring
            if (apimTimestamp is not null && DateTimeOffset.TryParse(apimTimestamp, out var apimTs))
                _logger.LogDebug("Gateway overhead: {OverheadMs}ms",
                    (DateTimeOffset.UtcNow - apimTs).TotalMilliseconds);

            return new AcceptedResult(location: null, value: new
            {
                eventId  = telemetryEvent.CorrelationId,
                status   = "Accepted",
                timestamp = DateTimeOffset.UtcNow
            });
        }
        catch (ServiceBusException ex) when (ex.Reason == ServiceBusFailureReason.MessagingEntityDisabled)
        {
            _logger.LogError(ex, "Service Bus topic {Topic} is disabled", topicName);
            return new ObjectResult(new { code = "SERVICE_UNAVAILABLE", message = "Event bus unavailable." })
            {
                StatusCode = StatusCodes.Status503ServiceUnavailable
            };
        }
    }

    // EDUCATIONAL: The /health endpoint has no auth (AuthorizationLevel.Anonymous)
    // matching the OpenAPI spec's `security: []` override on GET /health.
    // In APIM, operation-level policy can also exempt this route from JWT validation.
    [Function("HealthCheck")]
    public IActionResult Health(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "health")] HttpRequest req)
    {
        return new OkObjectResult(new
        {
            status    = "Healthy",
            version   = "1.0.0",
            timestamp = DateTimeOffset.UtcNow
        });
    }
}
