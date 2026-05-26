#!/usr/bin/env bash
set -euo pipefail

# Update Azure App Service container image with optional PIM role activation.

SCRIPT_NAME="$(basename "$0")"
RESOURCE_GROUP=""
APP_NAME=""
IMAGE=""
SUBSCRIPTION=""
SKIP_PIM=false
JUSTIFICATION="Deploying updated container image"
PIM_ROLE="Website Contributor"
PIM_POLL_INTERVAL=5
PIM_TIMEOUT=120

usage() {
    cat <<EOF
Usage: $SCRIPT_NAME [OPTIONS]

Activates the 'Website Contributor' role via Azure PIM, then updates the
container image tag on an Azure App Service.

Required:
  -g, --resource-group    Azure resource group name
  -n, --name              App Service name
  -i, --image             Full image reference (e.g. myregistry.azurecr.io/myapp:v2.1.0)

Optional:
  --subscription          Azure subscription ID (uses default if omitted)
  --skip-pim              Skip PIM role activation (if already elevated)
  --justification         Justification text for PIM activation (default: "Deploying updated container image")
  -h, --help              Show this help message

Examples:
  $SCRIPT_NAME -g my-rg -n my-app -i myregistry.azurecr.io/myapp:v2.1.0
  $SCRIPT_NAME -g my-rg -n my-app -i myregistry.azurecr.io/myapp:v2.1.0 --skip-pim
  $SCRIPT_NAME -g my-rg -n my-app -i myregistry.azurecr.io/myapp:v2.1.0 --subscription 00000000-0000-0000-0000-000000000000
EOF
}

log() { echo "[$(date '+%H:%M:%S')] $*"; }
error() { echo "[$(date '+%H:%M:%S')] ERROR: $*" >&2; }
die() { error "$@"; exit 1; }

# --- Parse arguments ---

while [[ $# -gt 0 ]]; do
    case "$1" in
        -g|--resource-group) RESOURCE_GROUP="$2"; shift 2 ;;
        -n|--name)           APP_NAME="$2"; shift 2 ;;
        -i|--image)          IMAGE="$2"; shift 2 ;;
        --subscription)      SUBSCRIPTION="$2"; shift 2 ;;
        --skip-pim)          SKIP_PIM=true; shift ;;
        --justification)     JUSTIFICATION="$2"; shift 2 ;;
        -h|--help)           usage; exit 0 ;;
        *) die "Unknown option: $1. Use --help for usage." ;;
    esac
done

# --- Validate required args ---

[[ -z "$RESOURCE_GROUP" ]] && die "Missing required argument: --resource-group"
[[ -z "$APP_NAME" ]]       && die "Missing required argument: --name"
[[ -z "$IMAGE" ]]          && die "Missing required argument: --image"

# --- Check prerequisites ---

log "Checking prerequisites..."

if ! command -v az &>/dev/null; then
    die "Azure CLI (az) is not installed. Install from https://aka.ms/install-azure-cli"
fi

if ! az account show &>/dev/null; then
    die "Not logged in to Azure CLI. Run 'az login' first."
fi

# Resolve subscription
if [[ -n "$SUBSCRIPTION" ]]; then
    SUB_FLAG="--subscription $SUBSCRIPTION"
    SUBSCRIPTION_ID="$SUBSCRIPTION"
else
    SUB_FLAG=""
    SUBSCRIPTION_ID=$(az account show --query id -o tsv)
fi

log "Using subscription: $SUBSCRIPTION_ID"
log "Resource group: $RESOURCE_GROUP"
log "App Service: $APP_NAME"
log "Image: $IMAGE"

# --- Operation 1: PIM Role Activation ---

activate_pim_role() {
    log "Activating PIM role '$PIM_ROLE' for resource group '$RESOURCE_GROUP'..."

    # Get current user's object ID
    local principal_id
    principal_id=$(az ad signed-in-user show --query id -o tsv) \
        || die "Failed to get signed-in user object ID"

    # Get the role definition ID for 'Website Contributor'
    local scope="/subscriptions/${SUBSCRIPTION_ID}/resourceGroups/${RESOURCE_GROUP}"
    local role_definition_id
    role_definition_id=$(az role definition list \
        --name "$PIM_ROLE" \
        --scope "$scope" \
        --query "[0].id" -o tsv) \
        || die "Failed to find role definition for '$PIM_ROLE'"

    [[ -z "$role_definition_id" ]] && die "Role '$PIM_ROLE' not found at scope $scope"

    # Find the eligible assignment schedule for this principal + role + scope
    local eligible_assignment_id
    eligible_assignment_id=$(az rest \
        --method GET \
        --url "https://management.azure.com${scope}/providers/Microsoft.Authorization/roleEligibilityScheduleInstances?api-version=2020-10-01&\$filter=principalId eq '${principal_id}' and roleDefinitionId eq '${role_definition_id}'" \
        --query "value[0].properties.roleEligibilityScheduleId" -o tsv 2>/dev/null) || true

    if [[ -z "$eligible_assignment_id" ]]; then
        die "No eligible PIM assignment found for role '$PIM_ROLE' on resource group '$RESOURCE_GROUP'. Ensure you have an eligible assignment configured."
    fi

    # Create a role assignment schedule request (activation)
    local request_id
    request_id=$(uuidgen | tr '[:upper:]' '[:lower:]')
    local start_time
    start_time=$(date -u +"%Y-%m-%dT%H:%M:%SZ")

    local body
    body=$(cat <<ENDJSON
{
    "properties": {
        "principalId": "${principal_id}",
        "roleDefinitionId": "${role_definition_id}",
        "requestType": "SelfActivate",
        "linkedRoleEligibilityScheduleId": "${eligible_assignment_id}",
        "justification": "${JUSTIFICATION}",
        "scheduleInfo": {
            "startDateTime": "${start_time}",
            "expiration": {
                "type": "AfterDuration",
                "duration": "PT1H"
            }
        }
    }
}
ENDJSON
)

    log "Submitting PIM activation request..."
    local response
    response=$(az rest \
        --method PUT \
        --url "https://management.azure.com${scope}/providers/Microsoft.Authorization/roleAssignmentScheduleRequests/${request_id}?api-version=2020-10-01" \
        --body "$body" \
        --headers "Content-Type=application/json" 2>&1) \
        || die "PIM activation request failed: $response"

    # Poll for activation to complete
    log "Waiting for PIM activation to complete..."
    local elapsed=0
    while [[ $elapsed -lt $PIM_TIMEOUT ]]; do
        local status
        status=$(az rest \
            --method GET \
            --url "https://management.azure.com${scope}/providers/Microsoft.Authorization/roleAssignmentScheduleRequests/${request_id}?api-version=2020-10-01" \
            --query "properties.status" -o tsv 2>/dev/null) || true

        case "$status" in
            Provisioned|Active)
                log "PIM role activation complete (status: $status)"
                return 0
                ;;
            Failed|Canceled|Denied)
                die "PIM activation failed with status: $status"
                ;;
            *)
                sleep "$PIM_POLL_INTERVAL"
                elapsed=$((elapsed + PIM_POLL_INTERVAL))
                ;;
        esac
    done

    die "PIM activation timed out after ${PIM_TIMEOUT}s. Check Azure portal for status."
}

if [[ "$SKIP_PIM" == "false" ]]; then
    activate_pim_role
else
    log "Skipping PIM activation (--skip-pim specified)"
fi

# --- Operation 2: Update App Service container image ---

log "Updating App Service container image..."

# shellcheck disable=SC2086
az webapp config container set \
    --resource-group "$RESOURCE_GROUP" \
    --name "$APP_NAME" \
    --image "$IMAGE" \
    $SUB_FLAG \
    --output none \
    || die "Failed to update container image on App Service '$APP_NAME'"

log "Container image updated successfully."

# Verify the update
log "Verifying configuration..."
# shellcheck disable=SC2086
current_image=$(az webapp config container show \
    --resource-group "$RESOURCE_GROUP" \
    --name "$APP_NAME" \
    $SUB_FLAG \
    --query "[?name=='DOCKER_CUSTOM_IMAGE_NAME'].value | [0]" -o tsv 2>/dev/null) || true

if [[ "$current_image" == "$IMAGE" ]]; then
    log "Verified: App Service is now using image '$IMAGE'"
else
    log "Warning: Could not verify image update (got '$current_image'). Check Azure portal."
fi

log "Done."
