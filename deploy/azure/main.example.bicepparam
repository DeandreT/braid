// Copy to main.bicepparam and replace the placeholders. Contains no secrets: the PostgreSQL
// administrator password is read from the deploying shell's environment.
using './main.bicep'

param location = 'eastus2'
param name = 'braid'
param entraTenantId = '00000000-0000-0000-0000-000000000000'
param entraClientId = '00000000-0000-0000-0000-000000000000'
param organizationId = '00000000-0000-0000-0000-000000000000'
param operatorPrincipalId = '00000000-0000-0000-0000-000000000000'
param operatorIpRanges = []
param customDomain = ''
param customDomainCertificateId = ''
param postgresAdministratorPassword = readEnvironmentVariable('BRAID_POSTGRES_ADMIN_PASSWORD')
param deployApplication = false
param imageTag = ''
// Native work through the Ferrule service: 'service' (internal container app) or 'sessions'
// (dynamic sessions). The pins come from initialize-secrets.sh; the identity from the image label.
param ferruleRunner = 'worker'
param ferruleServiceImage = ''
param ferruleServiceIdentity = ''
param ferruleServiceCertificateSha256 = ''
param ferruleServiceClientCertificateSha256 = ''
// Native upload and derivation, previews, regression runs and builds through the Ferrule service, with
// the worker app; builds need the compile profile and its toolchain pin. Chat requires native work.
param nativeWorkEnabled = false
param ferruleServiceCompile = false
param ferruleCompileToolchain = ''
param ferruleServiceWorkerClientCertificateSha256 = ''
param chatEnabled = false
// Bearer access for signed-in users through allowlisted public clients such as the braid CLI: the Session
// app registration exposes apiBearerScope (the scp name, for example 'braid.api') and accepts v2 tokens.
param apiBearerEnabled = false
param apiBearerScope = ''
param apiBearerClientIds = []
// Mapping endpoints execute through the Ferrule service runner: one entry per registered target,
// for example { name: 'orders', projectId: '…', mappingId: '…', environmentId: '…',
// environmentVersion: 1, targetId: '…', callers: [{ grantId: '…', objectId: '…' }] }.
param mappingEndpoints = []
param mappingEndpointAudience = ''
param mappingEndpointAdministratorId = ''
param ferruleServiceEndpointClientCertificateSha256 = ''
param compiledRuntimeIdentity = ''
