// The Braid container app (server plus same-replica edge) and its manual operator jobs.
// Every credential is a Key Vault reference resolved with the application identity.
param location string
param name string
param jobPrefix string
param environmentName string
param keyRingStorageName string
param identityId string
param identityClientId string
param registryServer string
param imageTag string
param vaultUri string
param databaseHost string
param postgresAdministratorLogin string
param entraTenantId string
param entraClientId string
param organizationId string
param customDomain string
param customDomainCertificateId string
param trustedIngressRange string
param enableMetadataEdits bool
param enableProjectCreation bool
param inspectionWorkerUrl string
param inspectionWorkerCertificateSha256 string
param ferruleWorkerImage string
@allowed(['worker', 'service', 'sessions'])
param ferruleRunner string
param ferruleServiceUrl string
param ferruleServiceCertificateSha256 string
param ferruleSessionsEndpoint string
param ferruleServiceIdentity string
@description('Optional compile toolchain pin beside the service and sessions runners; empty keeps the native identity.')
param ferruleCompileToolchain string = ''
@description('Native upload, report reads and derivation through the selected Ferrule runner, with previews, regression runs and builds recorded for the worker app.')
param nativeWorkEnabled bool = false
@description('Revision chat; requires nativeWorkEnabled.')
param chatEnabled bool = false
@description('Bearer access for signed-in users through the allowlisted public clients; the audience is entraClientId.')
param apiBearerEnabled bool = false
param apiBearerScope string = ''
param apiBearerClientIds array = []
param privateObjectsEndpoint string
param privateObjectsContainer string
param tags object

// The provisioning job uses the PostgreSQL client from the image whose major version the
// migrations are qualified against.
var postgresClientImage = 'docker.io/library/postgres@sha256:ec9f656f17c8b4dfebf49e58cf5c46d493d4846d4ea6d8b85d0637f68ee33bfc'
var braidImage = '${registryServer}/braid:${imageTag}'
var edgeImage = '${registryServer}/braid-edge:${imageTag}'
var applicationName = 'Braid.Session'
var keyDirectory = '/var/lib/braid/keys/ring'
var keyRingMountOptions = 'uid=1654,gid=1654,dir_mode=0700,file_mode=0600,nobrl,mfsymlinks,cache=strict'

resource environment 'Microsoft.App/managedEnvironments@2024-03-01' existing = {
  name: environmentName
}

var publicHost = empty(customDomain) ? '${name}.${environment.properties.defaultDomain}' : customDomain
var registries = [
  {
    server: registryServer
    identity: identityId
  }
]

// Key Vault secret names; `pg-*-connection` and the certificates are created by initialize-secrets.sh.
func vaultSecret(vaultUri string, identityId string, name string, secret string) object => {
  name: name
  keyVaultUrl: '${vaultUri}secrets/${secret}'
  identity: identityId
}

var roles = [
  { key: 'migrator', env: 'MIGRATOR' }
  { key: 'runtime', env: 'RUNTIME' }
  { key: 'ingest', env: 'INGEST' }
  { key: 'catalog', env: 'CATALOG' }
  { key: 'metadata', env: 'METADATA' }
  { key: 'creator', env: 'CREATOR' }
  { key: 'report', env: 'REPORT' }
  { key: 'derivation', env: 'DERIVATION' }
  { key: 'maintenance', env: 'MAINTENANCE' }
  { key: 'identity', env: 'IDENTITY' }
  { key: 'payload-permission', env: 'PAYLOAD_PERMISSION' }
  { key: 'session-reader', env: 'SESSION_READER' }
  { key: 'session-writer', env: 'SESSION_WRITER' }
  { key: 'chat-reader', env: 'CHAT_READER' }
  { key: 'chat-writer', env: 'CHAT_WRITER' }
  { key: 'chat-lifecycle', env: 'CHAT_LIFECYCLE' }
  { key: 'chat-approver', env: 'CHAT_APPROVER' }
  { key: 'draft-reader', env: 'DRAFT_READER' }
  { key: 'draft-writer', env: 'DRAFT_WRITER' }
  { key: 'preview-reader', env: 'PREVIEW_READER' }
  { key: 'preview-writer', env: 'PREVIEW_WRITER' }
  { key: 'case-reader', env: 'CASE_READER' }
  { key: 'case-writer', env: 'CASE_WRITER' }
  { key: 'run-reader', env: 'RUN_READER' }
  { key: 'run-writer', env: 'RUN_WRITER' }
  { key: 'comparison', env: 'COMPARISON' }
  { key: 'build-reader', env: 'BUILD_READER' }
  { key: 'build-writer', env: 'BUILD_WRITER' }
  { key: 'build-run-reader', env: 'BUILD_RUN_READER' }
  { key: 'build-run-writer', env: 'BUILD_RUN_WRITER' }
  { key: 'build-dispatcher', env: 'BUILD_DISPATCHER' }
  { key: 'build-metadata', env: 'BUILD_METADATA' }
  { key: 'qualification-reader', env: 'QUALIFICATION_READER' }
  { key: 'qualification-writer', env: 'QUALIFICATION_WRITER' }
  { key: 'release-reader', env: 'RELEASE_READER' }
  { key: 'release-writer', env: 'RELEASE_WRITER' }
  { key: 'invocation-grant-writer', env: 'INVOCATION_GRANT_WRITER' }
  { key: 'invocation-writer', env: 'INVOCATION_WRITER' }
  { key: 'api-audit-writer', env: 'API_AUDIT_WRITER' }
  { key: 'trigger-worker', env: 'TRIGGER_WORKER' }
]

// A separately hosted inspection worker; the settings stay dormant until native upload or
// derivation is enabled.
var inspectionWorker = !empty(inspectionWorkerUrl)

// Private objects live in the Blob container through the application identity. These carry no
// secret; they stay dormant until a feature that reads or writes private objects is enabled.
var privateStorageEnvironment = [
  { name: 'BRAID_PRIVATE_STORAGE_KIND', value: 'azure-blob' }
  { name: 'BRAID_PRIVATE_STORAGE_BLOB_ENDPOINT', value: privateObjectsEndpoint }
  { name: 'BRAID_PRIVATE_STORAGE_BLOB_CONTAINER', value: privateObjectsContainer }
  { name: 'BRAID_PRIVATE_STORAGE_BLOB_CREDENTIAL', value: 'managed-identity' }
  { name: 'BRAID_PRIVATE_STORAGE_BLOB_CLIENT_ID', value: identityClientId }
]

// Previews, regression cases and runs, and builds: each login reaches the Session host only as an owner-only
// file that the entrypoint writes (Key Vault login key, host setting and private file name).
var nativeWorkFiles = [
  { key: 'preview-reader', setting: 'BRAID_POSTGRES_PREVIEW_READER_CONNECTION_STRING', file: 'preview-reader' }
  { key: 'preview-writer', setting: 'BRAID_POSTGRES_PREVIEW_WRITER_CONNECTION_STRING', file: 'preview-writer' }
  { key: 'case-reader', setting: 'BRAID_REGRESSION_CASE_READER_CONNECTION_STRING', file: 'case-reader' }
  { key: 'case-writer', setting: 'BRAID_REGRESSION_CASE_WRITER_CONNECTION_STRING', file: 'case-writer' }
  { key: 'run-reader', setting: 'BRAID_REGRESSION_RUN_READER_CONNECTION_STRING', file: 'run-reader' }
  { key: 'run-writer', setting: 'BRAID_REGRESSION_RUN_WRITER_CONNECTION_STRING', file: 'run-writer' }
  { key: 'build-metadata', setting: 'BRAID_BUILD_METADATA_CONNECTION_STRING', file: 'build-metadata' }
  { key: 'build-writer', setting: 'BRAID_BUILD_CONTROL_CONNECTION_STRING', file: 'build-control' }
  { key: 'runtime', setting: 'BRAID_BUILD_ACCESS_CONNECTION_STRING', file: 'build-access' }
  { key: 'report', setting: 'BRAID_BUILD_REPORT_CONNECTION_STRING', file: 'build-report' }
  { key: 'case-reader', setting: 'BRAID_BUILD_CASE_READER_CONNECTION_STRING', file: 'build-case-reader' }
  { key: 'run-reader', setting: 'BRAID_BUILD_EVIDENCE_READER_CONNECTION_STRING', file: 'build-evidence' }
  { key: 'build-run-reader', setting: 'BRAID_BUILD_RUN_READER_CONNECTION_STRING', file: 'build-run-reader' }
  { key: 'build-run-writer', setting: 'BRAID_BUILD_RUN_WRITER_CONNECTION_STRING', file: 'build-run-writer' }
  { key: 'release-reader', setting: 'BRAID_RELEASE_READER_CONNECTION_STRING', file: 'release-reader' }
  { key: 'release-writer', setting: 'BRAID_RELEASE_WRITER_CONNECTION_STRING', file: 'release-writer' }
  { key: 'qualification-reader', setting: 'BRAID_QUALIFICATION_READER_CONNECTION_STRING', file: 'qualification-reader' }
]
var chatFiles = [
  { key: 'chat-reader', setting: 'BRAID_CHAT_READER_CONNECTION_STRING', file: 'chat-reader-connection' }
  { key: 'chat-writer', setting: 'BRAID_CHAT_WRITER_CONNECTION_STRING', file: 'chat-writer-connection' }
  { key: 'chat-approver', setting: 'BRAID_CHAT_APPROVER_CONNECTION_STRING', file: 'chat-approver-connection' }
  { key: 'payload-permission', setting: 'BRAID_CHAT_PAYLOAD_PERMISSION_CONNECTION_STRING', file: 'chat-payload-permission-connection' }
]
// Mutating bearer requests are audited through their own append-only login.
var bearerFiles = [
  { key: 'api-audit-writer', setting: 'BRAID_API_AUDIT_WRITER_CONNECTION_STRING', file: 'api-audit-writer' }
]
var serverFiles = concat(nativeWorkEnabled ? nativeWorkFiles : [], chatEnabled ? chatFiles : [], apiBearerEnabled ? bearerFiles : [])

// Container app secret names; `<key>-connection` resolves `pg-<key>-connection`, the rest are named below.
var serverSecretNames = union([
  'session-key-pem'
  'entra-client-pem'
  'catalog-connection'
  'session-reader-connection'
  'session-writer-connection'
  'identity-connection'
], enableMetadataEdits ? ['metadata-connection'] : [], enableProjectCreation ? ['creator-connection'] : [],
  inspectionWorker ? ['inspection-client-pem'] : [], ferruleRunner == 'service' ? ['ferrule-service-client-pem'] : [],
  nativeWorkEnabled ? ['ingest-connection', 'report-connection', 'derivation-connection'] : [],
  chatEnabled ? ['chat-profiles', 'model-provider-credential'] : [], map(serverFiles, login => '${login.key}-connection'))
var vaultNames = {
  'session-key-pem': 'braid-session-key'
  'entra-client-pem': 'braid-entra-client'
  'inspection-client-pem': 'braid-inspection-client'
  'ferrule-service-client-pem': 'braid-ferrule-service-client'
  'chat-profiles': 'braid-chat-profiles'
  'model-provider-credential': 'braid-model-provider-credential'
}

var serverEnvironment = concat([
  { name: 'BRAID_CATALOG_AUTH_MODE', value: 'session' }
  { name: 'BRAID_CATALOG_ENABLED', value: 'true' }
  { name: 'BRAID_PUBLIC_ORIGIN', value: 'https://${publicHost}' }
  { name: 'BRAID_HOST_TRANSPORT', value: 'trustedProxyOneHop' }
  { name: 'BRAID_LISTEN_ADDR', value: '127.0.0.1:8081' }
  { name: 'BRAID_KNOWN_PROXY_IP', value: '127.0.0.1' }
  { name: 'BRAID_SESSION_ORGANIZATION_ID', value: organizationId }
  { name: 'BRAID_SESSION_APPLICATION_NAME', value: applicationName }
  { name: 'BRAID_SESSION_KEY_DIRECTORY', value: keyDirectory }
  { name: 'BRAID_ENTRA_TENANT_ID', value: entraTenantId }
  { name: 'BRAID_ENTRA_CLIENT_ID', value: entraClientId }
  { name: 'BRAID_DELIVER_SESSION_KEY_PEM', secretRef: 'session-key-pem' }
  { name: 'BRAID_DELIVER_ENTRA_CLIENT_PEM', secretRef: 'entra-client-pem' }
  { name: 'BRAID_DELIVER_SESSION_READER_CONNECTION_STRING', secretRef: 'session-reader-connection' }
  { name: 'BRAID_DELIVER_SESSION_WRITER_CONNECTION_STRING', secretRef: 'session-writer-connection' }
  { name: 'BRAID_DELIVER_IDENTITY_READER_CONNECTION_STRING', secretRef: 'identity-connection' }
  { name: 'BRAID_CATALOG_CONNECTION_STRING', secretRef: 'catalog-connection' }
  // The host accepts only lowercase true/false; ARM string() would capitalize booleans.
  { name: 'BRAID_METADATA_EDITS_ENABLED', value: enableMetadataEdits ? 'true' : 'false' }
  { name: 'BRAID_PROJECT_CREATION_ENABLED', value: enableProjectCreation ? 'true' : 'false' }
], privateStorageEnvironment, enableMetadataEdits ? [
  { name: 'BRAID_POSTGRES_METADATA_CONNECTION_STRING', secretRef: 'metadata-connection' }
] : [], enableProjectCreation ? [
  { name: 'BRAID_POSTGRES_PROJECT_CREATION_CONNECTION_STRING', secretRef: 'creator-connection' }
] : [], inspectionWorker ? [
  { name: 'BRAID_INSPECTION_WORKER_URL', value: inspectionWorkerUrl }
  { name: 'BRAID_INSPECTION_WORKER_CERTIFICATE_SHA256', value: inspectionWorkerCertificateSha256 }
  { name: 'BRAID_FERRULE_WORKER_IMAGE', value: ferruleWorkerImage }
  { name: 'BRAID_DELIVER_INSPECTION_CLIENT_PEM', secretRef: 'inspection-client-pem' }
] : [], ferruleRunner == 'service' ? [
  // The Ferrule service in this environment, pinned in both directions over its TCP ingress.
  { name: 'BRAID_FERRULE_RUNNER', value: 'service' }
  { name: 'BRAID_FERRULE_SERVICE_URL', value: ferruleServiceUrl }
  { name: 'BRAID_FERRULE_SERVICE_CERTIFICATE_SHA256', value: ferruleServiceCertificateSha256 }
  { name: 'BRAID_FERRULE_WORKER_IMAGE', value: ferruleServiceIdentity }
  { name: 'BRAID_DELIVER_FERRULE_SERVICE_CLIENT_PEM', secretRef: 'ferrule-service-client-pem' }
] : [], ferruleRunner != 'worker' && !empty(ferruleCompileToolchain) ? [
  { name: 'BRAID_FERRULE_COMPILE_TOOLCHAIN', value: ferruleCompileToolchain }
] : [], ferruleRunner == 'sessions' ? [
  // Dynamic sessions with this app's managed identity; no credential is configured.
  { name: 'BRAID_FERRULE_RUNNER', value: 'azureContainerAppsSessions' }
  { name: 'BRAID_FERRULE_SESSIONS_POOL_ENDPOINT', value: ferruleSessionsEndpoint }
  { name: 'BRAID_FERRULE_SESSIONS_MANAGED_IDENTITY_CLIENT_ID', value: identityClientId }
  { name: 'BRAID_FERRULE_WORKER_IMAGE', value: ferruleServiceIdentity }
] : [], nativeWorkEnabled ? [
  // Native upload, report reads and derivation run here through the selected runner; previews, regression runs
  // and builds are recorded here and executed by the worker app.
  { name: 'BRAID_REVISION_INGESTION_ENABLED', value: 'true' }
  { name: 'BRAID_MFD_INTERCHANGE_ENABLED', value: ferruleRunner == 'worker' ? 'false' : 'true' }
  { name: 'BRAID_POSTGRES_INGESTION_CONNECTION_STRING', secretRef: 'ingest-connection' }
  { name: 'BRAID_REVISION_REPORTS_ENABLED', value: 'true' }
  { name: 'BRAID_POSTGRES_REPORT_CONNECTION_STRING', secretRef: 'report-connection' }
  { name: 'BRAID_REVISION_REPORT_DERIVATION_ENABLED', value: 'true' }
  { name: 'BRAID_POSTGRES_DERIVATION_CONNECTION_STRING', secretRef: 'derivation-connection' }
  { name: 'BRAID_PREVIEWS_ENABLED', value: 'true' }
  { name: 'BRAID_REGRESSIONS_ENABLED', value: 'true' }
  { name: 'BRAID_BUILDS_ENABLED', value: 'true' }
  { name: 'BRAID_RELEASE_OPERATIONS_ENABLED', value: 'true' }
] : [], chatEnabled ? [
  // The profile document and the model credential are private files; the profile's credential entry names
  // /run/braid/secrets/model-provider-credential or a managed-identity token.
  { name: 'BRAID_CHAT_ENABLED', value: 'true' }
  { name: 'BRAID_CHAT_PROFILES_FILE', value: '/run/braid/secrets/chat-profiles' }
  { name: 'BRAID_DELIVER_FILE_CHAT_PROFILES', secretRef: 'chat-profiles' }
  { name: 'BRAID_DELIVER_FILE_MODEL_PROVIDER_CREDENTIAL', secretRef: 'model-provider-credential' }
] : [], apiBearerEnabled ? [
  // Signed-in users' delegated v2 access tokens for the Session app's exposed scope, from the allowlisted clients.
  { name: 'BRAID_API_BEARER_ENABLED', value: 'true' }
  { name: 'BRAID_API_BEARER_AUDIENCE', value: entraClientId }
  { name: 'BRAID_API_BEARER_SCOPE', value: apiBearerScope }
  { name: 'BRAID_API_BEARER_CLIENT_IDS', value: join(apiBearerClientIds, ',') }
] : [], flatten(map(serverFiles, login => [
  { name: 'BRAID_DELIVER_FILE_${toUpper(replace(login.file, '-', '_'))}', secretRef: '${login.key}-connection' }
  { name: '${login.setting}_FILE', value: '/run/braid/secrets/${login.file}' }
])))

var healthProbe = {
  path: '/api/v1/health'
  port: 8080
}

resource app 'Microsoft.App/containerApps@2024-03-01' = {
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
        external: true
        targetPort: 8080
        transport: 'http'
        allowInsecure: false
        customDomains: empty(customDomain) ? [] : [
          {
            name: customDomain
            bindingType: empty(customDomainCertificateId) ? 'Disabled' : 'SniEnabled'
            certificateId: empty(customDomainCertificateId) ? null : customDomainCertificateId
          }
        ]
      }
      registries: registries
      secrets: [for secret in serverSecretNames: vaultSecret(vaultUri, identityId, secret, vaultNames[?secret] ?? 'pg-${secret}')]
    }
    template: {
      containers: [
        {
          name: 'server'
          image: braidImage
          args: ['server']
          env: serverEnvironment
          resources: {
            cpu: json('0.75')
            memory: '1.5Gi'
          }
          volumeMounts: [
            {
              volumeName: 'session-keys'
              mountPath: '/var/lib/braid/keys'
            }
          ]
        }
        {
          name: 'edge'
          image: edgeImage
          env: [
            { name: 'BRAID_PUBLIC_HOST', value: publicHost }
            { name: 'BRAID_EDGE_TRUSTED_PROXIES', value: trustedIngressRange }
          ]
          resources: {
            cpu: json('0.25')
            memory: '0.5Gi'
          }
          probes: [
            {
              type: 'Startup'
              httpGet: healthProbe
              initialDelaySeconds: 5
              periodSeconds: 10
              failureThreshold: 10
            }
            {
              type: 'Readiness'
              httpGet: union(healthProbe, { path: '/api/v1/health/ready' })
              periodSeconds: 10
              failureThreshold: 3
            }
            {
              type: 'Liveness'
              httpGet: healthProbe
              periodSeconds: 30
              failureThreshold: 3
            }
          ]
        }
      ]
      // One instance: the session key ring and its host profile are qualified for a single API process.
      scale: {
        minReplicas: 1
        maxReplicas: 1
      }
      volumes: [
        {
          name: 'session-keys'
          storageType: 'AzureFile'
          storageName: keyRingStorageName
          mountOptions: keyRingMountOptions
        }
      ]
    }
  }
}

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

// Creates the database, restricted logins, database ownership and parameter grant.
resource databaseRoles 'Microsoft.App/jobs@2024-03-01' = {
  name: '${jobPrefix}-database-roles'
  location: location
  tags: tags
  identity: jobIdentity
  properties: {
    environmentId: environment.id
    workloadProfileName: 'Consumption'
    configuration: union(jobConfiguration, {
      secrets: concat([vaultSecret(vaultUri, identityId, 'postgres-admin-password', 'postgres-admin-password')],
        map(roles, role => vaultSecret(vaultUri, identityId, 'pg-${role.key}-password', 'pg-${role.key}-password')))
    })
    template: {
      containers: [
        {
          name: 'database-roles'
          image: postgresClientImage
          command: ['bash', '-c', loadTextContent('../provision-database.sh')]
          env: concat([
            { name: 'PGHOST', value: databaseHost }
            { name: 'PGPORT', value: '5432' }
            { name: 'PGUSER', value: postgresAdministratorLogin }
            { name: 'PGPASSWORD', secretRef: 'postgres-admin-password' }
            // The client image has no CA bundle; the connection stays inside the virtual network.
            { name: 'PGSSLMODE', value: 'require' }
            { name: 'PGCONNECT_TIMEOUT', value: '15' }
          ], map(roles, role => { name: 'BRAID_${role.env}_PASSWORD', secretRef: 'pg-${role.key}-password' }))
          resources: jobResources
        }
      ]
    }
  }
}

resource migrate 'Microsoft.App/jobs@2024-03-01' = {
  name: '${jobPrefix}-migrate'
  location: location
  tags: tags
  identity: jobIdentity
  properties: {
    environmentId: environment.id
    workloadProfileName: 'Consumption'
    configuration: union(jobConfiguration, {
      secrets: [vaultSecret(vaultUri, identityId, 'migrator-connection', 'pg-migrator-connection')]
    })
    template: {
      containers: [
        {
          name: 'migrate'
          image: braidImage
          args: ['migrate']
          env: [
            { name: 'BRAID_MIGRATION_CONNECTION_STRING', secretRef: 'migrator-connection' }
          ]
          resources: jobResources
        }
      ]
    }
  }
}

resource sessionKeys 'Microsoft.App/jobs@2024-03-01' = {
  name: '${jobPrefix}-session-keys'
  location: location
  tags: tags
  identity: jobIdentity
  properties: {
    environmentId: environment.id
    workloadProfileName: 'Consumption'
    configuration: union(jobConfiguration, {
      secrets: [vaultSecret(vaultUri, identityId, 'session-key-pem', 'braid-session-key')]
    })
    template: {
      containers: [
        {
          name: 'session-keys'
          image: braidImage
          args: ['bootstrap-session-keys']
          env: [
            { name: 'BRAID_SESSION_ORGANIZATION_ID', value: organizationId }
            { name: 'BRAID_SESSION_APPLICATION_NAME', value: applicationName }
            { name: 'BRAID_SESSION_KEY_DIRECTORY', value: keyDirectory }
            { name: 'BRAID_DELIVER_SESSION_KEY_PEM', secretRef: 'session-key-pem' }
          ]
          resources: jobResources
          volumeMounts: [
            {
              volumeName: 'session-keys'
              mountPath: '/var/lib/braid/keys'
            }
          ]
        }
      ]
      volumes: [
        {
          name: 'session-keys'
          storageType: 'AzureFile'
          storageName: keyRingStorageName
          mountOptions: keyRingMountOptions
        }
      ]
    }
  }
}

// Provisions the claim-aware store in the existing private container once; a second run
// refuses the initialized container.
resource privateStorage 'Microsoft.App/jobs@2024-03-01' = {
  name: '${jobPrefix}-private-storage'
  location: location
  tags: tags
  identity: jobIdentity
  properties: {
    environmentId: environment.id
    workloadProfileName: 'Consumption'
    configuration: jobConfiguration
    template: {
      containers: [
        {
          name: 'private-storage'
          image: braidImage
          args: ['storage', 'initialize-blob']
          env: privateStorageEnvironment
          resources: jobResources
        }
      ]
    }
  }
}

// One bounded private-object maintenance run; supply `maintenance run-once ORGANIZATION_ID
// PROJECT_ID` (and optional cursors) as arguments when starting an execution.
resource maintenance 'Microsoft.App/jobs@2024-03-01' = {
  name: '${jobPrefix}-maintenance'
  location: location
  tags: tags
  identity: jobIdentity
  properties: {
    environmentId: environment.id
    workloadProfileName: 'Consumption'
    configuration: union(jobConfiguration, {
      secrets: [vaultSecret(vaultUri, identityId, 'maintenance-connection', 'pg-maintenance-connection')]
    })
    template: {
      containers: [
        {
          name: 'maintenance'
          image: braidImage
          args: ['maintenance', '--help']
          env: concat([
            { name: 'BRAID_MAINTENANCE_CONNECTION_STRING', secretRef: 'maintenance-connection' }
          ], privateStorageEnvironment)
          resources: jobResources
        }
      ]
    }
  }
}

// Owner commands with the migrator credential; supply the command as arguments when starting
// an execution. `braid-owner-input` becomes standard input for commands that read it.
resource owner 'Microsoft.App/jobs@2024-03-01' = {
  name: '${jobPrefix}-owner'
  location: location
  tags: tags
  identity: jobIdentity
  properties: {
    environmentId: environment.id
    workloadProfileName: 'Consumption'
    configuration: union(jobConfiguration, {
      secrets: [
        vaultSecret(vaultUri, identityId, 'migrator-connection', 'pg-migrator-connection')
        vaultSecret(vaultUri, identityId, 'owner-input', 'braid-owner-input')
      ]
    })
    template: {
      containers: [
        {
          name: 'owner'
          image: braidImage
          args: ['database']
          env: [
            { name: 'BRAID_MIGRATION_CONNECTION_STRING', secretRef: 'migrator-connection' }
            { name: 'BRAID_DELIVER_OWNER_INPUT', secretRef: 'owner-input' }
          ]
          resources: jobResources
        }
      ]
    }
  }
}

output publicHost string = publicHost
output redirectUri string = 'https://${publicHost}/api/auth/callback/entra'
output fqdn string = app.properties.configuration.ingress.fqdn
