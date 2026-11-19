# Mapping trigger sources and destinations

| Source | Studio Operations route action | API action | Destinations |
| --- | --- | --- | --- |
| Service Bus queue or topic subscription | Configure queue or subscription | `configureQueue` | Blob, SFTP, HTTPS, Service Bus topic |
| Scheduled Blob prefix | Configure Blob schedule | `configureBlob` | Blob, SFTP, HTTPS, Service Bus topic |
| Scheduled SFTP inbox | Configure SFTP to AS2 pilot | `configure` | AS2 only, through the [local SFTP pilot registration](sftp-as2-route.md) |

The actions use the same administrator-only Operations endpoint and reviewed trigger generation. `Braid.ReleaseTool` manages releases and packages; it has no trigger-authoring command. Operators configure connectors in protected host files and author routes in Studio or through the API. An SFTP **destination** on a queue or Blob route does not enable SFTP **intake**. The managed Azure plan supports queue/subscription and Blob sources, not the SFTP inbox pilot.

## Reading schedule and order outcomes

**Trigger runs** records scheduled occurrences, not remote deliveries. Studio schedules use UTC, a one-hour lookback and bounded catch-up of two: after an outage, the latest two due occurrences in that window are eligible to scan and earlier due occurrences in the window are recorded as `missed`. No run is promised outside the lookback. If a scan is still unfinished, a newly due run is recorded as `skipped`, not queued behind it. An expired scan lease can resume from the last committed page. A later scan can rediscover a file, but the same admitted source version does not become another order.

**Quarantined inputs and orders** shows rejected file versions and failed orders. For example, an SFTP digest mismatch leaves the file untouched and does not stop a valid sibling. A changed file must be published under its new digest. For queue input, retryable infrastructure or capacity failures abandon the message; a redelivery of completed work reuses the original outcome. A changed body under the same message ID is an idempotency conflict. Invalid or failed messages go to the broker's dead-letter queue with a reason; repeated abandonment can also reach the broker's maximum delivery count. Inspect broker dead letters at the broker, not in Studio's delivery list.

**Trigger deliveries** shows what happened after mapping. An accepted mapping has retained the original output in the outbox; it does not establish remote acceptance. Reconcile an unknown send against the original delivery rather than rerunning the order.

## Register the local queue and Blob worker

The mapping endpoint can run a local trigger worker for its registered target. Supply both `BRAID_ENDPOINT_LOCAL_TRIGGER_HOST_FILE` and `BRAID_ENDPOINT_LOCAL_TRIGGER_CONNECTION_STRING_FILE`. The latter is a protected file for the `braid_trigger_worker` login. Leaving both unset starts no worker. This plan is separate from the SFTP-to-AS2 pilot plan.

In Studio Operations, select the target and choose **Author local trigger route**. Choose **Configure queue or subscription** or **Configure Blob schedule**, enter the registered bindings and versions, and select the message format, single-order or batch envelope, and destination kind. Only a current project administrator can save the definition. Use generation zero to create a disabled route; refresh Triggers to enable it. An existing trigger cannot be reassigned to another source binding, connector ID, kind, or envelope. A destination revision is a new configuration version and does not rewrite the original version.

The JSON host file registers connectors, not mapping authority. Use `version: 1`, a protected `tokenFile` containing a current machine bearer token, a `serviceBusEmulatorConnectionFile` (or null for Blob-only use), and the `queues`, `schedules` and `destinations` arrays. A separately enabled database trigger must match the registered source, validated machine identity and destination connector versions. Register each destination binding with only one worker plan. Never change the meaning of an existing connector version.

- A queue entry specifies `triggerId`, `connectorId`, `connectorVersion`, `queueName`, `prefetchCount`, `maximumConcurrentCalls` and `retryBackoffSeconds`. For a subscription, use the topic name as `queueName` and provide `subscription`. The worker uses peek-lock, bounded lock renewal and manual settlement. A completed mapping has handed off to the durable outbox; it does not mean remote delivery succeeded.
- A schedule entry specifies `triggerId`, `connectorId`, `connectorVersion`, `serviceUri`, `container`, `credential: "emulator"`, `prefix`, `pageSize` and `tickSeconds`. The prefix is empty or ends in `/`. Listing is paged and every read uses the listed ETag. Replacement is not silently read as the original version.
- Each destination specifies `bindingId`, `kind`, `connectorId`, `connectorVersion` and its connector settings. `blob` takes the same emulator storage location and prefix; it publishes under the delivery ID without overwrite. `serviceBusTopic` takes `topicName` and sends the delivery ID as the message ID. `https` uses an HTTPS endpoint, explicit accepted and rejected status arrays, and a protected signing key file. `sftp` uses a pinned host key, one protected credential file, and a create-only publish directory. Unknown non-create-only sends require reconciliation.

Service Bus and Blob registrations here accept local development emulators only. `deploy/compose/compose.triggers.yml` defines an opt-in local broker, its SQL dependency, Azurite and an SFTP service. Broker acceptance requires explicit emulator license acceptance. None of these settings provisions a cloud resource or qualifies managed identity or Azure RBAC.

The [Azure registration](azure-deployment.md#triggers) uses a separate version-2 plan with
a target-specific managed identity. The endpoint refuses mixed local and managed plans.
