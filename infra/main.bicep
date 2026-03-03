// EDUCATIONAL: targetScope = 'subscription' allows a single deployment to:
// 1. Create the resource group itself
// 2. Deploy all resources inside it
// This is the recommended pattern for greenfield Azure deployments.
// Alternative: targetScope = 'resourceGroup' (requires the RG to already exist)
targetScope = 'subscription'

@description('Environment suffix applied to all resource names')
@allowed(['dev', 'staging', 'prod'])
param environmentName string = 'dev'

@description('Azure region for all resources')
param location string = 'eastus'

@description('Azure Entra ID tenant ID — used for JWT validation in APIM')
param entraIdTenantId string

@description('Client ID of the reactor-telemetry-api Entra app registration (the audience)')
param entraIdAudienceClientId string

@description('Client ID of the regulator system Entra app registration (allowed caller)')
param regulatorClientId string

// Consistent naming prefix used by all modules
var prefix = 'reactor-${environmentName}'
var resourceGroupName = 'rg-${prefix}'

// ── Resource Group ────────────────────────────────────────────────────────────
// Created at subscription scope; all modules below deploy into it.
resource rg 'Microsoft.Resources/resourceGroups@2024-03-01' = {
  name: resourceGroupName
  location: location
  tags: {
    environment: environmentName
    project: 'reactor-telemetry'
    managedBy: 'bicep'
  }
}

// ── Application Insights (deployed first — its connection string feeds all others) ──
// EDUCATIONAL: Log Analytics workspace is now required for workspace-based App Insights.
// Classic (non-workspace) App Insights is deprecated.
module appInsights 'modules/appinsights.bicep' = {
  name: 'appInsights'
  scope: rg
  params: {
    prefix: prefix
    location: location
  }
}

// ── Key Vault ─────────────────────────────────────────────────────────────────
// EDUCATIONAL: Key Vault uses the RBAC access model (not vault access policies).
// Access policies are legacy; RBAC gives you Azure standard role assignments
// that work with managed identities without extra configuration.
module keyVault 'modules/keyvault.bicep' = {
  name: 'keyVault'
  scope: rg
  params: {
    prefix: prefix
    location: location
    appInsightsConnectionString: appInsights.outputs.connectionString
  }
}

// ── Storage ───────────────────────────────────────────────────────────────────
// Used for: Azure Functions runtime checkpointing, and audit log Table Storage.
module storage 'modules/storage.bicep' = {
  name: 'storage'
  scope: rg
  params: {
    prefix: prefix
    location: location
  }
}

// ── Service Bus ───────────────────────────────────────────────────────────────
// EDUCATIONAL: Standard tier is required for Topics (pub/sub).
// Basic tier only supports Queues (point-to-point).
module serviceBus 'modules/servicebus.bicep' = {
  name: 'serviceBus'
  scope: rg
  params: {
    prefix: prefix
    location: location
  }
}

// ── Azure Functions (3 apps) ──────────────────────────────────────────────────
module functions 'modules/functions.bicep' = {
  name: 'functions'
  scope: rg
  params: {
    prefix: prefix
    location: location
    storageAccountName: storage.outputs.name
    serviceBusNamespaceName: serviceBus.outputs.namespaceName
    appInsightsConnectionString: appInsights.outputs.connectionString
    keyVaultName: keyVault.outputs.name
  }
}

// ── API Management (deployed after Functions — needs the ingestor URL) ────────
// EDUCATIONAL: Developer tier (~$49/mo) provides all features including the
// developer portal, policy engine, and App Insights integration.
// Consumption tier is cheaper but has cold-start latency and fewer features.
module apim 'modules/apim.bicep' = {
  name: 'apim'
  scope: rg
  params: {
    prefix: prefix
    location: location
    ingestorFunctionAppName: functions.outputs.ingestorFunctionAppName
    entraIdTenantId: entraIdTenantId
    entraIdAudienceClientId: entraIdAudienceClientId
    regulatorClientId: regulatorClientId
    appInsightsName: appInsights.outputs.name
    appInsightsConnectionString: appInsights.outputs.connectionString
  }
}

// ── RBAC (all role assignments in one place for easy auditing) ────────────────
module rbac 'modules/rbac.bicep' = {
  name: 'rbac'
  scope: rg
  params: {
    ingestorFunctionPrincipalId: functions.outputs.ingestorPrincipalId
    safetyProcessorPrincipalId: functions.outputs.safetyProcessorPrincipalId
    auditLoggerPrincipalId: functions.outputs.auditLoggerPrincipalId
    apimPrincipalId: apim.outputs.principalId
    keyVaultName: keyVault.outputs.name
    serviceBusNamespaceName: serviceBus.outputs.namespaceName
    storageAccountName: storage.outputs.name
  }
}

// ── Outputs ───────────────────────────────────────────────────────────────────
output resourceGroupName string = rg.name
output apimGatewayUrl string = apim.outputs.gatewayUrl
output appInsightsName string = appInsights.outputs.name
output serviceBusNamespaceName string = serviceBus.outputs.namespaceName
output ingestorFunctionAppName string = functions.outputs.ingestorFunctionAppName
