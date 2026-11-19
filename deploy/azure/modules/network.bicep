// Virtual network, delegated subnets and the private DNS zones resolved inside it.
param location string
param name string
param databaseServerName string
param addressSpace string
param appsSubnetPrefix string
param databaseSubnetPrefix string
param endpointsSubnetPrefix string
param serviceBusDnsEnabled bool = false
param tags object

resource network 'Microsoft.Network/virtualNetworks@2024-05-01' = {
  name: name
  location: location
  tags: tags
  properties: {
    addressSpace: {
      addressPrefixes: [addressSpace]
    }
    subnets: [
      {
        name: 'apps'
        properties: {
          addressPrefix: appsSubnetPrefix
          delegations: [
            {
              name: 'container-apps'
              properties: {
                serviceName: 'Microsoft.App/environments'
              }
            }
          ]
        }
      }
      {
        name: 'database'
        properties: {
          addressPrefix: databaseSubnetPrefix
          delegations: [
            {
              name: 'postgres'
              properties: {
                serviceName: 'Microsoft.DBforPostgreSQL/flexibleServers'
              }
            }
          ]
        }
      }
      {
        name: 'endpoints'
        properties: {
          addressPrefix: endpointsSubnetPrefix
          privateEndpointNetworkPolicies: 'Disabled'
        }
      }
    ]
  }
}

// Flexible Server private access needs a zone ending in .postgres.database.azure.com.
var zoneNames = concat([
  '${databaseServerName}.private.postgres.database.azure.com'
  'privatelink.vaultcore.azure.net'
  'privatelink.file.${environment().suffixes.storage}'
  'privatelink.blob.${environment().suffixes.storage}'
], serviceBusDnsEnabled ? ['privatelink.servicebus.windows.net'] : [])

resource zones 'Microsoft.Network/privateDnsZones@2024-06-01' = [for zone in zoneNames: {
  name: zone
  location: 'global'
  tags: tags
}]

resource links 'Microsoft.Network/privateDnsZones/virtualNetworkLinks@2024-06-01' = [for (zone, index) in zoneNames: {
  parent: zones[index]
  name: '${name}-link'
  location: 'global'
  tags: tags
  properties: {
    registrationEnabled: false
    virtualNetwork: {
      id: network.id
    }
  }
}]

output appsSubnetId string = network.properties.subnets[0].id
output databaseSubnetId string = network.properties.subnets[1].id
output endpointsSubnetId string = network.properties.subnets[2].id
output databaseZoneId string = zones[0].id
output vaultZoneId string = zones[1].id
output fileZoneId string = zones[2].id
output blobZoneId string = zones[3].id
output serviceBusZoneId string = serviceBusDnsEnabled ? zones[4].id : ''
