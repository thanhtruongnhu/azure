# Local Development Setup

Run all three Azure Functions locally against real or emulated services.

## Prerequisites

```bash
# .NET 8 SDK
dotnet --version  # should be 8.x

# Azure Functions Core Tools v4
npm install -g azure-functions-core-tools@4

# Azure CLI (for managed identity locally via az login)
az --version

# Azurite (Azure Storage emulator)
npm install -g azurite

# Service Bus Explorer (optional, for DLQ inspection)
# https://github.com/paolosalvatori/ServiceBusExplorer
```

## Option A: Local + Real Azure Service Bus

Best for testing the full pub/sub flow.

1. **Deploy infra first** (see README.md)

2. **Log in with Azure CLI** — `DefaultAzureCredential` picks this up:
   ```bash
   az login
   az account set --subscription "<your-subscription-id>"
   ```

3. **Assign yourself the Service Bus roles** (one-time):
   ```bash
   SB_ID=$(az servicebus namespace show \
     --name "sb-reactor-dev-<suffix>" \
     --resource-group "rg-reactor-dev" \
     --query id -o tsv)

   # Sender role (for Ingestor testing)
   az role assignment create \
     --assignee $(az ad signed-in-user show --query id -o tsv) \
     --role "Azure Service Bus Data Sender" \
     --scope "$SB_ID"

   # Receiver role (for SafetyProcessor + AuditLogger testing)
   az role assignment create \
     --assignee $(az ad signed-in-user show --query id -o tsv) \
     --role "Azure Service Bus Data Receiver" \
     --scope "$SB_ID"
   ```

4. **Configure local.settings.json** for each function:
   ```json
   {
     "Values": {
       "ServiceBusConnection__fullyQualifiedNamespace": "sb-reactor-dev-<suffix>.servicebus.windows.net"
     }
   }
   ```

5. **Start Azurite** (for AzureWebJobsStorage):
   ```bash
   azurite --silent --location /tmp/azurite &
   ```

6. **Start each function** (separate terminals):
   ```bash
   cd src/TelemetryIngestor/ReactorTelemetry.Ingestor && func start
   cd src/SafetyProcessor/ReactorTelemetry.SafetyProcessor && func start
   cd src/AuditLogger/ReactorTelemetry.AuditLogger && func start
   ```

7. **Send a test event** (bypasses APIM locally):
   ```bash
   curl -X POST http://localhost:7071/api/telemetry \
     -H "Content-Type: application/json" \
     -H "x-authenticated-subject: local-test" \
     -d '{
       "reactorId": "a1b2c3d4-e5f6-7890-abcd-ef1234567890",
       "timestamp": "'$(date -u +%Y-%m-%dT%H:%M:%SZ)'",
       "safetyLevel": "Warning",
       "readings": {
         "coreTemperatureCelsius": 315.2,
         "coolantPressureBar": 158.8
       }
     }'
   ```

## Option B: Fully Local (Azurite + Service Bus Emulator)

For offline development. Uses the Azure Service Bus emulator.

1. **Install Service Bus Emulator** via Docker:
   ```bash
   docker run -d \
     --name servicebus-emulator \
     -p 5672:5672 \
     mcr.microsoft.com/azure-messaging/servicebus-emulator:latest
   ```

2. **Update local.settings.json**:
   ```json
   {
     "Values": {
       "ServiceBusConnection": "Endpoint=sb://localhost;SharedAccessKeyName=RootManageSharedAccessKey;SharedAccessKey=SAS_KEY_VALUE;UseDevelopmentEmulator=true;",
       "AzureWebJobsStorage": "UseDevelopmentStorage=true"
     }
   }
   ```
   Note: The emulator uses connection string auth (no managed identity locally). This is expected.

## Running Tests

```bash
# From repo root:
dotnet test src/ReactorTelemetry.sln -v normal

# With coverage:
dotnet test src/ReactorTelemetry.sln \
  --collect:"XPlat Code Coverage" \
  --results-directory ./coverage

# View coverage report:
dotnet tool install -g dotnet-reportgenerator-globaltool
reportgenerator -reports:coverage/**/coverage.cobertura.xml -targetdir:coverage/report
open coverage/report/index.html
```

## Common Issues

**`az login` required:** If `DefaultAzureCredential` fails locally, run `az login` and `az account set`.

**Port conflicts:** Default Function ports: 7071, 7072, 7073. If starting multiple functions, each picks the next available port.

**Azurite not running:** `AzureWebJobsStorage` requires Azurite for local Storage emulation. Make sure `azurite` is running before `func start`.

**APIM not available locally:** APIM only runs in Azure. Test the Function HTTP endpoint directly on localhost when developing locally. The JWT and IP filter policies won't apply — that's intentional for local dev.
