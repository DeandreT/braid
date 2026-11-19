// Trigger infrastructure for the mapping endpoints that host triggers: one dedicated user-assigned identity,
// a private Service Bus namespace with the configured queues and topics, and a private identity-only storage
// account with the configured source and output containers. Each data role is granted to the trigger identity
// only, at the narrowest scope: receive on each queue, send on each topic, read on each source container and
// write on each output container. Nothing here is reachable from a public network.
// See docs/azure-deployment.md#triggers.
param location string
param identityName string
param serviceBusName string
param storageName string
param endpointsSubnetId string
param serviceBusZoneId string
param blobZoneId string
@description('One target owns this identity, its registrations and all data-plane role grants.')
param trigger object
param tags object

var sources = trigger.?queues ?? []
var destinations = trigger.?destinations ?? []
var queueNames = union(map(filter(sources, item => item.?subscription == null), item => item.queueName), [])
var subscriptionSources = filter(sources, item => item.?subscription != null)
var subscriptions = union(map(subscriptionSources, item => { topic: item.queueName, name: item.subscription }), [])
var sendTopics = union(map(filter(destinations, item => item.kind == 'serviceBusTopic'), item => item.topicName), [])
var topicNames = union(sendTopics, map(subscriptions, item => item.topic))
var sourceContainers = union(map(trigger.?schedules ?? [], item => item.container), [])
var outputContainers = union(map(filter(destinations, item => item.kind == 'blob'), item => item.container), [])

var roles = {
  serviceBusReceiver: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', '4f6d3b9b-027b-4f4c-9142-0e5a2a2247e0')
  serviceBusSender: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', '69a216fc-b8fb-44d8-bc22-1f3c2cd27a39')
  blobReader: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', '2a2b9908-6ea1-4ae2-8e65-a410df84e7d1')
  blobContributor: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', 'ba92f5b4-2d11-453d-a403-e96b0029c9fe')
}

resource identity 'Microsoft.ManagedIdentity/userAssignedIdentities@2023-01-31' = {
  name: identityName
  location: location
  tags: tags
}

// Premium is the tier that supports private endpoints; shared-key (SAS) access is disabled.
resource namespace 'Microsoft.ServiceBus/namespaces@2024-01-01' = if (!empty(queueNames) || !empty(topicNames)) {
  name: serviceBusName
  location: location
  tags: tags
  sku: {
    name: 'Premium'
    tier: 'Premium'
    capacity: 1
  }
  properties: {
    disableLocalAuth: true
    minimumTlsVersion: '1.2'
    publicNetworkAccess: 'Disabled'
    zoneRedundant: false
  }
}

// A message that keeps failing is dead-lettered by the broker after five deliveries, never looped.
resource queues 'Microsoft.ServiceBus/namespaces/queues@2024-01-01' = [for queue in queueNames: {
  parent: namespace
  name: queue
  properties: {
    lockDuration: 'PT30S'
    maxDeliveryCount: 5
    deadLetteringOnMessageExpiration: false
    requiresSession: false
    requiresDuplicateDetection: false
  }
}]

resource topics 'Microsoft.ServiceBus/namespaces/topics@2024-01-01' = [for topic in topicNames: {
  parent: namespace
  name: topic
  properties: {
    requiresDuplicateDetection: false
  }
}]

resource receivers 'Microsoft.Authorization/roleAssignments@2022-04-01' = [for (queue, index) in queueNames: {
  name: guid(queues[index].id, identity.id, roles.serviceBusReceiver)
  scope: queues[index]
  properties: {
    roleDefinitionId: roles.serviceBusReceiver
    principalId: identity.properties.principalId
    principalType: 'ServicePrincipal'
  }
}]

resource topicSubscriptions 'Microsoft.ServiceBus/namespaces/topics/subscriptions@2024-01-01' = [for entry in subscriptions: {
  name: '${serviceBusName}/${entry.topic}/${entry.name}'
  properties: {
    lockDuration: 'PT30S'
    maxDeliveryCount: 5
    requiresSession: false
  }
  dependsOn: [topics]
}]

resource subscriptionReceivers 'Microsoft.Authorization/roleAssignments@2022-04-01' = [for (entry, index) in subscriptions: {
  name: guid(topicSubscriptions[index].id, identity.id, roles.serviceBusReceiver)
  scope: topicSubscriptions[index]
  properties: {
    roleDefinitionId: roles.serviceBusReceiver
    principalId: identity.properties.principalId
    principalType: 'ServicePrincipal'
  }
}]

resource senders 'Microsoft.Authorization/roleAssignments@2022-04-01' = [for topic in sendTopics: {
  name: guid(topics[indexOf(topicNames, topic)].id, identity.id, roles.serviceBusSender)
  scope: topics[indexOf(topicNames, topic)]
  properties: {
    roleDefinitionId: roles.serviceBusSender
    principalId: identity.properties.principalId
    principalType: 'ServicePrincipal'
  }
}]

resource serviceBusEndpoint 'Microsoft.Network/privateEndpoints@2024-05-01' = if (!empty(queueNames) || !empty(topicNames)) {
  name: '${serviceBusName}-endpoint'
  location: location
  tags: tags
  properties: {
    subnet: {
      id: endpointsSubnetId
    }
    privateLinkServiceConnections: [
      {
        name: 'namespace'
        properties: {
          privateLinkServiceId: namespace.id
          groupIds: ['namespace']
        }
      }
    ]
  }
}

resource serviceBusDns 'Microsoft.Network/privateEndpoints/privateDnsZoneGroups@2024-05-01' = if (!empty(queueNames) || !empty(topicNames)) {
  parent: serviceBusEndpoint
  name: 'default'
  properties: {
    privateDnsZoneConfigs: [
      {
        name: 'servicebus'
        properties: {
          privateDnsZoneId: serviceBusZoneId
        }
      }
    ]
  }
}

// Trigger inputs and outputs stay apart from Braid's private objects; identity-only data access.
resource account 'Microsoft.Storage/storageAccounts@2023-05-01' = if (!empty(sourceContainers) || !empty(outputContainers)) {
  name: storageName
  location: location
  tags: tags
  kind: 'StorageV2'
  sku: {
    name: 'Standard_LRS'
  }
  properties: {
    accessTier: 'Hot'
    allowBlobPublicAccess: false
    allowSharedKeyAccess: false
    allowCrossTenantReplication: false
    defaultToOAuthAuthentication: true
    isHnsEnabled: false
    minimumTlsVersion: 'TLS1_2'
    supportsHttpsTrafficOnly: true
    publicNetworkAccess: 'Disabled'
    networkAcls: {
      bypass: 'None'
      defaultAction: 'Deny'
    }
  }
}

resource blobs 'Microsoft.Storage/storageAccounts/blobServices@2023-05-01' = if (!empty(sourceContainers) || !empty(outputContainers)) {
  parent: account
  name: 'default'
  properties: {
    containerDeleteRetentionPolicy: {
      enabled: true
      days: 14
    }
  }
}

// A container read by one trigger and written by another is declared once.
var containerNames = union(sourceContainers, outputContainers)

resource containers 'Microsoft.Storage/storageAccounts/blobServices/containers@2023-05-01' = [for container in containerNames: {
  parent: blobs
  name: container
  properties: {
    publicAccess: 'None'
  }
}]

resource readers 'Microsoft.Authorization/roleAssignments@2022-04-01' = [for container in sourceContainers: {
  name: guid(containers[indexOf(containerNames, container)].id, identity.id, roles.blobReader)
  scope: containers[indexOf(containerNames, container)]
  properties: {
    roleDefinitionId: roles.blobReader
    principalId: identity.properties.principalId
    principalType: 'ServicePrincipal'
  }
}]

// Output writes are create-only in the destination, but no narrower built-in role grants blob creation.
resource writers 'Microsoft.Authorization/roleAssignments@2022-04-01' = [for container in outputContainers: {
  name: guid(containers[indexOf(containerNames, container)].id, identity.id, roles.blobContributor)
  scope: containers[indexOf(containerNames, container)]
  properties: {
    roleDefinitionId: roles.blobContributor
    principalId: identity.properties.principalId
    principalType: 'ServicePrincipal'
  }
}]

resource blobEndpoint 'Microsoft.Network/privateEndpoints@2024-05-01' = if (!empty(sourceContainers) || !empty(outputContainers)) {
  name: '${storageName}-blob-endpoint'
  location: location
  tags: tags
  properties: {
    subnet: {
      id: endpointsSubnetId
    }
    privateLinkServiceConnections: [
      {
        name: 'blob'
        properties: {
          privateLinkServiceId: account.id
          groupIds: ['blob']
        }
      }
    ]
  }
}

resource blobDns 'Microsoft.Network/privateEndpoints/privateDnsZoneGroups@2024-05-01' = if (!empty(sourceContainers) || !empty(outputContainers)) {
  parent: blobEndpoint
  name: 'default'
  properties: {
    privateDnsZoneConfigs: [
      {
        name: 'blob'
        properties: {
          privateDnsZoneId: blobZoneId
        }
      }
    ]
  }
}

output identityId string = identity.id
output identityClientId string = identity.properties.clientId
@description('Grant this principal as each trigger\'s machine caller; it is also the object ID for the invocation grant.')
output identityPrincipalId string = identity.properties.principalId
output serviceBusNamespace string = empty(queueNames) && empty(topicNames) ? '' : '${serviceBusName}.servicebus.windows.net'
output blobServiceUri string = 'https://${storageName}.blob.${environment().suffixes.storage}'
