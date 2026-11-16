// Private storage account with the SMB share that persists the session key ring.
// Container Apps mounts it with the environment storage's account key; the account has no
// public network access and is reached through its private endpoint.
param location string
param name string
param endpointsSubnetId string
param privateDnsZoneId string
param tags object

resource account 'Microsoft.Storage/storageAccounts@2023-05-01' = {
  name: name
  location: location
  tags: tags
  kind: 'StorageV2'
  sku: {
    name: 'Standard_LRS'
  }
  properties: {
    accessTier: 'Hot'
    allowBlobPublicAccess: false
    allowSharedKeyAccess: true
    minimumTlsVersion: 'TLS1_2'
    supportsHttpsTrafficOnly: true
    publicNetworkAccess: 'Disabled'
    networkAcls: {
      bypass: 'None'
      defaultAction: 'Deny'
    }
  }
}

resource files 'Microsoft.Storage/storageAccounts/fileServices@2023-05-01' = {
  parent: account
  name: 'default'
  properties: {
    shareDeleteRetentionPolicy: {
      enabled: true
      days: 14
    }
  }
}

resource keyRing 'Microsoft.Storage/storageAccounts/fileServices/shares@2023-05-01' = {
  parent: files
  name: 'braid-session-keys'
  properties: {
    enabledProtocols: 'SMB'
    shareQuota: 1
  }
}

resource endpoint 'Microsoft.Network/privateEndpoints@2024-05-01' = {
  name: '${name}-file-endpoint'
  location: location
  tags: tags
  properties: {
    subnet: {
      id: endpointsSubnetId
    }
    privateLinkServiceConnections: [
      {
        name: 'file'
        properties: {
          privateLinkServiceId: account.id
          groupIds: ['file']
        }
      }
    ]
  }
}

resource endpointDns 'Microsoft.Network/privateEndpoints/privateDnsZoneGroups@2024-05-01' = {
  parent: endpoint
  name: 'default'
  properties: {
    privateDnsZoneConfigs: [
      {
        name: 'file'
        properties: {
          privateDnsZoneId: privateDnsZoneId
        }
      }
    ]
  }
}

output name string = account.name
output keyRingShareName string = keyRing.name
