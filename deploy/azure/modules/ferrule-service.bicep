// The Ferrule service as an internal-only container app. TLS terminates in the service (pinned mutual TLS),
// so ingress is layer-4 TCP: each operation's connection stays on one replica, and the platform scales
// replicas on concurrent connections. The service needs no outbound network and no Docker.
param location string
param name string
param environmentName string
param identityId string
param registryServer string
param vaultUri string
@description('Service image reference in the registry, preferably by digest (repository@sha256:...).')
param image string
@description('Comma-separated lowercase SHA-256 pins of the admitted client certificates.')
param clientCertificatePins string
param port int = 8443
param healthPort int = 8080
@description('Child processes per replica; size memory at 512 MiB per child (2 GiB more per child with compilation), plus one request frame per capacity slot and the service.')
param concurrency int = 2
@description('Serve the compile profile for hosted builds. The platform kernel must provide Landlock ABI 3 (Linux 6.2); otherwise the service does not start.')
param compile bool = false
param queue int = 2
param minReplicas int = 1
param maxReplicas int = 10
param cpu string = '1.0'
param memory string = '2Gi'
param tags object

resource environment 'Microsoft.App/managedEnvironments@2024-03-01' existing = {
  name: environmentName
}

var healthReady = {
  path: '/v1/health/ready'
  port: healthPort
}

resource service 'Microsoft.App/containerApps@2024-03-01' = {
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
    workloadProfileName: 'Consumption'
    configuration: {
      activeRevisionsMode: 'Single'
      ingress: {
        external: false
        transport: 'tcp'
        targetPort: port
        exposedPort: port
      }
      registries: [
        {
          server: registryServer
          identity: identityId
        }
      ]
      secrets: [
        {
          // A Key Vault certificate's secret is its PEM bundle: the private key and the certificate.
          name: 'ferrule-service-pem'
          keyVaultUrl: '${vaultUri}secrets/weft-ferrule-service'
          identity: identityId
        }
      ]
    }
    template: {
      containers: [
        {
          name: 'ferrule'
          image: '${registryServer}/${image}'
          env: [
            { name: 'WEFT_FERRULE_SERVICE_LISTEN_ADDR', value: '0.0.0.0:${port}' }
            { name: 'WEFT_FERRULE_SERVICE_HEALTH_ADDR', value: '0.0.0.0:${healthPort}' }
            { name: 'WEFT_FERRULE_SERVICE_PEM', secretRef: 'ferrule-service-pem' }
            { name: 'WEFT_FERRULE_SERVICE_CLIENT_CERTIFICATE_SHA256', value: clientCertificatePins }
            { name: 'WEFT_FERRULE_SERVICE_CONCURRENCY', value: string(concurrency) }
            { name: 'WEFT_FERRULE_SERVICE_QUEUE', value: string(queue) }
            { name: 'WEFT_FERRULE_SERVICE_COMPILE_ENABLED', value: compile ? 'true' : 'false' }
          ]
          resources: {
            cpu: json(cpu)
            memory: memory
          }
          volumeMounts: [
            {
              volumeName: 'operations'
              mountPath: '/tmp'
            }
          ]
          probes: [
            {
              type: 'Startup'
              httpGet: healthReady
              periodSeconds: 5
              failureThreshold: 24
            }
            {
              type: 'Readiness'
              httpGet: healthReady
              periodSeconds: 10
              failureThreshold: 3
            }
            {
              type: 'Liveness'
              httpGet: {
                path: '/v1/health/live'
                port: healthPort
              }
              periodSeconds: 30
              failureThreshold: 3
            }
          ]
        }
      ]
      // Each child gets a fresh private directory here; nothing persists across replicas or restarts.
      volumes: [
        {
          name: 'operations'
          storageType: 'EmptyDir'
        }
      ]
      // One operation holds one connection, so concurrent connections per replica track its children.
      scale: {
        minReplicas: minReplicas
        maxReplicas: maxReplicas
        rules: [
          {
            name: 'operations'
            tcp: {
              metadata: {
                concurrentConnections: string(concurrency)
              }
            }
          }
        ]
      }
    }
  }
}

@description('The canonical HTTPS origin Weft hosts use for the service inside the environment.')
output url string = 'https://${name}.internal.${environment.properties.defaultDomain}:${port}'
