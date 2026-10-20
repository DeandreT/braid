// Private image registry; the application identity may only pull.
param location string
param name string
param pullPrincipalId string
param tags object

resource registry 'Microsoft.ContainerRegistry/registries@2023-07-01' = {
  name: name
  location: location
  tags: tags
  sku: {
    name: 'Basic'
  }
  properties: {
    adminUserEnabled: false
  }
}

var acrPull = subscriptionResourceId('Microsoft.Authorization/roleDefinitions', '7f951dda-4ed3-4680-a7ca-43fe172d538d')

resource pull 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(registry.id, pullPrincipalId, acrPull)
  scope: registry
  properties: {
    roleDefinitionId: acrPull
    principalId: pullPrincipalId
    principalType: 'ServicePrincipal'
  }
}

output loginServer string = registry.properties.loginServer
