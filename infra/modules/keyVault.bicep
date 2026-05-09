// Key Vault in RBAC mode. Two role assignments:
//   - the somacoredev group gets Key Vault Secrets Officer (read + write so
//     operators can seed secrets, rotate WHOOP/Web client secrets, etc.)
//   - the user-assigned managed identity gets Key Vault Secrets Officer
//     (the Container App reads secrets at runtime; the token-refresh sweeper
//     writes new WHOOP refresh tokens back to the vault per ADR 0007)
//
// Phase-1 trade-off: both principals are over-privileged for pure read paths.
// Acceptable at three internal users; revisit with custom RBAC for prod.
//
// Secrets are populated post-deploy via az keyvault secret set. Bicep does not
// know any secret values.

@description('Azure region.')
param location string

@description('Key Vault name. 3-24 chars, alphanumeric and hyphens, must start with a letter.')
param vaultName string

@description('Object ID of the somacoredev Entra group.')
param somacoreDevGroupObjectId string

@description('Principal ID of the user-assigned managed identity.')
param uamiPrincipalId string

@description('Soft-delete retention in days. 7 is the minimum; production should pick 90.')
@minValue(7)
@maxValue(90)
param softDeleteRetentionInDays int = 7

@description('Set to true once secrets seeded; flip via parameter file for prod.')
param enablePurgeProtection bool = false

resource vault 'Microsoft.KeyVault/vaults@2023-07-01' = {
  name: vaultName
  location: location
  properties: {
    sku: {
      family: 'A'
      name: 'standard'
    }
    tenantId: tenant().tenantId
    enableRbacAuthorization: true
    enableSoftDelete: true
    softDeleteRetentionInDays: softDeleteRetentionInDays
    enablePurgeProtection: enablePurgeProtection ? true : null
    publicNetworkAccess: 'Enabled'
    networkAcls: {
      defaultAction: 'Allow'
      bypass: 'AzureServices'
    }
  }
}

// Built-in role: Key Vault Secrets Officer (read + write secrets)
// b86a8fe4-44ce-4948-aee5-eccb2c155cd7
var kvSecretsOfficerRoleId = 'b86a8fe4-44ce-4948-aee5-eccb2c155cd7'

resource kvAccessForGroup 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(vault.id, somacoreDevGroupObjectId, kvSecretsOfficerRoleId)
  scope: vault
  properties: {
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', kvSecretsOfficerRoleId)
    principalId: somacoreDevGroupObjectId
    principalType: 'Group'
  }
}

resource kvAccessForUami 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(vault.id, uamiPrincipalId, kvSecretsOfficerRoleId)
  scope: vault
  properties: {
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', kvSecretsOfficerRoleId)
    principalId: uamiPrincipalId
    principalType: 'ServicePrincipal'
  }
}

output id string = vault.id
output name string = vault.name
output uri string = vault.properties.vaultUri
