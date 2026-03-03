#!/usr/bin/env bash
# Creates two Entra ID app registrations for OAuth2 client-credentials flow:
#   1. reactor-telemetry-api  — the API (audience / resource server)
#   2. reactor-telemetry-client — the regulator system (client / caller)
#
# EDUCATIONAL: Client Credentials Grant (OAuth2 RFC 6749 §4.4)
#   Client → POST /oauth2/v2.0/token (client_id + client_secret + scope)
#   Entra  → access_token (JWT with roles claim)
#   Client → APIM with Bearer <token>
#   APIM   → validates token via validate-azure-ad-token policy
#
# Run: ./scripts/create-entra-app-registrations.sh
# Then: copy the output values into infra/main.bicepparam

set -euo pipefail

TENANT_ID=$(az account show --query tenantId -o tsv)
echo "Tenant ID: $TENANT_ID"

# ── 1. Create the API app registration ────────────────────────────────────────
# This is the "resource server" — it defines what scopes and roles exist.
API_APP_ID=$(az ad app create \
  --display-name "reactor-telemetry-api" \
  --identifier-uris "api://reactor-telemetry" \
  --query appId -o tsv)

echo "API App ID: $API_APP_ID"

# Add an app role: telemetry.write
# App roles are how service-to-service authorization works (no user context).
# The regulator client must be granted this role via admin consent.
az ad app update --id "$API_APP_ID" --app-roles '[
  {
    "allowedMemberTypes": ["Application"],
    "description": "Allows writing reactor telemetry events",
    "displayName": "Telemetry Writer",
    "isEnabled": true,
    "value": "telemetry.write",
    "id": "'"$(uuidgen)"'"
  }
]'

echo "App role telemetry.write created on API app"

# ── 2. Create the client (regulator system) app registration ──────────────────
CLIENT_APP_ID=$(az ad app create \
  --display-name "reactor-telemetry-client" \
  --query appId -o tsv)

CLIENT_SP_ID=$(az ad sp create --id "$CLIENT_APP_ID" --query id -o tsv)
echo "Client App ID: $CLIENT_APP_ID"
echo "Client Service Principal ID: $CLIENT_SP_ID"

# Create a client secret (expires in 1 year)
# EDUCATIONAL: In production, prefer certificate credentials over secrets.
CLIENT_SECRET=$(az ad app credential reset \
  --id "$CLIENT_APP_ID" \
  --years 1 \
  --query password -o tsv)

echo "Client Secret: $CLIENT_SECRET (save this — it won't be shown again)"

# ── 3. Grant the client the telemetry.write app role ─────────────────────────
# Get the API's service principal ID
API_SP_ID=$(az ad sp show --id "$API_APP_ID" --query id -o tsv)

# Get the app role ID
ROLE_ID=$(az ad app show --id "$API_APP_ID" \
  --query "appRoles[?value=='telemetry.write'].id" -o tsv)

# Grant admin consent: assign the app role to the client service principal
az rest --method POST \
  --uri "https://graph.microsoft.com/v1.0/servicePrincipals/$API_SP_ID/appRoleAssignedTo" \
  --body '{
    "principalId": "'"$CLIENT_SP_ID"'",
    "resourceId": "'"$API_SP_ID"'",
    "appRoleId": "'"$ROLE_ID"'"
  }'

echo "App role granted to client"

# ── 4. Output values for infra/main.bicepparam ────────────────────────────────
echo ""
echo "====== Copy these values into infra/main.bicepparam ======"
echo "param entraIdTenantId = '$TENANT_ID'"
echo "param entraIdAudienceClientId = '$API_APP_ID'"
echo "param regulatorClientId = '$CLIENT_APP_ID'"
echo ""
echo "====== Copy these values into GitHub Secrets ======"
echo "AZURE_TENANT_ID=$TENANT_ID"
echo "SMOKE_TEST_CLIENT_ID=$CLIENT_APP_ID"
echo "SMOKE_TEST_CLIENT_SECRET=$CLIENT_SECRET"
echo "ENTRA_AUDIENCE_CLIENT_ID=$API_APP_ID"
echo ""
echo "====== Use this to get a token for testing ======"
echo "az account get-access-token --resource api://reactor-telemetry --query accessToken -o tsv"
