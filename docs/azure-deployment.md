# Azure deployment

`deploy/azure` describes the first hosted Weft environment as Bicep, using the
[runtime images](runtime-packaging.md). The server runs in the Session profile behind its
same-replica edge, serves the Angular application from the same origin, and reaches every
dependency privately. Tracking work is [#146](https://github.com/DeandreT/weft/issues/146).

| Resource | Configuration |
| --- | --- |
| Virtual network | Subnets for the Container Apps environment, the delegated PostgreSQL server and private endpoints, with private DNS zones for PostgreSQL, Key Vault, Azure Files and Blob Storage. |
| Log Analytics | Container Apps console and system logs (30 days). |
| Container registry | Basic SKU, no admin user; the application identity has only `AcrPull`. |
| Managed identities | One user-assigned identity for the app and jobs: image pull, Key Vault secret reads and `Storage Blob Data Contributor` on the private object container only. A second, `<name>-worker-identity`, belongs to the worker app; see [Native work](#native-work-previews-regression-runs-and-builds). |
| Key Vault | RBAC authorization, purge protection, private endpoint; optional operator IP allow-list. Holds every credential and certificate. |
| Storage account | No public network access; private endpoint; SMB share `weft-session-keys` for the session key ring. |
| Private object storage | A separate account with shared-key access disabled, no public network access and a blob private endpoint; container `weft-private-objects` without anonymous access. Blob versioning and blob soft delete are off so retired objects are not retained; deleted containers are kept for 14 days. Holds every [private object](private-storage-blob.md). |
| PostgreSQL Flexible Server | Version 16, private VNet access only, password authentication, 14-day backups. |
| Container Apps environment | Workload-profile (Consumption) environment in the VNet with public ingress. |
| Container app | `server` and `edge` containers in one replica (fixed at one), ingress on the edge's port 8080, HTTPS only, probes on `/api/v1/health`, key-ring share mounted with UID/GID 1654 and modes 0700/0600. |
| Jobs (manual) | `<name>-database-roles`, `<name>-migrate`, `<name>-session-keys`, `<name>-private-storage`, `<name>-maintenance` and `<name>-owner`. |
| Ferrule service | Optional: the internal container app `<name>-ferrule` or the session pool `<name>-ferrule-sessions`, selected by `ferruleRunner`. See [Ferrule service](#ferrule-service). |
| Worker app | Optional, with `nativeWorkEnabled`: `<name>-worker` runs `Weft.Worker`, which executes previews, regression runs and builds through the Ferrule service. It has no ingress and runs one replica. See [Native work](#native-work-previews-regression-runs-and-builds). |
| Mapping endpoints | Optional: one internal container app `<name>-<endpoint>` per registered release target, and the manual jobs `<name>-endpoint-grants` and `<name>-endpoint-release`. See [Mapping endpoints](#mapping-endpoints). |

Templates contain no secrets. The PostgreSQL administrator password is read from the
deploying shell's environment by the parameter file and also stored in Key Vault for the
provisioning job. Every other credential is generated into Key Vault by
`deploy/azure/initialize-secrets.sh` and referenced by name.

## Operator inputs

| Input | Used for |
| --- | --- |
| Azure subscription and resource group | Deployment scope. The deploying principal needs Owner, or Contributor plus Role Based Access Control Administrator, on the resource group because the templates assign roles. |
| Region | `location`; it must offer Container Apps workload profiles and PostgreSQL Flexible Server. |
| Name prefix | `name` (2–10 lowercase characters); `uniqueSuffix` defaults to a hash of the resource group. |
| Entra tenant ID | `entraTenantId`: the organization whose accounts sign in. |
| Entra app registration | `entraClientId`; see below. |
| Weft organization ID | `organizationId`: a new canonical lowercase UUID, and the organization's display name for the owner command. |
| PostgreSQL administrator password | `WEFT_POSTGRES_ADMIN_PASSWORD` in the deploying shell; keep it for later deployments. |
| Operator object ID | `operatorPrincipalId`: user or group granted Key Vault Secrets Officer and Certificates Officer. |
| Operator IP ranges | Optional `operatorIpRanges` that restrict Key Vault's public endpoint. |
| Custom domain and DNS | Optional `customDomain`, a CNAME to the app's default host name and a TXT record `asuid.<domain>`. |
| Initial users' Entra object IDs | One per user, for identity bindings: the user's **Object ID** in the sign-in tenant (the Entra admin center user page, or the `id` that Microsoft Graph returns for the user). |
| Native work | Optional `nativeWorkEnabled`, with `ferruleRunner`, the Ferrule service image and pins, `ferruleServiceCompile` and `ferruleCompileToolchain`; see [Native work](#native-work-previews-regression-runs-and-builds). |
| Chat profile and model credential | Optional `chatEnabled`: the profile document, and the model-provider credential unless the profile uses the application identity, stored in Key Vault; see [Chat](#chat). |
| Mapping endpoints | Optional `mappingEndpoints`, `mappingEndpointAudience`, `mappingEndpointAdministratorId`, `ferruleServiceEndpointClientCertificateSha256` and `compiledRuntimeIdentity`; see [Mapping endpoints](#mapping-endpoints). |
| Docker inspection worker identity | Optional `workerPrincipalId`: the object ID of the managed identity of a separately hosted [Docker inspection worker](#docker-inspection-worker), granted the same data-plane role on the private object container. |

### Entra app registration

Register a single-tenant web application in the sign-in tenant:

- Redirect URI (Web): `https://<public host>/api/auth/callback/entra`, exactly. The public
  host is the custom domain or the app's default host name (deployment output `publicHost`;
  `entraRedirectUri` repeats the full URI).
- No client secret, implicit grant or front-channel logout URL. Sign-in requests exactly
  `openid profile` with authorization code and PKCE; `profile` is what puts the user's
  object ID in the ID token.
- Certificate credential: upload `weft-entra-client.pem` written by `initialize-secrets.sh`,
  for example `az ad app credential reset --id <client-id> --cert @weft-entra-client.pem --append`.

## Deployment

All commands run from the repository root with an authenticated Azure CLI that includes
Bicep. Copy `deploy/azure/main.example.bicepparam` to a private parameter file and replace
its placeholders.

1. **Platform.** With `deployApplication = false`:

   ```sh
   az group create --name <group> --location <region>
   export WEFT_POSTGRES_ADMIN_PASSWORD='<generated password>'
   az deployment group create --resource-group <group> \
     --template-file deploy/azure/main.bicep --parameters <params>.bicepparam
   ```

   Record the outputs `registryName`, `keyVaultName`, `databaseHost` and
   `environmentDefaultDomain`.

2. **Images.** Build both targets in the registry from the same commit:

   ```sh
   az acr build --registry <registryName> --file deploy/container/Dockerfile --target weft --image weft:<tag> .
   az acr build --registry <registryName> --file deploy/container/Dockerfile --target edge --image weft-edge:<tag> .
   # Only with native work:
   az acr build --registry <registryName> --file deploy/container/Dockerfile --target weft-worker --image weft-worker:<tag> .
   # Only with mapping endpoints:
   az acr build --registry <registryName> --file deploy/container/Dockerfile --target weft-mapping-endpoint --image weft-mapping-endpoint:<tag> .
   ```

   Native work and mapping endpoints also need the Ferrule service image in the registry; see
   [Ferrule service](ferrule-service.md).

3. **Secrets and certificates.** As the operator principal (from an allowed IP when
   `operatorIpRanges` is set):

   ```sh
   deploy/azure/initialize-secrets.sh <keyVaultName> <databaseHost> <private output directory>
   ```

   The script keeps existing passwords, certificates and placeholders, so it can be rerun.
   It writes each certificate pin to `<certificate>.sha256` in the output directory. Upload the
   Entra client certificate as described above.

4. **Application and jobs.** Redeploy with `deployApplication = true` and `imageTag = '<tag>'`.
   The server keeps restarting until the next two steps complete.

5. **Database.** Run the provisioning job, then migrations:

   ```sh
   az containerapp job start --resource-group <group> --name <name>-database-roles
   az containerapp job start --resource-group <group> --name <name>-migrate
   ```

   Follow each execution with `az containerapp job execution list` and its console logs in
   Log Analytics. Provisioning creates the `weft` database, every restricted login,
   the migrator's ownership and its parameter grant in one transaction and refuses to run
   again once roles exist. PostgreSQL 16 makes a non-superuser creator an admin member of
   each role it creates; the migrations refuse such memberships, so the job creates the
   roles through a temporary creator role that it drops before committing.

6. **Session key ring.** `az containerapp job start --resource-group <group> --name <name>-session-keys`
   prints `Bootstrapped` once (`ExistingKeyRingRetained` afterwards).

7. **Private object storage.** `az containerapp job start --resource-group <group> --name <name>-private-storage`
   writes the store marker into the empty container and prints `InitializedBlob`. It runs once: a
   rerun prints `UnsafeStorage` and changes nothing, and the server never initializes storage
   itself. Admission at first use requires the container, the marker and the identity's role;
   the job also proves the role by creating, leasing and deleting one probe blob.

8. **Start.** Restart the active revision
   (`az containerapp revision restart --resource-group <group> --name <name>-app --revision <revision>`),
   and with native work that of `<name>-worker` as well; like the server, it keeps restarting
   until the database and private storage steps are complete.

## Verification

- `curl -sS https://<public host>/api/v1/health` returns `{"status":"ok",...}` with a
  `Strict-Transport-Security` header.
- `curl -sS -H 'X-Weft-Session: 1' https://<public host>/api/auth/session` reports
  `"mode":"session"` and `"state":"anonymous"`.
- `https://<public host>/catalog` loads the application from the same origin.
- The server's console log contains `WeftSessionHostReady`. Startup failures print only
  `WeftHostConfigurationFailed` or `WeftSessionHostStartupFailed`; check the job results and
  the Key Vault references first.

## First administrator

The owner job runs `Weft.DatabaseTool` with the migrator credential. Arguments replace the
job's default; environment overrides replace its list, so repeat both secret references:

```sh
owner_env='WEFT_MIGRATION_CONNECTION_STRING=secretref:migrator-connection WEFT_DELIVER_OWNER_INPUT=secretref:owner-input'
# Organization, first principal and admission:
az containerapp job start -g <group> -n <name>-owner --container-name owner --args database provision-development-identity \
  --env-vars $owner_env WEFT_DEV_ORGANIZATION_ID=<organizationId> WEFT_DEV_PRINCIPAL_ID=<principal-uuid> WEFT_DEV_ORGANIZATION_NAME=<name>
# Allow that principal to create projects:
az containerapp job start -g <group> -n <name>-owner --container-name owner \
  --args database set-project-creation-capability <organizationId> <principal-uuid> true --env-vars $owner_env
```

To bind the user's Entra account, store their tenant and object ID as the
`weft-owner-input` secret (`{"tenantId":"<tenant>","objectId":"<user object id>"}`)
with `az keyvault secret set --file`, refresh the job's reference with
`az containerapp job secret set -g <group> -n <name>-owner --secrets owner-input=keyvaultref:<vault-uri>secrets/weft-owner-input,identityref:<identity-resource-id>`,
then start the job with `--args database bind-entra-user <principal-uuid>`. Restore
the placeholder `{}` afterwards. See [External identity bindings](external-identity-bindings.md)
for the other owner commands and their outcomes.

## Custom domain

Deploy once with `customDomain` set so the host name is added without a binding, create the
DNS records from `environmentDefaultDomain` and `customDomainVerificationId`, then bind a
managed certificate:

```sh
az containerapp hostname bind -g <group> -n <name>-app --hostname <domain> \
  --environment <name>-env --validation-method CNAME
```

Pass the resulting certificate resource ID as `customDomainCertificateId` in later
deployments so the binding is kept. Add the new redirect URI to the app registration.

## Operations

- **Updates.** Build a new tag, redeploy with it, then run the migrate job before or after the
  revision change as the release requires; migrations are forward-only.
- **Restart and recovery.** A replica restart reopens the key ring from the share and all
  other state from PostgreSQL; nothing depends on the previous replica. The app is fixed at one replica:
  the session key ring and host profile support one API instance, so do not enable scale-out
  or scale-to-zero.
- **Certificate rotation.** Neither certificate renews automatically. A new session key
  certificate version needs the previous version delivered as
  `WEFT_DELIVER_SESSION_DECRYPT_PEM_1` (a versioned Key Vault reference added to the app)
  for as long as keys it protects remain in the ring; rotation is restart-based. Add a new
  Entra client certificate to the app registration before restarting with it.
- **Backups.** PostgreSQL keeps 14 days of point-in-time restore. Snapshot the key-ring share
  to retain session continuity across a restore. Private objects are referenced by exact digest
  from the database; a database restore can refer to objects that maintenance has since retired,
  which then read as missing rather than as different content.
- **Private object maintenance.** Run one bounded [maintenance](maintenance-command.md) batch per
  project with `az containerapp job start -g <group> -n <name>-maintenance --container-name maintenance
  --args maintenance run-once <organizationId> <projectId>` (and its continuation cursors); environment
  overrides replace the job's list, so pass arguments only. The job uses the `pg-maintenance-connection`
  secret and the same Blob settings as the server.

## Ferrule service

Native work and the mapping endpoints run Ferrule through the [Ferrule service](ferrule-service.md),
which needs no Docker. `ferruleRunner` selects where it runs:

- `service` adds the internal-only `<name>-ferrule` container app: TCP ingress on 8443 (TLS
  terminates in the service), probes on its plain port 8080, an `EmptyDir` volume at `/tmp`, and a
  TCP scale rule targeting `ferruleServiceConcurrency` concurrent connections per replica, up to
  `ferruleServiceMaxReplicas`. It receives `weft-ferrule-service` from Key Vault and admits one
  client certificate per deployed caller: the Session host's (`ferruleServiceClientCertificateSha256`),
  the worker app's (`ferruleServiceWorkerClientCertificateSha256`) and the mapping endpoints'
  (`ferruleServiceEndpointClientCertificateSha256`). Every caller pins
  `ferruleServiceCertificateSha256`. `initialize-secrets.sh` creates all these certificates and
  writes their pins.
- `sessions` adds the `<name>-ferrule-sessions` custom-container session pool (egress disabled, one
  session per operation, `ferruleSessionsMax` sessions) and grants the Session Executor role on it
  to the application identity and, with native work, to the worker identity. No certificate is
  involved.
- `worker`, the default, wires the Session host to a separately hosted Docker-capable inspection
  worker instead; see [Docker inspection worker](#docker-inspection-worker).

Both service runners need `ferruleServiceImage` (pushed to the registry, preferably by digest) and
`ferruleServiceIdentity`. Container Apps does not offer a read-only root filesystem; the children's
own controls apply regardless. `ferruleServiceCompile=true` also serves the service's `compile`
profile, where a compiler may use a 2 GiB managed heap: each replica gets
`ferruleServiceConcurrency + 1` vCPU and twice as many GiB (at most concurrency 3 on the Consumption
profile), and each session 2 vCPU and 4 GiB. The platform kernel must provide Landlock ABI 3
(Linux 6.2), or the service does not start. Set `ferruleCompileToolchain` to the image's compile
toolchain identity (see [Ferrule service](ferrule-service.md#compile-operations)): the Session
host's reports and the worker app's builds then record the combined compiler identity that builds
through the service bind. For the session pool, the toggle adds
`WEFT_FERRULE_SERVICE_COMPILE_ENABLED=true` to each session.

## Native work, previews, regression runs and builds

`nativeWorkEnabled = true` turns on everything that runs Ferrule for the application, as the
Compose hosted stack does with `compose.ferrule-service.yml` and `compose.features.yml`:

- **Session host.** Native upload, report reads and report derivation run through the selected
  runner, with the `pg-ingest-connection`, `pg-report-connection` and `pg-derivation-connection`
  logins. Previews, regression cases and runs, and builds are recorded here (`WEFT_PREVIEWS_ENABLED`,
  `WEFT_REGRESSIONS_ENABLED` and `WEFT_BUILDS_ENABLED`). Their logins arrive as owner-only files,
  delivered with `WEFT_DELIVER_FILE_*` and named by the matching `*_CONNECTION_STRING_FILE`
  settings, the only form the Session host accepts for them.
- **Worker app.** `<name>-worker` runs the `weft-worker:<tag>` image (`worker` command) with
  preview, regression and build execution enabled, through the same runner. With `service` it
  presents its own `weft-ferrule-worker-client` certificate; with `sessions` it calls the pool with
  its own identity. Its eleven logins arrive as owner-only files in the same way. It has no
  ingress and always runs one replica. A stop cancels in-flight runs, whose rows stay retryable;
  the 150-second termination grace period covers a canceled compilation's 120-second wall
  deadline, child reaping and margin.
- **Identities.** The worker app holds the application identity only to pull its image and
  resolve its Key Vault references (lifecycle `None`, so its code cannot request tokens for it).
  It uses `<name>-worker-identity` for the private object container and the session pool.
  Admitting the private store proves that the credential can create, lease and delete a probe
  blob, so the worker identity holds `Storage Blob Data Contributor` on the container, like the
  application identity, although the worker only reads objects.

Native work requires `ferruleRunner = service` or `sessions`, plus `ferruleServiceCompile = true`
and `ferruleCompileToolchain`, because builds compile through the service. With `service` it also
requires the `weft-ferrule-worker-client` pin as `ferruleServiceWorkerClientCertificateSha256`. A
deployment that lacks any of these fails and names the missing requirement, instead of deploying
hosts that refuse to start. Build the `weft-worker` image in step 2.

### Docker inspection worker

With `ferruleRunner = worker`, the Session host is wired to a separately hosted
[inspection worker](ferrule-worker.md#hosted-inspection-transport). That host runs `Weft.Worker`
and the Ferrule image with a Docker daemon, which Container Apps cannot provide, so it needs a
Docker-capable Linux host inside the virtual network, reachable only from the apps subnet on its
inspection port. These templates do not create that host. They pass `inspectionWorkerUrl`,
`inspectionWorkerCertificateSha256` (from `weft-inspection-worker.sha256`) and
`ferruleWorkerImage`, and deliver the `weft-inspection-client` certificate. The worker host
receives `weft-inspection-worker` as `WEFT_DELIVER_INSPECTION_WORKER_PEM` and the client pin from
`weft-inspection-client.sha256`. `workerPrincipalId` grants its identity the private container
role. `nativeWorkEnabled` does not use this runner, so these settings stay dormant.

## Chat

`chatEnabled = true` enables [chat](chat-http.md) in the Session host. It requires
`nativeWorkEnabled`, because conversations pin the revision reports that derivation produces. The
deployment adds `WEFT_CHAT_ENABLED=true` and four private files:

- the `pg-chat-reader-connection` and `pg-chat-writer-connection` logins, delivered with
  `WEFT_DELIVER_FILE_CHAT_READER_CONNECTION` and `WEFT_DELIVER_FILE_CHAT_WRITER_CONNECTION` and
  named by `WEFT_CHAT_READER_CONNECTION_STRING_FILE=/run/weft/secrets/chat-reader-connection` and
  `WEFT_CHAT_WRITER_CONNECTION_STRING_FILE=/run/weft/secrets/chat-writer-connection` (the Session
  host refuses plain chat connection strings);
- the profile document from the `weft-chat-profiles` secret, delivered with
  `WEFT_DELIVER_FILE_CHAT_PROFILES` and named by `WEFT_CHAT_PROFILES_FILE=/run/weft/secrets/chat-profiles`;
- the model-provider credential from the `weft-model-provider-credential` secret, delivered with
  `WEFT_DELIVER_FILE_MODEL_PROVIDER_CREDENTIAL` to `/run/weft/secrets/model-provider-credential`,
  which a profile's `file` credential names.

`initialize-secrets.sh` creates both secrets as placeholders, and the placeholder document refuses
chat startup. Replace them before enabling chat, for example with
`az keyvault secret set --vault-name <keyVaultName> --name weft-chat-profiles --file <profiles.json>`;
the server reads new values when it restarts.

For an [Azure OpenAI profile](ai-chat.md#azure-openai-profiles), the profile can use the
application identity instead of a key. Its credential entry is
`{"reference": "azure-openai", "managedIdentity": {"clientId": "<identityClientId>"}}`, and the
placeholder credential stays unused. The identity needs the `Cognitive Services OpenAI User` role
on that resource:

```sh
az role assignment create --assignee-object-id <identityPrincipalId> --assignee-principal-type ServicePrincipal \
  --role "Cognitive Services OpenAI User" --scope <Azure OpenAI resource ID>
```

The resource needs a custom subdomain for Microsoft Entra tokens, and must be reachable from the
virtual network, either publicly or through a private endpoint whose DNS zone is linked to it.
To use an API key instead, store the key in `weft-model-provider-credential` and name that file in
the profile; the v1 endpoint accepts the key as the Bearer value, as the OpenAI client libraries
send it.

## Mapping endpoints

Each entry of `mappingEndpoints` deploys the [mapping endpoint](mapping-endpoint.md) for one
registered release target as the internal-only container app `<name>-<endpoint name>`. The
environment ingress terminates TLS and forwards plain HTTP (`platformIngress`), so callers
reach `https://<app>.internal.<environment domain>` from the virtual network only; deployment
output `mappingEndpointOrigins` lists the origins. The endpoints require `ferruleRunner =
service`: each executes its target's compiled artifacts through the Ferrule service's execute
profile, which the deployment then enables, with its own client certificate
`weft-ferrule-endpoint-client`, which the service admits by `ferruleServiceEndpointClientCertificateSha256`.
No endpoint needs Docker.

- **Entries.** Each entry names the endpoint (2–12 lowercase characters) and its target:
  `projectId`, `mappingId`, `environmentId`, `environmentVersion` and `targetId`. Optional
  `concurrency` (1–4, default 4) bounds the calls one replica admits, and `maxReplicas`
  (default 4) bounds scale-out; at least one replica always runs, and a scale rule adds
  replicas at `concurrency` concurrent requests each. Optional `callers` lists the machine
  principals to grant, as `{ grantId, objectId }`.
- **Identity and tokens.** `mappingEndpointAudience` is the application (client) ID of the
  endpoint's own app registration, which must accept v2 access tokens; callers request
  app-only tokens for it from `mappingEndpointTenantId` (default `entraTenantId`). See
  [machine authorization](machine-authorization.md).
- **Credentials.** Each endpoint receives its eight restricted logins from Key Vault through the
  application identity (`pg-*-connection`, created by `initialize-secrets.sh`). The entrypoint
  writes each to a protected private file, which is the only form the endpoint accepts.
- **Runtime.** `compiledRuntimeIdentity` is the execution identity that the Ferrule service image
  reports for its execute profile (see [compiled execution](compiled-service-runtime.md)). Every
  target registers it as its runtime image. The endpoints pin the service by
  `ferruleServiceCertificateSha256` and name the compiler of their builds by
  `ferruleServiceIdentity` and `ferruleCompileToolchain`.
- **Shutdown.** A replica has 100 s to stop, so calls drain within their 90-second deadline.

After a deployment with endpoints, prepare each target as one project administrator, with
`<principal-uuid>` as `mappingEndpointAdministratorId`. Release commands run through
`<name>-endpoint-release`, for example
`az containerapp job start -g <group> -n <name>-endpoint-release --container-name release --args release qualify-build <principal-uuid> <organizationId> <project> <mapping> <build> <qualification>`.
Then `register-release-target` and `activate-release` follow, as described in
[local release activation](local-releases.md#operator-commands). The job reaches the service
like the endpoints, so qualification executes through the same runtime. Grants run through
`<name>-endpoint-grants` with `--args database grant-machine-invocation …`; deployment output
`mappingEndpointGrantArguments` lists one argument list per configured caller. Pass arguments
only: environment overrides replace each job's list.

## Open questions and limits

- **Parameter grant on Flexible Server.** Migration 0011 requires
  `GRANT SET ON PARAMETER weft.organization_id, weft.chat_principal_id TO weft_migrator`.
  Its server administrator belongs to `azure_pg_admin` but is not a superuser, and
  PostgreSQL lets only a superuser or a holder of the grant option grant `SET` on a
  placeholder parameter. Verify with the database-roles job: success prints the provisioned
  line; refusal prints `permission denied for parameter weft.organization_id` and rolls back
  every role (only the empty `weft` database remains). Run it right after the platform
  deployment, before the rest of the procedure. If it is refused, either host PostgreSQL 16
  with a superuser-capable administrator in the same virtual network, or replace the chat
  routines' function-level `SET` clauses through a new reviewed migration (0011 itself is
  checksum-bound and must not be edited).
- **Key ring on Azure Files.** The server admits its ring only as a 0700 directory with
  single-link 0600 files owned by its user. The SMB mount presents those owners and modes.
  The session-keys job and a successful server start verify admission on the share. If it
  is refused, use a persistent POSIX volume such as an Azure Files NFS share prepared with
  the same ownership.
- **Provisioning connection.** The provisioning job's PostgreSQL client image has no CA
  bundle, so it uses `sslmode=require` inside the virtual network. Application and migration
  connections use `SSL Mode=VerifyFull`.
