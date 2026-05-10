// Container Apps Environment, Container App for the API, and a Container Apps Job
// for the future poller. Both compute resources start with a placeholder image so
// the environment is reachable before we have a real SomaCore.Api container.

@description('Azure region.')
param location string

@description('Container Apps Environment name.')
param environmentName string

@description('Container App name (for the API).')
param apiAppName string

@description('Container Apps Job name (for the poller / token-refresh sweeper).')
param pollerJobName string

@description('Log Analytics customer ID.')
param logAnalyticsCustomerId string

@description('Log Analytics shared key.')
@secure()
param logAnalyticsSharedKey string

@description('Application Insights connection string.')
param appInsightsConnectionString string

@description('User-assigned managed identity resource ID.')
param uamiId string

@description('User-assigned managed identity client ID (for Azure SDK auth).')
param uamiClientId string

@description('ACR login server (e.g., somacoredevacr.azurecr.io).')
param acrLoginServer string

@description('Image reference for the API container. Default is the placeholder.')
param apiImage string = 'mcr.microsoft.com/azuredocs/aci-helloworld:latest'

@description('Image reference for the poller job. Default is the placeholder.')
param pollerImage string = 'mcr.microsoft.com/azuredocs/aci-helloworld:latest'

@description('Container ingress target port. .NET 9 ASP.NET Core listens on 8080 inside containers; the placeholder image listens on 80.')
param targetPort int = 8080

@description('Custom hostname to bind to the Container App ingress (e.g., app-dev.tento100.com). Empty = no custom hostname.')
param customHostname string = ''

@description('Resource ID of the managed certificate that authorizes the custom hostname. Required when customHostname is set; obtain via the one-time `az containerapp hostname bind` flow.')
param customHostnameCertificateId string = ''

@description('Key Vault URI (https://<name>.vault.azure.net/), used to compose KV secret references.')
param keyVaultUri string = ''

@description('AzureAd config for OIDC sign-in. Leave defaults if KV bindings or env vars not yet ready.')
param azureAdInstance string = environment().authentication.loginEndpoint
param azureAdDomain string = ''
param azureAdTenantId string = ''
param azureAdClientId string = ''
param azureAdCallbackPath string = '/signin-oidc'

@description('Whether to bind KV-backed app secrets (postgres connection string, web/whoop client secrets) onto the Container App. Set true once KV secrets are populated and the image actually consumes them.')
param wireKeyVaultSecrets bool = false

// Secrets the Container App holds, sourced from Key Vault via the UAMI.
var kvSecrets = wireKeyVaultSecrets ? [
  {
    name: 'postgres-connection-string'
    keyVaultUrl: '${keyVaultUri}secrets/postgres-connection-string'
    identity: uamiId
  }
  {
    name: 'web-client-secret'
    keyVaultUrl: '${keyVaultUri}secrets/web-client-secret'
    identity: uamiId
  }
  {
    name: 'whoop-client-id'
    keyVaultUrl: '${keyVaultUri}secrets/whoop-client-id'
    identity: uamiId
  }
  {
    name: 'whoop-client-secret'
    keyVaultUrl: '${keyVaultUri}secrets/whoop-client-secret'
    identity: uamiId
  }
] : []

var staticEnv = [
  {
    name: 'APPLICATIONINSIGHTS_CONNECTION_STRING'
    value: appInsightsConnectionString
  }
  {
    name: 'AZURE_CLIENT_ID'
    value: uamiClientId
  }
  {
    name: 'AzureAd__Instance'
    value: azureAdInstance
  }
  {
    name: 'AzureAd__Domain'
    value: azureAdDomain
  }
  {
    name: 'AzureAd__TenantId'
    value: azureAdTenantId
  }
  {
    name: 'AzureAd__ClientId'
    value: azureAdClientId
  }
  {
    name: 'AzureAd__CallbackPath'
    value: azureAdCallbackPath
  }
]

var kvEnv = wireKeyVaultSecrets ? [
  {
    name: 'ConnectionStrings__Postgres'
    secretRef: 'postgres-connection-string'
  }
  {
    name: 'AzureAd__ClientSecret'
    secretRef: 'web-client-secret'
  }
  {
    name: 'Whoop__ClientId'
    secretRef: 'whoop-client-id'
  }
  {
    name: 'Whoop__ClientSecret'
    secretRef: 'whoop-client-secret'
  }
] : []

resource env 'Microsoft.App/managedEnvironments@2024-03-01' = {
  name: environmentName
  location: location
  properties: {
    appLogsConfiguration: {
      destination: 'log-analytics'
      logAnalyticsConfiguration: {
        customerId: logAnalyticsCustomerId
        sharedKey: logAnalyticsSharedKey
      }
    }
    workloadProfiles: [
      {
        name: 'Consumption'
        workloadProfileType: 'Consumption'
      }
    ]
    zoneRedundant: false
  }
}

resource apiApp 'Microsoft.App/containerApps@2024-03-01' = {
  name: apiAppName
  location: location
  identity: {
    type: 'UserAssigned'
    userAssignedIdentities: {
      '${uamiId}': {}
    }
  }
  properties: {
    environmentId: env.id
    workloadProfileName: 'Consumption'
    configuration: {
      activeRevisionsMode: 'Single'
      ingress: {
        external: true
        targetPort: targetPort
        allowInsecure: false
        transport: 'auto'
        traffic: [
          {
            weight: 100
            latestRevision: true
          }
        ]
        customDomains: empty(customHostname) ? [] : [
          {
            name: customHostname
            bindingType: 'SniEnabled'
            certificateId: customHostnameCertificateId
          }
        ]
      }
      registries: [
        {
          server: acrLoginServer
          identity: uamiId
        }
      ]
      secrets: kvSecrets
    }
    template: {
      containers: [
        {
          name: 'api'
          image: apiImage
          resources: {
            cpu: json('0.25')
            memory: '0.5Gi'
          }
          env: concat(staticEnv, kvEnv)
        }
      ]
      scale: {
        minReplicas: 0
        maxReplicas: 3
      }
    }
  }
}

resource pollerJob 'Microsoft.App/jobs@2024-03-01' = {
  name: pollerJobName
  location: location
  identity: {
    type: 'UserAssigned'
    userAssignedIdentities: {
      '${uamiId}': {}
    }
  }
  properties: {
    environmentId: env.id
    workloadProfileName: 'Consumption'
    configuration: {
      replicaTimeout: 1800
      replicaRetryLimit: 1
      triggerType: 'Manual'
      manualTriggerConfig: {
        replicaCompletionCount: 1
        parallelism: 1
      }
      registries: [
        {
          server: acrLoginServer
          identity: uamiId
        }
      ]
      secrets: kvSecrets
    }
    template: {
      containers: [
        {
          name: 'poller'
          image: pollerImage
          resources: {
            cpu: json('0.25')
            memory: '0.5Gi'
          }
          env: concat(staticEnv, kvEnv)
        }
      ]
    }
  }
}

output environmentId string = env.id
output apiFqdn string = apiApp.properties.configuration.ingress.fqdn
output apiAppName string = apiApp.name
output pollerJobName string = pollerJob.name
