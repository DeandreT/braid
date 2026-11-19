"""Offline resource and host-file checks. Run with Python 3 and the Bicep CLI on PATH."""
import json
import os
from pathlib import Path
import shutil
import subprocess
import tempfile


AZURE = Path(__file__).resolve().parents[1]
BICEP = os.environ.get("BICEP_CLI", "bicep")
ID = "00000000-0000-4000-8000-000000000001"
GROUP = f"/subscriptions/{ID}/resourceGroups/demo/providers/"
QUEUE = dict(triggerId=ID, connectorId="inbox", connectorVersion=1, queueName="inbox",
             prefetchCount=1, maximumConcurrentCalls=1, retryBackoffSeconds=5)
SUB = dict(QUEUE, triggerId="00000000-0000-4000-8000-000000000002", queueName="incoming", subscription="selected")
ROUTES = dict(queues=[QUEUE, SUB], schedules=[], destinations=[
    dict(kind="serviceBusTopic", topicName="outgoing", bindingId=ID, connectorId="send", connectorVersion=1),
    dict(kind="blob", container="published", prefix="files/", bindingId=SUB["triggerId"], connectorId="archive", connectorVersion=1),
    dict(kind="https", endpoint="https://receiver.example.invalid/orders", acceptedStatuses=[201], secret="outbound-signing",
         bindingId="00000000-0000-4000-8000-000000000003", connectorId="webhook", connectorVersion=1),
])


def snapshot(root, module, **params):
    file = root / f"{module}.bicepparam"
    file.write_text(f"using './modules/{module}.bicep'\n" + "".join(
        f"param {key} = json('{json.dumps(value)}')\n" for key, value in params.items()), encoding="utf-8")
    subprocess.run([BICEP, "snapshot", str(file), "--mode", "overwrite", "--location", "eastus2",
                    "--resource-group", "demo", "--subscription-id", ID], check=True)
    return json.loads(file.with_suffix(".snapshot.json").read_text())["predictedResources"]


def run(root):
    shutil.copytree(AZURE / "modules", root / "modules")
    subprocess.run([BICEP, "build", str(AZURE / "main.bicep"), "--outfile", str(root / "main.json")], check=True)
    resources = snapshot(root, "triggers", location="eastus2", identityName="demo-trigger", serviceBusName="demo-bus",
                         storageName="demotrigger", endpointsSubnetId=GROUP + "Microsoft.Network/virtualNetworks/demo/subnets/private",
                         serviceBusZoneId=GROUP + "Microsoft.Network/privateDnsZones/privatelink.servicebus.windows.net",
                         blobZoneId=GROUP + "Microsoft.Network/privateDnsZones/privatelink.blob.core.windows.net",
                         tags={}, trigger=dict(ROUTES, schedules=[dict(container="staged")]))
    scopes = {r["id"].split("/providers/")[1]: r["properties"]["roleDefinitionId"].split("/")[-1]
              for r in resources if r["type"] == "Microsoft.Authorization/roleAssignments"}
    receiver = "4f6d3b9b-027b-4f4c-9142-0e5a2a2247e0"
    broker = "Microsoft.ServiceBus/namespaces/demo-bus/"
    blob = "Microsoft.Storage/storageAccounts/demotrigger/blobServices/default/containers/"
    assert scopes == {broker + "queues/inbox": receiver, broker + "topics/incoming/subscriptions/selected": receiver,
                      broker + "topics/outgoing": "69a216fc-b8fb-44d8-bc22-1f3c2cd27a39",
                      blob + "staged": "2a2b9908-6ea1-4ae2-8e65-a410df84e7d1",
                      blob + "published": "ba92f5b4-2d11-453d-a403-e96b0029c9fe"}
    stores = [r["properties"] for r in resources if r["type"] in ("Microsoft.ServiceBus/namespaces", "Microsoft.Storage/storageAccounts")]
    assert len(stores) == 2 and all(s["publicNetworkAccess"] == "Disabled" for s in stores)
    assert any(s.get("disableLocalAuth") is True for s in stores) and any(s.get("allowSharedKeyAccess") is False for s in stores)
    base = dict(projectId=ID, mappingId=ID, environmentId=ID, environmentVersion=1, targetId=ID)
    identity = GROUP + "Microsoft.ManagedIdentity/userAssignedIdentities/"
    settings = dict(identityId=identity + "demo-trigger", identityClientId=ID,
                    serviceBusNamespace="demo-bus.servicebus.windows.net", blobServiceUri="https://demotrigger.blob.core.windows.net")
    resources = snapshot(root, "mapping-endpoint", location="eastus2", namePrefix="demo", environmentName="demo-env",
                         identityId=identity+"demo-host", registryServer="demo.example.invalid", endpointImage="endpoint:demo",
                         braidImage="studio:demo", vaultUri="https://demo.vault.azure.net/", organizationId=ID,
                         endpoints=[dict(base, name="orders", triggers=ROUTES), dict(base, name="plain")],
                         grantAdministratorId=ID, tenantId=ID, audience=ID, ferruleServiceUrl="https://demo.invalid",
                         ferruleServiceCertificateSha256="0"*64, ferruleServiceIdentity="demo", compileToolchain="demo",
                         compiledRuntimeIdentity="demo", triggerRegistrations=[dict(settings, name="orders")], tags={})
    apps = {r["name"]: r for r in resources if r["type"] == "Microsoft.App/containerApps"}
    def env(app):
        return {e["name"]: e for e in app["properties"]["template"]["containers"][0]["env"]}
    active = env(apps["demo-orders"])
    host = json.loads(active["BRAID_DELIVER_FILE_ENDPOINT_TRIGGER_HOST"]["value"])
    assert host["version"] == 2 and host["queues"] == [QUEUE, SUB]
    assert host["managedIdentityClientId"] == ID and host["serviceBusNamespace"] == settings["serviceBusNamespace"]
    assert host["destinations"][1]["serviceUri"] == settings["blobServiceUri"] and host["destinations"][1]["credential"] == "managedIdentity"
    assert host["destinations"][2]["signingKeyFile"] == "/run/braid/secrets/endpoint-trigger-secret-2"
    assert active["BRAID_DELIVER_FILE_ENDPOINT_TRIGGER_SECRET_2"]["secretRef"] == "trigger-secret-2"
    named_secrets = {s["name"]: s for s in apps["demo-orders"]["properties"]["configuration"]["secrets"]}
    assert named_secrets["trigger-secret-2"]["keyVaultUrl"].endswith("/secrets/outbound-signing")
    assert active["BRAID_DELIVER_FILE_ENDPOINT_TRIGGER_WORKER"]["secretRef"] == "trigger-worker-connection"
    assert settings["identityId"] in apps["demo-orders"]["identity"]["userAssignedIdentities"]
    assert settings["identityId"] not in apps["demo-plain"]["identity"]["userAssignedIdentities"]
    assert not any("TRIGGER" in key for key in env(apps["demo-plain"]))


if __name__ == "__main__":
    with tempfile.TemporaryDirectory() as tmp:
        run(Path(tmp))
    print("Managed trigger resource scopes and endpoint registrations verified offline.")
