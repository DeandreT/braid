# Documentation

Choose a starting point for your task. The [project README](../README.md)
introduces braid; the [roadmap](../ROADMAP.md) owns current priorities and links
to acceptance work. A feature being implemented does not mean it is enabled or
verified in your installation.

## Use braid

- [Getting started](user-guide/overview.md): sign in, find a project and inspect a mapping.
- [Create a mapping from specifications](user-guide/creation.md): capture sources,
  review a proposal, try samples, give feedback and explicitly save.
- [Chat](user-guide/chat.md): ask questions, review edits and recover an uncertain save.
- [Unconfirmed results](user-guide/unconfirmed-results.md): check the original request before retrying.
- [Command-line tasks](user-guide/cli.md): use `weft` as the signed-in user.

The user guide also ships in the app under **Help & guide**. Its availability
instructions apply to the server you are using, not every possible deployment.

## Develop braid

- [Local development](development.md): the full Linux development environment,
  frontend-only development, prerequisites and checks.
- [Architecture](architecture.md): components, trust boundaries and design decisions.
- [Help authoring](help.md): the embedded guide's Markdown rules, API and checks.
- [API contract updates](development.md#updating-api-contracts): generated OpenAPI and TypeScript.

## Operate an installation

- [Runtime packaging](runtime-packaging.md): start with the Compose Session stack
  and Ferrule service for packaged evaluation; optional lanes stay separate.
- [Session host](session-host.md): authentication, protected settings and operator setup.
- [Release operations](release-operations.md): inspect targets, change releases and recover safely.
- [Hosted acceptance](hosted-acceptance.md) and [release qualification](release-qualification.md):
  verify the stated environment before relying on it.
- [Azure deployment](azure-deployment.md): cloud prerequisites and procedures;
  cloud acceptance is not established by local checks.
- [Document worker host](spec-document-host.md): optional Office/PDF installation
  admission and its separate qualification requirements.

## Reference

- **Catalog:** [ownership](project-ownership.md), [reads](catalog-reads.md),
  [project creation](project-creation.md), [native ingestion](native-revision-ingestion.md)
  and [revision uploads](catalog-revision-uploads.md).
- **Identity and access:** [API authentication](api-authentication.md),
  [administration](administration.md), [source payload permission](project-payload-permissions.md)
  and [private storage](private-storage.md).
- **Mapping creation and editing:** [creation application](mapping-creation-application.md),
  [typed formats](mapping-creation-formats.md), [creation review](creation-review-ui.md),
  [specification intake](spec-document-intake.md) and [mapping editing](mapping-editing.md).
- **Chat:** [workspace chat](workspace-chat.md), [exact edit approval](chat-proposal-review-ui.md)
  and [creation feedback](creation-feedback-api.md).
- **Execution:** [previews](preview-runs.md), [regression runs](regression-runs.md),
  [build workflow](build-workflow.md) and [Ferrule service](ferrule-service.md).
- **Interfaces:** [full CLI reference](cli.md) and generated [browser/session](../contracts/weft-v1.json),
  [machine endpoint](../contracts/weft-endpoint-v1.json) and [Ferrule service](../contracts/ferrule-service-v1.json)
  OpenAPI documents.

## Plans and history

Use the [roadmap](../ROADMAP.md) for priorities, not old implementation checklists.
[Archive/deletion](archive-deletion-design.md), [organization switching](organization-switching-design.md)
and [trigger destinations](trigger-destination-design.md) are design references,
not setup procedures. The [catalog persistence](catalog-persistence-plan.md),
[API](catalog-api-plan.md) and [ingestion](catalog-ingestion-plan.md) plans retain
their original requirements and links to current contract owners.

[v1 integration](v1-integration.md), [CSV port](v1-csv-port.md),
[XML port](v1-xml-port.md) and [chat browser qualification](chat-browser-qualification.md)
retain evidence from their stated runs. Read their environment limits and deferred
acceptance gates; historical results do not qualify another installation or provider.
