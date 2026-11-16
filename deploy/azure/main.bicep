// First Azure-hosted Braid environment. Deploy in two stages: the platform first, then the
// application and jobs after the images and Key Vault secrets exist. See docs/azure-deployment.md.
targetScope = 'resourceGroup'

@description('Azure region for every resource.')
param location string = resourceGroup().location

@description('Short lowercase prefix for resource names.')
@minLength(2)
@maxLength(10)
param name string = 'braid'

@description('Suffix that makes the globally unique names (registry, vault, storage, database server) distinct.')
@minLength(4)
@maxLength(13)
param uniqueSuffix string = uniqueString(resourceGroup().id)

@description('Microsoft Entra tenant ID whose organizational accounts sign in.')
param entraTenantId string

@description('Application (client) ID of the Braid app registration.')
param entraClientId string

@description('Braid organization ID served by this environment (canonical lowercase UUID).')
param organizationId string

@description('Object ID of the operator user or group that initializes Key Vault secrets and certificates. Empty assigns no operator role.')
param operatorPrincipalId string = ''

@description('Public IPv4 ranges (CIDR) allowed to reach Key Vault for operator initialization. Empty leaves the public endpoint open to Entra-authorized callers only.')
param operatorIpRanges array = []

@description('Custom public host name, such as braid.contoso.com. Empty uses the Container Apps default host name.')
param customDomain string = ''

@description('Resource ID of the managed certificate bound to the custom domain. Empty adds the host name without a binding.')
param customDomainCertificateId string = ''

@description('Deploy the application and its jobs. Requires the images in the registry and the Key Vault secrets.')
param deployApplication bool = false

@description('Image tag of the braid and braid-edge repositories in the registry.')
param imageTag string = ''

@description('Enable conditional mapping metadata edits.')
param enableMetadataEdits bool = true

@description('Enable explicit project creation.')
param enableProjectCreation bool = true

@description('HTTPS origin of a separately hosted Braid.Worker inspection service in the virtual network, such as https://10.40.4.4:8443. Container Apps cannot run its Docker runner, so this environment does not create it. Empty leaves the Session host without an inspection worker.')
param inspectionWorkerUrl string = ''

@description('Lowercase SHA-256 of the inspection worker certificate (DER). Required with inspectionWorkerUrl.')
param inspectionWorkerCertificateSha256 string = ''

@description('Immutable lowercase sha256 image ID of the Ferrule worker image that the inspection worker runs. Required with inspectionWorkerUrl.')
param ferruleWorkerImage string = ''

@description('Where native work runs: worker (a separately hosted Braid.Worker with Docker, see inspectionWorkerUrl), service (the Ferrule service container app in this environment) or sessions (the Ferrule service in a dynamic-sessions pool). Native work stays dormant until nativeWorkEnabled, which requires service or sessions.')
@allowed(['worker', 'service', 'sessions'])
param ferruleRunner string = 'worker'

@description('Ferrule service image in the registry, preferably by digest (braid-ferrule-service@sha256:...). Required with the service and sessions runners.')
param ferruleServiceImage string = ''

@description('Build identity of that image: sha256: followed by its io.braid.ferrule.worker-sha256 label. Required with the service and sessions runners.')
param ferruleServiceIdentity string = ''

@description('Lowercase SHA-256 of the braid-ferrule-service certificate (DER), from initialize-secrets.sh. Required with the service runner.')
param ferruleServiceCertificateSha256 string = ''

@description('Lowercase SHA-256 of the braid-ferrule-service-client certificate (DER), from initialize-secrets.sh. Required with the service runner.')
param ferruleServiceClientCertificateSha256 string = ''

@description('Child processes per Ferrule service replica (512 MiB each), which is also its concurrent-connection scale target.')
param ferruleServiceConcurrency int = 2

@description('Most Ferrule service replicas the scale rule may run.')
param ferruleServiceMaxReplicas int = 10

@description('Most concurrent dynamic sessions, which bounds concurrent Ferrule operations with the sessions runner.')
param ferruleSessionsMax int = 20

@description('Serve the Ferrule service compile profile for hosted builds; a compiler may use a 2 GiB managed heap. Each replica then gets ferruleServiceConcurrency + 1 vCPU and twice as many GiB (so at most concurrency 3 on the Consumption profile), and each session 2 vCPU and 4 GiB. The platform kernel must provide Landlock ABI 3 (Linux 6.2); otherwise the service does not start.')
param ferruleServiceCompile bool = false

@description('Compile toolchain identity of the Ferrule service image, as the service reports it for its compile profile (see docs/ferrule-service.md). With it, the Session host pins that toolchain and its reports record the combined compiler identity that builds through the service bind. Requires ferruleServiceCompile; empty keeps the native identity.')
param ferruleCompileToolchain string = ''

@description('Enable native work through the Ferrule service: native upload and report derivation in the Session host, and previews, regression runs and builds executed by the <name>-worker app. Requires ferruleRunner = service or sessions, ferruleServiceCompile and ferruleCompileToolchain, and with the service runner ferruleServiceWorkerClientCertificateSha256.')
param nativeWorkEnabled bool = false

@description('Lowercase SHA-256 of the braid-ferrule-worker-client certificate (DER), from initialize-secrets.sh, which the Ferrule service admits for the worker app. Required with nativeWorkEnabled and the service runner.')
param ferruleServiceWorkerClientCertificateSha256 string = ''

@description('Enable revision chat in the Session host. Requires nativeWorkEnabled, and the braid-chat-profiles and braid-model-provider-credential Key Vault secrets (see docs/azure-deployment.md).')
param chatEnabled bool = false

@description('Bearer access to /api/v1 for signed-in users through allowlisted public clients such as the braid CLI. The Session app registration (entraClientId) must expose apiBearerScope and accept v2 access tokens; see docs/api-authentication.md.')
param apiBearerEnabled bool = false

@description('The delegated scope name the Session app registration exposes, exactly as it appears in the token\'s scp claim, for example braid.api. Required with apiBearerEnabled.')
param apiBearerScope string = ''

@description('Client IDs of the public client applications allowed to present bearer tokens (1-16), for example the braid CLI registration. Required with apiBearerEnabled.')
param apiBearerClientIds array = []

@description('Machine-facing mapping endpoints, one internal container app per registered release target: name (2-12 lowercase characters), projectId, mappingId, environmentId, environmentVersion, targetId, and optional concurrency (1-4, default 4), maxReplicas (default 4) and callers ([{ grantId, objectId }]). They execute through the Ferrule service, so they require ferruleRunner = service. Empty deploys none.')
param mappingEndpoints array = []

@description('Application (client) ID of the mapping endpoint app registration: the audience of callers\' app-only access tokens. Required with mappingEndpoints.')
param mappingEndpointAudience string = ''

@description('Entra tenant whose app-only access tokens the mapping endpoints accept. Empty uses entraTenantId.')
param mappingEndpointTenantId string = ''

@description('Braid principal ID of the project administrator who grants the endpoint callers; it fills the grant commands output only.')
param mappingEndpointAdministratorId string = ''

@description('Lowercase SHA-256 of the braid-ferrule-endpoint-client certificate (DER), from initialize-secrets.sh, which the Ferrule service admits for the endpoints. Required with mappingEndpoints.')
param ferruleServiceEndpointClientCertificateSha256 string = ''

@description('Execution identity the Ferrule service image reports for its execute profile (see docs/compiled-service-runtime.md); every endpoint target registers it as its runtime image. Required with mappingEndpoints.')
param compiledRuntimeIdentity string = ''

@description('Object ID of a separately hosted Braid.Worker managed identity, granted the same data-plane role on the private object container. Empty grants none.')
param workerPrincipalId string = ''

@description('PostgreSQL administrator login, used only by the database provisioning job.')
param postgresAdministratorLogin string = 'braidadmin'

@description('PostgreSQL administrator password. Supply it from the environment; it is also stored in Key Vault for the provisioning job.')
@secure()
param postgresAdministratorPassword string

@description('PostgreSQL compute SKU.')
param postgresSkuName string = 'Standard_B1ms'

@description('PostgreSQL compute tier matching the SKU.')
@allowed(['Burstable', 'GeneralPurpose', 'MemoryOptimized'])
param postgresSkuTier string = 'Burstable'

@description('PostgreSQL storage size in GiB.')
param postgresStorageSizeGB int = 32

@description('Virtual network address space and subnets.')
param addressSpace string = '10.40.0.0/16'
param appsSubnetPrefix string = '10.40.0.0/23'
param databaseSubnetPrefix string = '10.40.2.0/28'
param endpointsSubnetPrefix string = '10.40.3.0/27'

param tags object = {}

var compactSuffix = toLower(uniqueSuffix)
var names = {
  logs: '${name}-logs'
  network: '${name}-vnet'
  identity: '${name}-identity'
  workerIdentity: '${name}-worker-identity'
  registry: take('${name}acr${compactSuffix}', 50)
  vault: take('${name}-kv-${compactSuffix}', 24)
  storage: take('${name}st${compactSuffix}', 24)
  objects: take('${name}obj${compactSuffix}', 24)
  database: '${name}-pg-${compactSuffix}'
  environment: '${name}-env'
  app: '${name}-app'
  ferrule: '${name}-ferrule'
  ferruleSessions: '${name}-ferrule-sessions'
  worker: '${name}-worker'
}

// Mapping endpoints run their compiled artifacts through the Ferrule service container app.
var mappingEndpointsEnabled = deployApplication && ferruleRunner == 'service' && !empty(mappingEndpoints)

// Explicit feature combinations: an inconsistent request fails the deployment with its reason rather than
// deploying hosts that refuse to start. Each branch is evaluated only when selected.
var featureRequirements = !nativeWorkEnabled && !chatEnabled ? true
  : !nativeWorkEnabled ? fail('chatEnabled requires nativeWorkEnabled: conversations pin reports that derivation produces')
  : ferruleRunner == 'worker' ? fail('nativeWorkEnabled requires ferruleRunner = service or sessions')
  : !ferruleServiceCompile || empty(ferruleCompileToolchain) ? fail('nativeWorkEnabled requires ferruleServiceCompile = true and ferruleCompileToolchain: builds compile through the Ferrule service')
  : ferruleRunner == 'service' && empty(ferruleServiceWorkerClientCertificateSha256) ? fail('nativeWorkEnabled with ferruleRunner = service requires ferruleServiceWorkerClientCertificateSha256')
  : true
var workerEnabled = deployApplication && nativeWorkEnabled && featureRequirements

// Bearer access is complete or absent; the host refuses a partial profile at startup, so the deployment does first.
var bearerRequirements = !apiBearerEnabled ? (empty(apiBearerScope) && empty(apiBearerClientIds) ? true
    : fail('apiBearerScope and apiBearerClientIds require apiBearerEnabled'))
  : empty(apiBearerScope) || length(apiBearerScope) > 120 ? fail('apiBearerEnabled requires apiBearerScope, the scope name as it appears in scp')
  : empty(apiBearerClientIds) || length(apiBearerClientIds) > 16 ? fail('apiBearerEnabled requires 1-16 apiBearerClientIds')
  : contains(apiBearerClientIds, entraClientId) ? fail('apiBearerClientIds must not contain the Session app registration itself')
  : true

module network 'modules/network.bicep' = {
  name: 'network'
  params: {
    location: location
    name: names.network
    databaseServerName: names.database
    addressSpace: addressSpace
    appsSubnetPrefix: appsSubnetPrefix
    databaseSubnetPrefix: databaseSubnetPrefix
    endpointsSubnetPrefix: endpointsSubnetPrefix
    tags: tags
  }
}

module monitoring 'modules/monitoring.bicep' = {
  name: 'monitoring'
  params: {
    location: location
    name: names.logs
    tags: tags
  }
}

resource identity 'Microsoft.ManagedIdentity/userAssignedIdentities@2023-01-31' = {
  name: names.identity
  location: location
  tags: tags
}

// The worker app's own identity: the private object container and, with the sessions runner, the session pool.
resource workerIdentity 'Microsoft.ManagedIdentity/userAssignedIdentities@2023-01-31' = {
  name: names.workerIdentity
  location: location
  tags: tags
}

module registry 'modules/registry.bicep' = {
  name: 'registry'
  params: {
    location: location
    name: names.registry
    pullPrincipalId: identity.properties.principalId
    tags: tags
  }
}

module vault 'modules/keyvault.bicep' = {
  name: 'keyvault'
  params: {
    location: location
    name: names.vault
    readerPrincipalId: identity.properties.principalId
    operatorPrincipalId: operatorPrincipalId
    operatorIpRanges: operatorIpRanges
    endpointsSubnetId: network.outputs.endpointsSubnetId
    privateDnsZoneId: network.outputs.vaultZoneId
    postgresAdministratorPassword: postgresAdministratorPassword
    tags: tags
  }
}

module storage 'modules/storage.bicep' = {
  name: 'storage'
  params: {
    location: location
    name: names.storage
    endpointsSubnetId: network.outputs.endpointsSubnetId
    privateDnsZoneId: network.outputs.fileZoneId
    tags: tags
  }
}

module objects 'modules/objects.bicep' = {
  name: 'objects'
  params: {
    location: location
    name: names.objects
    containerName: 'braid-private-objects'
    endpointsSubnetId: network.outputs.endpointsSubnetId
    privateDnsZoneId: network.outputs.blobZoneId
    // Admitting the store proves the credential can create, lease and delete a probe blob, so every
    // consumer, even one that only reads objects, holds the contributor role on the container.
    contributorPrincipalIds: concat([identity.properties.principalId], empty(workerPrincipalId) ? [] : [workerPrincipalId],
      nativeWorkEnabled ? [workerIdentity.properties.principalId] : [])
    tags: tags
  }
}

module database 'modules/postgres.bicep' = {
  name: 'postgres'
  params: {
    location: location
    name: names.database
    administratorLogin: postgresAdministratorLogin
    administratorPassword: postgresAdministratorPassword
    skuName: postgresSkuName
    skuTier: postgresSkuTier
    storageSizeGB: postgresStorageSizeGB
    delegatedSubnetId: network.outputs.databaseSubnetId
    privateDnsZoneId: network.outputs.databaseZoneId
    tags: tags
  }
}

module environment 'modules/environment.bicep' = {
  name: 'environment'
  params: {
    location: location
    name: names.environment
    infrastructureSubnetId: network.outputs.appsSubnetId
    logAnalyticsWorkspaceName: monitoring.outputs.name
    storageAccountName: storage.outputs.name
    keyRingShareName: storage.outputs.keyRingShareName
    tags: tags
  }
}

// The Ferrule service: either an internal container app scaled on concurrent connections, or a
// dynamic-sessions pool with one sandbox per operation. Neither needs Docker or outbound network.
module ferruleService 'modules/ferrule-service.bicep' = if (deployApplication && ferruleRunner == 'service') {
  name: 'ferrule-service'
  params: {
    location: location
    name: names.ferrule
    environmentName: environment.outputs.name
    identityId: identity.id
    registryServer: registry.outputs.loginServer
    vaultUri: vault.outputs.uri
    image: ferruleServiceImage
    // The Session host's client, then the worker's and the mapping endpoints' when they are deployed.
    clientCertificatePins: join(concat([ferruleServiceClientCertificateSha256],
      workerEnabled ? [ferruleServiceWorkerClientCertificateSha256] : [],
      mappingEndpointsEnabled ? [ferruleServiceEndpointClientCertificateSha256] : []), ',')
    concurrency: ferruleServiceConcurrency
    queue: ferruleServiceConcurrency
    maxReplicas: ferruleServiceMaxReplicas
    compile: ferruleServiceCompile
    execute: mappingEndpointsEnabled
    cpu: ferruleServiceCompile ? '${ferruleServiceConcurrency + 1}.0' : '1.0'
    memory: ferruleServiceCompile ? '${2 * (ferruleServiceConcurrency + 1)}Gi' : '2Gi'
    tags: tags
  }
}

module ferruleSessions 'modules/ferrule-session-pool.bicep' = if (deployApplication && ferruleRunner == 'sessions') {
  name: 'ferrule-session-pool'
  params: {
    location: location
    name: names.ferruleSessions
    environmentName: environment.outputs.name
    identityId: identity.id
    callerPrincipalIds: concat([identity.properties.principalId], workerEnabled ? [workerIdentity.properties.principalId] : [])
    registryServer: registry.outputs.loginServer
    image: ferruleServiceImage
    maxSessions: ferruleSessionsMax
    compile: ferruleServiceCompile
    cpu: ferruleServiceCompile ? '2.0' : '1.0'
    memory: ferruleServiceCompile ? '4Gi' : '2Gi'
    tags: tags
  }
}

module application 'modules/application.bicep' = if (deployApplication && featureRequirements && bearerRequirements) {
  name: 'application'
  params: {
    location: location
    name: names.app
    jobPrefix: name
    environmentName: environment.outputs.name
    keyRingStorageName: environment.outputs.keyRingStorageName
    identityId: identity.id
    identityClientId: identity.properties.clientId
    registryServer: registry.outputs.loginServer
    imageTag: imageTag
    vaultUri: vault.outputs.uri
    databaseHost: database.outputs.host
    postgresAdministratorLogin: postgresAdministratorLogin
    entraTenantId: entraTenantId
    entraClientId: entraClientId
    organizationId: organizationId
    customDomain: customDomain
    customDomainCertificateId: customDomainCertificateId
    trustedIngressRange: appsSubnetPrefix
    enableMetadataEdits: enableMetadataEdits
    enableProjectCreation: enableProjectCreation
    inspectionWorkerUrl: ferruleRunner == 'worker' ? inspectionWorkerUrl : ''
    inspectionWorkerCertificateSha256: inspectionWorkerCertificateSha256
    ferruleWorkerImage: ferruleWorkerImage
    ferruleRunner: ferruleRunner
    ferruleServiceUrl: ferruleService.?outputs.url ?? ''
    ferruleServiceCertificateSha256: ferruleServiceCertificateSha256
    ferruleSessionsEndpoint: ferruleSessions.?outputs.poolManagementEndpoint ?? ''
    ferruleServiceIdentity: ferruleServiceIdentity
    ferruleCompileToolchain: ferruleServiceCompile ? ferruleCompileToolchain : ''
    nativeWorkEnabled: nativeWorkEnabled
    chatEnabled: chatEnabled
    apiBearerEnabled: apiBearerEnabled
    apiBearerScope: apiBearerScope
    apiBearerClientIds: apiBearerClientIds
    privateObjectsEndpoint: objects.outputs.blobEndpoint
    privateObjectsContainer: objects.outputs.containerName
    tags: tags
  }
}

// Previews, regression runs and builds that the Session host records, executed through the same Ferrule runner.
module worker 'modules/worker.bicep' = if (workerEnabled) {
  name: 'worker'
  params: {
    location: location
    name: names.worker
    environmentName: environment.outputs.name
    identityId: identity.id
    workerIdentityId: workerIdentity.id
    workerIdentityClientId: workerIdentity.properties.clientId
    registryServer: registry.outputs.loginServer
    image: 'braid-worker:${imageTag}'
    vaultUri: vault.outputs.uri
    ferruleRunner: ferruleRunner == 'sessions' ? 'sessions' : 'service'
    ferruleServiceUrl: ferruleService.?outputs.url ?? ''
    ferruleServiceCertificateSha256: ferruleServiceCertificateSha256
    ferruleSessionsEndpoint: ferruleSessions.?outputs.poolManagementEndpoint ?? ''
    ferruleServiceIdentity: ferruleServiceIdentity
    compileToolchain: ferruleCompileToolchain
    privateObjectsEndpoint: objects.outputs.blobEndpoint
    privateObjectsContainer: objects.outputs.containerName
    tags: tags
  }
}

module mappingEndpoint 'modules/mapping-endpoint.bicep' = if (mappingEndpointsEnabled) {
  name: 'mapping-endpoint'
  params: {
    location: location
    namePrefix: name
    environmentName: environment.outputs.name
    identityId: identity.id
    registryServer: registry.outputs.loginServer
    endpointImage: 'braid-mapping-endpoint:${imageTag}'
    braidImage: 'braid:${imageTag}'
    vaultUri: vault.outputs.uri
    organizationId: organizationId
    endpoints: mappingEndpoints
    grantAdministratorId: mappingEndpointAdministratorId
    tenantId: empty(mappingEndpointTenantId) ? entraTenantId : mappingEndpointTenantId
    audience: mappingEndpointAudience
    ferruleServiceUrl: ferruleService.?outputs.url ?? ''
    ferruleServiceCertificateSha256: ferruleServiceCertificateSha256
    ferruleServiceIdentity: ferruleServiceIdentity
    compileToolchain: ferruleCompileToolchain
    compiledRuntimeIdentity: compiledRuntimeIdentity
    tags: tags
  }
}

output registryLoginServer string = registry.outputs.loginServer
output registryName string = names.registry
output keyVaultName string = names.vault
output databaseHost string = database.outputs.host
output environmentDefaultDomain string = environment.outputs.defaultDomain
output customDomainVerificationId string = environment.outputs.customDomainVerificationId
output publicHost string = application.?outputs.publicHost ?? ''
output entraRedirectUri string = application.?outputs.redirectUri ?? ''
output applicationName string = names.app
output privateObjectsEndpoint string = objects.outputs.blobEndpoint
output privateObjectsContainer string = objects.outputs.containerName
output identityClientId string = identity.properties.clientId
output identityPrincipalId string = identity.properties.principalId
output workerName string = worker.?outputs.name ?? ''
output mappingEndpointOrigins array = mappingEndpoint.?outputs.origins ?? []
output mappingEndpointGrantArguments array = mappingEndpoint.?outputs.grantArguments ?? []
