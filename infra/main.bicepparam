using 'main.bicep'

// ── Dev environment parameters ────────────────────────────────────────────────
// Fill in entraIdTenantId, entraIdAudienceClientId, and regulatorClientId
// after running: ./scripts/create-entra-app-registrations.sh

param environmentName = 'dev'
param location = 'eastus'

// From: az account show --query tenantId -o tsv
param entraIdTenantId = '<your-entra-tenant-id>'

// From: az ad app show --id reactor-telemetry-api --query appId -o tsv
// (created by create-entra-app-registrations.sh)
param entraIdAudienceClientId = '<reactor-telemetry-api-client-id>'

// From: az ad app show --id reactor-telemetry-client --query appId -o tsv
// (created by create-entra-app-registrations.sh)
param regulatorClientId = '<reactor-telemetry-client-client-id>'
