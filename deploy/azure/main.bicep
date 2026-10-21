// First Azure-hosted Weft environment. Deploy in two stages: the platform first, then the
// application and jobs after the images and Key Vault secrets exist. See docs/azure-deployment.md.
targetScope = 'resourceGroup'

@description('Azure region for every resource.')
param location string = resourceGroup().location

@description('Short lowercase prefix for resource names.')
@minLength(2)
@maxLength(10)
param name string = 'weft'

@description('Suffix that makes the globally unique names (registry, vault, storage, database server) distinct.')
@minLength(4)
@maxLength(13)
param uniqueSuffix string = uniqueString(resourceGroup().id)

@description('Microsoft Entra tenant ID whose organizational accounts sign in.')
param entraTenantId string

@description('Application (client) ID of the Weft app registration.')
param entraClientId string

@description('Weft organization ID served by this environment (canonical lowercase UUID).')
param organizationId string

@description('Object ID of the operator user or group that initializes Key Vault secrets and certificates. Empty assigns no operator role.')
param operatorPrincipalId string = ''

@description('Public IPv4 ranges (CIDR) allowed to reach Key Vault for operator initialization. Empty leaves the public endpoint open to Entra-authorized callers only.')
param operatorIpRanges array = []

@description('Custom public host name, such as weft.contoso.com. Empty uses the Container Apps default host name.')
param customDomain string = ''

@description('Resource ID of the managed certificate bound to the custom domain. Empty adds the host name without a binding.')
param customDomainCertificateId string = ''

@description('Deploy the application and its jobs. Requires the images in the registry and the Key Vault secrets.')
param deployApplication bool = false

@description('Image tag of the weft and weft-edge repositories in the registry.')
param imageTag string = ''

@description('Enable conditional mapping metadata edits.')
param enableMetadataEdits bool = true

@description('Enable explicit project creation.')
param enableProjectCreation bool = true

@description('HTTPS origin of a separately hosted Weft.Worker inspection service in the virtual network, such as https://10.40.4.4:8443. Container Apps cannot run its Docker runner, so this environment does not create it. Empty leaves the Session host without an inspection worker.')
param inspectionWorkerUrl string = ''

@description('Lowercase SHA-256 of the inspection worker certificate (DER). Required with inspectionWorkerUrl.')
param inspectionWorkerCertificateSha256 string = ''

@description('Immutable lowercase sha256 image ID of the Ferrule worker image that the inspection worker runs. Required with inspectionWorkerUrl.')
param ferruleWorkerImage string = ''

@description('PostgreSQL administrator login, used only by the database provisioning job.')
param postgresAdministratorLogin string = 'weftadmin'

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
  registry: take('${name}acr${compactSuffix}', 50)
  vault: take('${name}-kv-${compactSuffix}', 24)
  storage: take('${name}st${compactSuffix}', 24)
  database: '${name}-pg-${compactSuffix}'
  environment: '${name}-env'
  app: '${name}-app'
}

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

module application 'modules/application.bicep' = if (deployApplication) {
  name: 'application'
  params: {
    location: location
    name: names.app
    jobPrefix: name
    environmentName: environment.outputs.name
    keyRingStorageName: environment.outputs.keyRingStorageName
    identityId: identity.id
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
    inspectionWorkerUrl: inspectionWorkerUrl
    inspectionWorkerCertificateSha256: inspectionWorkerCertificateSha256
    ferruleWorkerImage: ferruleWorkerImage
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
