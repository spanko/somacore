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

@description('ACR login server (e.g., somacoredevacr.azurecr.io). Used so the registries property is wired up even before we push a real image.')
param acrLoginServer string

@description('Placeholder image to deploy until the real SomaCore.Api image exists.')
param placeholderImage string = 'mcr.microsoft.com/azuredocs/aci-helloworld:latest'

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
        targetPort: 80
        allowInsecure: false
        transport: 'auto'
        traffic: [
          {
            weight: 100
            latestRevision: true
          }
        ]
      }
      registries: [
        {
          server: acrLoginServer
          identity: uamiId
        }
      ]
    }
    template: {
      containers: [
        {
          name: 'api'
          image: placeholderImage
          resources: {
            cpu: json('0.25')
            memory: '0.5Gi'
          }
          env: [
            {
              name: 'APPLICATIONINSIGHTS_CONNECTION_STRING'
              value: appInsightsConnectionString
            }
            {
              name: 'AZURE_CLIENT_ID'
              value: uamiClientId
            }
          ]
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
    }
    template: {
      containers: [
        {
          name: 'poller'
          image: placeholderImage
          resources: {
            cpu: json('0.25')
            memory: '0.5Gi'
          }
          env: [
            {
              name: 'APPLICATIONINSIGHTS_CONNECTION_STRING'
              value: appInsightsConnectionString
            }
            {
              name: 'AZURE_CLIENT_ID'
              value: uamiClientId
            }
          ]
        }
      ]
    }
  }
}

output environmentId string = env.id
output apiFqdn string = apiApp.properties.configuration.ingress.fqdn
output apiAppName string = apiApp.name
output pollerJobName string = pollerJob.name
