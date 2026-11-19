"""Keep a prepared, disposable build-workbench fixture open for local review."""
import argparse
import json
import os
from pathlib import Path
import subprocess
import sys
import uuid


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('action', choices=['start', 'stop'])
    parser.add_argument('--bindings', type=Path, required=True)
    parser.add_argument('--bun', type=Path, default='bun')
    args = parser.parse_args()
    if os.name != 'nt' or not args.bindings.is_absolute():
        parser.error('Use native Windows and an absolute prepared bindings path.')
    binding = json.loads(args.bindings.read_text())
    if str(uuid.UUID(binding['fixtureId'])) != binding['fixtureId']:
        parser.error('Invalid fixture identity.')
    stop = args.bindings.parent / 'local-workbench.stop'
    ready = args.bindings.parent / 'local-workbench-ready.json'
    if args.action == 'stop':
        stop.touch(exist_ok=True)
        print('Requested local viewer shutdown and disposal of its test data.')
        return 0
    if ready.exists() or stop.exists():
        parser.error('Use fresh bindings for a new viewer; existing evidence is preserved.')
    if subprocess.check_output([str(args.bun), '--version'], text=True).strip() != '1.4.5':
        parser.error('The local viewer requires the pinned Bun 1.4.5.')
    root = Path(__file__).resolve().parents[2]
    client = (root / 'apps/web/tests/browser-execution-owner.ts').as_uri()
    source = '''
import {existsSync, writeFileSync} from 'node:fs';
const settings = JSON.parse(process.env.BRAID_LOCAL_VIEWER_OPTIONS);
const {ExecutionOwner} = await import(settings.clientModule);
const owner = new ExecutionOwner('build', () => process.env.BRAID_BUILD_BINDINGS);
let resolveStop;
const stopped = new Promise(resolve => { resolveStop = resolve; });
process.once('SIGINT', () => resolveStop());
process.once('SIGTERM', () => resolveStop());
const timer = setInterval(() => { if (existsSync(settings.stopFile)) resolveStop(); }, 500);
try {
  await owner.start();
  const m = owner.manifest, r = m.revision;
  const url = `${m.readOrigin}/catalog/organizations/${r.organizationId}/projects/${r.projectId}/mappings/${r.mappingId}/revisions/${r.revisionId}`;
  const ready = {schemaVersion:'braid.local-workbench.v1', fixtureId:m.fixtureId, url, temporary:true, dataLifecycle:'Disposed when this viewer stops'};
  writeFileSync(settings.readyFile, JSON.stringify(ready) + '\\n', {flag:'wx'});
  console.log(JSON.stringify(ready));
  await stopped;
} finally {
  clearInterval(timer);
  await owner.stop();
}
'''
    settings = {'clientModule': client, 'stopFile': str(stop), 'readyFile': str(ready)}
    env = {**os.environ, 'BRAID_BUILD_BINDINGS': str(args.bindings), 'BRAID_LOCAL_VIEWER_OPTIONS': json.dumps(settings)}
    print('Starting a disposable local workbench. Do not use it for data you need to retain.', flush=True)
    child = subprocess.Popen([str(args.bun), '-e', source], cwd=root, env=env)
    try:
        return child.wait()
    except KeyboardInterrupt:
        stop.touch(exist_ok=True)
        print('Waiting for the existing fixture owner to dispose its resources safely.', flush=True)
        return child.wait()


if __name__ == '__main__':
    sys.exit(main())
