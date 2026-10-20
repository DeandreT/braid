// PostgreSQL Flexible Server with private (VNet-integrated) access only. The `weft` database
// and its restricted roles are created by the provisioning job, not by this template.
param location string
param name string
param administratorLogin string
@secure()
param administratorPassword string
param skuName string
param skuTier string
param storageSizeGB int
param delegatedSubnetId string
param privateDnsZoneId string
param tags object

resource server 'Microsoft.DBforPostgreSQL/flexibleServers@2024-08-01' = {
  name: name
  location: location
  tags: tags
  sku: {
    name: skuName
    tier: skuTier
  }
  properties: {
    // Weft migrations are qualified against PostgreSQL 16.
    version: '16'
    administratorLogin: administratorLogin
    administratorLoginPassword: administratorPassword
    authConfig: {
      activeDirectoryAuth: 'Disabled'
      passwordAuth: 'Enabled'
    }
    storage: {
      storageSizeGB: storageSizeGB
      autoGrow: 'Enabled'
    }
    backup: {
      backupRetentionDays: 14
      geoRedundantBackup: 'Disabled'
    }
    highAvailability: {
      mode: 'Disabled'
    }
    network: {
      delegatedSubnetResourceId: delegatedSubnetId
      privateDnsZoneArmResourceId: privateDnsZoneId
      publicNetworkAccess: 'Disabled'
    }
  }
}

output host string = server.properties.fullyQualifiedDomainName
