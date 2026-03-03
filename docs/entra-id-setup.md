# Entra ID (Azure AD) Setup Walkthrough

Sets up two app registrations for the OAuth2 client credentials flow.

## Automated Setup

Run the script (requires `az login` with permissions to create app registrations):
```bash
./scripts/create-entra-app-registrations.sh
```

Copy the output values into `infra/main.bicepparam`.

---

## Manual Setup (Portal)

### Step 1: Create the API App Registration (resource server)

1. Portal → **Microsoft Entra ID** → **App registrations** → **New registration**
2. Name: `reactor-telemetry-api`
3. Supported account types: **Single tenant**
4. Click **Register**
5. Copy the **Application (client) ID** → this is `entraIdAudienceClientId`
6. **Expose an API** → **Add** → set Application ID URI to `api://reactor-telemetry`
7. **App roles** → **Create app role**:
   - Display name: `Telemetry Writer`
   - Allowed member types: **Applications**
   - Value: `telemetry.write`
   - Description: `Allows writing reactor telemetry events`
   - Enable: checked

### Step 2: Create the Client App Registration (regulator system)

1. Portal → **Microsoft Entra ID** → **App registrations** → **New registration**
2. Name: `reactor-telemetry-client`
3. Click **Register**
4. Copy the **Application (client) ID** → this is `regulatorClientId`
5. **Certificates & secrets** → **New client secret** → copy the secret value (visible once only)
6. **API permissions** → **Add a permission** → **My APIs** → `reactor-telemetry-api`
7. Select **Application permissions** → check `telemetry.write` → **Add permissions**
8. Click **Grant admin consent for [your tenant]** → Confirm

### Step 3: Update Bicep parameters

Edit `infra/main.bicepparam`:
```
param entraIdTenantId       = '<tenant-id from Portal → Overview>'
param entraIdAudienceClientId = '<Application ID of reactor-telemetry-api>'
param regulatorClientId       = '<Application ID of reactor-telemetry-client>'
```

### Step 4: Test token acquisition

```bash
TENANT_ID=<your-tenant-id>
CLIENT_ID=<reactor-telemetry-client app id>
CLIENT_SECRET=<the secret you copied>
AUDIENCE_CLIENT_ID=<reactor-telemetry-api app id>

curl -X POST \
  "https://login.microsoftonline.com/$TENANT_ID/oauth2/v2.0/token" \
  -H "Content-Type: application/x-www-form-urlencoded" \
  -d "grant_type=client_credentials" \
  -d "client_id=$CLIENT_ID" \
  -d "client_secret=$CLIENT_SECRET" \
  -d "scope=api://$AUDIENCE_CLIENT_ID/.default"
```

Decode the resulting `access_token` at [jwt.ms](https://jwt.ms) and verify:
- `aud`: `api://reactor-telemetry`
- `roles`: `["telemetry.write"]`
- `iss`: contains your tenant ID

---

## GitHub OIDC Setup (for CI/CD)

Configure federated credentials so GitHub Actions can authenticate without storing secrets.

```bash
APP_ID=<github-actions app registration id>

# For push to main (deploy workflows)
az ad app federated-credential create --id $APP_ID --parameters '{
  "name": "github-push-main",
  "issuer": "https://token.actions.githubusercontent.com",
  "subject": "repo:<owner>/<repo>:ref:refs/heads/main",
  "audiences": ["api://AzureADTokenExchange"]
}'

# For pull requests (pr-validate workflow)
az ad app federated-credential create --id $APP_ID --parameters '{
  "name": "github-pull-request",
  "issuer": "https://token.actions.githubusercontent.com",
  "subject": "repo:<owner>/<repo>:pull_request",
  "audiences": ["api://AzureADTokenExchange"]
}'

# Grant Contributor role for deployments
az role assignment create \
  --assignee $APP_ID \
  --role "Contributor" \
  --scope "/subscriptions/<subscription-id>"

# Store as GitHub repository secrets:
# AZURE_CLIENT_ID = <app id of the GitHub Actions app registration>
# AZURE_TENANT_ID = <your tenant id>
# AZURE_SUBSCRIPTION_ID = <your subscription id>
```
