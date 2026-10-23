// The Ferrule service in Azure Container Apps dynamic sessions: a custom-container pool running the same
// service image in platform-sandbox mode, one Hyper-V sandbox per operation, with outbound network
// disabled. Weft hosts reach sessions through the pool management endpoint with a managed-identity token.
param location string
param name string
param environmentName string
param identityId string
@description('Principal IDs of the Weft host identities that call the pool.')
param callerPrincipalIds array
param registryServer string
@description('Service image reference in the registry, preferably by digest (repository@sha256:...).')
param image string
@description('Sessions allocated at the same time, which bounds concurrent operations.')
param maxSessions int = 20
@description('Sessions kept ready to absorb bursts.')
param readySessions int = 2
@description('Serve the compile profile for hosted builds. The platform kernel must provide Landlock ABI 3 (Linux 6.2); otherwise the service does not start.')
param compile bool = false
param cpu string = '1.0'
param memory string = '2Gi'
param tags object

var port = 8080
var healthReady = {
  path: '/v1/health/ready'
  port: port
}

resource environment 'Microsoft.App/managedEnvironments@2024-03-01' existing = {
  name: environmentName
}

resource pool 'Microsoft.App/sessionPools@2026-07-01' = {
  name: name
  location: location
  tags: tags
  identity: {
    type: 'UserAssigned'
    userAssignedIdentities: {
      '${identityId}': {}
    }
  }
  properties: {
    environmentId: environment.id
    poolManagementType: 'Dynamic'
    containerType: 'CustomContainer'
    scaleConfiguration: {
      maxConcurrentSessions: maxSessions
      readySessionInstances: readySessions
    }
    // A session serves one operation and is then stopped; the cooldown only reclaims a lost stop.
    dynamicPoolConfiguration: {
      lifecycleConfiguration: {
        lifecycleType: 'Timed'
        cooldownPeriodInSeconds: 300
      }
    }
    sessionNetworkConfiguration: {
      status: 'EgressDisabled'
    }
    // The identity only pulls the image; session code can never obtain its tokens.
    managedIdentitySettings: [
      {
        identity: identityId
        lifecycle: 'None'
      }
    ]
    customContainerTemplate: {
      registryCredentials: {
        server: registryServer
        identity: identityId
      }
      ingress: {
        targetPort: port
      }
      containers: [
        {
          name: 'ferrule'
          image: '${registryServer}/${image}'
          env: [
            { name: 'WEFT_FERRULE_SERVICE_AUTHENTICATION', value: 'platformSandbox' }
            { name: 'WEFT_FERRULE_SERVICE_LISTEN_ADDR', value: '0.0.0.0:${port}' }
            { name: 'WEFT_FERRULE_SERVICE_CONCURRENCY', value: '1' }
            { name: 'WEFT_FERRULE_SERVICE_QUEUE', value: '0' }
            { name: 'WEFT_FERRULE_SERVICE_COMPILE_ENABLED', value: compile ? 'true' : 'false' }
          ]
          resources: {
            cpu: json(cpu)
            memory: memory
          }
          probes: [
            {
              type: 'Startup'
              httpGet: healthReady
              periodSeconds: 5
              failureThreshold: 24
            }
            {
              type: 'Liveness'
              httpGet: {
                path: '/v1/health/live'
                port: port
              }
              periodSeconds: 10
              failureThreshold: 3
            }
          ]
        }
      ]
    }
  }
}

// Azure ContainerApps Session Executor: send requests to the pool's sessions, nothing else.
var sessionExecutorRoleId = '0fb8eba5-a2bb-4abe-b1c1-49dfad359bb0'

resource executors 'Microsoft.Authorization/roleAssignments@2022-04-01' = [for principalId in callerPrincipalIds: {
  scope: pool
  name: guid(pool.id, principalId, sessionExecutorRoleId)
  properties: {
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', sessionExecutorRoleId)
    principalId: principalId
    principalType: 'ServicePrincipal'
  }
}]

output poolManagementEndpoint string = pool.properties.poolManagementEndpoint
