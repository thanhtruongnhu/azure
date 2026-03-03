// EDUCATIONAL: Application Insights architecture (modern):
//
//   Log Analytics Workspace  ←  App Insights Component
//         (data store)              (query surface)
//
// All telemetry flows into the Log Analytics workspace.
// App Insights is the lens through which you query it (Live Metrics, Transaction Search, etc.).
// Classic App Insights (without workspace) is deprecated — always use workspace-based.
//
// Key settings:
// - retentionInDays: 30 (default). Increase for compliance (up to 730 days, extra cost).
// - publicNetworkAccessForIngestion: Enabled for learning; lock down with Private Link in prod.

param prefix string
param location string

// Log Analytics Workspace — the underlying data store
resource logAnalyticsWorkspace 'Microsoft.OperationalInsights/workspaces@2023-09-01' = {
  name: 'log-${prefix}'
  location: location
  properties: {
    sku: {
      name: 'PerGB2018'  // Pay-per-GB. Most cost-effective for variable ingestion.
    }
    retentionInDays: 30
    features: {
      enableLogAccessUsingOnlyResourcePermissions: true
    }
    publicNetworkAccessForIngestion: 'Enabled'
    publicNetworkAccessForQuery: 'Enabled'
  }
}

// Application Insights Component — workspace-based (modern)
resource appInsights 'Microsoft.Insights/components@2020-02-02' = {
  name: 'appi-${prefix}'
  location: location
  kind: 'web'
  properties: {
    Application_Type: 'web'
    WorkspaceResourceId: logAnalyticsWorkspace.id
    // EDUCATIONAL: Use connection string (modern), NOT instrumentationKey (legacy).
    // Connection string supports regional endpoints and is more resilient.
    publicNetworkAccessForIngestion: 'Enabled'
    publicNetworkAccessForQuery: 'Enabled'
  }
}

// ── Outputs ───────────────────────────────────────────────────────────────────
output name string = appInsights.name
output connectionString string = appInsights.properties.ConnectionString
output instrumentationKey string = appInsights.properties.InstrumentationKey
output workspaceName string = logAnalyticsWorkspace.name
