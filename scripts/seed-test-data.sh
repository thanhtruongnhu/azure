#!/usr/bin/env bash
# Sends sample telemetry events through APIM for testing.
# Usage:
#   ./scripts/seed-test-data.sh              # interactive mode
#   ./scripts/seed-test-data.sh --smoke-test # CI mode (exits 0 on success, 1 on failure)
#
# Required env vars:
#   APIM_GATEWAY_URL   — e.g., https://apim-reactor-dev.azure-api.net
#   ENTRA_TENANT_ID    — from: az account show --query tenantId -o tsv
#   ENTRA_CLIENT_ID    — client app registration ID
#   ENTRA_CLIENT_SECRET — client app secret
#   ENTRA_AUDIENCE     — API app registration ID

set -euo pipefail

SMOKE_TEST=${1:-""}
APIM_URL="${APIM_GATEWAY_URL:-}"
TENANT_ID="${ENTRA_TENANT_ID:-}"
CLIENT_ID="${ENTRA_CLIENT_ID:-}"
CLIENT_SECRET="${ENTRA_CLIENT_SECRET:-}"
AUDIENCE="${ENTRA_AUDIENCE:-}"

if [[ -z "$APIM_URL" || -z "$TENANT_ID" || -z "$CLIENT_ID" || -z "$CLIENT_SECRET" || -z "$AUDIENCE" ]]; then
  echo "Error: Required environment variables not set."
  echo "Set: APIM_GATEWAY_URL, ENTRA_TENANT_ID, ENTRA_CLIENT_ID, ENTRA_CLIENT_SECRET, ENTRA_AUDIENCE"
  exit 1
fi

# ── Get an access token (client credentials grant) ────────────────────────────
# EDUCATIONAL: This is the OAuth2 client credentials grant in action.
# We POST to the Entra ID token endpoint with our client credentials
# and receive a JWT access token that APIM validates.
echo "Requesting access token..."
TOKEN_RESPONSE=$(curl -s -X POST \
  "https://login.microsoftonline.com/$TENANT_ID/oauth2/v2.0/token" \
  -H "Content-Type: application/x-www-form-urlencoded" \
  -d "grant_type=client_credentials" \
  -d "client_id=$CLIENT_ID" \
  -d "client_secret=$CLIENT_SECRET" \
  -d "scope=api://$AUDIENCE/.default")

ACCESS_TOKEN=$(echo "$TOKEN_RESPONSE" | jq -r '.access_token')

if [[ "$ACCESS_TOKEN" == "null" || -z "$ACCESS_TOKEN" ]]; then
  echo "Failed to get access token:"
  echo "$TOKEN_RESPONSE" | jq
  exit 1
fi

echo "Token acquired successfully."

API_BASE="$APIM_URL/reactor/v1"
REACTOR_ID=$(uuidgen | tr '[:upper:]' '[:lower:]')

# Helper function to send an event and check the response
send_event() {
  local description="$1"
  local payload="$2"
  local correlation_id
  correlation_id=$(uuidgen | tr '[:upper:]' '[:lower:]')

  echo ""
  echo "Sending: $description"

  RESPONSE=$(curl -s -w "\n%{http_code}" -X POST "$API_BASE/telemetry" \
    -H "Authorization: Bearer $ACCESS_TOKEN" \
    -H "Content-Type: application/json" \
    -H "x-correlation-id: $correlation_id" \
    -d "$payload")

  HTTP_CODE=$(echo "$RESPONSE" | tail -1)
  BODY=$(echo "$RESPONSE" | head -n -1)

  echo "  HTTP $HTTP_CODE | CorrelationId: $correlation_id"
  echo "  Response: $(echo "$BODY" | jq -c .)"

  if [[ "$HTTP_CODE" != "202" ]]; then
    echo "  ERROR: Expected 202, got $HTTP_CODE"
    [[ "$SMOKE_TEST" == "--smoke-test" ]] && exit 1
  fi
}

# ── Health check (no auth) ────────────────────────────────────────────────────
echo "Checking /health..."
HEALTH=$(curl -s -o /dev/null -w "%{http_code}" "$API_BASE/health")
echo "Health: HTTP $HEALTH"
[[ "$HEALTH" != "200" ]] && echo "ERROR: Health check failed" && exit 1

# ── Normal telemetry ──────────────────────────────────────────────────────────
send_event "Normal reading" "{
  \"reactorId\": \"$REACTOR_ID\",
  \"timestamp\": \"$(date -u +%Y-%m-%dT%H:%M:%SZ)\",
  \"safetyLevel\": \"Normal\",
  \"readings\": {
    \"coreTemperatureCelsius\": 285.4,
    \"coolantPressureBar\": 155.0,
    \"neutronFluxPerCm2s\": 3.2e13
  }
}"

# ── Warning event (triggers Safety Processor) ─────────────────────────────────
send_event "Warning event" "{
  \"reactorId\": \"$REACTOR_ID\",
  \"timestamp\": \"$(date -u +%Y-%m-%dT%H:%M:%SZ)\",
  \"safetyLevel\": \"Warning\",
  \"readings\": {
    \"coreTemperatureCelsius\": 315.2,
    \"coolantPressureBar\": 158.8
  }
}"

# ── Critical event (triggers Safety Processor with higher severity) ───────────
send_event "Critical event" "{
  \"reactorId\": \"$REACTOR_ID\",
  \"timestamp\": \"$(date -u +%Y-%m-%dT%H:%M:%SZ)\",
  \"safetyLevel\": \"Critical\",
  \"correlationId\": \"$(uuidgen | tr '[:upper:]' '[:lower:]')\",
  \"readings\": {
    \"coreTemperatureCelsius\": 338.7,
    \"coolantPressureBar\": 163.1
  }
}"

echo ""
echo "Done. Check Application Insights Transaction Search for correlated traces."
echo "KQL: traces | where customDimensions.CorrelationId == '<id>' | project timestamp, message, severityLevel"
