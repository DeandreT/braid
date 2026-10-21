// The Weft container app (server plus same-replica edge) and its manual operator jobs.
// Every credential is a Key Vault reference resolved with the application identity.
param location string
param name string
param jobPrefix string
param environmentName string
param keyRingStorageName string
param identityId string
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
param tags object

// The provisioning job uses the PostgreSQL client from the image whose major version the
// migrations are qualified against.
var postgresClientImage = 'docker.io/library/postgres@sha256:ec9f656f17c8b4dfebf49e58cf5c46d493d4846d4ea6d8b85d0637f68ee33bfc'
var weftImage = '${registryServer}/weft:${imageTag}'
var edgeImage = '${registryServer}/weft-edge:${imageTag}'
var applicationName = 'Weft.Session'
var keyDirectory = '/var/lib/weft/keys/ring'
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
  { key: 'preview-reader', env: 'PREVIEW_READER' }
  { key: 'preview-writer', env: 'PREVIEW_WRITER' }
  { key: 'case-reader', env: 'CASE_READER' }
  { key: 'case-writer', env: 'CASE_WRITER' }
  { key: 'run-reader', env: 'RUN_READER' }
  { key: 'run-writer', env: 'RUN_WRITER' }
  { key: 'build-reader', env: 'BUILD_READER' }
  { key: 'build-writer', env: 'BUILD_WRITER' }
  { key: 'build-run-reader', env: 'BUILD_RUN_READER' }
  { key: 'build-run-writer', env: 'BUILD_RUN_WRITER' }
  { key: 'build-dispatcher', env: 'BUILD_DISPATCHER' }
]

var serverSecretNames = concat([
  'session-key-pem'
  'entra-client-pem'
  'catalog-connection'
  'session-reader-connection'
  'session-writer-connection'
  'identity-connection'
], enableMetadataEdits ? ['metadata-connection'] : [], enableProjectCreation ? ['creator-connection'] : [])
var vaultNames = {
  'session-key-pem': 'weft-session-key'
  'entra-client-pem': 'weft-entra-client'
  'catalog-connection': 'pg-catalog-connection'
  'session-reader-connection': 'pg-session-reader-connection'
  'session-writer-connection': 'pg-session-writer-connection'
  'identity-connection': 'pg-identity-connection'
  'metadata-connection': 'pg-metadata-connection'
  'creator-connection': 'pg-creator-connection'
}

var serverEnvironment = concat([
  { name: 'WEFT_CATALOG_AUTH_MODE', value: 'session' }
  { name: 'WEFT_CATALOG_ENABLED', value: 'true' }
  { name: 'WEFT_PUBLIC_ORIGIN', value: 'https://${publicHost}' }
  { name: 'WEFT_HOST_TRANSPORT', value: 'trustedProxyOneHop' }
  { name: 'WEFT_LISTEN_ADDR', value: '127.0.0.1:8081' }
  { name: 'WEFT_KNOWN_PROXY_IP', value: '127.0.0.1' }
  { name: 'WEFT_SESSION_ORGANIZATION_ID', value: organizationId }
  { name: 'WEFT_SESSION_APPLICATION_NAME', value: applicationName }
  { name: 'WEFT_SESSION_KEY_DIRECTORY', value: keyDirectory }
  { name: 'WEFT_ENTRA_TENANT_ID', value: entraTenantId }
  { name: 'WEFT_ENTRA_CLIENT_ID', value: entraClientId }
  { name: 'WEFT_DELIVER_SESSION_KEY_PEM', secretRef: 'session-key-pem' }
  { name: 'WEFT_DELIVER_ENTRA_CLIENT_PEM', secretRef: 'entra-client-pem' }
  { name: 'WEFT_DELIVER_SESSION_READER_CONNECTION_STRING', secretRef: 'session-reader-connection' }
  { name: 'WEFT_DELIVER_SESSION_WRITER_CONNECTION_STRING', secretRef: 'session-writer-connection' }
  { name: 'WEFT_DELIVER_IDENTITY_READER_CONNECTION_STRING', secretRef: 'identity-connection' }
  { name: 'WEFT_CATALOG_CONNECTION_STRING', secretRef: 'catalog-connection' }
  // The host accepts only lowercase true/false; ARM string() would capitalize booleans.
  { name: 'WEFT_METADATA_EDITS_ENABLED', value: enableMetadataEdits ? 'true' : 'false' }
  { name: 'WEFT_PROJECT_CREATION_ENABLED', value: enableProjectCreation ? 'true' : 'false' }
], enableMetadataEdits ? [
  { name: 'WEFT_POSTGRES_METADATA_CONNECTION_STRING', secretRef: 'metadata-connection' }
] : [], enableProjectCreation ? [
  { name: 'WEFT_POSTGRES_PROJECT_CREATION_CONNECTION_STRING', secretRef: 'creator-connection' }
] : [])

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
      secrets: [for secret in serverSecretNames: vaultSecret(vaultUri, identityId, secret, vaultNames[secret])]
    }
    template: {
      containers: [
        {
          name: 'server'
          image: weftImage
          args: ['server']
          env: serverEnvironment
          resources: {
            cpu: json('0.75')
            memory: '1.5Gi'
          }
          volumeMounts: [
            {
              volumeName: 'session-keys'
              mountPath: '/var/lib/weft/keys'
            }
          ]
        }
        {
          name: 'edge'
          image: edgeImage
          env: [
            { name: 'WEFT_PUBLIC_HOST', value: publicHost }
            { name: 'WEFT_EDGE_TRUSTED_PROXIES', value: trustedIngressRange }
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
              httpGet: healthProbe
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
          ], map(roles, role => { name: 'WEFT_${role.env}_PASSWORD', secretRef: 'pg-${role.key}-password' }))
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
          image: weftImage
          args: ['migrate']
          env: [
            { name: 'WEFT_MIGRATION_CONNECTION_STRING', secretRef: 'migrator-connection' }
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
      secrets: [vaultSecret(vaultUri, identityId, 'session-key-pem', 'weft-session-key')]
    })
    template: {
      containers: [
        {
          name: 'session-keys'
          image: weftImage
          args: ['bootstrap-session-keys']
          env: [
            { name: 'WEFT_SESSION_ORGANIZATION_ID', value: organizationId }
            { name: 'WEFT_SESSION_APPLICATION_NAME', value: applicationName }
            { name: 'WEFT_SESSION_KEY_DIRECTORY', value: keyDirectory }
            { name: 'WEFT_DELIVER_SESSION_KEY_PEM', secretRef: 'session-key-pem' }
          ]
          resources: jobResources
          volumeMounts: [
            {
              volumeName: 'session-keys'
              mountPath: '/var/lib/weft/keys'
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

// Owner commands with the migrator credential; supply the command as arguments when starting
// an execution. `weft-owner-input` becomes standard input for commands that read it.
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
        vaultSecret(vaultUri, identityId, 'owner-input', 'weft-owner-input')
      ]
    })
    template: {
      containers: [
        {
          name: 'owner'
          image: weftImage
          args: ['database']
          env: [
            { name: 'WEFT_MIGRATION_CONNECTION_STRING', secretRef: 'migrator-connection' }
            { name: 'WEFT_DELIVER_OWNER_INPUT', secretRef: 'owner-input' }
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
