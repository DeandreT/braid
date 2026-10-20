// Copy to main.bicepparam and replace the placeholders. Contains no secrets: the PostgreSQL
// administrator password is read from the deploying shell's environment.
using './main.bicep'

param location = 'eastus2'
param name = 'weft'
param entraTenantId = '00000000-0000-0000-0000-000000000000'
param entraClientId = '00000000-0000-0000-0000-000000000000'
param organizationId = '00000000-0000-0000-0000-000000000000'
param operatorPrincipalId = '00000000-0000-0000-0000-000000000000'
param operatorIpRanges = []
param customDomain = ''
param customDomainCertificateId = ''
param postgresAdministratorPassword = readEnvironmentVariable('WEFT_POSTGRES_ADMIN_PASSWORD')
param deployApplication = false
param imageTag = ''
