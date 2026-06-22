# scripts/deploy-jobs.ps1
#
# Mirror of deploy-app.ps1 for the IngestionJobs binary. Builds the
# SomaCore.IngestionJobs image in ACR and updates the Container Apps Job's
# image to the new tag. Targets ONLY the somacore-poller job — does not touch
# Postgres, Key Vault, or the API Container App.
#
# Use this for any change under src/SomaCore.IngestionJobs/ or shared code
# under src/SomaCore.Infrastructure/ + src/SomaCore.Domain/ that the poller
# depends on. The API and the poller share Infrastructure + Domain, so most
# substantive changes need BOTH this script and deploy-app.ps1 run.
#
# Container Apps Jobs caches the image at the job-resource level (not
# per-execution); updating the image with `az containerapp job update`
# applies to the next scheduled or manually-triggered execution.

[CmdletBinding()]
param(
    [string]$ResourceGroup  = 'somacore-dev-rg',
    [string]$Registry       = 'somacoredevacr',
    [string]$JobName        = 'somacore-poller',
    [string]$ImageName      = 'somacore-jobs',
    [string]$DockerfilePath = 'src/SomaCore.IngestionJobs/Dockerfile',
    [string]$ContextPath    = '.',
    [string]$Tag            = $(git rev-parse --short HEAD)
)

$ErrorActionPreference = 'Stop'

if ([string]::IsNullOrWhiteSpace($Tag)) {
    throw "No image tag provided and `git rev-parse --short HEAD` returned nothing."
}

Write-Host "=== Building $ImageName`:$Tag in $Registry ===" -ForegroundColor Cyan
az acr build `
    --registry  $Registry `
    --image     "$ImageName`:$Tag" `
    --image     "$ImageName`:latest" `
    --file      $DockerfilePath `
    $ContextPath 2>&1 | Select-Object -Last 5
# az is a native exe — failures set $LASTEXITCODE but do NOT throw, even with
# $ErrorActionPreference='Stop'. Without this check the script would happily
# proceed to point the Job at a phantom image tag and report success.
if ($LASTEXITCODE -ne 0) {
    throw "az acr build failed (exit code $LASTEXITCODE) — image was NOT pushed. Aborting."
}

# Verify the manifest is actually in ACR before pointing the Job at it.
$tagExists = az acr repository show-tags --name $Registry --repository $ImageName --query "[?@=='$Tag']|[0]" -o tsv
if (-not $tagExists) {
    throw "Tag '$Tag' not found in $Registry/$ImageName after build. Aborting before update."
}

$image = "$Registry.azurecr.io/$ImageName`:$Tag"

Write-Host "`n=== Updating Container Apps Job $JobName -> $image ===" -ForegroundColor Cyan
az containerapp job update `
    --resource-group $ResourceGroup `
    --name           $JobName `
    --image          $image `
    --query "{name:name, image:properties.template.containers[0].image}" `
    -o table
if ($LASTEXITCODE -ne 0) {
    throw "az containerapp job update failed (exit code $LASTEXITCODE)."
}

Write-Host "`nDeployed $image" -ForegroundColor Green
Write-Host "Next scheduled execution will use this image." -ForegroundColor Green
Write-Host "To trigger an immediate test run:" -ForegroundColor DarkGray
Write-Host "  az containerapp job start -g $ResourceGroup -n $JobName" -ForegroundColor DarkGray
