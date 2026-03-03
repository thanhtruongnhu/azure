using Azure.Messaging.ServiceBus;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Logging;

namespace ReactorTelemetry.SafetyProcessor.Functions;

// EDUCATIONAL: Dead-Letter Queue (DLQ) handling
//
// The DLQ is a sub-queue that automatically receives messages that:
//   1. Exceeded maxDeliveryCount (3 for safety-processor)
//   2. Expired (TTL exceeded) and enableDeadLetteringOnMessageExpiration = true
//   3. Were explicitly dead-lettered by application code (DeadLetterMessageAsync)
//   4. Failed subscription filter evaluation (if deadLetteringOnFilterEvaluationExceptions = true)
//
// DLQ path: <topic>/subscriptions/<subscription>/$deadletterqueue
//
// DLQs do NOT process themselves. You need explicit code to:
//   a) Inspect the message and its DeadLetterReason / DeadLetterErrorDescription
//   b) Decide: fix-and-resubmit OR alert operator OR archive and delete
//
// This timer-triggered function runs every 5 minutes and drains the DLQ.
// In production, you'd send an alert to an on-call channel and potentially
// resubmit after fixing the root cause.

public class ReprocessDeadLetterFunction
{
    private readonly ILogger<ReprocessDeadLetterFunction> _logger;
    private readonly ServiceBusClient _serviceBusClient;

    public ReprocessDeadLetterFunction(
        ILogger<ReprocessDeadLetterFunction> logger,
        ServiceBusClient serviceBusClient)
    {
        _logger = logger;
        _serviceBusClient = serviceBusClient;
    }

    // EDUCATIONAL: CRON expression format for Azure Functions timer:
    //   {second} {minute} {hour} {day} {month} {day-of-week}
    //   "0 */5 * * * *" = at second 0, every 5 minutes, every hour, every day
    [Function(nameof(ReprocessDeadLetterFunction))]
    public async Task Run(
        [TimerTrigger("0 */5 * * * *")] TimerInfo timer,
        CancellationToken cancellationToken)
    {
        if (timer.IsPastDue)
            _logger.LogWarning("DLQ drain timer is running late");

        var topicName        = Environment.GetEnvironmentVariable("ServiceBusTopic") ?? "reactor-events";
        var subscriptionName = Environment.GetEnvironmentVariable("ServiceBusSubscription") ?? "safety-processor";

        // EDUCATIONAL: The DLQ is accessed by appending /$deadletterqueue to the subscription path.
        // ServiceBusClient.CreateReceiver has a SubQueue parameter for this.
        var receiver = _serviceBusClient.CreateReceiver(
            topicName,
            subscriptionName,
            new ServiceBusReceiverOptions { SubQueue = SubQueue.DeadLetter });

        await using (receiver)
        {
            int processed = 0;

            // Drain up to 50 DLQ messages per run (prevents function timeout on large DLQs)
            while (processed < 50)
            {
                // ReceiveMessageAsync with a short timeout — returns null if DLQ is empty
                var deadLetter = await receiver.ReceiveMessageAsync(
                    maxWaitTime: TimeSpan.FromSeconds(2),
                    cancellationToken);

                if (deadLetter is null)
                    break; // DLQ is empty

                var reason = deadLetter.DeadLetterReason ?? "Unknown";
                var description = deadLetter.DeadLetterErrorDescription ?? "";

                _logger.LogWarning(
                    "DLQ message {MessageId}: Reason={Reason}, Description={Description}, " +
                    "DeliveryCount={DeliveryCount}, EnqueuedAt={EnqueuedAt}",
                    deadLetter.MessageId, reason, description,
                    deadLetter.DeliveryCount, deadLetter.EnqueuedTime);

                // Decision logic: what to do with this DLQ message?
                if (reason == "DeserializationFailed")
                {
                    // Non-retriable: log the raw body for manual inspection, then complete (discard)
                    _logger.LogError(
                        "Non-retriable DLQ message {MessageId}. Raw body: {Body}",
                        deadLetter.MessageId,
                        deadLetter.Body.ToString()[..Math.Min(500, deadLetter.Body.ToString().Length)]);

                    // In production: write raw body to a "poison message" storage container
                    // for forensic analysis, then alert the on-call engineer.
                    await receiver.CompleteMessageAsync(deadLetter, cancellationToken);
                }
                else if (reason == "MaxDeliveryCountExceeded")
                {
                    // Possibly retriable (transient failure exhausted retries).
                    // For now: log with high severity and complete (discard).
                    // In production: determine if the root cause is fixed, then resubmit.
                    _logger.LogError(
                        "Message {MessageId} exhausted all retries. ReactorId={ReactorId}",
                        deadLetter.MessageId,
                        deadLetter.ApplicationProperties.GetValueOrDefault("ReactorId", "unknown"));

                    await receiver.CompleteMessageAsync(deadLetter, cancellationToken);
                }
                else
                {
                    // Unknown reason — complete and log for investigation
                    _logger.LogError(
                        "Unknown DLQ reason for {MessageId}: {Reason}",
                        deadLetter.MessageId, reason);
                    await receiver.CompleteMessageAsync(deadLetter, cancellationToken);
                }

                processed++;
            }

            if (processed > 0)
                _logger.LogInformation("DLQ drain completed: {Count} messages processed", processed);
        }
    }
}
