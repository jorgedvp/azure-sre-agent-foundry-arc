# Despliegue de la Etapa 2 — recursos creados

## Resource group nuevo (independiente de `rg-sre-customer-demo`)

`rg-sre-foundry-arc-poc` (eastus2):

| Recurso | Tipo | Propósito |
|---|---|---|
| `foundry-arc-poc` | Microsoft.CognitiveServices/accounts (kind=AIServices) | Cuenta de Microsoft Foundry |
| `gpt-4o-arc-agent` | Model deployment (GlobalStandard, gpt-4o) | Modelo con function calling usado por el agente |
| `id-sre-foundry-arc-poc` | User-assigned managed identity | Identidad dedicada de este POC (ver permisos abajo) |
| `acrsrefoundryarcpoc` | Azure Container Registry | Build/push de la imagen del backend |
| `law-sre-foundry-arc-poc` | Log Analytics workspace | Logs del Container Apps Environment |
| `cae-sre-foundry-arc-poc` | Container Apps Environment | Entorno de hosting |
| `ca-arc-foundry-copilot` | Container App | Backend .NET + frontend estático (un solo contenedor) |

## Único cambio en el ambiente existente (`rg-sre-customer-demo`)

3 asignaciones de rol **aditivas** a `id-sre-foundry-arc-poc` (nunca se tocó nada del SRE Agent ni de las VMs):

| Rol | Alcance | Por qué |
|---|---|---|
| Reader | rg-sre-customer-demo | Listar/inspeccionar recursos Arc |
| Azure Connected Machine Resource Administrator | rg-sre-customer-demo | Ejecutar Run Command (mismo rol que usa el SRE Agent — no existe un rol más granular de "solo lectura" en Arc) |
| Log Analytics Reader | rg-sre-customer-demo | Consultas KQL de solo lectura |

Además, `id-sre-foundry-arc-poc` tiene el rol **Cognitive Services OpenAI User** sobre `foundry-arc-poc` (dentro del resource group nuevo, no afecta nada existente).

## Cómo correr este despliegue de nuevo (si se borra el POC)

```bash
export SUBSCRIPTION="<tu-subscription-id>"
RG="rg-sre-foundry-arc-poc"
LOCATION="eastus2"

# 1. Resource group + Foundry + modelo
az group create --name "$RG" --location "$LOCATION"
az cognitiveservices account create --name "foundry-arc-poc" --resource-group "$RG" \
  --kind AIServices --sku S0 --location "$LOCATION" --custom-domain "foundry-arc-poc"
az cognitiveservices account deployment create --name "foundry-arc-poc" --resource-group "$RG" \
  --deployment-name "gpt-4o-arc-agent" --model-name gpt-4o --model-version "2024-11-20" \
  --model-format OpenAI --sku-capacity 50 --sku-name GlobalStandard

# 2. Identidad + permisos aditivos sobre el demo existente
az identity create --name "id-sre-foundry-arc-poc" --resource-group "$RG" --location "$LOCATION"
PRINCIPAL_ID=$(az identity show -g "$RG" -n id-sre-foundry-arc-poc --query principalId -o tsv)
CUSTOMER_DEMO_SCOPE="/subscriptions/$SUBSCRIPTION/resourceGroups/rg-sre-customer-demo"
az role assignment create --assignee "$PRINCIPAL_ID" --role Reader --scope "$CUSTOMER_DEMO_SCOPE"
az role assignment create --assignee "$PRINCIPAL_ID" --role "Azure Connected Machine Resource Administrator" --scope "$CUSTOMER_DEMO_SCOPE"
az role assignment create --assignee "$PRINCIPAL_ID" --role "Log Analytics Reader" --scope "$CUSTOMER_DEMO_SCOPE"
az role assignment create --assignee "$PRINCIPAL_ID" --role "Cognitive Services OpenAI User" \
  --scope "/subscriptions/$SUBSCRIPTION/resourceGroups/$RG/providers/Microsoft.CognitiveServices/accounts/foundry-arc-poc"

# 3. ACR + Container Apps
az acr create --name "acrsrefoundryarcpoc" --resource-group "$RG" --sku Basic --admin-enabled true
LAW_ID=$(az monitor log-analytics workspace create -g "$RG" -n law-sre-foundry-arc-poc -l "$LOCATION" --query customerId -o tsv)
LAW_KEY=$(az monitor log-analytics workspace get-shared-keys -g "$RG" -n law-sre-foundry-arc-poc --query primarySharedKey -o tsv)
az containerapp env create --name "cae-sre-foundry-arc-poc" --resource-group "$RG" --location "$LOCATION" \
  --logs-workspace-id "$LAW_ID" --logs-workspace-key "$LAW_KEY"

# 4. Build + deploy de la app
az acr build --registry acrsrefoundryarcpoc --image arc-foundry-api:latest --file Dockerfile .
IDENTITY_ID=$(az identity show -g "$RG" -n id-sre-foundry-arc-poc --query id -o tsv)
IDENTITY_CLIENT_ID=$(az identity show -g "$RG" -n id-sre-foundry-arc-poc --query clientId -o tsv)
ACR_USER=$(az acr credential show --name acrsrefoundryarcpoc --query username -o tsv)
ACR_PASS=$(az acr credential show --name acrsrefoundryarcpoc --query "passwords[0].value" -o tsv)
az containerapp create --name "ca-arc-foundry-copilot" --resource-group "$RG" \
  --environment "cae-sre-foundry-arc-poc" --image "acrsrefoundryarcpoc.azurecr.io/arc-foundry-api:latest" \
  --registry-server "acrsrefoundryarcpoc.azurecr.io" --registry-username "$ACR_USER" --registry-password "$ACR_PASS" \
  --target-port 8080 --ingress external --min-replicas 1 --max-replicas 1 --cpu 0.5 --memory 1.0Gi \
  --user-assigned "$IDENTITY_ID" --env-vars "Azure__ManagedIdentityClientId=$IDENTITY_CLIENT_ID"

# 5. GitHub token para que el agente pueda crear Issues (reusa el mismo PAT del demo original)
az containerapp secret set -g "$RG" -n ca-arc-foundry-copilot --secrets "github-pat=$(gh auth token)"
az containerapp update -g "$RG" -n ca-arc-foundry-copilot --set-env-vars "GitHub__Token=secretref:github-pat"
```

## Prerrequisito antes de la demo: encender la VM del demo original

Igual que con `azure-sre-agent-customer-demo`, este POC lee el inventario Arc en tiempo real — si las VMs están desasignadas (ver `prompts/LAUNCH-DEMO.md` del repo original), el agente Foundry va a reportarlas como "Disconnected" (comportamiento correcto, pero no es lo que quieres mostrar). Enciende `vm-onprem-sql` / `vm-onprem-iis` antes de la demo, igual que para el demo principal.

## Validado en vivo (26-oct-2026)

- ✅ `list_arc_machines` — inventario real devuelto
- ✅ `get_machine_inventory` — detalle completo de `vm-onprem-sql`
- ✅ `run_diagnostic` — `df -h` ejecutado realmente vía Arc Run Command, resultado real devuelto
- ✅ Guardrail de seguridad — el agente rechazó (tanto por juicio propio como por el bloqueo de código) un intento de `systemctl stop mssql-server`
- ✅ `create_github_issue` — [Issue #3](https://github.com/jorgedvp/azure-sre-agent-customer-demo/issues/3), etiquetado `foundry-poc`, sin interferir con el Issue #1 del SRE Agent real

## Costo aproximado de este POC

Mucho menor que el demo principal — sin VMs propias (lee las del demo existente):
- Container Apps (Consumption, 0.5 vCPU / 1GB, min-replica 1): ~$15-20/mes si queda siempre encendido
- Azure Container Registry (Basic): ~$5/mes
- AI Foundry / modelo gpt-4o (GlobalStandard): pago por uso de tokens, marginal para demos puntuales
- Log Analytics: marginal

**Recomendación**: escalar `ca-arc-foundry-copilot` a `min-replicas 0` entre demos (Container Apps soporta scale-to-zero) para no pagar nada cuando no se está usando.
