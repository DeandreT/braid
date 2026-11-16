# Documentation

Find instructions for using, developing and running braid. Start with the
[project README](../README.md) or see the [roadmap](../ROADMAP.md) for priorities.

## Use braid

- [Getting started](user-guide/overview.md): sign in, find a project and inspect a mapping.
- [Create a mapping from specifications](user-guide/creation.md): capture sources,
  review a proposal, try samples, give feedback and explicitly save.
- [Chat](user-guide/chat.md): ask questions, review edits and recover an uncertain save.
- [Unconfirmed results](user-guide/unconfirmed-results.md): check the original request before retrying.

The user guide is also available in the app under **Help & guide**.

## Develop braid

- [Local development](development.md): the full Linux development environment,
  frontend-only development, prerequisites and checks.
- [Architecture](architecture.md): components, trust boundaries and design decisions.
- [Help authoring](help.md): the embedded guide's Markdown rules, API and checks.
- [API contract updates](development.md#updating-api-contracts): generated OpenAPI and TypeScript.

## Operate an installation

- [Runtime packaging](runtime-packaging.md): start with the Compose Session stack
  and Ferrule service.
- [Session host](session-host.md): authentication, protected settings and operator setup.
- [Release operations](release-operations.md): inspect targets, change releases and recover safely.
- [Hosted acceptance](hosted-acceptance.md) and [release qualification](release-qualification.md):
  deployment and release checks.
- [Azure deployment](azure-deployment.md): cloud setup and operation.
- [Document worker host](spec-document-host.md): optional Office/PDF installation
  and configuration.

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
- **Interfaces:** generated [browser/session](../contracts/braid-v1.json),
  [machine endpoint](../contracts/braid-endpoint-v1.json) and [Ferrule service](../contracts/ferrule-service-v1.json)
  OpenAPI documents.

## Design references

[Archive/deletion](archive-deletion-design.md), [organization switching](organization-switching-design.md)
and [trigger destinations](trigger-destination-design.md) describe planned extensions.
