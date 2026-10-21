# Azure deployment

`deploy/azure` describes the first hosted Weft environment as Bicep, using the
[runtime images](runtime-packaging.md). The server runs in the Session profile behind its
same-replica edge, serves the Angular application from the same origin, and reaches every
dependency privately. Tracking work is [#146](https://github.com/DeandreT/weft/issues/146).

| Resource | Configuration |
| --- | --- |
| Virtual network | Subnets for the Container Apps environment, the delegated PostgreSQL server and private endpoints, with private DNS zones for PostgreSQL, Key Vault and Azure Files. |
| Log Analytics | Container Apps console and system logs (30 days). |
| Container registry | Basic SKU, no admin user; the application identity has only `AcrPull`. |
| Managed identity | One user-assigned identity for the app and jobs: image pull and Key Vault secret reads. |
| Key Vault | RBAC authorization, purge protection, private endpoint; optional operator IP allow-list. Holds every credential and certificate. |
| Storage account | No public network access; private endpoint; SMB share `weft-session-keys` for the session key ring. |
| PostgreSQL Flexible Server | Version 16, private VNet access only, password authentication, 14-day backups. |
| Container Apps environment | Workload-profile (Consumption) environment in the VNet with public ingress. |
| Container app | `server` and `edge` containers in one replica (fixed at one), ingress on the edge's port 8080, HTTPS only, probes on `/api/v1/health`, key-ring share mounted with UID/GID 1654 and modes 0700/0600. |
| Jobs (manual) | `<name>-database-roles`, `<name>-migrate`, `<name>-session-keys` and `<name>-owner`. |

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
| Initial users' Entra `sub` values | One per user, for identity bindings: the ID-token `sub` claim issued to this client ID. It is pairwise per application and differs from the object ID. |
| Model-provider credential | Only once chat can be enabled (see the limits below); stored in Key Vault. |

### Entra app registration

Register a single-tenant web application in the sign-in tenant:

- Redirect URI (Web): `https://<public host>/api/auth/callback/entra`, exactly. The public
  host is the custom domain or the app's default host name (deployment output `publicHost`;
  `entraRedirectUri` repeats the full URI).
- No client secret, implicit grant or front-channel logout URL. Sign-in requests only
  `openid` with authorization code and PKCE.
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
   ```

3. **Secrets and certificates.** As the operator principal (from an allowed IP when
   `operatorIpRanges` is set):

   ```sh
   deploy/azure/initialize-secrets.sh <keyVaultName> <databaseHost> <private output directory>
   ```

   The script keeps existing passwords and certificates, so it can be rerun. Upload the
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

7. **Start.** Restart the active revision
   (`az containerapp revision restart --resource-group <group> --name <name>-app --revision <revision>`).

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

To bind the user's Entra account, store the key as the `weft-owner-input` secret
(`{"provider":"entra","authority":"https://login.microsoftonline.com/<tenant>/v2.0","subject":"<sub>"}`)
with `az keyvault secret set --file`, refresh the job's reference with
`az containerapp job secret set -g <group> -n <name>-owner --secrets owner-input=keyvaultref:<vault-uri>secrets/weft-owner-input,identityref:<identity-resource-id>`,
then start the job with `--args database bind-external-identity <principal-uuid>`. Restore
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
  to retain session continuity across a restore.

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
- **Private object storage and native work.** The private storage adapter admits only local
  ext4, Btrfs, XFS, tmpfs and overlay filesystems; Azure Files (SMB or NFS) is refused, so
  native upload, report reads and report derivation stay disabled here until a Blob storage
  adapter (or a qualified network-filesystem profile) exists. Native work also needs the
  [hosted inspection worker](ferrule-worker.md#hosted-inspection-transport): `Weft.Worker`
  runs the Ferrule image with a Docker daemon, which Container Apps cannot provide, so it
  must run on a Docker-capable Linux host inside the virtual network, reachable only from the
  apps subnet on its inspection port. These templates do not create that host. They wire the
  Session host to it with `inspectionWorkerUrl`, `inspectionWorkerCertificateSha256` (from
  `weft-inspection-worker.sha256`) and `ferruleWorkerImage`, delivering the
  `weft-inspection-client` certificate; the settings stay dormant until native upload or
  derivation is enabled. `initialize-secrets.sh` creates both inspection certificates in Key
  Vault and writes their pins; the worker host receives `weft-inspection-worker` as
  `WEFT_DELIVER_INSPECTION_WORKER_PEM` and the client pin from `weft-inspection-client.sha256`.
- **Chat.** [Chat](chat-http.md) requires revision report reads, so it stays disabled until
  the storage and worker limits above are resolved. Enabling it then means
  `WEFT_CHAT_ENABLED=true`, the `pg-chat-reader-connection` and `pg-chat-writer-connection`
  secrets delivered with `WEFT_DELIVER_FILE_CHAT_READER_CONNECTION` and
  `WEFT_DELIVER_FILE_CHAT_WRITER_CONNECTION` and named by
  `WEFT_CHAT_READER_CONNECTION_STRING_FILE=/run/weft/secrets/chat-reader-connection` and
  `WEFT_CHAT_WRITER_CONNECTION_STRING_FILE=/run/weft/secrets/chat-writer-connection` (the
  Session host refuses plain chat connection strings), and the profile document and
  model-provider credential stored in Key Vault and delivered with
  `WEFT_DELIVER_FILE_CHAT_PROFILES` and `WEFT_DELIVER_FILE_MODEL_PROVIDER_CREDENTIAL`
  (the profile's credential entry then names `/run/weft/secrets/model-provider-credential`).
- **Previews.** The Session host can record previews, but execution needs the separate
  `Weft.Worker` host with a Ferrule worker transport, which this environment does not run, so
  previews stay disabled. Enabling them later means `WEFT_PREVIEWS_ENABLED=true` and the
  `pg-preview-writer-connection` and `pg-preview-reader-connection` secrets delivered with
  `WEFT_DELIVER_FILE_PREVIEW_WRITER_CONNECTION` and `WEFT_DELIVER_FILE_PREVIEW_READER_CONNECTION`
  and named by `WEFT_POSTGRES_PREVIEW_WRITER_CONNECTION_STRING_FILE` and
  `WEFT_POSTGRES_PREVIEW_READER_CONNECTION_STRING_FILE`.
- **Provisioning connection.** The provisioning job's PostgreSQL client image has no CA
  bundle, so it uses `sslmode=require` inside the virtual network. Application and migration
  connections use `SSL Mode=VerifyFull`.
