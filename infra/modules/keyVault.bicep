// Key Vault in RBAC mode. Two role assignments:
//   - the somacoredev group gets Key Vault Secrets User (read at runtime, debug)
//   - the user-assigned managed identity gets Key Vault Secrets User (Container App pulls secrets)
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

// Built-in role: Key Vault Secrets User
// 4633458b-17de-408a-b874-0445c86b69e6
var kvSecretsUserRoleId = '4633458b-17de-408a-b874-0445c86b69e6'

resource kvAccessForGroup 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(vault.id, somacoreDevGroupObjectId, kvSecretsUserRoleId)
  scope: vault
  properties: {
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', kvSecretsUserRoleId)
    principalId: somacoreDevGroupObjectId
    principalType: 'Group'
  }
}

resource kvAccessForUami 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(vault.id, uamiPrincipalId, kvSecretsUserRoleId)
  scope: vault
  properties: {
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', kvSecretsUserRoleId)
    principalId: uamiPrincipalId
    principalType: 'ServicePrincipal'
  }
}

output id string = vault.id
output name string = vault.name
output uri string = vault.properties.vaultUri
