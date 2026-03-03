# Troubleshooting Guide

## APIM Issues

### 401 Unauthorized

**Causes and fixes:**

1. **Token audience mismatch** — the token's `aud` claim doesn't match `api://reactor-telemetry`
   ```bash
   # Get a token and inspect it
   TOKEN=$(az account get-access-token --resource api://reactor-telemetry --query accessToken -o tsv)
   # Decode at jwt.ms or:
   echo $TOKEN | cut -d. -f2 | base64 -d 2>/dev/null | jq .aud
   ```

2. **Wrong scope requested** — use `api://<audienceClientId>/.default` not `openid`
   ```bash
   curl -X POST "https://login.microsoftonline.com/$TENANT_ID/oauth2/v2.0/token" \
     -d "scope=api://$AUDIENCE_CLIENT_ID/.default"
   ```

3. **APIM Named Value mismatch** — `EntraTenantId` or `EntraAudienceClientId` wrong
   - Portal → APIM → Named Values → verify values match `az account show --query tenantId`

4. **App role not granted** — client app missing `telemetry.write` role
   ```bash
   az ad app show --id $CLIENT_APP_ID --query "requiredResourceAccess"
   ```

### 429 Too Many Requests

Rate limit hit (100 req/60s per IP). Response includes:
- `Retry-After: N` — seconds until reset
- `X-Rate-Limit-Remaining: 0`

To increase limits: edit `apim-policies/reactor-api-policy.xml` → change `calls="100"`.

### 403 Forbidden

Token is valid but missing the `telemetry.write` app role. Fix:
```bash
# Re-run the grant step from create-entra-app-registrations.sh
# Or manually in Portal → Entra ID → Enterprise Apps → reactor-telemetry-api → App Roles
```

---

## Azure Functions Issues

### Function never triggers (Service Bus)

1. **Managed identity missing role:**
   ```bash
   az role assignment list \
     --assignee $(az functionapp identity show \
       --name func-reactor-dev-safety \
       --resource-group rg-reactor-dev \
       --query principalId -o tsv) \
     --query "[].{role:roleDefinitionName, scope:scope}"
   ```
   Expected: `Azure Service Bus Data Receiver` on the namespace.

2. **App setting misconfigured:**
   ```bash
   az functionapp config appsettings list \
     --name func-reactor-dev-safety \
     --resource-group rg-reactor-dev \
     --query "[?name=='ServiceBusConnection__fullyQualifiedNamespace']"
   ```
   Should be `<namespace>.servicebus.windows.net` (no `sb://` prefix, no connection string).

3. **Subscription filter rejecting messages:**
   - Portal → Service Bus → Topics → reactor-events → Subscriptions → safety-processor → Rules
   - Verify the SQL filter: `SafetyLevel IN ('Warning', 'Critical', 'Emergency')`
   - Check that `$Default` rule is disabled (FalseFilter)

### Function fails with `AuthorizationFailedException`

Managed identity doesn't have the right RBAC role. Check `infra/modules/rbac.bicep` was deployed:
```bash
az deployment group show \
  --name rbac \
  --resource-group rg-reactor-dev \
  --query properties.provisioningState
```

---

## Service Bus / Dead-Letter Queue

### Inspect DLQ messages

```bash
# Portal: Service Bus → Topics → reactor-events → Subscriptions → safety-processor → Dead-letter

# CLI: count DLQ messages
az servicebus topic subscription show \
  --namespace-name sb-reactor-dev-<suffix> \
  --resource-group rg-reactor-dev \
  --topic-name reactor-events \
  --name safety-processor \
  --query "countDetails.deadLetterMessageCount"
```

### Common DLQ reasons

| DeadLetterReason | Cause | Fix |
|---|---|---|
| `DeserializationFailed` | Malformed JSON in message body | Fix the publisher; messages with this reason cannot be reprocessed |
| `MaxDeliveryCountExceeded` | Processing failed 3 times | Check App Insights for the root cause error. Fix and potentially resubmit |
| `TTLExpiredException` | Message TTL expired (1 hour) | Consumer was down too long. Check Function scaling |
| `NullPayload` | Message deserialized to null | Publisher sent a null/empty body |

### View DLQ message body in App Insights

```kql
-- Find DLQ events logged by ReprocessDeadLetterFunction
traces
| where message contains "DLQ message"
| project timestamp, message, customDimensions
| order by timestamp desc
```

---

## Application Insights

### No traces appearing

1. Check `APPLICATIONINSIGHTS_CONNECTION_STRING` is set (not the legacy `APPINSIGHTS_INSTRUMENTATIONKEY`)
2. Allow 2–5 minutes for telemetry to appear (buffered)
3. Check if sampling is filtering out requests (`host.json` sampling settings)

### Useful KQL Queries

```kql
-- End-to-end trace for a correlation ID
union traces, requests, exceptions, customEvents
| where customDimensions.CorrelationId == "<your-id>"
   or operation_Id == "<your-id>"
| project timestamp, itemType, message, name, cloud_RoleName
| order by timestamp asc

-- Function execution failures
exceptions
| where timestamp > ago(1h)
| project timestamp, type, outerMessage, cloud_RoleName
| order by timestamp desc

-- Service Bus message processing rate
customMetrics
| where name == "ServiceBus.IncomingMessages"
| summarize sum(value) by bin(timestamp, 1m)
| render timechart

-- APIM request latency percentiles
requests
| where cloud_RoleName contains "apim"
| summarize
    p50 = percentile(duration, 50),
    p95 = percentile(duration, 95),
    p99 = percentile(duration, 99)
  by bin(timestamp, 5m)
| render timechart

-- Emergency events in last 24h
customEvents
| where name == "EmergencyEvent"
| where timestamp > ago(24h)
| project timestamp,
    reactorId = customDimensions.ReactorId,
    coreTemp  = customDimensions.CoreTempCelsius
```

---

## Bicep / Deployment Issues

### APIM deployment takes too long

APIM Developer tier first-time provisioning takes 30–45 minutes. This is normal. Subsequent deployments are much faster (2–5 minutes). Deploy App Insights, Key Vault, Service Bus, and Functions first — they complete quickly.

### "Role assignment already exists" error

Bicep uses `guid()` to generate deterministic role assignment names. If you see this, it means the role is already assigned (idempotent). Safe to ignore.

### Key Vault soft-delete conflict

If you delete and redeploy Key Vault, you'll get a conflict because soft-deleted vaults retain the name for the retention period. Fix:
```bash
az keyvault purge --name kv-reactor-dev-<suffix> --location eastus
```
Note: `enablePurgeProtection: false` in `keyvault.bicep` (dev only) allows this.
