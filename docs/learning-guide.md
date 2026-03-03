# Topic-by-Topic Learning Guide

A study reference for each Azure concept demonstrated in this project.

---

## 1. API-First

**What it is:** Define the API contract (OpenAPI spec) before writing any implementation. The spec drives everything downstream.

**How it's used here:**
- `api/reactor-telemetry.openapi.yaml` is the source of truth
- Bicep imports it directly: `loadTextContent('../../api/reactor-telemetry.openapi.yaml')`
- APIM operations, schemas, and examples all come from the spec
- You can generate a regulator client SDK from this spec before the backend exists

**Key gotcha:** The `format: 'openapi'` in Bicep requires the spec to be a valid OpenAPI 3.0 document. Use `npx @stoplight/spectral-cli lint api/reactor-telemetry.openapi.yaml` to validate before deploying.

**How to verify:** After deploying, open the APIM Developer Portal. The `POST /telemetry` operation should show the correct request/response schemas from the spec.

---

## 2. APIM Policies

**What it is:** Policy XML is injected at four scopes (Global → API → Operation → Product). Policies run in order inbound, then outbound in reverse.

**Policies used here:**

| Policy | File | What it does |
|---|---|---|
| `<cors>` | global-policy.xml | CORS headers for browser clients |
| `<set-variable>` + `<set-header>` | global-policy.xml | Correlation ID injection |
| `<rate-limit-by-key>` | reactor-api-policy.xml | 100 req/60s per caller IP |
| `<ip-filter>` | reactor-api-policy.xml | IP allowlist |
| `<validate-azure-ad-token>` | reactor-api-policy.xml | JWT validation, roles claim check |
| `<log-to-eventhub>` | reactor-api-policy.xml | Structured audit log to App Insights |
| `<set-header>` (JWT claim extraction) | ingest-operation-policy.xml | `x-authenticated-subject` header |
| `<set-backend-service>` | ingest-operation-policy.xml | Named backend routing |

**Key gotcha:** `<base />` is how a policy inherits from its parent scope. Without `<base />` in the inbound section of your API policy, the global policy won't run. The order matters: `<base />` at the top means parent runs first; at the bottom means current policy runs first.

**How to verify:** In APIM Portal → APIs → Test tab → send a request without a Bearer token → should get 401. With a valid token → 202.

---

## 3. OAuth2 / JWT Validation

**What it is:** Client Credentials Grant (RFC 6749 §4.4). No user involved — service-to-service authentication. The regulator system authenticates with its own identity.

**Flow:**
```
Regulator System
  → POST https://login.microsoftonline.com/{tenantId}/oauth2/v2.0/token
     client_id={clientId}&client_secret={secret}&scope=api://{audience}/.default&grant_type=client_credentials
  ← access_token (JWT)
  → POST APIM /reactor/v1/telemetry  Bearer {token}
  APIM validates:
    - Signature (from Entra JWKS endpoint, auto-discovered)
    - Audience (api://reactor-telemetry)
    - Issuer (https://sts.windows.net/{tenantId}/)
    - 'roles' claim contains 'telemetry.write'
    - Token not expired (exp claim)
```

**Key gotcha:** `roles` claim (app roles) vs `scp` claim (delegated scopes):
- `scp` is for user-delegated flows (user signs in and grants consent)
- `roles` is for app-only flows (service account, no user)
- Client credentials flow produces tokens with `roles`, not `scp`

**How to verify:** Decode your access token at [jwt.ms](https://jwt.ms). Confirm `roles: ["telemetry.write"]`, `aud: "api://reactor-telemetry"`, `iss` contains your tenant ID.

---

## 4. Pub/Sub with Service Bus Topics

**What it is:** A Topic is a broadcast channel. Each Subscription is an independent consumer of matching messages. One published message can be consumed by N subscribers independently.

**How it's used here:**
```
Topic: reactor-events
  Subscription: safety-processor  (SQL filter: SafetyLevel IN ('Warning','Critical','Emergency'))
  Subscription: audit-logger      (no filter — receives all events)
```

**SQL filters** evaluate `message.ApplicationProperties` (not the body). In the Ingestor Function:
```csharp
message.ApplicationProperties["SafetyLevel"] = telemetryEvent.SafetyLevel.ToString();
```

**Key gotcha:** Every new subscription gets a `$Default` rule that accepts ALL messages. When you add a custom SQL filter rule, you must also disable/remove the `$Default` rule or your filter is ignored. See `servicebus.bicep` — we replace `$Default` with a `FalseFilter`.

**How to verify:** Portal → Service Bus → Topics → reactor-events → Subscriptions → Click `safety-processor` → peek messages → you should only see Warning/Critical/Emergency events.

---

## 5. Event-Driven Architecture

**What it is:** Components communicate via events (messages) rather than direct calls. The producer doesn't know who consumes the event or when.

**Concepts demonstrated:**
- **Async ingestion:** HTTP 202 Accepted means "I received your request and will process it" — not "it's done"
- **Retry with backoff:** Service Bus retries up to `maxDeliveryCount` times automatically
- **Dead-letter queue (DLQ):** Messages that can't be processed after N retries go to the DLQ for forensic analysis
- **Idempotency:** `MessageId = CorrelationId` + duplicate detection window prevents double-processing
- **Explicit completion:** `messageActions.CompleteMessageAsync()` — only called after successful processing

**Key gotcha:** Don't dead-letter retriable errors! If your downstream database has a 5-second outage, throwing (not dead-lettering) lets Service Bus retry. Wasting all 3 attempts on a transient error means the DLQ fills up with messages that would have processed fine on the 4th attempt.

**How to verify:** Send a message, then stop the Safety Processor Function. Watch the message become available again after the lock duration (5 min). Start the Function — it processes on the next delivery attempt.

---

## 6. Monitoring with Application Insights

**What it is:** Distributed tracing + telemetry aggregation. Every component emits telemetry that correlates into a single end-to-end trace.

**What's instrumented:**
- APIM: request logs via `apimLogger` (the `<log-to-eventhub>` policy)
- Ingestor: `ILogger` with `BeginScope({"CorrelationId": ...})` → appears in `traces` table
- SafetyProcessor: `TelemetryClient.TrackEvent()` → custom events per safety level
- SafetyProcessor: `TelemetryClient.TrackMetric()` → `CoreTemperatureCelsius`, `CoolantPressureBar`

**Key KQL queries:**
```kql
-- Find all traces for a specific correlation ID
traces
| where customDimensions.CorrelationId == "<id>"
| project timestamp, message, severityLevel, cloud_RoleName
| order by timestamp asc

-- All emergency events in the last 24 hours
customEvents
| where name == "EmergencyEvent"
| where timestamp > ago(24h)
| project timestamp, customDimensions.ReactorId, customDimensions.CoreTempCelsius

-- Average core temperature by reactor over time
customMetrics
| where name == "CoreTemperatureCelsius"
| summarize avg(value) by bin(timestamp, 5m), tostring(customDimensions.ReactorId)
| render timechart

-- APIM request rate
requests
| where cloud_RoleName contains "apim"
| summarize count() by bin(timestamp, 1m), resultCode
| render columnchart
```

**Key gotcha:** App Insights sampling (enabled in `host.json`) means not every request is logged. For Emergency events, we call `_telemetryClient.Flush()` to force immediate delivery. Adjust sampling in production based on volume and cost.

---

## 7. CI/CD with GitHub Actions

**What it is:** Automated pipelines that build, test, and deploy on git events.

**Three workflows:**
| Workflow | Trigger | Jobs |
|---|---|---|
| `pr-validate.yml` | Pull request to main | Build, test, Bicep lint, Bicep what-if |
| `deploy-infra.yml` | Push to main (infra/**) | Bicep validate, Bicep deploy, output summary |
| `deploy-functions.yml` | Push to main (src/**) | Build, test, publish, deploy (matrix), smoke test |

**OIDC (no stored secrets):** GitHub requests a short-lived token from Entra ID. Azure validates the token's `sub` claim (which encodes the repo + branch/PR). No secrets are stored in GitHub — only three app registration IDs.

**Key gotcha:** You must create a federated credential on the Entra app registration for EACH GitHub trigger context (pull_request, push to main, etc.). See the comment in `pr-validate.yml` for the AZ CLI commands.

---

## 8. IaC with Bicep

**What it is:** Bicep is Azure's native declarative IaC language. It compiles to ARM JSON templates.

**Key patterns used:**
- `targetScope = 'subscription'` — creates resource group AND all resources in one deployment
- Modular structure: `main.bicep` orchestrates 7 modules
- `uniqueString(resourceGroup().id)` — generates a stable, unique suffix for globally unique names
- `existing` keyword — references resources created in other modules without redeploying them
- `guid(...)` — deterministic GUID for role assignment names (prevents duplicate assignments)

**Key gotcha:** APIM Developer tier takes ~40 minutes to provision on first deploy. On subsequent deployments it updates in place (~2 minutes). Plan your learning time accordingly.

**How to verify:** `az deployment sub show --name reactor-telemetry-dev --query properties.outputs`

---

## 9. DevOps Practices

**What it is:** Operational practices around the full software delivery lifecycle.

**Practices demonstrated:**
- **Environment management:** `main.bicepparam` for dev, `main.prod.bicepparam` for prod (gitignored)
- **Secrets management:** Key Vault + managed identity. No secrets in source code or CI variables except OIDC app registration IDs.
- **Gitignore discipline:** `local.settings.json` and prod param files are excluded from git
- **GitHub Environments:** `deploy` jobs target the `dev` environment, enabling approval gates

---

## 10. Troubleshooting

See [troubleshooting.md](troubleshooting.md) for a detailed guide. Quick reference:

| Symptom | Likely Cause | Fix |
|---|---|---|
| APIM 401 | Token invalid or wrong audience | Check `az account get-access-token --resource api://reactor-telemetry`. Verify audience in policy matches API app ID |
| APIM 429 | Rate limit hit | Wait 60s or increase limit in reactor-api-policy.xml |
| Function not triggered | Managed identity missing RBAC role | Check rbac.bicep was deployed. Run `az role assignment list` |
| DLQ growing | Repeated processing failures | Check App Insights for errors. Look at DLQ message `DeadLetterReason` |
| No traces in App Insights | Connection string missing | Verify `APPLICATIONINSIGHTS_CONNECTION_STRING` app setting is set |

---

## 11. Scalability

**APIM:** Scale out by increasing `capacity` in `apim.bicep` (Developer: 1 unit only. Standard: 1-4. Premium: 1-12+). Or upgrade to Premium for multi-region active-active.

**Azure Functions (Consumption):** Scales to zero automatically. Scale out is controlled by the Functions host scale controller — for Service Bus triggers, it scales based on the number of messages in the subscription.

**Service Bus:** Standard tier has a 256KB message size limit and 80GB topic capacity. Premium tier supports 1MB+ messages and session-based processing. For very high throughput, enable partitioning (note: this disables ordered delivery within a partition key).

**Table Storage:** Scales transparently. For high read throughput, add PartitionKey to filter queries to avoid full-table scans.
