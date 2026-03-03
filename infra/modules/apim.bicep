// EDUCATIONAL: Azure API Management tiers:
//
//   Consumption:  Serverless, pay-per-call, no SLA, no developer portal.
//                 Cold start latency (~2s). Good for: minimal cost, simple scenarios.
//   Developer:    Full features + developer portal. No SLA. ~$49/month.
//                 Best for: learning, development, integration testing.
//   Basic/Standard: Production SLA (99.95%). No VNet. 1-4 units.
//   Premium:      VNet injection, multi-region, 99.99% SLA.
//
// We use Developer tier: all policy features, App Insights integration, Named Values,
// developer portal for testing, but no production SLA.
//
// Named Values = APIM environment variables. Referenced in policies as {{NamedValue}}.
// Secret Named Values can reference Key Vault secrets directly.
//
// The API is imported from our OpenAPI spec via loadTextContent().
// This implements API-first: spec is the source of truth, APIM is derived from it.

param prefix string
param location string
param ingestorFunctionAppName string
param entraIdTenantId string
param entraIdAudienceClientId string
param regulatorClientId string
param appInsightsName string
param appInsightsConnectionString string

resource appInsights 'Microsoft.Insights/components@2020-02-02' existing = {
  name: appInsightsName
}

// ── APIM Instance ─────────────────────────────────────────────────────────────
resource apim 'Microsoft.ApiManagement/service@2023-05-01-preview' = {
  name: 'apim-${prefix}'
  location: location
  sku: {
    name: 'Developer'
    capacity: 1
  }
  identity: {
    type: 'SystemAssigned'  // MI used to pull secrets from Key Vault
  }
  properties: {
    publisherEmail: 'admin@reactorplatform.example'
    publisherName: 'Reactor Telemetry Platform'
    virtualNetworkType: 'None'
    // EDUCATIONAL: customProperties can set gateway protocol versions, ciphers, etc.
    customProperties: {
      'Microsoft.WindowsAzure.ApiManagement.Gateway.Security.Protocols.Tls10': 'false'
      'Microsoft.WindowsAzure.ApiManagement.Gateway.Security.Protocols.Tls11': 'false'
      'Microsoft.WindowsAzure.ApiManagement.Gateway.Security.Backend.Protocols.Tls10': 'false'
      'Microsoft.WindowsAzure.ApiManagement.Gateway.Security.Backend.Protocols.Tls11': 'false'
    }
  }
}

// ── Application Insights Logger ───────────────────────────────────────────────
// Links APIM to App Insights for distributed tracing and request logging.
// After this, all API calls appear in App Insights with end-to-end correlation.
resource apimLogger 'Microsoft.ApiManagement/service/loggers@2023-05-01-preview' = {
  parent: apim
  name: 'appinsights-logger'
  properties: {
    loggerType: 'applicationInsights'
    credentials: {
      // EDUCATIONAL: connectionString is the modern approach (not instrumentationKey)
      connectionString: appInsightsConnectionString
    }
    isBuffered: true  // Buffer telemetry to reduce HTTP calls to App Insights
    resourceId: appInsights.id
  }
}

// ── Named Values (APIM environment variables) ─────────────────────────────────
// Referenced in policy XML as {{VariableName}}. Secret = true hides value in portal.
resource namedValueTenantId 'Microsoft.ApiManagement/service/namedValues@2023-05-01-preview' = {
  parent: apim
  name: 'EntraTenantId'
  properties: {
    displayName: 'EntraTenantId'
    value: entraIdTenantId
    secret: false
  }
}

resource namedValueAudienceClientId 'Microsoft.ApiManagement/service/namedValues@2023-05-01-preview' = {
  parent: apim
  name: 'EntraAudienceClientId'
  properties: {
    displayName: 'EntraAudienceClientId'
    value: entraIdAudienceClientId
    secret: false
  }
}

resource namedValueRegulatorClientId 'Microsoft.ApiManagement/service/namedValues@2023-05-01-preview' = {
  parent: apim
  name: 'RegulatorClientId'
  properties: {
    displayName: 'RegulatorClientId'
    value: regulatorClientId
    secret: false
  }
}

// ── Backend: Ingestor Function App ────────────────────────────────────────────
// EDUCATIONAL: The backend resource tells APIM where to forward requests.
// We use the Function App's default hostname. In production, use a custom domain.
// x-functions-key header authentication: APIM holds the function key; clients never see it.
resource ingestorBackend 'Microsoft.ApiManagement/service/backends@2023-05-01-preview' = {
  parent: apim
  name: 'reactor-ingestor-backend'
  properties: {
    description: 'Telemetry Ingestor Azure Function'
    url: 'https://${ingestorFunctionAppName}.azurewebsites.net/api'
    protocol: 'http'
    tls: {
      validateCertificateChain: true
      validateCertificateName: true
    }
  }
}

// ── API — imported from OpenAPI spec ──────────────────────────────────────────
// EDUCATIONAL: format: 'openapi' imports our YAML spec directly.
// This means APIM's operations, schemas, and examples all come from the spec —
// true API-first: the spec is the single source of truth.
resource reactorApi 'Microsoft.ApiManagement/service/apis@2023-05-01-preview' = {
  parent: apim
  name: 'reactor-telemetry-v1'
  properties: {
    displayName: 'Reactor Telemetry API v1'
    description: 'Ingests reactor telemetry events from regulatory systems'
    path: 'reactor/v1'
    protocols: ['https']
    subscriptionRequired: false   // Auth via JWT, not APIM subscription keys
    format: 'openapi'
    value: loadTextContent('../../api/reactor-telemetry.openapi.yaml')
    serviceUrl: 'https://${ingestorFunctionAppName}.azurewebsites.net/api'
    apiVersion: 'v1'
    isCurrent: true
  }
}

// ── API-level Diagnostics (links to App Insights logger) ─────────────────────
// EDUCATIONAL: Diagnostic settings control what APIM logs to App Insights.
// sampling: 100% logs all requests. Reduce in high-volume production.
resource apiDiagnostics 'Microsoft.ApiManagement/service/apis/diagnostics@2023-05-01-preview' = {
  parent: reactorApi
  name: 'applicationinsights'
  properties: {
    loggerId: apimLogger.id
    alwaysLog: 'allErrors'  // Always log errors, even if sampling skips the request
    sampling: {
      samplingType: 'fixed'
      percentage: 100       // 100% for learning; tune down in production
    }
    request: {
      headers: ['x-correlation-id', 'x-authenticated-subject']
      body: {
        bytes: 0            // Don't log request bodies (may contain sensitive telemetry)
      }
    }
    response: {
      headers: ['x-correlation-id']
      body: {
        bytes: 0
      }
    }
  }
}

// ── Outputs ───────────────────────────────────────────────────────────────────
output apimName string = apim.name
output principalId string = apim.identity.principalId
output gatewayUrl string = apim.properties.gatewayUrl
output reactorApiId string = reactorApi.id
