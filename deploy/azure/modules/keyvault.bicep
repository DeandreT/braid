// Key Vault for database credentials and certificates. RBAC only; the application identity
// reads secrets, and the optional operator principal initializes them.
param location string
param name string
param readerPrincipalId string
param operatorPrincipalId string
param operatorIpRanges array
param endpointsSubnetId string
param privateDnsZoneId string
@secure()
param postgresAdministratorPassword string
param tags object

resource vault 'Microsoft.KeyVault/vaults@2023-07-01' = {
  name: name
  location: location
  tags: tags
  properties: {
    tenantId: subscription().tenantId
    sku: {
      family: 'A'
      name: 'standard'
    }
    enableRbacAuthorization: true
    enableSoftDelete: true
    softDeleteRetentionInDays: 90
    enablePurgeProtection: true
    publicNetworkAccess: 'Enabled'
    networkAcls: {
      bypass: 'None'
      defaultAction: empty(operatorIpRanges) ? 'Allow' : 'Deny'
      ipRules: [for range in operatorIpRanges: {
        value: range
      }]
    }
  }
}

// Used only by the database provisioning job.
resource administratorPassword 'Microsoft.KeyVault/vaults/secrets@2023-07-01' = {
  parent: vault
  name: 'postgres-admin-password'
  properties: {
    value: postgresAdministratorPassword
    contentType: 'text/plain'
  }
}

resource endpoint 'Microsoft.Network/privateEndpoints@2024-05-01' = {
  name: '${name}-endpoint'
  location: location
  tags: tags
  properties: {
    subnet: {
      id: endpointsSubnetId
    }
    privateLinkServiceConnections: [
      {
        name: 'vault'
        properties: {
          privateLinkServiceId: vault.id
          groupIds: ['vault']
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
        name: 'vault'
        properties: {
          privateDnsZoneId: privateDnsZoneId
        }
      }
    ]
  }
}

var secretsUser = subscriptionResourceId('Microsoft.Authorization/roleDefinitions', '4633458b-17de-408a-b874-0445c86b69e6')
var secretsOfficer = subscriptionResourceId('Microsoft.Authorization/roleDefinitions', 'b86a8fe4-44ce-4948-aee5-eccb2c155cd7')
var certificatesOfficer = subscriptionResourceId('Microsoft.Authorization/roleDefinitions', 'a4417e6f-fecd-4de8-b567-7b0420556985')

resource reader 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(vault.id, readerPrincipalId, secretsUser)
  scope: vault
  properties: {
    roleDefinitionId: secretsUser
    principalId: readerPrincipalId
    principalType: 'ServicePrincipal'
  }
}

resource operatorRoles 'Microsoft.Authorization/roleAssignments@2022-04-01' = [for role in (empty(operatorPrincipalId) ? [] : [secretsOfficer, certificatesOfficer]): {
  name: guid(vault.id, operatorPrincipalId, role)
  scope: vault
  properties: {
    roleDefinitionId: role
    principalId: operatorPrincipalId
  }
}]

output uri string = vault.properties.vaultUri
