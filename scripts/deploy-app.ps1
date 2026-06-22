# scripts/deploy-app.ps1
#
# Fast app rollout. Builds the SomaCore.Api image in ACR and updates the
# Container App's image to the new tag. Targets ONLY the Container App
# resource — does not touch Postgres, Key Vault, or anything else in the
# Bicep template.
#
# Use this for code, UI, copy, or Dockerfile changes — anything that doesn't
# change the resource graph in infra/main.bicep.
#
# For changes that DO touch infra (new env var, new role binding, new
# resource, Postgres config, etc.), use scripts/deploy-infra.ps1 instead.

[CmdletBinding()]
param(
    [string]$ResourceGroup  = 'somacore-dev-rg',
    [string]$Registry       = 'somacoredevacr',
    [string]$AppName        = 'somacore-api',
    [string]$DockerfilePath = 'src/SomaCore.Api/Dockerfile',
    [string]$ContextPath    = '.',
    [string]$Tag            = $(git rev-parse --short HEAD)
)

$ErrorActionPreference = 'Stop'

if ([string]::IsNullOrWhiteSpace($Tag)) {
    throw "No image tag provided and `git rev-parse --short HEAD` returned nothing."
}

Write-Host "=== Building $AppName`:$Tag in $Registry ===" -ForegroundColor Cyan
az acr build `
    --registry  $Registry `
    --image     "$AppName`:$Tag" `
    --image     "$AppName`:latest" `
    --file      $DockerfilePath `
    $ContextPath 2>&1 | Select-Object -Last 5
# az is a native exe — failures set $LASTEXITCODE but do NOT throw, even with
# $ErrorActionPreference='Stop'. If we skipped this check the script would
# happily proceed to roll the Container App to a phantom image tag and then
# claim success. Hard-fail here.
if ($LASTEXITCODE -ne 0) {
    throw "az acr build failed (exit code $LASTEXITCODE) — image was NOT pushed. Aborting."
}

# Verify the manifest is actually in ACR before we point the revision at it.
# Belt-and-braces: catches the case where the build returned 0 but the push
# was silently dropped (we hit this exact mode in 2026-06 due to an MCR pull
# failure mid-build that the acr build command swallowed).
# Avoid JMESPath subscript syntax ([0]) in the --query arg — PowerShell on
# Windows interprets the brackets as a wildcard glob before az ever sees
# them. Fetch the full tag list and check membership in PS instead.
$allTags = (az acr repository show-tags --name $Registry --repository $AppName -o tsv) -split "`r?`n"
if ($allTags -notcontains $Tag) {
    throw "Tag '$Tag' not found in $Registry/$AppName after build. Aborting before update."
}

$image = "$Registry.azurecr.io/$AppName`:$Tag"

Write-Host "`n=== Rolling Container App $AppName -> $image ===" -ForegroundColor Cyan
az containerapp update `
    --resource-group $ResourceGroup `
    --name           $AppName `
    --image          $image `
    --query "{revision:properties.latestRevisionName, state:properties.provisioningState}" `
    -o table
if ($LASTEXITCODE -ne 0) {
    throw "az containerapp update failed (exit code $LASTEXITCODE)."
}

Write-Host "`nDeployed $image" -ForegroundColor Green
Write-Host "Health: " -NoNewline
$apiBase = az containerapp show -g $ResourceGroup -n $AppName --query "properties.configuration.ingress.fqdn" -o tsv
try {
    Start-Sleep -Seconds 5
    $r = Invoke-WebRequest -Uri "https://$apiBase/admin/health/live" -UseBasicParsing -TimeoutSec 30
    Write-Host "$($r.StatusCode) — $($r.Content)" -ForegroundColor Green
} catch {
    Write-Host "$($_.Exception.Message)" -ForegroundColor Yellow
}
