// EDUCATIONAL: Service Bus pub/sub topology:
//
//   Publisher (Ingestor Function)
//         │  sends to Topic
//         ▼
//   Topic: reactor-events
//         │
//   ┌─────┴─────────────────────┐
//   │                           │
//   Subscription: safety-processor   Subscription: audit-logger
//   SQL filter: SafetyLevel            No filter (all messages)
//   IN ('Warning','Critical',          Useful for: regulatory audit trail
//        'Emergency')
//   Useful for: alert/response
//
// Key concepts:
// - Topics require Standard tier (Basic only supports Queues)
// - Each subscription gets its OWN independent copy of matching messages
//   (unlike a Queue where only one consumer gets each message)
// - SQL filters evaluate message ApplicationProperties, not the body
// - disableLocalAuth = true forces managed identity auth; no connection strings stored
// - maxDeliveryCount = N: after N failed attempts, message moves to Dead-Letter Queue
// - requiresDuplicateDetection: uses MessageId (our correlationId) to deduplicate
//   within duplicateDetectionHistoryTimeWindow
// - DLQ path: reactor-events/subscriptions/{name}/$deadletterqueue

param prefix string
param location string

resource serviceBusNamespace 'Microsoft.ServiceBus/namespaces@2022-10-01-preview' = {
  name: 'sb-${prefix}-${uniqueString(resourceGroup().id)}'
  location: location
  sku: {
    name: 'Standard'  // Required for Topics. Premium adds VNet injection + dedicated capacity.
    tier: 'Standard'
  }
  properties: {
    disableLocalAuth: true  // Managed identity only — no connection strings
    zoneRedundant: false    // Standard tier doesn't support zone redundancy (Premium does)
    minimumTlsVersion: '1.2'
  }
}

// ── Topic ─────────────────────────────────────────────────────────────────────
resource reactorEventsTopic 'Microsoft.ServiceBus/namespaces/topics@2022-10-01-preview' = {
  parent: serviceBusNamespace
  name: 'reactor-events'
  properties: {
    defaultMessageTimeToLive: 'PT1H'            // Messages expire after 1 hour
    maxSizeInMegabytes: 1024
    requiresDuplicateDetection: true             // Deduplication using MessageId (correlationId)
    duplicateDetectionHistoryTimeWindow: 'PT10M' // 10-minute dedup window
    enablePartitioning: false                    // Partitioning disables ordered delivery
    supportOrdering: false
  }
}

// ── Subscription A: Safety Processor ─────────────────────────────────────────
// Only receives Warning, Critical, Emergency events (not Normal)
resource safetyProcessorSubscription 'Microsoft.ServiceBus/namespaces/topics/subscriptions@2022-10-01-preview' = {
  parent: reactorEventsTopic
  name: 'safety-processor'
  properties: {
    maxDeliveryCount: 3                           // 3 attempts before dead-lettering
    enableDeadLetteringOnMessageExpiration: true  // Expired messages go to DLQ
    lockDuration: 'PT5M'                          // 5 minutes to process before unlock
    deadLetteringOnFilterEvaluationExceptions: false
    requiresSession: false
  }
}

// SQL filter: only non-Normal events reach the Safety Processor
// EDUCATIONAL: ApplicationProperties are set on the ServiceBusMessage in code.
// The trigger binding evaluates these properties BEFORE delivering the message.
// This saves compute: the Safety Processor function never even sees Normal readings.
resource safetyProcessorFilter 'Microsoft.ServiceBus/namespaces/topics/subscriptions/rules@2022-10-01-preview' = {
  parent: safetyProcessorSubscription
  name: 'safety-level-filter'
  properties: {
    filterType: 'SqlFilter'
    sqlFilter: {
      sqlExpression: 'SafetyLevel IN (\'Warning\', \'Critical\', \'Emergency\')'
      compatibilityLevel: 20
    }
  }
}

// Remove the auto-created $Default true-filter rule that would let all messages through.
// EDUCATIONAL: Every new subscription gets a $Default rule that accepts everything.
// We must either delete it or replace it with a false-filter when using custom rules.
resource safetyProcessorDefaultRule 'Microsoft.ServiceBus/namespaces/topics/subscriptions/rules@2022-10-01-preview' = {
  parent: safetyProcessorSubscription
  name: '$Default'
  dependsOn: [safetyProcessorFilter] // Create our filter first
  properties: {
    filterType: 'FalseFilter'         // Rejects all messages (effectively disabling the default rule)
    falseFilter: {}
  }
}

// ── Subscription B: Audit Logger ──────────────────────────────────────────────
// Receives ALL events — no filter needed (default $Default rule = accept all)
resource auditLoggerSubscription 'Microsoft.ServiceBus/namespaces/topics/subscriptions@2022-10-01-preview' = {
  parent: reactorEventsTopic
  name: 'audit-logger'
  properties: {
    maxDeliveryCount: 5                           // More retries for audit — data loss is costly
    enableDeadLetteringOnMessageExpiration: true
    lockDuration: 'PT2M'                          // Shorter lock: audit writing is fast
    requiresSession: false
  }
}

// ── Outputs ───────────────────────────────────────────────────────────────────
output namespaceName string = serviceBusNamespace.name
output namespaceId string = serviceBusNamespace.id
output topicName string = reactorEventsTopic.name
output fullyQualifiedNamespace string = '${serviceBusNamespace.name}.servicebus.windows.net'
