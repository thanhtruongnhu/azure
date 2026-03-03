# Azure Reactor Telemetry Platform

An API-first, event-driven integration platform that securely exposes reactor telemetry and incident events to external regulatory systems. Built on Azure as a study project covering APIM, OAuth2/JWT, Service Bus, Azure Functions, Application Insights, Bicep IaC, and GitHub Actions CI/CD.

## Architecture

```
Client (Regulator System)
  │  Bearer JWT (Entra ID)
  ▼
Azure API Management
  │  rate-limit, ip-filter, validate-azure-ad-token, transform
  ▼
Azure Function: TelemetryIngestor   (HTTP Trigger, .NET 8)
  │  validate + publish
  ▼
Azure Service Bus Topic: reactor-events
  ├── Subscription: safety-processor  (SQL filter: SafetyLevel != Normal)
  │     ▼
  │   Azure Function: SafetyProcessor  (SB Trigger)
  │     + Dead-letter drain (Timer Trigger every 5 min)
  │
  └── Subscription: audit-logger  (all events)
        ▼
      Azure Function: AuditLogger  (SB Trigger)
        ▼
      Azure Table Storage: reactorauditlog

All components ──► Application Insights  (distributed tracing, custom metrics)
Secrets          ──► Azure Key Vault
IaC              ──► Bicep (subscription-scoped)
CI/CD            ──► GitHub Actions (3 workflows)
```

## Topics Covered

| Topic | Where |
|---|---|
| API-first | `api/reactor-telemetry.openapi.yaml` imported directly into APIM |
| APIM policies | `apim-policies/` — rate limiting, JWT validation, IP filter, CORS, request transform |
| OAuth2/JWT | Entra ID app registrations, `validate-azure-ad-token` policy, `roles` claim |
| Pub/Sub | Service Bus Topic with 2 subscriptions + SQL filter |
| Event-driven architecture | Service Bus triggered Functions, retry, DLQ handling |
| Monitoring | App Insights distributed tracing, custom metrics, KQL queries |
| CI/CD | GitHub Actions: PR validation, infra deploy, function deploy |
| IaC | Bicep: subscription-scoped, modular, managed identity everywhere |
| DevOps | Env management (dev/prod), OIDC federated credentials, Key Vault secrets |
| Troubleshooting | DLQ drain function, App Insights queries in `docs/troubleshooting.md` |
| Scalability | APIM tiers, Consumption plan scaling, Service Bus partitioning notes |

## Prerequisites

- Azure subscription with Owner role
- Azure CLI (`az`) ≥ 2.60
- .NET 8 SDK
- Azure Functions Core Tools v4
- Azurite (local Storage emulator): `npm install -g azurite`

## Quick Start

### 1. Deploy Infrastructure

```bash
# Login and set subscription
az login
az account set --subscription "<your-subscription-id>"

# Register required providers
az provider register --namespace Microsoft.ApiManagement
az provider register --namespace Microsoft.ServiceBus

# Deploy all Azure resources
az deployment sub create \
  --name reactor-telemetry-dev \
  --location eastus \
  --template-file infra/main.bicep \
  --parameters infra/main.bicepparam
```

### 2. Set Up Entra ID (OAuth2)

```bash
./scripts/create-entra-app-registrations.sh
# Follow the output to update infra/main.bicepparam with the generated IDs
```

### 3. Run Locally

```bash
# Copy and fill in local settings for each function
cp local.settings.json.template src/TelemetryIngestor/ReactorTelemetry.Ingestor/local.settings.json
cp local.settings.json.template src/SafetyProcessor/ReactorTelemetry.SafetyProcessor/local.settings.json
cp local.settings.json.template src/AuditLogger/ReactorTelemetry.AuditLogger/local.settings.json

# Start Azurite
azurite --silent &

# Start functions (in separate terminals)
cd src/TelemetryIngestor/ReactorTelemetry.Ingestor && func start
cd src/SafetyProcessor/ReactorTelemetry.SafetyProcessor && func start
cd src/AuditLogger/ReactorTelemetry.AuditLogger && func start
```

### 4. Send a Test Event

```bash
./scripts/seed-test-data.sh
```

## Project Structure

```
azure/
├── .github/workflows/    CI/CD pipelines
├── api/                  OpenAPI specification (source of truth)
├── apim-policies/        APIM XML policy files
├── infra/                Bicep IaC templates
├── src/                  .NET 8 Azure Functions source code
├── scripts/              AZ CLI helper scripts
└── docs/                 Architecture, learning guide, troubleshooting
```

## Learning Resources

- [Architecture & Design Decisions](docs/architecture.md)
- [Topic-by-Topic Learning Guide](docs/learning-guide.md)
- [Local Development Setup](docs/local-development.md)
- [Entra ID Setup Walkthrough](docs/entra-id-setup.md)
- [Troubleshooting Guide](docs/troubleshooting.md)
