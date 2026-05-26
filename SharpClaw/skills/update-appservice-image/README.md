# Update App Service Image

Activates the **Website Contributor** role via Azure PIM (Privileged Identity Management), then updates the container image tag on an Azure App Service.

## Prerequisites

- [Azure CLI](https://aka.ms/install-azure-cli) installed and authenticated (`az login`)
- An eligible PIM assignment for the 'Website Contributor' role on the target resource group
- `uuidgen` available (standard on Linux/macOS)

## Usage

```bash
./run.sh -g <resource-group> -n <app-service-name> -i <image:tag> [OPTIONS]
```

### Required Arguments

| Flag | Description |
|------|-------------|
| `-g`, `--resource-group` | Azure resource group name |
| `-n`, `--name` | App Service name |
| `-i`, `--image` | Full image reference (e.g. `myregistry.azurecr.io/myapp:v2.1.0`) |

### Optional Arguments

| Flag | Description |
|------|-------------|
| `--subscription` | Azure subscription ID (uses current default if omitted) |
| `--skip-pim` | Skip PIM role activation (use if already elevated) |
| `--justification` | Justification text for PIM request (default: "Deploying updated container image") |
| `-h`, `--help` | Show help message |

## Examples

```bash
# Full flow: activate PIM + update image
./run.sh -g my-rg -n my-app -i myregistry.azurecr.io/myapp:v2.1.0

# Skip PIM (already elevated)
./run.sh -g my-rg -n my-app -i myregistry.azurecr.io/myapp:v2.1.0 --skip-pim

# Explicit subscription + custom justification
./run.sh -g my-rg -n my-app -i myregistry.azurecr.io/myapp:v2.1.0 \
  --subscription 00000000-0000-0000-0000-000000000000 \
  --justification "Hotfix for CVE-2026-1234"
```

## How It Works

1. **PIM Activation** — Calls the Azure PIM REST API (`Microsoft.Authorization/roleAssignmentScheduleRequests`) to self-activate the Website Contributor eligible role assignment. Polls until activation completes (up to 120s timeout).

2. **Image Update** — Uses `az webapp config container set --image` to update the App Service container configuration with the new image reference.

3. **Verification** — Reads back the container config to confirm the image was applied.

## Notes

- PIM activation grants the role for 1 hour by default.
- The script uses `set -euo pipefail` for strict error handling.
- All output is timestamped for easy log correlation.
