# braid roadmap

## Create mappings from customer specifications

The first release focuses on the complete creation workflow in
[#443](https://github.com/DeandreT/braid/issues/443):

1. Import specifications, examples and API documentation.
2. Generate a new mapping for JSON, X12, EDIFACT, XML or CSV/TSV.
3. Try sample inputs and inspect the outputs.
4. Give feedback and review the revised proposal.
5. Save the reviewed mapping and recover interrupted requests without duplicate saves.

The next checks are an end-to-end run with a real model provider and a hosted
deployment covering sign-in, storage, execution and recovery.

Relevant references:

- [Specification intake](docs/spec-intake-ui.md) and [document extraction](docs/spec-document-intake.md).
- [Creation formats](docs/mapping-creation-formats.md), [application workflow](docs/mapping-creation-application.md)
  and [server setup](docs/creation-host-composition.md).
- [Proposal library](docs/creation-proposal-library.md), [review](docs/creation-review-ui.md)
  and [feedback](docs/creation-feedback-api.md).
- [Mapping editing](docs/mapping-editing.md) and [chat edit review](docs/chat-proposal-review-ui.md).

## Deployment

- Complete the Azure mapping-endpoint work in
  [#159](https://github.com/DeandreT/braid/issues/159).
- Check service restart, credential rotation and recovery in a hosted installation.
- Extend execution targets with queue triggers, schedules and connectors.

See [runtime packaging](docs/runtime-packaging.md), [Azure deployment](docs/azure-deployment.md)
and [release operations](docs/release-operations.md).

## Further development

- Add mapping engines and model-provider adapters.
- Expand visual authoring and compiled format support.
- Add organization switching, retention and deletion workflows.
- Extend automation and output destinations.

See the [documentation index](docs/README.md) for current features and design references.
