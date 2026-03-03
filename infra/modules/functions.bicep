// EDUCATIONAL: Azure Functions hosting plan options:
//
//   Consumption (Y1/Dynamic):   Scale to zero, pay-per-execution. Cold starts.
//                               Best for: dev/learning, spiky workloads.
//   Flex Consumption (FC1):     Newer; scale to zero + VNet + faster cold start.
//                               Best for: modern prod with VNet requirements.
//   Premium (EP1/EP2/EP3):      Always-warm, VNet integration, predictable latency.
//                               Best for: latency-sensitive production.
//   App Service (Dedicated):    Traditional VM-based; manual scale.
//
// We use Consumption for learning (cheapest; all features work).
//
// Managed identity auth pattern for Service Bus:
//   ServiceBusConnection__fullyQualifiedNamespace = "<ns>.servicebus.windows.net"
//   (no connection string; SDK uses DefaultAzureCredential)
//
// Managed identity auth pattern for Storage:
//   AzureWebJobsStorage__accountName = "<storage-account-name>"
//   AzureWebJobsStorage__credential = "managedidentity"
//   (or with __accountName alone on newer runtime versions)

param prefix string
param location string
param storageAccountName string
param serviceBusNamespaceName string
param appInsightsConnectionString string
param keyVaultName string

// One Consumption plan shared by all 3 Function Apps
// EDUCATIONAL: On Consumption, multiple Function Apps on the same plan still
// scale independently. The plan is just a billing container, not a compute limit.
resource consumptionPlan 'Microsoft.Web/serverfarms@2023-12-01' = {
  name: 'plan-${prefix}-functions'
  location: location
  sku: {
    name: 'Y1'
    tier: 'Dynamic'
  }
  kind: 'functionapp'
  properties: {
    reserved: true  // required for Linux
  }
}

// ── Ingestor Function App ─────────────────────────────────────────────────────
// HTTP Trigger: receives telemetry from APIM → validates → publishes to Service Bus
resource ingestorFunctionApp 'Microsoft.Web/sites@2023-12-01' = {
  name: 'func-${prefix}-ingestor'
  location: location
  kind: 'functionapp,linux'
  identity: {
    type: 'SystemAssigned'  // Managed identity — no stored credentials
  }
  properties: {
    serverFarmId: consumptionPlan.id
    httpsOnly: true
    siteConfig: {
      linuxFxVersion: 'DOTNET-ISOLATED|8.0'
      appSettings: [
        { name: 'FUNCTIONS_EXTENSION_VERSION', value: '~4' }
        { name: 'FUNCTIONS_WORKER_RUNTIME', value: 'dotnet-isolated' }
        // EDUCATIONAL: Managed identity auth for Storage (no key needed)
        { name: 'AzureWebJobsStorage__accountName', value: storageAccountName }
        { name: 'AzureWebJobsStorage__credential', value: 'managedidentity' }
        // EDUCATIONAL: Use connection string (modern), not instrumentation key (legacy)
        { name: 'APPLICATIONINSIGHTS_CONNECTION_STRING', value: appInsightsConnectionString }
        // EDUCATIONAL: Managed identity auth for Service Bus
        // Setting __fullyQualifiedNamespace (not a connection string) triggers MI auth
        { name: 'ServiceBusConnection__fullyQualifiedNamespace', value: '${serviceBusNamespaceName}.servicebus.windows.net' }
        { name: 'ServiceBusTopic', value: 'reactor-events' }
      ]
      ftpsState: 'Disabled'
      minTlsVersion: '1.2'
    }
  }
}

// ── Safety Processor Function App ─────────────────────────────────────────────
// Service Bus Trigger: consumes from safety-processor subscription (filtered)
// Timer Trigger: drains Dead Letter Queue every 5 minutes
resource safetyProcessorFunctionApp 'Microsoft.Web/sites@2023-12-01' = {
  name: 'func-${prefix}-safety'
  location: location
  kind: 'functionapp,linux'
  identity: {
    type: 'SystemAssigned'
  }
  properties: {
    serverFarmId: consumptionPlan.id
    httpsOnly: true
    siteConfig: {
      linuxFxVersion: 'DOTNET-ISOLATED|8.0'
      appSettings: [
        { name: 'FUNCTIONS_EXTENSION_VERSION', value: '~4' }
        { name: 'FUNCTIONS_WORKER_RUNTIME', value: 'dotnet-isolated' }
        { name: 'AzureWebJobsStorage__accountName', value: storageAccountName }
        { name: 'AzureWebJobsStorage__credential', value: 'managedidentity' }
        { name: 'APPLICATIONINSIGHTS_CONNECTION_STRING', value: appInsightsConnectionString }
        { name: 'ServiceBusConnection__fullyQualifiedNamespace', value: '${serviceBusNamespaceName}.servicebus.windows.net' }
        { name: 'ServiceBusTopic', value: 'reactor-events' }
        { name: 'ServiceBusSubscription', value: 'safety-processor' }
      ]
      ftpsState: 'Disabled'
      minTlsVersion: '1.2'
    }
  }
}

// ── Audit Logger Function App ──────────────────────────────────────────────────
// Service Bus Trigger: consumes ALL events from audit-logger subscription
// Writes each event to Azure Table Storage as an immutable audit record
resource auditLoggerFunctionApp 'Microsoft.Web/sites@2023-12-01' = {
  name: 'func-${prefix}-audit'
  location: location
  kind: 'functionapp,linux'
  identity: {
    type: 'SystemAssigned'
  }
  properties: {
    serverFarmId: consumptionPlan.id
    httpsOnly: true
    siteConfig: {
      linuxFxVersion: 'DOTNET-ISOLATED|8.0'
      appSettings: [
        { name: 'FUNCTIONS_EXTENSION_VERSION', value: '~4' }
        { name: 'FUNCTIONS_WORKER_RUNTIME', value: 'dotnet-isolated' }
        { name: 'AzureWebJobsStorage__accountName', value: storageAccountName }
        { name: 'AzureWebJobsStorage__credential', value: 'managedidentity' }
        { name: 'APPLICATIONINSIGHTS_CONNECTION_STRING', value: appInsightsConnectionString }
        { name: 'ServiceBusConnection__fullyQualifiedNamespace', value: '${serviceBusNamespaceName}.servicebus.windows.net' }
        { name: 'ServiceBusTopic', value: 'reactor-events' }
        { name: 'ServiceBusSubscription', value: 'audit-logger' }
        // Table Storage using managed identity (same storage account)
        { name: 'AuditStorageConnection__accountName', value: storageAccountName }
        { name: 'AuditStorageConnection__credential', value: 'managedidentity' }
        { name: 'AuditTableName', value: 'reactorauditlog' }
      ]
      ftpsState: 'Disabled'
      minTlsVersion: '1.2'
    }
  }
}

// ── Outputs ───────────────────────────────────────────────────────────────────
output ingestorFunctionAppName string = ingestorFunctionApp.name
output ingestorPrincipalId string = ingestorFunctionApp.identity.principalId
output safetyProcessorPrincipalId string = safetyProcessorFunctionApp.identity.principalId
output auditLoggerPrincipalId string = auditLoggerFunctionApp.identity.principalId
