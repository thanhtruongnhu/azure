// EDUCATIONAL: Storage account serves two purposes here:
//
// 1. Azure Functions runtime state (AzureWebJobsStorage):
//    - Checkpoint state for Service Bus triggers
//    - Blob leases for distributed coordination
//    - Timer trigger state
//    We use managed identity auth via: AzureWebJobsStorage__accountName (no key)
//
// 2. Audit log Table Storage:
//    AuditLogger Function writes every telemetry event here as an immutable audit record.
//    Table Storage is simple key-value at massive scale (~$0.045/GB/month).
//    Partition key = ReactorId → fast queries per reactor.
//    Row key = CorrelationId → unique per event.
//
// StorageV2 (General Purpose v2) supports all services: Blob, Queue, Table, File.
// accessTier: Hot = frequent access. Cool = infrequent. Archive = cold.

param prefix string
param location string

resource storageAccount 'Microsoft.Storage/storageAccounts@2023-04-01' = {
  name: 'st${replace(prefix, '-', '')}${take(uniqueString(resourceGroup().id), 6)}'
  location: location
  kind: 'StorageV2'
  sku: {
    name: 'Standard_LRS'  // Locally redundant storage — cheapest, fine for dev
    // Production: Standard_ZRS (zone redundant) or Standard_GRS (geo redundant)
  }
  properties: {
    accessTier: 'Hot'
    supportsHttpsTrafficOnly: true
    minimumTlsVersion: 'TLS1_2'
    allowBlobPublicAccess: false
    allowSharedKeyAccess: true  // Required for AzureWebJobsStorage runtime operations
    // EDUCATIONAL: Set to false in prod and use managed identity everywhere.
    // Azure Functions runtime still requires shared key for some internal operations
    // on Consumption plan; Flex Consumption plan supports full MI-only auth.
  }
}

// Audit log table — pre-created so Functions don't need CREATE TABLE permissions
resource auditTable 'Microsoft.Storage/storageAccounts/tableServices/tables@2023-04-01' = {
  name: '${storageAccount.name}/default/reactorauditlog'
}

// ── Outputs ───────────────────────────────────────────────────────────────────
output name string = storageAccount.name
output id string = storageAccount.id
