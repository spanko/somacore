// Postgres Flexible Server (Burstable B1ms). Public access enabled with a firewall
// rule allowing all Azure-internal services so the Container App can connect.
// Database `somacore` created; admin password seeded into Key Vault by main.bicep.

@description('Azure region.')
param location string

@description('Postgres server name (also FQDN prefix).')
param serverName string

@description('Database name on the server.')
param databaseName string = 'somacore'

@description('Admin username for password auth.')
param adminUsername string

@description('Admin password. Generated at deploy time and passed via @secure() parameter.')
@secure()
param adminPassword string

@description('Server SKU.')
param skuName string = 'Standard_B1ms'

@description('Compute tier.')
param tier string = 'Burstable'

@description('Storage size in GB. 32 is the minimum.')
param storageSizeGB int = 32

@description('Postgres version.')
param version string = '16'

@description('Backup retention in days. 7 is the minimum.')
@minValue(7)
@maxValue(35)
param backupRetentionDays int = 7

resource server 'Microsoft.DBforPostgreSQL/flexibleServers@2024-08-01' = {
  name: serverName
  location: location
  sku: {
    name: skuName
    tier: tier
  }
  properties: {
    version: version
    administratorLogin: adminUsername
    administratorLoginPassword: adminPassword
    storage: {
      storageSizeGB: storageSizeGB
      autoGrow: 'Enabled'
      tier: 'P4'
    }
    backup: {
      backupRetentionDays: backupRetentionDays
      geoRedundantBackup: 'Disabled'
    }
    highAvailability: {
      mode: 'Disabled'
    }
    network: {
      publicNetworkAccess: 'Enabled'
    }
    authConfig: {
      activeDirectoryAuth: 'Disabled'
      passwordAuth: 'Enabled'
    }
  }
}

// Allow-list Postgres extensions our schema needs. azure.extensions is a server
// parameter that controls which extensions CREATE EXTENSION can target. We need
// pgcrypto today for the schema's gen_random_uuid() fallback (the actual UUID v7
// generation happens in C# via Guid.CreateVersion7()).
resource extensionsConfig 'Microsoft.DBforPostgreSQL/flexibleServers/configurations@2024-08-01' = {
  parent: server
  name: 'azure.extensions'
  properties: {
    value: 'PGCRYPTO'
    source: 'user-override'
  }
}

resource db 'Microsoft.DBforPostgreSQL/flexibleServers/databases@2024-08-01' = {
  parent: server
  name: databaseName
  properties: {
    charset: 'UTF8'
    collation: 'en_US.utf8'
  }
}

// Allow Azure-internal services (Container App, Container Apps Job) to reach the server.
// 0.0.0.0/0.0.0.0 is the documented marker for "AllowAllAzureIps" on Flex Server.
resource allowAzure 'Microsoft.DBforPostgreSQL/flexibleServers/firewallRules@2024-08-01' = {
  parent: server
  name: 'AllowAllAzureServices'
  properties: {
    startIpAddress: '0.0.0.0'
    endIpAddress: '0.0.0.0'
  }
}

output id string = server.id
output name string = server.name
output fqdn string = server.properties.fullyQualifiedDomainName
output databaseName string = db.name
