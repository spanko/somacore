# scripts/deploy-infra.ps1
#
# Full infrastructure rollout via Bicep. Run this when something in infra/
# has changed (new resource, role binding, env var schema, Postgres config,
# etc.). For app-only changes (code/UI/copy/Dockerfile), use deploy-app.ps1.
#
# Preserves state we don't want this run to mutate:
#   - Reads the Postgres admin password from Key Vault so a re-run doesn't
#     rotate the secret.
#   - Reads the currently-deployed Container App image so the deploy doesn't
#     accidentally roll the API back to the placeholder.
#
# Wraps `az deployment group create` with a single retry on the Postgres
# `ServerIsBusy` 409 (which Postgres Flex returns when the server is mid-
# operation; usually clears in 30-60 seconds).

[CmdletBinding()]
param(
    [string]$ResourceGroup = 'somacore-dev-rg',
    [string]$BicepFile     = 'infra/main.bicep',
    [string]$ParameterFile = 'infra/parameters.dev.json',
    [string]$VaultName     = 'somacore-dev-kv',
    [string]$AppName       = 'somacore-api',
    [switch]$WhatIf
)

$ErrorActionPreference = 'Stop'

Write-Host "=== Reading current state to preserve ===" -ForegroundColor Cyan

$pgPass = az keyvault secret show --vault-name $VaultName --name postgres-admin-password --query value -o tsv
if (-not $pgPass) { throw "Couldn't read postgres-admin-password from $VaultName." }
Write-Host "  postgres-admin-password: <length $($pgPass.Length)>"

$currentImage = az containerapp show `
    --resource-group $ResourceGroup `
    --name           $AppName `
    --query "properties.template.containers[0].image" -o tsv
if (-not $currentImage) { throw "Couldn't read current image from Container App $AppName." }
Write-Host "  current image:           $currentImage"

$paramOverrides = @(
    "postgresAdminPassword=$pgPass"
    "apiImage=$currentImage"
    'apiTargetPort=8080'
    'wireKeyVaultSecrets=true'
)

if ($WhatIf) {
    Write-Host "`n=== Running what-if ===" -ForegroundColor Cyan
    az deployment group what-if `
        --resource-group  $ResourceGroup `
        --template-file   $BicepFile `
        --parameters      $ParameterFile `
        --parameters      $paramOverrides `
        --result-format   ResourceIdOnly
    return
}

function Invoke-Deploy {
    $name = "somacore-dev-infra-$(Get-Date -Format 'yyyyMMdd-HHmmss')"
    az deployment group create `
        --resource-group  $ResourceGroup `
        --name            $name `
        --template-file   $BicepFile `
        --parameters      $ParameterFile `
        --parameters      $paramOverrides `
        --query "{state:properties.provisioningState, duration:properties.duration}" `
        -o table
    return $LASTEXITCODE
}

Write-Host "`n=== Deploying ===" -ForegroundColor Cyan
$rc = Invoke-Deploy
if ($rc -ne 0) {
    Write-Host "`nFirst attempt failed; checking whether to retry..." -ForegroundColor Yellow
    # Most common transient is Postgres ServerIsBusy. Wait, then retry once.
    Start-Sleep -Seconds 60
    Write-Host "=== Retrying ===" -ForegroundColor Cyan
    $rc = Invoke-Deploy
}

if ($rc -ne 0) {
    throw "Deploy failed twice; check the deployment operations in the portal."
}
Write-Host "`nDone." -ForegroundColor Green
