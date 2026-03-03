# Architecture

## System Context (C4 Level 1)

```
┌─────────────────────────────────────────────────────────────────────┐
│                          Azure Subscription                         │
│                                                                     │
│  Regulator         ┌──────────┐    ┌──────────┐    ┌────────────┐  │
│  System      ──►   │   APIM   │──► │ Ingestor │──► │ Service    │  │
│  (external)        │          │    │ Function │    │ Bus Topic  │  │
│                    └──────────┘    └──────────┘    └─────┬──────┘  │
│                         │                                │         │
│                         │                    ┌───────────┴──────┐  │
│                         │                    │                  │  │
│                         │              ┌─────▼──────┐   ┌──────▼─┐ │
│                         │              │  Safety    │   │ Audit  │ │
│                         │              │ Processor  │   │ Logger │ │
│                         │              └─────┬──────┘   └──────┬─┘ │
│                         │                    │                 │   │
│                         ▼                    ▼                 ▼   │
│                   App Insights ◄─────────────────────────────────  │
│                   Key Vault                                         │
│                   Table Storage ◄───────────────────────────────── │
└─────────────────────────────────────────────────────────────────────┘
```

## Component Diagram (C4 Level 2)

| Component | Technology | Responsibility |
|---|---|---|
| Azure API Management | Developer tier | North-facing gateway: JWT validation, rate limiting, IP filter, CORS, request transform |
| TelemetryIngestor | Azure Function, HTTP trigger, .NET 8 | Validates input, publishes to Service Bus, returns 202 |
| reactor-events topic | Service Bus Standard, Topic | Fan-out: delivers one message to N subscriptions independently |
| safety-processor subscription | Service Bus subscription + SQL filter | Receives only Warning/Critical/Emergency events |
| audit-logger subscription | Service Bus subscription, no filter | Receives ALL events for complete audit trail |
| SafetyProcessor | Azure Function, SB trigger, .NET 8 | Evaluates safety events, emits custom App Insights metrics, DLQ drain |
| AuditLogger | Azure Function, SB trigger, .NET 8 | Writes all events to Table Storage as immutable audit records |
| Application Insights | Workspace-based | Distributed tracing, custom metrics, dashboards, alerts |
| Key Vault | RBAC access model | Stores secrets; referenced via managed identity, no credentials in code |
| Table Storage | Azure Storage, Table service | Audit log: PartitionKey=ReactorId, RowKey=CorrelationId |

## Architecture Decision Records

### ADR-001: API-First with OpenAPI

**Decision:** Define the OpenAPI spec first (`api/reactor-telemetry.openapi.yaml`), then import it directly into APIM via `loadTextContent()` in Bicep.

**Rationale:** The spec becomes the single source of truth. APIM operations, schemas, and examples all derive from it. You can generate client SDKs and mock servers before the backend exists.

**Consequence:** Any change to the API contract requires updating the spec first, then redeploying. This is intentional — it prevents undocumented API drift.

### ADR-002: Service Bus Standard Tier

**Decision:** Use Service Bus Standard tier, not Basic or Premium.

**Rationale:**
- Basic supports Queues only (no Topics). We need Topics for pub/sub fan-out.
- Premium adds VNet injection, dedicated capacity, and zone redundancy — overkill for learning.
- Standard has Topics + duplicate detection + SQL filters, which is exactly what we need.

**Consequence:** No VNet integration (IP filtering is application-layer only). No zone redundancy. For a production safety system, upgrade to Premium.

### ADR-003: Managed Identity Everywhere

**Decision:** All Azure service authentication uses managed identity. `disableLocalAuth: true` on Service Bus.

**Rationale:** No connection strings or keys stored in app settings means no credential rotation, no secrets leakage, and no access after identity is deleted.

**Consequence:** Local development requires `az login` and developer's identity must have the same roles as the managed identity. See [local-development.md](local-development.md).

### ADR-004: RBAC in One Module

**Decision:** All role assignments are in `infra/modules/rbac.bicep`.

**Rationale:** Security posture is auditable at a glance. You can answer "what can the Ingestor access?" by reading one file.

### ADR-005: Explicit Message Completion

**Decision:** Service Bus trigger functions use `autoComplete: false` (set in `host.json`) and explicitly call `messageActions.CompleteMessageAsync()`.

**Rationale:** Explicit completion ensures the message is only deleted from the queue AFTER successful processing. If the function crashes mid-execution, the message lock expires and it is redelivered — safe retry semantics.

### ADR-006: Dead-Letter Immediately for Non-Retriable Errors

**Decision:** Deserialization failures dead-letter immediately (don't retry).

**Rationale:** A malformed JSON payload won't fix itself between retries. Burning all 3 delivery attempts wastes time and makes the DLQ noisy. Retriable errors (transient failures) should throw and let Service Bus retry automatically.
