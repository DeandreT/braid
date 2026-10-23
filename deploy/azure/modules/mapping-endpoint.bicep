// Machine-facing mapping endpoints: one internal-only container app per registered release target, behind
// the environment ingress, which terminates TLS (the platformIngress transport). Each endpoint executes its
// target's compiled artifacts through the Ferrule service's execute profile over pinned mutual TLS, so it
// needs no Docker. Two manual jobs administer invocation grants and run the release operator tool.
// See docs/mapping-endpoint.md and docs/azure-deployment.md.
param location string
param namePrefix string
param environmentName string
param identityId string
param registryServer string
@description('Endpoint image reference in the registry, such as weft-mapping-endpoint:<tag>.')
param endpointImage string
@description('Weft image reference in the registry, for the grant job.')
param weftImage string
param vaultUri string
param organizationId string
@description('One entry per registered target: name, projectId, mappingId, environmentId, environmentVersion, targetId, and optional concurrency (1-4), maxReplicas and callers ([{ grantId, objectId }]).')
param endpoints array
@description('Weft principal ID of the project administrator who grants the callers; it only fills the grant commands output.')
param grantAdministratorId string
@description('Entra tenant whose app-only access tokens the endpoints accept.')
param tenantId string
@description('Application (client) ID of the endpoint app registration, the audience of callers\' tokens.')
param audience string
param ferruleServiceUrl string
param ferruleServiceCertificateSha256 string
@description('Native build identity of the Ferrule service image.')
param ferruleServiceIdentity string
@description('Compile toolchain identity that, with the native identity, names the compiler of the targets\' builds.')
param compileToolchain string
@description('Execution identity the Ferrule service reports for its execute profile; the targets register it as their runtime.')
param compiledRuntimeIdentity string
param tags object

resource environment 'Microsoft.App/managedEnvironments@2024-03-01' existing = {
  name: environmentName
}

var registries = [
  {
    server: registryServer
    identity: identityId
  }
]

func vaultSecret(vaultUri string, identityId string, name string, secret string) object => {
  name: name
  keyVaultUrl: '${vaultUri}secrets/${secret}'
  identity: identityId
}

// The endpoint's eight restricted logins: Key Vault secret key and endpoint setting.
var logins = [
  { key: 'invocation-writer', setting: 'INVOCATION_WRITER' }
  { key: 'release-reader', setting: 'RELEASE_READER' }
  { key: 'build-reader', setting: 'BUILD_READER' }
  { key: 'build-run-reader', setting: 'BUILD_RUN_READER' }
  { key: 'runtime', setting: 'ACCESS' }
  { key: 'report', setting: 'REPORT_READER' }
  { key: 'run-reader', setting: 'RUN_READER' }
  { key: 'case-reader', setting: 'CASE_READER' }
]

// The release tool's logins: the release writer and qualification reader for activation, and the
// qualification writer with its readers for qualifying a build.
var releaseLogins = [
  { key: 'release-writer', setting: 'WEFT_RELEASE_WRITER_CONNECTION_STRING' }
  { key: 'qualification-reader', setting: 'WEFT_QUALIFICATION_READER_CONNECTION_STRING' }
  { key: 'qualification-writer', setting: 'WEFT_QUALIFICATION_WRITER_CONNECTION_STRING' }
  { key: 'build-reader', setting: 'WEFT_QUALIFICATION_BUILD_READER_CONNECTION_STRING' }
  { key: 'build-run-reader', setting: 'WEFT_QUALIFICATION_BUILD_RUN_READER_CONNECTION_STRING' }
  { key: 'runtime', setting: 'WEFT_QUALIFICATION_ACCESS_CONNECTION_STRING' }
  { key: 'report', setting: 'WEFT_QUALIFICATION_REPORT_READER_CONNECTION_STRING' }
  { key: 'run-reader', setting: 'WEFT_QUALIFICATION_RUN_READER_CONNECTION_STRING' }
  { key: 'case-reader', setting: 'WEFT_QUALIFICATION_CASE_READER_CONNECTION_STRING' }
]

// The compiled runtime: the Ferrule service's execute profile with this host's own client certificate.
var runtimeEnvironment = [
  { name: 'WEFT_FERRULE_RUNNER', value: 'service' }
  { name: 'WEFT_FERRULE_SERVICE_URL', value: ferruleServiceUrl }
  { name: 'WEFT_FERRULE_SERVICE_CERTIFICATE_SHA256', value: ferruleServiceCertificateSha256 }
  { name: 'WEFT_FERRULE_WORKER_IMAGE', value: ferruleServiceIdentity }
  { name: 'WEFT_FERRULE_COMPILE_TOOLCHAIN', value: compileToolchain }
  { name: 'WEFT_COMPILED_RUNTIME_IMAGE', value: compiledRuntimeIdentity }
  { name: 'WEFT_DELIVER_FERRULE_SERVICE_CLIENT_PEM', secretRef: 'ferrule-endpoint-client-pem' }
]

var healthReady = {
  path: '/v1/health/ready'
  port: 8080
}

resource endpointApps 'Microsoft.App/containerApps@2024-03-01' = [for endpoint in endpoints: {
  name: '${namePrefix}-${endpoint.name}'
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
      // Internal only; the environment ingress terminates TLS and forwards plain HTTP.
      ingress: {
        external: false
        targetPort: 8080
        transport: 'http'
        allowInsecure: false
      }
      registries: registries
      secrets: concat(map(logins, login => vaultSecret(vaultUri, identityId, '${login.key}-connection', 'pg-${login.key}-connection')), [
        // A Key Vault certificate's secret is its PEM bundle: the private key and the certificate.
        vaultSecret(vaultUri, identityId, 'ferrule-endpoint-client-pem', 'weft-ferrule-endpoint-client')
      ])
    }
    template: {
      // A stop drains calls for up to their 90-second deadline, then joins the runtime's work.
      terminationGracePeriodSeconds: 100
      containers: [
        {
          name: 'endpoint'
          image: '${registryServer}/${endpointImage}'
          args: ['endpoint']
          // Each login reaches the host only as a protected private file written by the entrypoint.
          env: concat([
            { name: 'WEFT_ENDPOINT_LISTEN_ADDR', value: '0.0.0.0:8080' }
            { name: 'WEFT_ENDPOINT_TRANSPORT', value: 'platformIngress' }
            { name: 'WEFT_ENDPOINT_ORGANIZATION_ID', value: organizationId }
            { name: 'WEFT_ENDPOINT_PROJECT_ID', value: endpoint.projectId }
            { name: 'WEFT_ENDPOINT_MAPPING_ID', value: endpoint.mappingId }
            { name: 'WEFT_ENDPOINT_ENVIRONMENT_ID', value: endpoint.environmentId }
            { name: 'WEFT_ENDPOINT_ENVIRONMENT_VERSION', value: string(endpoint.environmentVersion) }
            { name: 'WEFT_ENDPOINT_TARGET_ID', value: endpoint.targetId }
            { name: 'WEFT_ENDPOINT_ENTRA_TENANT_ID', value: tenantId }
            { name: 'WEFT_ENDPOINT_ENTRA_AUDIENCE', value: audience }
            { name: 'WEFT_ENDPOINT_CONCURRENCY', value: string(endpoint.?concurrency ?? 4) }
          ], runtimeEnvironment, flatten(map(logins, login => [
            { name: 'WEFT_DELIVER_FILE_ENDPOINT_${login.setting}', secretRef: '${login.key}-connection' }
            { name: 'WEFT_ENDPOINT_${login.setting}_CONNECTION_STRING_FILE', value: '/run/weft/secrets/endpoint-${toLower(replace(login.setting, '_', '-'))}' }
          ])))
          resources: {
            cpu: json('0.5')
            memory: '1Gi'
          }
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
                port: 8080
              }
              periodSeconds: 30
              failureThreshold: 3
            }
          ]
        }
      ]
      // At least one replica; each replica admits no more concurrent calls than its endpoint setting,
      // and a saturated endpoint answers 503 rather than queueing, so the platform scales out first.
      scale: {
        minReplicas: 1
        maxReplicas: endpoint.?maxReplicas ?? 4
        rules: [
          {
            name: 'calls'
            http: {
              metadata: {
                concurrentRequests: string(endpoint.?concurrency ?? 4)
              }
            }
          }
        ]
      }
    }
  }
}]

var jobConfiguration = {
  triggerType: 'Manual'
  replicaTimeout: 900
  replicaRetryLimit: 0
  manualTriggerConfig: {
    parallelism: 1
    replicaCompletionCount: 1
  }
  registries: registries
}

var jobIdentity = {
  type: 'UserAssigned'
  userAssignedIdentities: {
    '${identityId}': {}
  }
}

var jobResources = {
  cpu: json('0.5')
  memory: '1Gi'
}

// Invocation grants with Weft.DatabaseTool as a named project administrator; supply the grant command as
// arguments when starting an execution, for example `database grant-machine-invocation ADMIN ORG ...`.
resource grants 'Microsoft.App/jobs@2024-03-01' = {
  name: '${namePrefix}-endpoint-grants'
  location: location
  tags: tags
  identity: jobIdentity
  properties: {
    environmentId: environment.id
    workloadProfileName: 'Consumption'
    configuration: union(jobConfiguration, {
      secrets: [vaultSecret(vaultUri, identityId, 'invocation-grant-writer-connection', 'pg-invocation-grant-writer-connection')]
    })
    template: {
      containers: [
        {
          name: 'grants'
          image: '${registryServer}/${weftImage}'
          args: ['database']
          env: [
            { name: 'WEFT_INVOCATION_GRANT_WRITER_CONNECTION_STRING', secretRef: 'invocation-grant-writer-connection' }
          ]
          resources: jobResources
        }
      ]
    }
  }
}

// Release operations with Weft.ReleaseTool as a named principal: qualify a completed build through the
// service's execute profile, register a target, activate or roll back. Supply `release COMMAND ...`.
resource release 'Microsoft.App/jobs@2024-03-01' = {
  name: '${namePrefix}-endpoint-release'
  location: location
  tags: tags
  identity: jobIdentity
  properties: {
    environmentId: environment.id
    workloadProfileName: 'Consumption'
    configuration: union(jobConfiguration, {
      secrets: concat(map(releaseLogins, login => vaultSecret(vaultUri, identityId, '${login.key}-connection', 'pg-${login.key}-connection')), [
        vaultSecret(vaultUri, identityId, 'ferrule-endpoint-client-pem', 'weft-ferrule-endpoint-client')
      ])
    })
    template: {
      containers: [
        {
          name: 'release'
          image: '${registryServer}/${endpointImage}'
          args: ['release']
          env: concat(runtimeEnvironment, map(releaseLogins, login => { name: login.setting, secretRef: '${login.key}-connection' }))
          resources: jobResources
        }
      ]
    }
  }
}

@description('One grant job argument list per configured caller, for `az containerapp job start --args`.')
output grantArguments array = flatten(map(endpoints, endpoint => map(endpoint.?callers ?? [], caller => join([
  'database'
  'grant-machine-invocation'
  grantAdministratorId
  organizationId
  endpoint.projectId
  endpoint.mappingId
  endpoint.environmentId
  string(endpoint.environmentVersion)
  endpoint.targetId
  caller.grantId
  tenantId
  caller.objectId
], ' '))))

@description('The internal HTTPS origin of each endpoint inside the environment.')
output origins array = [for (endpoint, index) in endpoints: 'https://${endpointApps[index].properties.configuration.ingress.fqdn}']
