// EDUCATIONAL: Centralizing all role assignments in one module makes the
// security posture of the entire platform auditable at a glance.
//
// Principle of Least Privilege applied:
//   - Ingestor can SEND to Service Bus, but cannot RECEIVE
//   - Safety Processor can RECEIVE from Service Bus, but cannot SEND
//   - Audit Logger can RECEIVE from Service Bus + write to Table Storage
//   - APIM managed identity can read Key Vault secrets
//   - All Functions can read Key Vault secrets (for any future stored secrets)
//
// Built-in role GUIDs (stable across all tenants):
//   Service Bus Data Sender:      69a216fc-b8fb-44d8-bc22-1f3c2cd27a39
//   Service Bus Data Receiver:    4f6d3b9b-027b-4f4c-9142-0e5a2a2247e0
//   Key Vault Secrets User:       4633458b-17de-408a-b874-0445c86b69e0
//   Storage Blob Data Contributor:ba92f5b4-2d11-453d-a403-e96b0029c9fe
//   Storage Table Data Contrib:   0a9a7e1f-b9d0-4cc4-a60d-0319b160aaa3
//   Storage Account Contributor:  17d1049b-9a84-46fb-8f53-869881c3d3ab
//
// EDUCATIONAL: Role assignments at namespace scope (not topic/subscription) because
// Azure Service Bus Data Sender/Receiver are namespace-level roles.
// There are no built-in topic-level or subscription-level roles.

param ingestorFunctionPrincipalId string
param safetyProcessorPrincipalId string
param auditLoggerPrincipalId string
param apimPrincipalId string
param keyVaultName string
param serviceBusNamespaceName string
param storageAccountName string

var serviceBusSenderRoleId = '69a216fc-b8fb-44d8-bc22-1f3c2cd27a39'
var serviceBusReceiverRoleId = '4f6d3b9b-027b-4f4c-9142-0e5a2a2247e0'
var keyVaultSecretsUserRoleId = '4633458b-17de-408a-b874-0445c86b69e0'
var storageTableContributorRoleId = '0a9a7e1f-b9d0-4cc4-a60d-0319b160aaa3'
var storageBlobContributorRoleId = 'ba92f5b4-2d11-453d-a403-e96b0029c9fe'

resource serviceBus 'Microsoft.ServiceBus/namespaces@2022-10-01-preview' existing = {
  name: serviceBusNamespaceName
}

resource keyVault 'Microsoft.KeyVault/vaults@2023-07-01' existing = {
  name: keyVaultName
}

resource storage 'Microsoft.Storage/storageAccounts@2023-04-01' existing = {
  name: storageAccountName
}

// ── Ingestor: can SEND to Service Bus ────────────────────────────────────────
resource ingestorSbSender 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(serviceBus.id, ingestorFunctionPrincipalId, serviceBusSenderRoleId)
  scope: serviceBus
  properties: {
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', serviceBusSenderRoleId)
    principalId: ingestorFunctionPrincipalId
    principalType: 'ServicePrincipal'
  }
}

// ── Safety Processor: can RECEIVE from Service Bus ───────────────────────────
resource safetyProcessorSbReceiver 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(serviceBus.id, safetyProcessorPrincipalId, serviceBusReceiverRoleId)
  scope: serviceBus
  properties: {
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', serviceBusReceiverRoleId)
    principalId: safetyProcessorPrincipalId
    principalType: 'ServicePrincipal'
  }
}

// ── Audit Logger: can RECEIVE from Service Bus ────────────────────────────────
resource auditLoggerSbReceiver 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(serviceBus.id, auditLoggerPrincipalId, serviceBusReceiverRoleId)
  scope: serviceBus
  properties: {
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', serviceBusReceiverRoleId)
    principalId: auditLoggerPrincipalId
    principalType: 'ServicePrincipal'
  }
}

// ── Audit Logger: can write to Table Storage ──────────────────────────────────
resource auditLoggerTableStorage 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(storage.id, auditLoggerPrincipalId, storageTableContributorRoleId)
  scope: storage
  properties: {
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', storageTableContributorRoleId)
    principalId: auditLoggerPrincipalId
    principalType: 'ServicePrincipal'
  }
}

// ── All Functions: Blob storage for AzureWebJobsStorage runtime ──────────────
resource ingestorBlobStorage 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(storage.id, ingestorFunctionPrincipalId, storageBlobContributorRoleId)
  scope: storage
  properties: {
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', storageBlobContributorRoleId)
    principalId: ingestorFunctionPrincipalId
    principalType: 'ServicePrincipal'
  }
}

resource safetyProcessorBlobStorage 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(storage.id, safetyProcessorPrincipalId, storageBlobContributorRoleId)
  scope: storage
  properties: {
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', storageBlobContributorRoleId)
    principalId: safetyProcessorPrincipalId
    principalType: 'ServicePrincipal'
  }
}

resource auditLoggerBlobStorage 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(storage.id, auditLoggerPrincipalId, storageBlobContributorRoleId)
  scope: storage
  properties: {
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', storageBlobContributorRoleId)
    principalId: auditLoggerPrincipalId
    principalType: 'ServicePrincipal'
  }
}

// ── APIM: can read Key Vault secrets ─────────────────────────────────────────
resource apimKeyVaultAccess 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(keyVault.id, apimPrincipalId, keyVaultSecretsUserRoleId)
  scope: keyVault
  properties: {
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', keyVaultSecretsUserRoleId)
    principalId: apimPrincipalId
    principalType: 'ServicePrincipal'
  }
}

// ── All Functions: can read Key Vault secrets ─────────────────────────────────
resource ingestorKeyVaultAccess 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(keyVault.id, ingestorFunctionPrincipalId, keyVaultSecretsUserRoleId)
  scope: keyVault
  properties: {
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', keyVaultSecretsUserRoleId)
    principalId: ingestorFunctionPrincipalId
    principalType: 'ServicePrincipal'
  }
}

resource safetyProcessorKeyVaultAccess 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(keyVault.id, safetyProcessorPrincipalId, keyVaultSecretsUserRoleId)
  scope: keyVault
  properties: {
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', keyVaultSecretsUserRoleId)
    principalId: safetyProcessorPrincipalId
    principalType: 'ServicePrincipal'
  }
}

resource auditLoggerKeyVaultAccess 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(keyVault.id, auditLoggerPrincipalId, keyVaultSecretsUserRoleId)
  scope: keyVault
  properties: {
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', keyVaultSecretsUserRoleId)
    principalId: auditLoggerPrincipalId
    principalType: 'ServicePrincipal'
  }
}
