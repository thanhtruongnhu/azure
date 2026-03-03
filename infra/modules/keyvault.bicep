// EDUCATIONAL: Key Vault access models:
//
//   Vault Access Policies (LEGACY) — custom Key Vault concept, hard to audit
//   RBAC Access Model (MODERN)     — standard Azure role assignments, audit via IAM
//
// We use RBAC. Role assignments are in rbac.bicep (centralized for auditing).
//
// enableSoftDelete: protects against accidental deletion (90-day recovery window).
// enablePurgeProtection: prevents permanent deletion even during soft-delete window.
//   Note: purge protection cannot be disabled once enabled — careful in dev!
//
// We store the App Insights connection string here so Functions reference it
// via Key Vault reference syntax: @Microsoft.KeyVault(VaultName=...) instead of plain text.

param prefix string
param location string
param appInsightsConnectionString string

resource keyVault 'Microsoft.KeyVault/vaults@2023-07-01' = {
  name: 'kv-${prefix}-${uniqueString(resourceGroup().id)}'
  location: location
  properties: {
    sku: {
      family: 'A'
      name: 'standard'
    }
    tenantId: tenant().tenantId
    enableRbacAuthorization: true  // Use RBAC, not vault access policies
    enableSoftDelete: true
    softDeleteRetentionInDays: 7   // 7 days for dev (default is 90)
    enablePurgeProtection: false   // Keep false in dev so you can redeploy cleanly
    publicNetworkAccess: 'Enabled' // Lock down with Private Endpoint in prod
    networkAcls: {
      bypass: 'AzureServices'
      defaultAction: 'Allow'
    }
  }
}

// Store App Insights connection string as a Key Vault secret
// Functions reference this via: @Microsoft.KeyVault(VaultName=kv-...;SecretName=AppInsightsConnectionString)
resource appInsightsSecret 'Microsoft.KeyVault/vaults/secrets@2023-07-01' = {
  parent: keyVault
  name: 'AppInsightsConnectionString'
  properties: {
    value: appInsightsConnectionString
    attributes: {
      enabled: true
    }
  }
}

// ── Outputs ───────────────────────────────────────────────────────────────────
output name string = keyVault.name
output id string = keyVault.id
output uri string = keyVault.properties.vaultUri
