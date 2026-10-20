// VNet-integrated Container Apps environment with the key-ring share registered as storage.
param location string
param name string
param infrastructureSubnetId string
param logAnalyticsWorkspaceName string
param storageAccountName string
param keyRingShareName string
param tags object

resource workspace 'Microsoft.OperationalInsights/workspaces@2023-09-01' existing = {
  name: logAnalyticsWorkspaceName
}

resource account 'Microsoft.Storage/storageAccounts@2023-05-01' existing = {
  name: storageAccountName
}

resource environment 'Microsoft.App/managedEnvironments@2024-03-01' = {
  name: name
  location: location
  tags: tags
  properties: {
    workloadProfiles: [
      {
        name: 'Consumption'
        workloadProfileType: 'Consumption'
      }
    ]
    vnetConfiguration: {
      infrastructureSubnetId: infrastructureSubnetId
      internal: false
    }
    appLogsConfiguration: {
      destination: 'log-analytics'
      logAnalyticsConfiguration: {
        customerId: workspace.properties.customerId
        sharedKey: workspace.listKeys().primarySharedKey
      }
    }
  }
}

resource keyRing 'Microsoft.App/managedEnvironments/storages@2024-03-01' = {
  parent: environment
  name: 'session-keys'
  properties: {
    azureFile: {
      accountName: account.name
      accountKey: account.listKeys().keys[0].value
      shareName: keyRingShareName
      accessMode: 'ReadWrite'
    }
  }
}

output name string = environment.name
output defaultDomain string = environment.properties.defaultDomain
output customDomainVerificationId string = environment.properties.customDomainConfiguration.customDomainVerificationId
output keyRingStorageName string = keyRing.name
