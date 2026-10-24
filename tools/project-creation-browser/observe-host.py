"""Creation-only read-only Docker observer: one PG, zero native workers; never removes resources."""
import argparse
import datetime
import http.client
import json
import os
from pathlib import Path
import re
import socket
import stat
import sys
import threading
import time
import urllib.parse
import uuid

PG_IMAGE = 'postgres@sha256:ec9f656f17c8b4dfebf49e58cf5c46d493d4846d4ea6d8b85d0637f68ee33bfc'
WORKER_IMAGE = 'sha256:439291fb4d665e3ba9c086e88214a73fd44552af80932a9b40c97f5816c7047e'
OWNER, NONCE, CASE = ('weft.qualification.' + key for key in ('owner', 'nonce', 'case'))
LABELS = (OWNER, NONCE, CASE)
WORKER = re.compile(r'weft-inspect-[0-9a-f]{32}')
IDENTITY = re.compile(r'[0-9a-f]{64}')


class Refusal(Exception):
    pass


def require(value, category):
    if not value:
        raise Refusal(category)


class UnixHttp(http.client.HTTPConnection):
    def connect(self):
        self.sock = socket.socket(socket.AF_UNIX, socket.SOCK_STREAM)
        self.sock.settimeout(self.timeout)
        self.sock.connect('/var/run/docker.sock')


def get(path):
    connection = UnixHttp('localhost', timeout=5)
    try:
        connection.request('GET', path)
        response = connection.getresponse()
        body = response.read(4194305)
        require(len(body) <= 4194304, 'daemonResponseLimit')
        return response.status, json.loads(body) if response.status == 200 else None
    finally:
        connection.close()


def inventory(fixture_id):
    label_filter = urllib.parse.urlencode({'filters': json.dumps({'label': [OWNER + '=' + fixture_id]})})
    result = {}
    for kind, endpoint, key in [('container', '/containers/json?all=1&', 'Id'),
                                ('network', '/networks?', 'Id'), ('volume', '/volumes?', 'Name')]:
        status, values = get(endpoint + label_filter)
        require(status == 200, 'inventoryUnavailable')
        if kind == 'volume':
            values = values.get('Volumes') or []
        result[kind] = sorted(value[key] for value in values)
    status, values = get('/containers/json?all=1')
    require(status == 200, 'workerInventoryUnavailable')
    result['preexistingWorker'] = sorted(value['Id'] for value in values
        if value.get('Image') == WORKER_IMAGE or value.get('ImageID') == WORKER_IMAGE
        or any(WORKER.fullmatch(name.lstrip('/')) for name in value.get('Names', [])))
    return result


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--fixture-id', required=True)
    parser.add_argument('--output', required=True)
    arguments = parser.parse_args()
    fixture_id = arguments.fixture_id
    require(str(uuid.UUID(fixture_id)) == fixture_id and uuid.UUID(fixture_id).int != 0, 'invalidFixtureId')
    output = Path(arguments.output)
    facts = output.lstat()
    require(output.is_absolute() and stat.S_ISDIR(facts.st_mode) and not output.is_symlink()
        and facts.st_uid == os.geteuid() and stat.S_IMODE(facts.st_mode) == 0o700
        and not any(output.iterdir()), 'invalidOutputDirectory')
    os.umask(0o077)

    def save(name, value):
        with (output / name).open('x', encoding='utf-8') as stream:
            json.dump(value, stream, indent=2)
            stream.write('\n')

    status, pg_image = get('/images/' + urllib.parse.quote(PG_IMAGE, safe='') + '/json')
    require(status == 200 and PG_IMAGE in pg_image.get('RepoDigests', [])
        and pg_image.get('Os') == 'linux' and pg_image.get('Architecture') == 'amd64', 'postgresImageUnavailable')
    errors, events, profiles, associated = [], [], [], {}
    stopping = threading.Event()
    changed = threading.Condition()

    def fail(category):
        with changed:
            errors.append(category)
            changed.notify_all()

    def owned_labels(value):
        require(value.get(OWNER) == fixture_id and re.fullmatch(r'[0-9a-f]{16}', value.get(NONCE, ''))
            and value.get(CASE) == 'weft-project-creation-browser-' + value[NONCE], 'ownedLabelsMismatch')
        return {key: value[key] for key in LABELS}

    def capture(event, is_worker):
        identity = event['Actor']['ID']
        require(IDENTITY.fullmatch(identity), 'invalidContainerIdentity')
        status, raw = get('/containers/' + identity + '/json')
        require(status == 200 and raw.get('Id') == identity, 'createdContainerNotInspectable')
        host, config = raw['HostConfig'], raw['Config']
        safe = {'id': identity, 'name': raw['Name'], 'action': event['Action'], 'image': raw['Image'],
            'configuredImage': config['Image'], 'memory': host['Memory'], 'memorySwap': host['MemorySwap'],
            'nanoCpus': host['NanoCpus'], 'pidsLimit': host['PidsLimit'], 'networkMode': host['NetworkMode'],
            'readOnly': host['ReadonlyRootfs'], 'capDrop': host['CapDrop'], 'user': config['User'],
            'mounts': [{key: mount[key] for key in ('Type', 'Name', 'Destination', 'RW') if key in mount}
                       for mount in raw['Mounts']]}
        require(not is_worker, 'nativeWorkerForbidden')
        safe['kind'], safe['labels'] = 'postgres', owned_labels(config.get('Labels') or {})
        case = safe['labels'][CASE]
        require(config['Image'] == PG_IMAGE and raw['Image'] == pg_image['Id']
            and raw['Name'] == '/' + case + '-postgres', 'postgresIdentityMismatch')
        require((safe['memory'], safe['memorySwap'], safe['nanoCpus'], safe['pidsLimit'])
            == (536870912, 536870912, 1000000000, 128) and not raw['State'].get('OOMKilled'), 'postgresProfileMismatch')
        ports = host.get('PortBindings') or {}
        require(set(ports) == {'5432/tcp'} and len(ports['5432/tcp']) == 1, 'postgresPortMismatch')
        binding = ports['5432/tcp'][0]
        require(binding.get('HostIp') == '127.0.0.1' and re.fullmatch(r'[1-9][0-9]{0,4}', binding.get('HostPort', ''))
            and int(binding['HostPort']) <= 65535, 'postgresPortMismatch')
        safe['portBindings'] = ports
        if event['Action'] == 'start':
            require(raw['NetworkSettings']['Ports'] == ports, 'postgresAssignedPortMismatch')
            safe['assignedPorts'] = raw['NetworkSettings']['Ports']
        require(len(raw['Mounts']) == 1, 'postgresMountMismatch')
        mount = raw['Mounts'][0]
        require(mount['Type'] == 'volume' and mount['Name'] == case + '-data'
            and mount['Destination'] == '/var/lib/postgresql/data' and mount['RW'], 'postgresMountMismatch')
        for kind, endpoint, resource_id in [('volume', '/volumes/', mount['Name']),
                                            ('network', '/networks/', host['NetworkMode'])]:
            status, value = get(endpoint + urllib.parse.quote(resource_id, safe=''))
            require(status == 200 and owned_labels(value.get('Labels') or {}) == safe['labels'], 'associatedOwnershipMismatch')
            if kind == 'network':
                resource_id = value['Id']
                require(IDENTITY.fullmatch(resource_id) and value['Name'] == case + '-network'
                    and value['Driver'] == 'bridge' and not value['Internal'], 'associatedNetworkMismatch')
                if event['Action'] == 'start':
                    require(set(raw['NetworkSettings']['Networks']) == {value['Name']}
                        and raw['NetworkSettings']['Networks'][value['Name']]['NetworkID'] == resource_id, 'postgresNetworkMismatch')
            else:
                require(value['Driver'] == 'local', 'associatedVolumeMismatch')
            associated[(kind, resource_id)] = {'kind': kind, 'identity': resource_id,
                'labels': safe['labels'], 'liveDaemonStatus': status, 'observedFromContainer': identity}
        profiles.append(safe)

    connection = UnixHttp('localhost', timeout=5)
    filters = {'type': ['container']}
    connection.request('GET', '/events?' + urllib.parse.urlencode({'filters': json.dumps(filters)}))
    response = connection.getresponse()
    require(response.status == 200, 'eventAdmissionUnavailable')
    event_socket = connection.sock
    require(event_socket is not None, 'eventSocketUnavailable')
    event_socket.settimeout(None)

    def observe():
        try:
            with (output / 'filtered-events.jsonl').open('x', encoding='utf-8') as stream:
                while not stopping.is_set():
                    line = response.readline(65537)
                    require(line and len(line) <= 65536, 'eventStreamEndedOrOversized')
                    raw = json.loads(line)
                    actor = raw.get('Actor') or {}
                    attributes = actor.get('Attributes') or {}
                    worker = bool(WORKER.fullmatch(attributes.get('name', ''))) or attributes.get('image') == WORKER_IMAGE
                    if attributes.get(OWNER) != fixture_id and not worker:
                        continue
                    require(IDENTITY.fullmatch(actor.get('ID', '')), 'invalidEventContainerIdentity')
                    event = {key: raw[key] for key in ('Type', 'Action', 'time', 'timeNano', 'scope') if key in raw}
                    event['Actor'] = {'ID': actor['ID'], 'Attributes': {key: attributes[key]
                        for key in (*LABELS, 'name', 'image') if key in attributes}}
                    require(len(events) < 20000 and event.get('Type') == 'container', 'eventLimitOrTypeMismatch')
                    with changed:
                        events.append(event)
                        changed.notify_all()
                    stream.write(json.dumps(event, separators=(',', ':')) + '\n')
                    stream.flush()
                    require(not worker, 'nativeWorkerForbidden')
                    if event['Action'] == 'create' or (not worker and event['Action'] == 'start'):
                        capture(event, worker)
        except Exception as error:
            if not stopping.is_set():
                fail(str(error) if isinstance(error, Refusal) else 'eventObservationFailure')

    observer = threading.Thread(target=observe, daemon=False)
    observer.start()
    before, finished = None, False
    try:
        before = inventory(fixture_id)
        save('resources-before.json', before)
        require(observer.is_alive() and not errors, 'eventObserverUnavailableBeforeAdmission')
        admission = {'schemaVersion': 'weft.project-creation-browser-host-observer-admission.v1', 'fixtureId': fixture_id,
            'daemonStatus': response.status, 'filters': filters,
            'utcBeforeWorkload': datetime.datetime.now(datetime.timezone.utc).isoformat(),
            'observerResourceRemoval': False, 'workerWindow': 'exclusive creation-only window; native worker events forbidden'}
        save('observer-admission.json', admission)
        print(json.dumps(admission, separators=(',', ':')), flush=True)
        require(sys.stdin.buffer.readline(65) == b'finish\n', 'invalidFinishControl')
        finished = True
        # Operator has settled cleanup. Await queued destroy facts, never remove anything.
        deadline = time.monotonic() + 5
        with changed:
            while time.monotonic() < deadline:
                created = {event['Actor']['ID'] for event in events if event['Action'] == 'create'}
                destroyed = {event['Actor']['ID'] for event in events if event['Action'] == 'destroy'}
                if errors or (created and created <= destroyed):
                    break
                changed.wait(timeout=min(0.1, max(0, deadline - time.monotonic())))
    except Exception as error:
        fail(str(error) if isinstance(error, Refusal) else 'observerControlOrInventoryFailure')
    finally:
        stopping.set()
        try:
            event_socket.shutdown(socket.SHUT_RDWR)
        except OSError:
            pass
        observer.join(timeout=30)
        connection.close()
        if observer.is_alive():
            fail('eventThreadDidNotSettle')

    after, probes = None, []
    try:
        after = inventory(fixture_id)
        save('resources-after.json', after)
        created = {event['Actor']['ID'] for event in events if event['Action'] == 'create'}
        for identity in sorted(created):
            status, _ = get('/containers/' + identity + '/json')
            probes.append({'kind': 'container', 'identity': identity, 'daemonStatus': status, 'absent': status == 404})
        for (kind, identity), value in sorted(associated.items()):
            status, _ = get({'network': '/networks/', 'volume': '/volumes/'}[kind] + urllib.parse.quote(identity, safe=''))
            probes.append({**value, 'daemonStatus': status, 'absent': status == 404})
    except Exception:
        fail('finalReconciliationUnavailable')
    created = {event['Actor']['ID'] for event in events if event['Action'] == 'create'}
    destroyed = {event['Actor']['ID'] for event in events if event['Action'] == 'destroy'}
    workers = {event['Actor']['ID'] for event in events if event['Action'] == 'create'
        and (WORKER.fullmatch(event['Actor']['Attributes'].get('name', '')) or event['Actor']['Attributes'].get('image') == WORKER_IMAGE)}
    pg_created = created - workers
    checks = {'finishReceived': finished, 'eventThreadSettled': not observer.is_alive(),
        'preexistingIdentitiesUnchanged': before is not None and before == after,
        'allCreatedContainersDestroyed': bool(created) and created <= destroyed,
        'allDestroyedContainersAccountedFor': before is not None and destroyed <= created | set(before['container']) | set(before['preexistingWorker']),
        'zeroNativeWorkersCreated': len(workers) == 0,
        'zeroNativeWorkerEvents': not any(WORKER.fullmatch(event['Actor']['Attributes'].get('name', ''))
            or event['Actor']['Attributes'].get('image') == WORKER_IMAGE for event in events),
        'oneOwnedPostgresCreatedAndStarted': len(pg_created) == 1 and pg_created == {p['id'] for p in profiles if p['kind'] == 'postgres' and p['action'] == 'start'},
        'allOwnedPostgresProfiled': pg_created == {p['id'] for p in profiles if p['kind'] == 'postgres'},
        'associatedNetworkAndVolumeObserved': {kind for kind, _ in associated} == {'network', 'volume'} and len(associated) == 2,
        'allCapturedResourcesExactly404': bool(probes) and len(probes) == len(created) + len(associated) and all(p['absent'] for p in probes)}
    receipt = {'schemaVersion': 'weft.project-creation-browser-host-observer-receipt.v1', 'fixtureId': fixture_id,
        'outcome': 'PASS' if not errors and all(checks.values()) else 'FAIL', 'checks': checks,
        'errors': errors, 'eventCount': len(events), 'workersCreated': len(workers), 'workersDestroyed': len(workers & destroyed), 'postgresCreated': len(pg_created),
        'observerResourceRemoval': False, 'postgresNetworkVolumeCleanupOwner': 'fixture operator',
        'limits': 'Filtered metadata only. Daemon lifecycle proves destroy/absence, not the removal caller. Zero worker events are required across the exclusive host window.'}
    save('live-profiles.json', profiles)
    save('associated-live-identities.json', list(associated.values()))
    save('exact-daemon-absence.json', probes)
    save('receipt.json', receipt)
    print(json.dumps(receipt, separators=(',', ':')), flush=True)
    return 0 if receipt['outcome'] == 'PASS' else 1


if __name__ == '__main__':
    try:
        sys.exit(main())
    except Exception:
        print('{"outcome":"FAIL","errors":["observerAdmissionOrFinalizationFailure"]}', flush=True)
        sys.exit(1)
