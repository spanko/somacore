// User-assigned managed identity used by the Container App and the Container Apps Job
// to pull from ACR and read secrets from Key Vault.

@description('Azure region.')
param location string

@description('Managed identity name.')
param identityName string

resource uami 'Microsoft.ManagedIdentity/userAssignedIdentities@2023-01-31' = {
  name: identityName
  location: location
}

output id string = uami.id
output principalId string = uami.properties.principalId
output clientId string = uami.properties.clientId
output name string = uami.name
