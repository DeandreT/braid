// The Braid.Worker `runs` host: previews, the regression runs that execute them and approved builds, through
// the Ferrule service (container app or dynamic sessions) with its own credentials. No ingress, Docker or
// published port; it only claims durable work from PostgreSQL and reads native objects from the private
// container. See docs/azure-deployment.md and deploy/compose/compose.features.yml.
param location string
param name string
param environmentName string
@description('Shared application identity: image pull and Key Vault references only; the worker\'s code cannot use it.')
param identityId string
@description('The worker\'s own identity: private object store and, with the sessions runner, the session pool.')
param workerIdentityId string
param workerIdentityClientId string
param registryServer string
@description('Worker image reference in the registry, such as braid-worker:<tag>.')
param image string
param vaultUri string
@allowed(['service', 'sessions'])
param ferruleRunner string
param ferruleServiceUrl string
param ferruleServiceCertificateSha256 string
param ferruleSessionsEndpoint string
@description('Native build identity of the Ferrule service image.')
param ferruleServiceIdentity string
@description('Compile toolchain identity of the Ferrule service image; builds bind it with the native identity.')
param compileToolchain string
param privateObjectsEndpoint string
param privateObjectsContainer string
param tags object

resource environment 'Microsoft.App/managedEnvironments@2024-03-01' existing = {
  name: environmentName
}

func vaultSecret(vaultUri string, identityId string, name string, secret string) object => {
  name: name
  keyVaultUrl: '${vaultUri}secrets/${secret}'
  identity: identityId
}

// Each worker login: Key Vault login key, worker setting and private file name. The entrypoint writes each
// delivery to an owner-only file, and the worker reads only that file.
var logins = [
  { key: 'preview-writer', setting: 'BRAID_PREVIEW_WRITER_CONNECTION_STRING', file: 'preview-writer' }
  { key: 'run-writer', setting: 'BRAID_REGRESSION_RUN_WRITER_CONNECTION_STRING', file: 'run-writer' }
  { key: 'comparison', setting: 'BRAID_AUTOMATION_COMPARISON_CONNECTION_STRING', file: 'comparison' }
  { key: 'case-reader', setting: 'BRAID_REGRESSION_CASE_READER_CONNECTION_STRING', file: 'case-reader' }
  { key: 'runtime', setting: 'BRAID_REGRESSION_ACCESS_CONNECTION_STRING', file: 'regression-access' }
  { key: 'report', setting: 'BRAID_REGRESSION_REPORT_READER_CONNECTION_STRING', file: 'regression-report' }
  { key: 'build-dispatcher', setting: 'BRAID_BUILD_DISPATCHER_CONNECTION_STRING', file: 'build-dispatcher' }
  { key: 'build-writer', setting: 'BRAID_BUILD_WRITER_CONNECTION_STRING', file: 'build-writer' }
  { key: 'runtime', setting: 'BRAID_BUILD_ACCESS_CONNECTION_STRING', file: 'build-access' }
  { key: 'report', setting: 'BRAID_BUILD_REPORT_READER_CONNECTION_STRING', file: 'build-report' }
  { key: 'run-reader', setting: 'BRAID_BUILD_REGRESSION_RUN_READER_CONNECTION_STRING', file: 'build-evidence' }
  { key: 'case-reader', setting: 'BRAID_BUILD_CASE_READER_CONNECTION_STRING', file: 'build-case-reader' }
]
var loginKeys = union(map(logins, login => login.key), [])

var runnerEnvironment = ferruleRunner == 'service' ? [
  // The Ferrule service in this environment, pinned in both directions with the worker's own client certificate.
  { name: 'BRAID_FERRULE_RUNNER', value: 'service' }
  { name: 'BRAID_FERRULE_SERVICE_URL', value: ferruleServiceUrl }
  { name: 'BRAID_FERRULE_SERVICE_CERTIFICATE_SHA256', value: ferruleServiceCertificateSha256 }
  { name: 'BRAID_DELIVER_FERRULE_SERVICE_CLIENT_PEM', secretRef: 'ferrule-worker-client-pem' }
] : [
  // Dynamic sessions with the worker's own identity, which holds the Session Executor role on the pool.
  { name: 'BRAID_FERRULE_RUNNER', value: 'azureContainerAppsSessions' }
  { name: 'BRAID_FERRULE_SESSIONS_POOL_ENDPOINT', value: ferruleSessionsEndpoint }
  { name: 'BRAID_FERRULE_SESSIONS_MANAGED_IDENTITY_CLIENT_ID', value: workerIdentityClientId }
]

// Every setting the worker reads; credentials arrive only as private files or Key Vault references.
var workerEnvironment = concat([
  { name: 'BRAID_PREVIEW_WORKER_ENABLED', value: 'true' }
  { name: 'BRAID_REGRESSION_WORKER_ENABLED', value: 'true' }
  { name: 'BRAID_BUILD_WORKER_ENABLED', value: 'true' }
  { name: 'BRAID_FERRULE_WORKER_IMAGE', value: ferruleServiceIdentity }
  { name: 'BRAID_FERRULE_COMPILE_TOOLCHAIN', value: compileToolchain }
  // Native objects come from the private container through the worker's own identity.
  { name: 'BRAID_PRIVATE_STORAGE_KIND', value: 'azure-blob' }
  { name: 'BRAID_PRIVATE_STORAGE_BLOB_ENDPOINT', value: privateObjectsEndpoint }
  { name: 'BRAID_PRIVATE_STORAGE_BLOB_CONTAINER', value: privateObjectsContainer }
  { name: 'BRAID_PRIVATE_STORAGE_BLOB_CREDENTIAL', value: 'managed-identity' }
  { name: 'BRAID_PRIVATE_STORAGE_BLOB_CLIENT_ID', value: workerIdentityClientId }
], runnerEnvironment, flatten(map(logins, login => [
  { name: 'BRAID_DELIVER_FILE_${toUpper(replace(login.file, '-', '_'))}', secretRef: '${login.key}-connection' }
  { name: '${login.setting}_FILE', value: '/run/braid/secrets/${login.file}' }
])))

resource worker 'Microsoft.App/containerApps@2025-07-01' = {
  name: name
  location: location
  tags: tags
  identity: {
    type: 'UserAssigned'
    userAssignedIdentities: {
      '${identityId}': {}
      '${workerIdentityId}': {}
    }
  }
  properties: {
    environmentId: environment.id
    workloadProfileName: 'Consumption'
    configuration: {
      activeRevisionsMode: 'Single'
      // The shared identity pulls the image and resolves Key Vault references; the worker's code never sees it.
      identitySettings: [
        {
          identity: identityId
          lifecycle: 'None'
        }
        {
          identity: workerIdentityId
          lifecycle: 'Main'
        }
      ]
      registries: [
        {
          server: registryServer
          identity: identityId
        }
      ]
      secrets: concat(map(loginKeys, key => vaultSecret(vaultUri, identityId, '${key}-connection', 'pg-${key}-connection')),
        ferruleRunner == 'service' ? [
          // A Key Vault certificate's secret is its PEM bundle: the private key and the certificate.
          vaultSecret(vaultUri, identityId, 'ferrule-worker-client-pem', 'braid-ferrule-worker-client')
        ] : [])
    }
    template: {
      // A stop cancels in-flight runs; a canceled compilation settles within its 120-second wall deadline,
      // child reaping and margin, and the run's row stays retryable for the next worker.
      terminationGracePeriodSeconds: 150
      containers: [
        {
          name: 'worker'
          image: '${registryServer}/${image}'
          args: ['worker']
          env: workerEnvironment
          resources: {
            cpu: json('0.5')
            memory: '1Gi'
          }
        }
      ]
      // One replica, always running: it claims durable work rather than serving requests.
      scale: {
        minReplicas: 1
        maxReplicas: 1
      }
    }
  }
}

output name string = worker.name
