// Qualification-only caller job for deploy/azure/qualify.sh; never part of a deployment. The mapping endpoints
// are internal to the Container Apps environment, so the harness reaches them from a manual job inside it. Each
// execution runs caller.js once in the Bun image that deploy/container/Dockerfile pins, without an identity,
// registry credential or volume. The harness obtains app-only tokens on the operator's machine and redeploys this
// job with them as secure parameters when they change; they never appear in the job's arguments or output.
param location string = resourceGroup().location
param name string
param environmentName string
@description('Bun image by digest, the BUN_IMAGE of deploy/container/Dockerfile.')
param image string
@secure()
@description('Token for the endpoint audience.')
param callerToken string
@secure()
@description('The same caller\'s token for another audience.')
param foreignToken string
@secure()
@description('The run\'s first caller token, kept until it has expired.')
param expiringToken string
param tags object

resource environment 'Microsoft.App/managedEnvironments@2024-03-01' existing = {
  name: environmentName
}

resource caller 'Microsoft.App/jobs@2024-03-01' = {
  name: name
  location: location
  tags: tags
  properties: {
    environmentId: environment.id
    workloadProfileName: 'Consumption'
    configuration: {
      triggerType: 'Manual'
      replicaTimeout: 600
      replicaRetryLimit: 0
      manualTriggerConfig: {
        parallelism: 1
        replicaCompletionCount: 1
      }
      secrets: [
        { name: 'caller-token', value: callerToken }
        { name: 'foreign-token', value: foreignToken }
        { name: 'expiring-token', value: expiringToken }
      ]
    }
    template: {
      containers: [
        {
          name: 'caller'
          image: image
          command: ['bun', '-e', loadTextContent('caller.js')]
          // Each execution supplies its own arguments: invoke CALL ORIGIN REQUEST CREDENTIAL, or ready CALL ORIGIN.
          args: ['ready', 'none', 'https://localhost']
          env: [
            { name: 'BRAID_QUALIFY_CALLER_TOKEN', secretRef: 'caller-token' }
            { name: 'BRAID_QUALIFY_FOREIGN_TOKEN', secretRef: 'foreign-token' }
            { name: 'BRAID_QUALIFY_EXPIRING_TOKEN', secretRef: 'expiring-token' }
          ]
          resources: {
            cpu: json('0.25')
            memory: '0.5Gi'
          }
        }
      ]
    }
  }
}

output name string = caller.name
